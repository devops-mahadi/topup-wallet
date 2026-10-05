using Microsoft.EntityFrameworkCore;
using Wallet.Api.Audit;
using Wallet.Api.Data;
using Wallet.Api.Domain;

namespace Wallet.Api.Services;

/// <summary>
/// The money-movement core. Every debit/credit here is:
///   1. IDEMPOTENT  — same key never executes twice (returns the stored result).
///   2. ATOMIC      — balance update + ledger write + idempotency record all commit
///                    together in one DB transaction, or none do.
///   3. CONCURRENCY-SAFE — optimistic concurrency via the Account.RowVersion token;
///                    two simultaneous debits can't both read the old balance and overdraw.
/// </summary>
public class WalletService
{
    private readonly WalletDbContext _db;
    private readonly IAuditLog _audit;
    public WalletService(WalletDbContext db, IAuditLog audit)
    {
        _db = db;
        _audit = audit;
    }

    public Task<MoneyResult> DebitAsync(MoneyRequest req, CancellationToken ct = default)
        => MoveAsync(req, TransactionType.Debit, ct);

    public Task<MoneyResult> CreditAsync(MoneyRequest req, CancellationToken ct = default)
        => MoveAsync(req, TransactionType.Credit, ct);

    private async Task<MoneyResult> MoveAsync(MoneyRequest req, TransactionType type, CancellationToken ct)
    {
        if (req.Amount <= 0)
            throw new ArgumentException("Amount must be positive.", nameof(req.Amount));

        // ---- Idempotency check #1 (fast path): already processed? return stored result. ----
        var existing = await _db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Key == req.IdempotencyKey, ct);
        if (existing is not null)
            return new MoneyResult(existing.TransactionId, existing.ResultingBalance, WasReplay: true);

        // When we're running inside a MassTransit consumer, the transactional-outbox
        // filter has ALREADY opened a transaction on this (scoped) DbContext and will
        // commit it — together with the OutboxMessage row for whatever the consumer
        // publishes. In that case we must NOT open or commit our own: we enlist in
        // theirs, so the balance update, the ledger row, the idempotency record and
        // the published event all commit atomically or not at all. That is the whole
        // point of the outbox — it closes the commit-then-publish gap where money
        // moves but the event announcing it is lost.
        //
        // On the HTTP path there is no ambient transaction, so we own one as before.
        var ambient = _db.Database.CurrentTransaction is not null;

        // Retry loop: optimistic concurrency can fail if another txn updates the
        // same account first. We reload + retry a few times rather than overdraw.
        // Under an ambient transaction we can't roll back just our own work, so the
        // retry is the consumer's job: MassTransit redelivers the message and the
        // idempotency key makes the replay safe.
        var maxAttempts = ambient ? 1 : 3;
        for (var attempt = 1; ; attempt++)
        {
            // One DB transaction wraps the whole money move → atomicity.
            await using var tx = ambient
                ? null
                : await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == req.AccountId, ct)
                    ?? throw new KeyNotFoundException("Account not found.");

                // Apply the money math (decimal — exact).
                if (type == TransactionType.Debit)
                {
                    if (account.Balance < req.Amount)
                        throw new InsufficientFundsException();
                    account.Balance -= req.Amount;
                }
                else
                {
                    account.Balance += req.Amount;
                }

                var txn = new WalletTransaction
                {
                    Id = Guid.CreateVersion7(),   // time-ordered UUIDv7 — sequential, index-friendly
                    AccountId = account.Id,
                    Type = type,
                    Amount = req.Amount,
                    BalanceAfter = account.Balance,
                    IdempotencyKey = req.IdempotencyKey,
                    CreatedAtUtc = DateTime.UtcNow
                };
                _db.Transactions.Add(txn);

                // Record the idempotency key WITH the result, in the SAME transaction.
                // If two requests with the same key race, the PK uniqueness on Key makes
                // the second commit fail → caught below → we return the stored result.
                _db.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    Key = req.IdempotencyKey,
                    TransactionId = txn.Id,
                    ResultingBalance = account.Balance,
                    CreatedAtUtc = DateTime.UtcNow
                });

                // SaveChanges issues the UPDATE with "WHERE RowVersion = @original".
                // If another txn changed the account first → DbUpdateConcurrencyException.
                await _db.SaveChangesAsync(ct);
                if (tx is not null) await tx.CommitAsync(ct);

                // Audit AFTER commit. The money is already durably committed in SQL;
                // the audit log is a separate append-only trail in Mongo. We don't put
                // it inside the DB transaction because it's a different store — instead
                // it's best-effort post-commit. (Production: for guaranteed audit you'd
                // use the OUTBOX pattern — write an audit intent in the same SQL txn,
                // publish async — so you never commit-but-fail-to-audit.)
                await _audit.WriteAsync(new AuditEvent
                {
                    EventType = type.ToString(),       // "Debit" / "Credit"
                    AccountId = account.Id,
                    TransactionId = txn.Id,
                    Amount = req.Amount,
                    BalanceAfter = account.Balance,
                    IdempotencyKey = req.IdempotencyKey
                }, ct);

                return new MoneyResult(txn.Id, account.Balance, WasReplay: false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another transaction won the race on this account. Roll back, reset the
                // tracked entities, and retry with a fresh read of the balance.
                // Under an ambient transaction we let it propagate instead: rolling back
                // the outbox filter's transaction here would also discard the event it
                // is about to publish, so we leave the unwind to MassTransit.
                if (tx is null) throw;
                await tx.RollbackAsync(ct);
                if (attempt >= maxAttempts) throw;
                foreach (var entry in _db.ChangeTracker.Entries().ToList())
                    await entry.ReloadAsync(ct);
                _db.ChangeTracker.Clear();
            }
            catch (DbUpdateException)
            {
                // Could be the idempotency-key unique-PK collision — a concurrent duplicate
                // request with the same key committed first. Can't await in a catch filter,
                // so we check here: if the key now exists, return the stored result
                // (still exactly-once); otherwise it's a real failure → rethrow.
                //
                // Under an ambient transaction we can't recover: the failed SaveChanges
                // has poisoned the outbox filter's transaction, and reading through it
                // would be reading from a doomed transaction. Rethrow and let the
                // consumer's redelivery hit the fast-path idempotency check at the top,
                // which returns the stored result on a clean transaction.
                if (tx is null) throw;
                await tx.RollbackAsync(ct);
                var rec = await _db.IdempotencyRecords.AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Key == req.IdempotencyKey, ct);
                if (rec is not null)
                    return new MoneyResult(rec.TransactionId, rec.ResultingBalance, WasReplay: true);
                throw;
            }
            catch
            {
                // Ambient: the outbox filter owns the transaction and unwinds it when
                // the consumer faults, so we only roll back one we opened ourselves.
                if (tx is not null) await tx.RollbackAsync(ct);
                throw;
            }
        }
    }
}
