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

        // Compound index matching the query + sort below: filter on AccountId,
        // then OccurredAtUtc descending. Without it, reading a trail scans the whole
        // collection and sorts in memory; with it, Mongo serves the page straight
        // from the index. CreateOne is idempotent (no-op if the index exists).
        var keys = Builders<AuditEvent>.IndexKeys
            .Ascending(x => x.AccountId)
            .Descending(x => x.OccurredAtUtc);
        _events.Indexes.CreateOne(new CreateIndexModel<AuditEvent>(keys));
    }

    /// <summary>Append one immutable audit event. Best-effort: audit must not break money flow.</summary>
    public Task WriteAsync(AuditEvent e, CancellationToken ct = default)
        => _events.InsertOneAsync(e, cancellationToken: ct);

    /// <summary>One page of an account's audit trail, newest first.</summary>
    public async Task<AuditPage> ForAccountAsync(Guid accountId, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        // Clamp to sane bounds so a client can't ask for page 0 or a huge pageSize.
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 200 ? 50 : pageSize;

        var filter = Builders<AuditEvent>.Filter.Eq(x => x.AccountId, accountId);
        var total = await _events.CountDocumentsAsync(filter, cancellationToken: ct);

        // Skip/Limit paging: simple and fine here. At very deep pages Skip gets
        // costly (it still walks skipped entries); the scale-up is keyset/seek
        // paging — pass the last seen OccurredAtUtc and filter < it instead of Skip.
        var items = await _events.Find(filter)
            .SortByDescending(x => x.OccurredAtUtc)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new AuditPage(items, total, page, pageSize);
    }
}
