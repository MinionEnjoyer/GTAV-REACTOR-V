using RageWebUI.Script;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class MenuRevealGateTests
{
    [Fact]
    public void OnlyMatchingPaintAcknowledgementCanRevealPendingPresentation()
    {
        var gate = new MenuRevealGate();
        gate.Begin("presentation-a", 100);

        Assert.False(gate.TryAccept("presentation-stale", 150, out _));
        Assert.Equal("presentation-a", gate.PendingPresentationId);
        Assert.True(gate.TryAccept("presentation-a", 164, out var waitMilliseconds));
        Assert.Equal(64, waitMilliseconds);
        Assert.Null(gate.PendingPresentationId);
        Assert.False(gate.TryAccept("presentation-a", 170, out _));
    }

    [Fact]
    public void ReplacementInvalidatesEarlierPresentation()
    {
        var gate = new MenuRevealGate();
        gate.Begin("presentation-a", 100);
        gate.Begin("presentation-b", 120);

        Assert.False(gate.TryAccept("presentation-a", 130, out _));
        Assert.True(gate.TryAccept("presentation-b", 150, out var waitMilliseconds));
        Assert.Equal(30, waitMilliseconds);
    }

    [Fact]
    public void TimeoutFailsClosedAndCancelsPendingReveal()
    {
        var gate = new MenuRevealGate(250);
        gate.Begin("presentation-a", 100);

        Assert.False(gate.TryExpire(349, out _));
        Assert.True(gate.TryExpire(350, out var expiredId));
        Assert.Equal("presentation-a", expiredId);
        Assert.Null(gate.PendingPresentationId);
        Assert.False(gate.TryAccept("presentation-a", 351, out _));
    }

    [Theory]
    [InlineData(350)]
    [InlineData(351)]
    public void PaintAcknowledgementAtOrAfterDeadlineCannotWinBeforeExpirySweep(
        long acknowledgedAtMilliseconds)
    {
        var gate = new MenuRevealGate(250);
        gate.Begin("presentation-a", 100);

        // Broker dispatch precedes TryExpire in the production tick. The
        // acknowledgement itself must therefore enforce the same deadline.
        Assert.False(gate.TryAccept(
            "presentation-a",
            acknowledgedAtMilliseconds,
            out _));
        Assert.Equal("presentation-a", gate.PendingPresentationId);
        Assert.True(gate.TryExpire(
            acknowledgedAtMilliseconds,
            out var expiredId));
        Assert.Equal("presentation-a", expiredId);
        Assert.Null(gate.PendingPresentationId);
    }

    [Fact]
    public void AuthoritativeHostHideRejectsALatePaintAcknowledgement()
    {
        var gate = new MenuRevealGate();
        gate.Begin("presentation-a", 100);

        gate.Cancel();

        Assert.Null(gate.PendingPresentationId);
        Assert.False(gate.TryAccept("presentation-a", 150, out _));
    }
}
