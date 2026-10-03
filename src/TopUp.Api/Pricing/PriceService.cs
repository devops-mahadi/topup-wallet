using StackExchange.Redis;

namespace TopUp.Api.Pricing;

/// <summary>
/// Looks up the fee for a top-up amount, cached in Redis (cache-aside pattern).
/// The "source" here is a simulated operator price book; in production it'd be a
/// slow call to the operator's pricing API. Caching it:
///   - cuts latency and load on the external partner
///   - the TTL bounds staleness (prices refresh at most every 60s)
///
/// Cache-aside flow:
///   1. read Redis; HIT -> return it
///   2. MISS -> compute/fetch from source, write to Redis with a TTL, return it
/// We cache-aside (app manages the cache) rather than read-through so the cache
/// being down degrades to "slower", never "broken" — a Redis outage just means
/// every lookup recomputes.
/// </summary>
public class PriceService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<PriceService> _log;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public PriceService(IConnectionMultiplexer redis, ILogger<PriceService> log)
    {
        _redis = redis;
        _log = log;
    }

    /// <summary>Fee charged on a top-up of this amount (e.g. 2% service fee).</summary>
    public async Task<decimal> GetFeeAsync(decimal amount)
    {
        var db = _redis.GetDatabase();
        var key = $"price:fee:{amount}";

        var cached = await db.StringGetAsync(key);
        if (cached.HasValue)
        {
            _log.LogInformation("Price cache HIT for {Amount}", amount);
            return decimal.Parse(cached!);
        }

        _log.LogInformation("Price cache MISS for {Amount} — computing from source", amount);
        var fee = await ComputeFeeFromSourceAsync(amount);
        await db.StringSetAsync(key, fee.ToString(), Ttl);
        return fee;
    }

    // Stand-in for a slow external pricing call.
    private static async Task<decimal> ComputeFeeFromSourceAsync(decimal amount)
    {
        await Task.Delay(200);                 // simulate the slow source
        return Math.Round(amount * 0.02m, 4);  // 2% fee
    }
}
