namespace TopUp.Api.Gateways;

/// <summary>
/// The boundary to an EXTERNAL mobile operator (telco) / payment partner.
/// This is the one place in the system that makes a real network call we don't
/// control — so it's isolated behind an interface. Everything about talking to
/// the outside world (HTTP client, auth, retries, timeouts) lives behind here;
/// the saga only sees Charge()/Refund() returning a result.
///
/// Why an interface:
///   - swap the live telco for a SIMULATOR in dev/tests (no real money, no flakiness)
///   - wrap the live impl in Polly (retry/timeout/circuit-breaker) in Phase 5
///     without the saga knowing
///   - the saga logic stays pure and unit-testable (mock this)
/// </summary>
public interface IOperatorGateway
{
    /// <summary>Charge the operator to deliver airtime to a phone number.
    /// Returns a result — never throws for a *business* failure (declined);
    /// a thrown exception means an infrastructure fault worth retrying.</summary>
    Task<OperatorResult> ChargeAsync(Guid topUpId, string phoneNumber, decimal amount, CancellationToken ct = default);
}

/// <summary>Outcome of an operator charge. A reference (not a throw) because a
/// decline is a normal business branch the saga must handle (→ compensate).</summary>
public record OperatorResult(bool Success, string? OperatorReference, string? FailureReason)
{
    public static OperatorResult Ok(string reference) => new(true, reference, null);
    public static OperatorResult Declined(string reason) => new(false, null, reason);
}
