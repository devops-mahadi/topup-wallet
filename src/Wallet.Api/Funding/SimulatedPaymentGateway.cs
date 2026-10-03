using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wallet.Api.Funding;

/// <summary>
/// Fake PSP for dev/demo. Deterministic branches off the payment token so we can
/// exercise every path without a real provider:
///   token "tok_ok"      -> Succeeded  (webhook will confirm)
///   token "tok_3ds"     -> RequiresAction (3-D Secure challenge)
///   token "tok_decline" -> Declined
///   anything else       -> Succeeded
///
/// It also SIGNS its webhook payloads with the same HMAC scheme a real PSP uses,
/// so the signature-verification code path is real (not stubbed). In production
/// this class is replaced by e.g. StripePaymentGateway calling the Stripe SDK;
/// the interface and the rest of the app don't change.
/// </summary>
public class SimulatedPaymentGateway : IPaymentGateway
{
    private readonly byte[] _webhookSecret;
    private readonly ILogger<SimulatedPaymentGateway> _log;

    public SimulatedPaymentGateway(IConfiguration config, ILogger<SimulatedPaymentGateway> log)
    {
        // The webhook signing secret — a real PSP gives you this; here it's config.
        var secret = config["Payments:WebhookSecret"] ?? "dev-webhook-secret-change-me";
        _webhookSecret = Encoding.UTF8.GetBytes(secret);
        _log = log;
    }

    public Task<DepositIntentResult> CreateDepositAsync(string paymentToken, decimal amount, string currency, CancellationToken ct = default)
    {
        var intentId = $"pi_{Guid.CreateVersion7():N}"[..16];
        var result = paymentToken switch
        {
            "tok_decline" => new DepositIntentResult(intentId, DepositStatus.Declined, null, "card_declined"),
            "tok_3ds"     => new DepositIntentResult(intentId, DepositStatus.RequiresAction, $"{intentId}_secret", null),
            _             => new DepositIntentResult(intentId, DepositStatus.Succeeded, null, null)
        };
        _log.LogInformation("PSP create deposit {Intent} token={Token} -> {Status}", intentId, paymentToken, result.Status);
        return Task.FromResult(result);
    }

    public PaymentWebhookEvent? VerifyAndParseWebhook(string rawBody, string signatureHeader)
    {
        // Recompute the HMAC over the RAW body and constant-time compare. A real
        // PSP signs exactly this way (Stripe: HMAC-SHA256, secret = whsec_...).
        var expected = Sign(rawBody);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signatureHeader ?? "")))
        {
            _log.LogWarning("Webhook signature mismatch — rejecting");
            return null;
        }

        try
        {
            var doc = JsonDocument.Parse(rawBody).RootElement;
            return new PaymentWebhookEvent(
                doc.GetProperty("eventId").GetString()!,
                doc.GetProperty("providerIntentId").GetString()!,
                Enum.Parse<DepositStatus>(doc.GetProperty("status").GetString()!, ignoreCase: true));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Webhook parse failed: {Msg}", ex.Message);
            return null;
        }
    }

    /// <summary>Helper to produce a valid signature for a body — used by the demo
    /// webhook-sender endpoint so you can trigger a signed webhook locally.</summary>
    public string Sign(string rawBody)
    {
        using var hmac = new HMACSHA256(_webhookSecret);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
    }
}
