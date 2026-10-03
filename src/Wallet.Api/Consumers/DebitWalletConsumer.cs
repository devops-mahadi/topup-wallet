using MassTransit;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
using Wallet.Api.Data;
using Wallet.Api.Services;

namespace Wallet.Api.Consumers;

/// <summary>
/// Receives a DebitWallet command from the TopUp saga, performs the (idempotent,
/// atomic, concurrency-safe) debit via WalletService, and publishes the outcome
/// as an event the saga is waiting for.
///
/// The debit itself is still exactly-once because we forward the saga's
/// IdempotencyKey into WalletService — so even if RabbitMQ redelivers this
/// command (at-least-once delivery), the money only moves once.
///
/// Ownership: the command carries the requesting UserId (from the JWT at the
/// TopUp edge). We verify the account belongs to that user before moving money,
/// so even on the internal message path a user can't top up from someone else's
/// wallet — defense in depth, not just an edge check.
/// </summary>
public class DebitWalletConsumer : IConsumer<DebitWallet>
{
    private readonly WalletService _wallet;
    private readonly WalletDbContext _db;
    private readonly ILogger<DebitWalletConsumer> _log;

    public DebitWalletConsumer(WalletService wallet, WalletDbContext db, ILogger<DebitWalletConsumer> log)
    {
        _wallet = wallet;
        _db = db;
        _log = log;
    }

    public async Task Consume(ConsumeContext<DebitWallet> ctx)
    {
        var m = ctx.Message;

        // Ownership guard: the account must belong to the requesting user.
        var ownerId = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == m.AccountId)
            .Select(a => (string?)a.UserId)
            .FirstOrDefaultAsync();
        if (ownerId is null)
        {
            await ctx.Publish(new WalletDebitFailed(m.TopUpId, m.AccountId, "account_not_found"));
            return;
        }
        if (ownerId != m.UserId)
        {
            _log.LogWarning("Ownership violation: user {User} tried to debit account {Account} owned by {Owner}",
                m.UserId, m.AccountId, ownerId);
            await ctx.Publish(new WalletDebitFailed(m.TopUpId, m.AccountId, "forbidden"));
            return;
        }

        try
        {
            var result = await _wallet.DebitAsync(
                new MoneyRequest(m.AccountId, m.Amount, m.IdempotencyKey));

            _log.LogInformation("Debited {Amount} from {Account} (txn {Txn}) for topUp {TopUp}",
                m.Amount, m.AccountId, result.TransactionId, m.TopUpId);

            await ctx.Publish(new WalletDebited(m.TopUpId, m.AccountId, m.Amount, result.TransactionId));
        }
        catch (InsufficientFundsException)
        {
            await ctx.Publish(new WalletDebitFailed(m.TopUpId, m.AccountId, "insufficient_funds"));
        }
        catch (KeyNotFoundException)
        {
            await ctx.Publish(new WalletDebitFailed(m.TopUpId, m.AccountId, "account_not_found"));
        }
    }
}
