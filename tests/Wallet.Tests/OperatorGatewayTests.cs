using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TopUp.Api.Gateways;
using Xunit;

namespace Wallet.Tests;

/// <summary>
/// Tests the operator boundary: the simulator's deterministic rules, and the
/// resilient decorator's mapping of infra faults to clean business declines
/// (so the saga compensates instead of crashing).
/// </summary>
public class OperatorGatewayTests
{
    [Fact]
    public async Task Simulator_succeeds_for_a_normal_charge()
    {
        var g = new SimulatedOperatorGateway(NullLogger<SimulatedOperatorGateway>.Instance);
        var r = await g.ChargeAsync(Guid.NewGuid(), "+880171", 5m);
        Assert.True(r.Success);
        Assert.NotNull(r.OperatorReference);
    }

    [Fact]
    public async Task Simulator_declines_amount_ending_13_cents()
    {
        var g = new SimulatedOperatorGateway(NullLogger<SimulatedOperatorGateway>.Instance);
        var r = await g.ChargeAsync(Guid.NewGuid(), "+880171", 5.13m);
        Assert.False(r.Success);
        Assert.Equal("operator_declined", r.FailureReason);
    }

    [Fact]
    public async Task Simulator_declines_phone_containing_fail()
    {
        var g = new SimulatedOperatorGateway(NullLogger<SimulatedOperatorGateway>.Instance);
        var r = await g.ChargeAsync(Guid.NewGuid(), "fail-number", 5m);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Resilient_wrapper_retries_transient_throws_then_succeeds()
    {
        // Inner throws twice (transient), succeeds on the 3rd call. Retry should
        // mask the transient faults and return the eventual success.
        var inner = new Mock<IOperatorGateway>();
        var calls = 0;
        inner.Setup(x => x.ChargeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                if (calls < 3) throw new HttpRequestException("transient");
                return Task.FromResult(OperatorResult.Ok("OP-123"));
            });

        var g = new ResilientOperatorGateway(inner.Object, NullLogger<ResilientOperatorGateway>.Instance);
        var r = await g.ChargeAsync(Guid.NewGuid(), "+880171", 5m);

        Assert.True(r.Success);
        Assert.Equal(3, calls);   // 1 original + 2 retries
    }

    [Fact]
    public async Task Resilient_wrapper_does_not_retry_a_business_decline()
    {
        // A decline is a RESULT, not a throw — must NOT be retried.
        var inner = new Mock<IOperatorGateway>();
        var calls = 0;
        inner.Setup(x => x.ChargeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns(() => { calls++; return Task.FromResult(OperatorResult.Declined("operator_declined")); });

        var g = new ResilientOperatorGateway(inner.Object, NullLogger<ResilientOperatorGateway>.Instance);
        var r = await g.ChargeAsync(Guid.NewGuid(), "+880171", 5m);

        Assert.False(r.Success);
        Assert.Equal(1, calls);   // called exactly once — no retry on a decline
    }
}
