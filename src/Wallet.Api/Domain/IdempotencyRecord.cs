namespace Wallet.Api.Domain;

/// <summary>
/// Records that a given idempotency key has already been processed, with the
/// result we returned. The core of "money moves exactly once":
/// before doing any debit/credit, we check this table. If the key exists,
/// we return the STORED result instead of executing again — so a client retry,
/// a double-click, or an at-least-once message redelivery can't double-charge.
/// </summary>
public class IdempotencyRecord
{
    /// <summary>The client-supplied idempotency key = primary key. Uniqueness enforced by the DB.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The transaction id we produced the first time — returned on replay.</summary>
    public Guid TransactionId { get; set; }

    /// <summary>The balance we reported the first time — returned on replay.</summary>
    public decimal ResultingBalance { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
