using MassTransit;
using Shared.Contracts;
using Wallet.Api.Services;

namespace Wallet.Api.Consumers;

/// <summary>
/// Receives the compensating RefundWallet command and posts an equal, opposite
/// CREDIT — the saga's "undo" for a debit whose downstream step (operator charge)
/// failed. We never physically reverse the original debit row; the ledger stays
/// append-only, so the account shows debit then credit (a full audit trail).
///
/// The refund uses its OWN idempotency key ("<orig>:refund"), so retries of the
/// refund are exactly-once and can't collide with the original debit's key.
/// </summary>
public class RefundWalletConsumer : IConsumer<RefundWallet>
{
    private readonly WalletService _wallet;
    private readonly ILogger<RefundWalletConsumer> _log;

    public RefundWalletConsumer(WalletService wallet, ILogger<RefundWalletConsumer> log)
    {
        _wallet = wallet;
        _log = log;
    }

    public async Task Consume(ConsumeContext<RefundWallet> ctx)
    {
        var m = ctx.Message;
        var result = await _wallet.CreditAsync(
            new MoneyRequest(m.AccountId, m.Amount, m.IdempotencyKey));

        _log.LogInformation("Refunded {Amount} to {Account} (txn {Txn}) for topUp {TopUp}",
            m.Amount, m.AccountId, result.TransactionId, m.TopUpId);

        await ctx.Publish(new WalletRefunded(m.TopUpId, m.AccountId, m.Amount, result.TransactionId));
    }
}
