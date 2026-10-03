using MongoDB.Driver;

namespace Wallet.Api.Audit;

/// <summary>
/// Append-only audit log backed by MongoDB. Only inserts + reads — no update/delete.
/// Registered as a singleton: the Mongo client is thread-safe and manages its own
/// connection pool, so one instance is shared across the app.
/// </summary>
public class AuditLog : IAuditLog
{
    private readonly IMongoCollection<AuditEvent> _events;

    public AuditLog(IConfiguration config)
    {
        var conn = config.GetConnectionString("Mongo") ?? "mongodb://localhost:27017";
        var client = new MongoClient(conn);
        var db = client.GetDatabase("walletaudit");
        _events = db.GetCollection<AuditEvent>("events");
    }

    /// <summary>Append one immutable audit event. Best-effort: audit must not break money flow.</summary>
    public Task WriteAsync(AuditEvent e, CancellationToken ct = default)
        => _events.InsertOneAsync(e, cancellationToken: ct);

    /// <summary>Read an account's audit trail, newest first.</summary>
    public async Task<List<AuditEvent>> ForAccountAsync(Guid accountId, CancellationToken ct = default)
        => await _events.Find(x => x.AccountId == accountId)
            .SortByDescending(x => x.OccurredAtUtc)
            .ToListAsync(ct);
}
