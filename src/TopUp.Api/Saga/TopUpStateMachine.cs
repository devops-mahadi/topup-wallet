using MassTransit;
using Shared.Contracts;
using TopUp.Api.Gateways;

namespace TopUp.Api.Saga;

/// <summary>
/// Orchestrates one top-up as a saga (a long-running transaction made of local
/// steps, each with a compensating action):
///
///   Requested
///     --(DebitWallet command)-->  DebitingWallet
///   DebitingWallet
///     --WalletDebited event-->     call operator:
///                                    OK       -> Completed
///                                    declined -> Compensating (RefundWallet command)
///     --WalletDebitFailed event--> Failed  (nothing to undo; no money moved)
///   Compensating
///     --WalletRefunded event-->    Refunded
///
/// Key fintech idea: we don't hold one DB transaction across the external call
/// (you can't lock a row for a network round-trip). Each step commits locally,
/// and if a LATER step fails we run a COMPENSATION (the refund) to restore
/// consistency. That's the saga pattern vs a distributed 2-phase commit.
/// </summary>
public class TopUpStateMachine : MassTransitStateMachine<TopUpState>
{
    private readonly IOperatorGateway _operator;

    // States
    public State ChargingOperator { get; private set; } = null!;
    public State Compensating { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Refunded { get; private set; } = null!;
    public State Failed { get; private set; } = null!;

    // Events (incoming messages the machine reacts to)
    public Event<StartTopUp> TopUpRequested { get; private set; } = null!;
    public Event<WalletDebited> Debited { get; private set; } = null!;
    public Event<WalletDebitFailed> DebitFailed { get; private set; } = null!;
    public Event<WalletRefunded> Refund { get; private set; } = null!;

    public TopUpStateMachine(IOperatorGateway operatorGateway)
    {
        _operator = operatorGateway;

        InstanceState(x => x.CurrentState);

        // Correlation: StartTopUp creates the instance; others find it by TopUpId.
        Event(() => TopUpRequested, e => e.CorrelateById(m => m.Message.TopUpId));
        Event(() => Debited,        e => e.CorrelateById(m => m.Message.TopUpId));
        Event(() => DebitFailed,    e => e.CorrelateById(m => m.Message.TopUpId));
        Event(() => Refund,         e => e.CorrelateById(m => m.Message.TopUpId));

        // ----- Requested: capture data, send the debit command -----
        Initially(
            When(TopUpRequested)
                .Then(ctx =>
                {
                    var m = ctx.Message;
                    ctx.Saga.AccountId = m.AccountId;
                    ctx.Saga.UserId = m.UserId;
                    ctx.Saga.PhoneNumber = m.PhoneNumber;
                    ctx.Saga.Amount = m.Amount;
                    ctx.Saga.DebitIdempotencyKey = m.IdempotencyKey;
                    ctx.Saga.RefundIdempotencyKey = $"{m.IdempotencyKey}:refund";
                    ctx.Saga.CreatedAtUtc = DateTime.UtcNow;
                    ctx.Saga.UpdatedAtUtc = DateTime.UtcNow;
                })
                .Send(new Uri("queue:wallet-debit"), ctx => new DebitWallet(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.AccountId,
                    ctx.Saga.UserId,
                    ctx.Saga.Amount,
                    ctx.Saga.DebitIdempotencyKey))
                .TransitionTo(ChargingOperator));

        // ----- ChargingOperator: wait for the wallet's answer, then call telco -----
        During(ChargingOperator,
            When(Debited)
                .Then(ctx => ctx.Saga.WalletTransactionId = ctx.Message.TransactionId)
                // Call the external operator. The result decides the next branch.
                .ThenAsync(async ctx =>
                {
                    var r = await _operator.ChargeAsync(
                        ctx.Saga.CorrelationId, ctx.Saga.PhoneNumber, ctx.Saga.Amount);
                    ctx.Saga.UpdatedAtUtc = DateTime.UtcNow;
                    ctx.Saga.OperatorReference = r.OperatorReference;
                    if (!r.Success) ctx.Saga.FailureReason = r.FailureReason;
                })
                // Branch on the stored outcome.
                .IfElse(ctx => ctx.Saga.OperatorReference is not null,
                    ok => ok
                        .TransitionTo(Completed)
                        .Finalize(),
                    bad => bad
                        // Compensate: tell the Wallet to refund the committed debit.
                        .Send(new Uri("queue:wallet-refund"), ctx => new RefundWallet(
                            ctx.Saga.CorrelationId,
                            ctx.Saga.AccountId,
                            ctx.Saga.Amount,
                            ctx.Saga.RefundIdempotencyKey))
                        .TransitionTo(Compensating)),

            When(DebitFailed)
                // No money moved — end as Failed, no compensation.
                .Then(ctx => ctx.Saga.FailureReason = ctx.Message.Reason)
                .TransitionTo(Failed)
                .Finalize());

        // ----- Compensating: wait for the refund to confirm -----
        During(Compensating,
            When(Refund)
                .Then(ctx => ctx.Saga.WalletTransactionId = ctx.Message.TransactionId)
                .TransitionTo(Refunded)
                .Finalize());

        SetCompletedWhenFinalized();
    }
}
