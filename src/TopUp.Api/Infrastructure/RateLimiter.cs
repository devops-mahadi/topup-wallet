using StackExchange.Redis;

namespace TopUp.Api.Infrastructure;

/// <summary>
/// Distributed, fixed-window rate limiter backed by Redis — a fraud/abuse guard
/// so one account can't fire hundreds of top-ups a minute. Redis (not in-memory)
/// because the limit must hold ACROSS all TopUp.Api instances: behind a load
/// balancer, a per-process counter would let N instances each allow the full
/// quota. Redis is the shared source of truth.
///
/// Mechanics: INCR a per-account-per-minute key. The FIRST increment in a window
/// sets the key's TTL to the window length, so the counter auto-expires and the
/// next window starts fresh — no cleanup job. INCR is atomic, so concurrent
/// requests can't both read "0" and both pass.
/// </summary>
public class RateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RateLimiter> _log;

    public RateLimiter(IConnectionMultiplexer redis, ILogger<RateLimiter> log)
    {
        _redis = redis;
        _log = log;
    }

    /// <summary>True if the action is allowed; false if the account is over the limit.</summary>
    public async Task<bool> AllowAsync(Guid accountId, int maxPerWindow, TimeSpan window)
    {
        var db = _redis.GetDatabase();
        // Bucket key includes the window start so each window is a distinct counter.
        var bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)window.TotalSeconds;
        var key = $"ratelimit:topup:{accountId}:{bucket}";

        var count = await db.StringIncrementAsync(key);
        if (count == 1)
            await db.KeyExpireAsync(key, window);   // set TTL only on first hit in window

        var allowed = count <= maxPerWindow;
        if (!allowed)
            _log.LogWarning("Rate limit hit: account={Account} count={Count}/{Max}", accountId, count, maxPerWindow);
        return allowed;
    }
}
