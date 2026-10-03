namespace TopUp.Api.Gateways;

/// <summary>
/// Fake telco for dev/demo. Simulates latency and a deterministic failure rule so
/// we can exercise BOTH saga branches (success and compensation) without a real
/// partner. In production this class is replaced by an HttpOperatorGateway that
/// calls the real API — the saga code doesn't change.
///
/// Failure rule (deterministic so demos/tests are repeatable):
///   amount ending in .13  -> declined (test the compensation path)
///   phoneNumber "fail"    -> declined
///   otherwise             -> success
/// </summary>
public class SimulatedOperatorGateway : IOperatorGateway
{
    private readonly ILogger<SimulatedOperatorGateway> _log;
    public SimulatedOperatorGateway(ILogger<SimulatedOperatorGateway> log) => _log = log;

    public async Task<OperatorResult> ChargeAsync(Guid topUpId, string phoneNumber, decimal amount, CancellationToken ct = default)
    {
        _log.LogInformation("Operator charge start: topUp={TopUpId} phone={Phone} amount={Amount}", topUpId, phoneNumber, amount);

        // Simulate a network round-trip to the telco.
        await Task.Delay(300, ct);

        var centsPart = Math.Abs((amount - Math.Truncate(amount)) * 100);
        var declined = phoneNumber.Contains("fail", StringComparison.OrdinalIgnoreCase)
                       || (int)centsPart == 13;

        if (declined)
        {
            _log.LogWarning("Operator DECLINED: topUp={TopUpId}", topUpId);
            return OperatorResult.Declined("operator_declined");
        }

        var reference = $"OP-{Guid.CreateVersion7():N}"[..12];
        _log.LogInformation("Operator OK: topUp={TopUpId} ref={Ref}", topUpId, reference);
        return OperatorResult.Ok(reference);
    }
}
