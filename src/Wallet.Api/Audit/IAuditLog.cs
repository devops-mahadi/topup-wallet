namespace Wallet.Api.Audit;

/// <summary>
/// Abstraction over the audit sink so the money core (WalletService) depends on
/// an interface, not the concrete Mongo client. Lets tests substitute a no-op /
/// fake audit log without standing up Mongo, and lets production swap the backing
/// store without touching WalletService.
/// </summary>
public interface IAuditLog
{
    Task WriteAsync(AuditEvent e, CancellationToken ct = default);

    /// <summary>One page of an account's audit trail, newest first. Paginated so
    /// we never load an entire (potentially huge) trail into memory.</summary>
    Task<AuditPage> ForAccountAsync(Guid accountId, int page = 1, int pageSize = 50, CancellationToken ct = default);
}

/// <summary>A page of audit events plus the total count, for client paging.</summary>
public record AuditPage(IReadOnlyList<AuditEvent> Items, long Total, int Page, int PageSize);
