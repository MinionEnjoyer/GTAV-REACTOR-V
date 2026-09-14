using RageWebUI.Script;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class PassiveHudPresentationGateTests
{
    [Fact]
    public void RequestIsNotVisibilityAndDoesNotReopenWhileAwaitingHost()
    {
        var gate = new PassiveHudPresentationGate();
        Assert.Equal(PassiveHudPresentationAction.Show, gate.Update(0, true, true, false));
        Assert.Equal(PassiveHudPresentationState.Requested, gate.State);
        for (var now = 1; now < 4000; now += 100)
            Assert.Equal(PassiveHudPresentationAction.None, gate.Update(now, true, true, false));
        Assert.Equal(1, gate.Attempts);
        Assert.Equal(PassiveHudPresentationAction.None, gate.Update(3999, true, true, true, true));
        Assert.Equal(PassiveHudPresentationState.Presented, gate.State);
        Assert.Equal(PassiveHudPresentationAction.None, gate.Update(9000, true, true, true, true));
    }

    [Fact]
    public void NeverPresentedAttemptTimesOutRetriesOnceThenWaitsForFreshActivation()
    {
        var gate = new PassiveHudPresentationGate();
        gate.Update(0, true, true, false);
        Assert.Equal(PassiveHudPresentationAction.Hide, gate.Update(4000, true, true, false));
        Assert.False(gate.IsRequested);
        Assert.Equal(PassiveHudPresentationAction.None, gate.Update(4999, true, true, false));
        Assert.Equal(PassiveHudPresentationAction.Show, gate.Update(5000, true, true, false));
        Assert.Equal(PassiveHudPresentationAction.Hide, gate.Update(9000, true, true, false));
        Assert.Equal(PassiveHudPresentationState.Exhausted, gate.State);
        Assert.Equal(PassiveHudPresentationAction.None, gate.Update(9999999, true, true, false));
        gate.Update(10000000, false, true, false);
        Assert.Equal(PassiveHudPresentationAction.Show, gate.Update(10000001, true, true, false));
        Assert.Equal(1, gate.Attempts);
    }

    [Fact]
    public void LostHostVisibilityReconcilesInsteadOfLatchingRequestedForever()
    {
        var gate = new PassiveHudPresentationGate();
        gate.Update(0, true, true, false);
        gate.Update(50, true, true, true, true);
        Assert.Equal(PassiveHudPresentationAction.Hide, gate.Update(60, true, true, false));
        Assert.False(gate.IsRequested);
        Assert.Equal(PassiveHudPresentationState.CoolingDown, gate.State);
        Assert.Equal(PassiveHudPresentationAction.None, gate.Update(1059, true, true, false));
        Assert.Equal(PassiveHudPresentationAction.Show, gate.Update(1060, true, true, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeaseExpiryPauseOrDisconnectHidesPendingOrPresentedHud(bool presented)
    {
        var gate = new PassiveHudPresentationGate();
        gate.Update(0, true, true, false);
        if (presented) gate.Update(10, true, true, true, true);
        Assert.Equal(PassiveHudPresentationAction.Hide, gate.Update(20, false, true, presented));
        Assert.False(gate.IsRequested);
        Assert.Equal(PassiveHudPresentationState.Idle, gate.State);
        Assert.Equal(PassiveHudPresentationAction.None, gate.Update(30, false, true, presented));
    }

    [Fact]
    public void MenuPreemptionNeverHidesOrReopensTheMenu()
    {
        var gate = new PassiveHudPresentationGate();
        gate.Update(0, true, true, false);
        Assert.Equal(PassiveHudPresentationAction.None, gate.Update(50, true, false, true));
        Assert.False(gate.IsRequested);
        Assert.Equal(PassiveHudPresentationAction.None, gate.Update(10000, true, false, true));
        Assert.Equal(PassiveHudPresentationAction.None, gate.Update(10001, true, true, true));
        Assert.Equal(PassiveHudPresentationAction.Show, gate.Update(10002, true, true, false));
    }

    [Fact]
    public void RetryWaitsUntilOldHostSurfaceHasActuallyHidden()
    {
        var gate = new PassiveHudPresentationGate();
        gate.Update(0, true, true, false);
        gate.Update(4000, true, true, false);
        Assert.Equal(PassiveHudPresentationAction.None, gate.Update(5000, true, true, true));
        Assert.Equal(PassiveHudPresentationAction.Show, gate.Update(5001, true, true, false));
    }
}
