using RageWebUI.DirectX.Browser;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class AcceleratedSubmitHealthPolicyTests
{
    [Fact]
    public void BoundedPoolPressureOnlyDropsFrames()
    {
        var policy = new AcceleratedSubmitHealthPolicy(3);
        for (var i = 0; i < 1000; i++)
            Assert.Equal(AcceleratedSubmitDecision.Dropped, policy.Observe(AcceleratedSubmitHealth.Backpressure));
    }

    [Fact]
    public void TypedSessionOrAdapterInvalidationDisablesImmediately()
    {
        var policy = new AcceleratedSubmitHealthPolicy(8);
        Assert.Equal(
            AcceleratedSubmitDecision.Disable,
            policy.Observe(AcceleratedSubmitHealth.HardInvalidation));
    }

    [Fact]
    public void SustainedHardFailuresDisableAtBound()
    {
        var policy = new AcceleratedSubmitHealthPolicy(3);
        Assert.Equal(AcceleratedSubmitDecision.Dropped, policy.Observe(AcceleratedSubmitHealth.HardFailure));
        Assert.Equal(AcceleratedSubmitDecision.Dropped, policy.Observe(AcceleratedSubmitHealth.HardFailure));
        Assert.Equal(AcceleratedSubmitDecision.Disable, policy.Observe(AcceleratedSubmitHealth.HardFailure));
    }

    [Fact]
    public void SuccessAndBackpressureBreakAConsecutiveHardFailureStreak()
    {
        var policy = new AcceleratedSubmitHealthPolicy(2);
        Assert.Equal(AcceleratedSubmitDecision.Dropped, policy.Observe(AcceleratedSubmitHealth.HardFailure));
        Assert.Equal(AcceleratedSubmitDecision.Dropped, policy.Observe(AcceleratedSubmitHealth.Backpressure));
        Assert.Equal(AcceleratedSubmitDecision.Dropped, policy.Observe(AcceleratedSubmitHealth.HardFailure));
        Assert.Equal(AcceleratedSubmitDecision.Accepted, policy.Observe(AcceleratedSubmitHealth.Submitted));
        Assert.Equal(AcceleratedSubmitDecision.Dropped, policy.Observe(AcceleratedSubmitHealth.HardFailure));
    }
}
