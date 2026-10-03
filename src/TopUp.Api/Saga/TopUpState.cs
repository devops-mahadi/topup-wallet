using MassTransit;

namespace TopUp.Api.Saga;

/// <summary>
/// The persisted state of ONE top-up flow. MassTransit loads/saves this per
/// incoming message (keyed by CorrelationId = TopUpId), so the saga survives
/// process restarts: a top-up half-finished when the service crashed resumes
/// from the stored CurrentState when the next message arrives.
///
/// This is an orchestration saga: the state machine is the single brain that
/// decides the next step; the Wallet service just obeys commands and reports back.
/// </summary>
public class TopUpState : SagaStateMachineInstance
{
    // Correlation id — same value as TopUpId on every message in this flow.
    public Guid CorrelationId { get; set; }

    // Which state the machine is in: Requested / DebitingWallet / ChargingOperator
    // / Completed / Compensating / Refunded / Failed. Stored as a string.
    public string CurrentState { get; set; } = string.Empty;

    // Business data we carry across steps (needed by later steps / compensation).
    public Guid AccountId { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    // Same key the client sent — forwarded to the Wallet so the debit is exactly-once.
    public string DebitIdempotencyKey { get; set; } = string.Empty;
    // A DISTINCT key for the refund, so the compensating credit is itself idempotent
    // and never collides with the original debit's key.
    public string RefundIdempotencyKey { get; set; } = string.Empty;

    public Guid? WalletTransactionId { get; set; }
    public string? OperatorReference { get; set; }
    public string? FailureReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
