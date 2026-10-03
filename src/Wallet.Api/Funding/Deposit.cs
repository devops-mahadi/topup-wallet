namespace Wallet.Api.Funding;

/// <summary>
/// A funding attempt: user → card → wallet. We persist it so the async webhook
/// can be correlated back to the account/amount, and so a deposit has an
/// auditable lifecycle (Pending → Succeeded/Declined) independent of the PSP.
///
/// We DON'T credit the wallet when the deposit is created — only when the PSP's
/// webhook confirms the money was actually captured. Until then it's Pending.
/// </summary>
public class Deposit
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid AccountId { get; set; }
    public string UserId { get; set; } = string.Empty;

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";

    /// <summary>The PSP's intent id — links this row to the webhook that confirms it.</summary>
    public string ProviderIntentId { get; set; } = string.Empty;

    public DepositState State { get; set; } = DepositState.Pending;

    /// <summary>Set once the confirming webhook has credited the wallet, so a
    /// replayed webhook is a no-op (belt-and-braces alongside the idempotency store).</summary>
    public bool Credited { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public enum DepositState
{
    Pending,
    Succeeded,
    Declined
}
