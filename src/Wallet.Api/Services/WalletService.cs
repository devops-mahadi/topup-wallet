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

        // Retry loop: optimistic concurrency can fail if another txn updates the
        // same account first. We reload + retry a few times rather than overdraw.
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            // One DB transaction wraps the whole money move → atomicity.
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
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
                await tx.CommitAsync(ct);

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
                await tx.RollbackAsync(ct);
                var rec = await _db.IdempotencyRecords.AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Key == req.IdempotencyKey, ct);
                if (rec is not null)
                    return new MoneyResult(rec.TransactionId, rec.ResultingBalance, WasReplay: true);
                throw;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
    }
}
