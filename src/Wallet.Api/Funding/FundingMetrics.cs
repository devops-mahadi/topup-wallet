using System.Diagnostics.Metrics;

namespace Wallet.Api.Funding;

/// <summary>
/// Custom business metrics for funding — the signals ops/fraud actually watch,
/// which auto-instrumentation can't know about. Emitted through a named Meter so
/// the OpenTelemetry metrics pipeline picks them up and exports them over OTLP.
/// Register the meter name with AddMeter("TopUpWallet.Funding") in the OTel setup.
/// </summary>
public class FundingMetrics
{
    public const string MeterName = "TopUpWallet.Funding";

    private readonly Counter<long> _depositsCreated;
    private readonly Counter<long> _depositsCredited;
    private readonly Counter<long> _webhookRejected;   // bad-signature = possible fraud/forgery

    public FundingMetrics(IMeterFactory factory)
    {
        var meter = factory.Create(MeterName);
        _depositsCreated  = meter.CreateCounter<long>("funding.deposits.created");
        _depositsCredited = meter.CreateCounter<long>("funding.deposits.credited");
        _webhookRejected  = meter.CreateCounter<long>("funding.webhook.rejected");
    }

    public void DepositCreated() => _depositsCreated.Add(1);
    public void DepositCredited() => _depositsCredited.Add(1);
    public void WebhookRejected() => _webhookRejected.Add(1);
}
