using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace TopUp.Api.Gateways;

/// <summary>
/// Decorator that wraps ANY IOperatorGateway in a Polly resilience pipeline:
///   TIMEOUT          — cap each attempt (don't hang forever on a stuck telco)
///   RETRY            — a few retries with exponential backoff + jitter, for
///                      transient faults (thrown exceptions / timeouts), NOT for
///                      business declines (a declined result is returned, not thrown)
///   CIRCUIT BREAKER  — if the telco is failing a lot, open the circuit and fail
///                      fast for a cool-down, so we stop hammering a dead partner
///                      and don't pile up threads waiting on it.
///
/// Decorator pattern: the saga depends on IOperatorGateway and never knows whether
/// it's talking to the raw gateway or this resilient wrapper. Pipeline order
/// (outer->inner): retry -> circuit-breaker -> timeout, so a per-attempt timeout
/// counts as a failure the breaker and retry can see.
/// </summary>
public class ResilientOperatorGateway : IOperatorGateway
{
    private readonly IOperatorGateway _inner;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<ResilientOperatorGateway> _log;

    public ResilientOperatorGateway(IOperatorGateway inner, ILogger<ResilientOperatorGateway> log)
    {
        _inner = inner;
        _log = log;

        _pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(200),
                OnRetry = args =>
                {
                    _log.LogWarning("Operator call retry {Attempt} after {Ex}",
                        args.AttemptNumber, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new Polly.CircuitBreaker.CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,                       // 50% failures in the window...
                MinimumThroughput = 4,                    // ...over at least 4 calls...
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(15), // ...opens the circuit for 15s
            })
            .AddTimeout(TimeSpan.FromSeconds(3))          // per-attempt timeout
            .Build();
    }

    public async Task<OperatorResult> ChargeAsync(Guid topUpId, string phoneNumber, decimal amount, CancellationToken ct = default)
    {
        try
        {
            return await _pipeline.ExecuteAsync(
                async token => await _inner.ChargeAsync(topUpId, phoneNumber, amount, token), ct);
        }
        catch (BrokenCircuitException)
        {
            // Breaker open — fail fast without touching the telco.
            _log.LogError("Operator circuit OPEN — failing fast for topUp {TopUp}", topUpId);
            return OperatorResult.Declined("operator_unavailable");
        }
        catch (TimeoutRejectedException)
        {
            _log.LogError("Operator call timed out for topUp {TopUp}", topUpId);
            return OperatorResult.Declined("operator_timeout");
        }
    }
}
