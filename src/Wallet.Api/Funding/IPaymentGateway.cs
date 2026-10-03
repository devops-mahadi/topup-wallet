namespace Wallet.Api.Funding;

/// <summary>
/// Boundary to an external PAYMENT gateway / PSP (Stripe, Adyen, SSLCommerz…) —
/// the "money in" side: a user funds their wallet from a card. Mirror of the
/// operator gateway on the "money out" side: one interface, a simulator for dev,
/// a real HTTP impl in prod — the rest of the app doesn't change.
///
/// CRITICAL design rule this models: the raw card number (PAN) NEVER touches our
/// server. The browser sends the PAN straight to the PSP (hosted fields / PSP.js)
/// and gets back a one-time PAYMENT TOKEN. We only ever see that token → keeps us
/// out of most PCI-DSS scope. CreateDeposit takes the token, not card details.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>
    /// Ask the PSP to charge the tokenized payment method. Returns the created
    /// intent with its status. We do NOT credit the wallet here — the authoritative
    /// confirmation arrives asynchronously via webhook (see VerifyAndParseWebhook).
    /// </summary>
    Task<DepositIntentResult> CreateDepositAsync(string paymentToken, decimal amount, string currency, CancellationToken ct = default);

    /// <summary>
    /// Verify a webhook's signature (HMAC over the raw body with the PSP's signing
    /// secret) and parse it. Returns null if the signature is invalid — an unsigned
    /// or forged webhook must NEVER be trusted, or anyone could mint balance by
    /// POSTing a fake "payment succeeded".
    /// </summary>
    PaymentWebhookEvent? VerifyAndParseWebhook(string rawBody, string signatureHeader);
}

/// <summary>Status of a deposit as the PSP sees it.</summary>
public enum DepositStatus
{
    Succeeded,       // charged, funds captured
    RequiresAction,  // needs 3-D Secure / SCA challenge in the browser
    Declined         // issuer declined
}

/// <summary>Result of creating a deposit intent at the PSP.</summary>
public record DepositIntentResult(
    string ProviderIntentId,   // the PSP's id for this charge (e.g. Stripe pi_...)
    DepositStatus Status,
    string? ClientSecret,      // for RequiresAction: browser uses this to complete 3DS
    string? DeclineReason);

/// <summary>A parsed, signature-verified webhook event from the PSP.</summary>
public record PaymentWebhookEvent(
    string EventId,            // PSP's unique event id → our idempotency key
    string ProviderIntentId,   // ties back to the deposit
    DepositStatus Status);
