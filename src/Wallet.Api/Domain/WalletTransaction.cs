namespace Wallet.Api.Domain;

public enum TransactionType { Debit, Credit }

/// <summary>
/// An immutable record of one money movement on an account.
/// We never mutate or delete these — append-only. This is the ledger:
/// the balance is really the sum of its transactions, and this gives us
/// the audit trail + reconciliation fintech requires.
/// </summary>
public class WalletTransaction
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public TransactionType Type { get; set; }

    /// <summary>Always positive; Type says debit or credit. DECIMAL(19,4).</summary>
    public decimal Amount { get; set; }

    /// <summary>Balance AFTER this transaction — snapshot for audit/reconciliation.</summary>
    public decimal BalanceAfter { get; set; }

    /// <summary>
    /// The idempotency key of the request that created this transaction.
    /// Lets us tie a ledger entry back to the originating request + detect duplicates.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
