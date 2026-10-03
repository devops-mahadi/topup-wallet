namespace Wallet.Api.Audit;

/// <summary>
/// One immutable audit record of a money event, stored in MongoDB.
/// Why Mongo (not the SQL ledger): the audit log is append-only, write-heavy,
/// and schema-flexible (different event types carry different detail). Keeping it
/// in a SEPARATE store also means the audit trail survives independently of the
/// transactional DB — a regulator/dispute can be served even while SQL is busy.
/// We never update or delete these documents.
/// </summary>
public class AuditEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>What happened, e.g. "Debit", "Credit", "DebitRejected_InsufficientFunds".</summary>
    public string EventType { get; set; } = string.Empty;

    public Guid AccountId { get; set; }

    /// <summary>The ledger transaction id, when one was produced (null for rejections).</summary>
    public Guid? TransactionId { get; set; }

    public decimal Amount { get; set; }

    /// <summary>Balance after the event (when applicable).</summary>
    public decimal? BalanceAfter { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
