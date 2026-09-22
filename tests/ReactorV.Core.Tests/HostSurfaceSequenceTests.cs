using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class HostSurfaceSequenceTests
{
    [Fact]
    public void ClosedSurfaceCannotBeResurrectedByDelayedFrameOrConflictingReplay()
    {
        var sequence = new HostSurfaceSequence();
        Assert.True(sequence.TryAccept("initializing", 11));
        Assert.True(sequence.TryAccept("none", 12));
        Assert.False(sequence.TryAccept("initializing", 11));
        Assert.False(sequence.TryAccept("initializing", 12));
        Assert.True(sequence.TryAccept("none", 12));
        Assert.True(sequence.TryAccept("initializing", 13));
    }

    [Fact]
    public void MalformedSurfaceCannotAdvanceWatermarkAndSameSurfaceCanReplayAfterRecovery()
    {
        var sequence = new HostSurfaceSequence();
        Assert.False(sequence.TryAccept("bogus", 100));
        Assert.False(sequence.TryAccept("initializing", 0));
        Assert.True(sequence.TryAccept("passive-hud", 1));
        Assert.True(sequence.TryAccept("passive-hud", 1));
        Assert.True(sequence.TryAccept("none", int.MaxValue));
        Assert.False(sequence.TryAccept("passive-hud", 1));
    }
}
