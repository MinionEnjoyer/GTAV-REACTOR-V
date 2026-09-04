using RageWebUI.DirectX.Browser;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class AcceleratedBootstrapRepaintPolicyTests
{
    [Fact]
    public void Reopen_retries_a_dropped_static_paint_until_the_current_surface_is_acknowledged()
    {
        // Transport already works, but the new menu has not delivered a frame.
        var surfaceReady = false;
        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Equal(AcceleratedBootstrapRepaintDecision.RequestInvalidate,
                AcceleratedBootstrapRepaintPolicy.EvaluateSurface(false, true, surfaceReady, false, false, attempt, 41));
        surfaceReady = true;
        Assert.Equal(AcceleratedBootstrapRepaintDecision.StopReady,
            AcceleratedBootstrapRepaintPolicy.EvaluateSurface(false, true, surfaceReady, false, false, 3, 41));
        Assert.Equal(AcceleratedBootstrapRepaintDecision.StopUnavailable,
            AcceleratedBootstrapRepaintPolicy.EvaluateSurface(false, true, false, true, false, 3, 41));
        Assert.Equal(AcceleratedBootstrapRepaintDecision.StopDeadline,
            AcceleratedBootstrapRepaintPolicy.EvaluateSurface(false, true, false, false, true, 3, 41));
        Assert.Equal(AcceleratedBootstrapRepaintDecision.StopBudgetExhausted,
            AcceleratedBootstrapRepaintPolicy.EvaluateSurface(false, true, false, false, false, 41, 41));
    }
    [Fact]
    public void PendingBootstrapRequestsOnlyWithinDeadlineAndAttemptBudget()
    {
        Assert.Equal(
            AcceleratedBootstrapRepaintDecision.RequestInvalidate,
            AcceleratedBootstrapRepaintPolicy.Evaluate(
                bootstrapPending: true,
                transportReady: false,
                unavailable: false,
                deadlineReached: false,
                attempts: 39,
                maximumAttempts: 40));
        Assert.Equal(
            AcceleratedBootstrapRepaintDecision.StopBudgetExhausted,
            AcceleratedBootstrapRepaintPolicy.Evaluate(
                bootstrapPending: true,
                transportReady: false,
                unavailable: false,
                deadlineReached: false,
                attempts: 40,
                maximumAttempts: 40));
        Assert.Equal(
            AcceleratedBootstrapRepaintDecision.StopDeadline,
            AcceleratedBootstrapRepaintPolicy.Evaluate(
                bootstrapPending: true,
                transportReady: false,
                unavailable: false,
                deadlineReached: true,
                attempts: 1,
                maximumAttempts: 40));
    }

    [Fact]
    public void PumpStopsWhenBootstrapIsNotPendingReadyOrUnavailable()
    {
        Assert.Equal(
            AcceleratedBootstrapRepaintDecision.StopNotPending,
            AcceleratedBootstrapRepaintPolicy.Evaluate(
                bootstrapPending: false,
                transportReady: false,
                unavailable: false,
                deadlineReached: false,
                attempts: 0,
                maximumAttempts: 40));
        Assert.Equal(
            AcceleratedBootstrapRepaintDecision.StopReady,
            AcceleratedBootstrapRepaintPolicy.Evaluate(
                bootstrapPending: true,
                transportReady: true,
                unavailable: false,
                deadlineReached: false,
                attempts: 0,
                maximumAttempts: 40));
        Assert.Equal(
            AcceleratedBootstrapRepaintDecision.StopUnavailable,
            AcceleratedBootstrapRepaintPolicy.Evaluate(
                bootstrapPending: true,
                transportReady: false,
                unavailable: true,
                deadlineReached: false,
                attempts: 0,
                maximumAttempts: 40));
    }
}
