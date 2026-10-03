using MassTransit;
using Shared.Contracts;
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
/// </summary>
public class DebitWalletConsumer : IConsumer<DebitWallet>
{
    private readonly WalletService _wallet;
    private readonly ILogger<DebitWalletConsumer> _log;

    public DebitWalletConsumer(WalletService wallet, ILogger<DebitWalletConsumer> log)
    {
        _wallet = wallet;
        _log = log;
    }

    public async Task Consume(ConsumeContext<DebitWallet> ctx)
    {
        var m = ctx.Message;
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
