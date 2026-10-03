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
    Task<List<AuditEvent>> ForAccountAsync(Guid accountId, CancellationToken ct = default);
}
