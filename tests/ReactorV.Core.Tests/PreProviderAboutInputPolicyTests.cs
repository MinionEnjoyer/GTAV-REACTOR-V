using ReactorV.BootstrapInput;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class PreProviderAboutInputPolicyTests
{
    [Theory]
    [InlineData(true, true, "about", false, true, true)]
    [InlineData(false, true, "about", false, true, false)]
    [InlineData(true, false, "about", false, true, false)]
    [InlineData(true, true, "initializing", false, true, false)]
    [InlineData(true, true, "about", true, true, false)]
    [InlineData(true, true, "about", false, false, false)]
    public void SamplesOnlyTheVisibleForegroundPreProviderAboutSurface(
        bool contentReady,
        bool visible,
        string surface,
        bool providerConnected,
        bool gameForeground,
        bool expected)
    {
        Assert.Equal(
            expected,
            PreProviderAboutInputPolicy.ShouldSample(
                contentReady,
                visible,
                surface,
                providerConnected,
                gameForeground));
    }

    [Theory]
    [InlineData(true, true, "about", false)]
    [InlineData(false, true, "about", false)]
    [InlineData(true, false, "about", false)]
    [InlineData(true, true, "initializing", false)]
    [InlineData(true, true, "about", true)]
    public void PreProviderAboutNeverCapturesPhysicalWindowHitTests(
        bool contentReady,
        bool visible,
        string surface,
        bool providerConnected)
    {
        Assert.False(PreProviderAboutInputPolicy.ShouldCaptureWindowHitTests(
            contentReady,
            visible,
            surface,
            providerConnected));
    }

    [Fact]
    public void NormalizesOnlyPointsInsideAUsableGameClient()
    {
        Assert.True(PreProviderAboutInputPolicy.TryNormalize(
            100, 200, 1920, 1080, 100, 200, out var left, out var top));
        Assert.Equal(0f, left);
        Assert.Equal(0f, top);

        Assert.True(PreProviderAboutInputPolicy.TryNormalize(
            100, 200, 1920, 1080, 2019, 1279, out var right, out var bottom));
        Assert.Equal(1f, right);
        Assert.Equal(1f, bottom);

        Assert.False(PreProviderAboutInputPolicy.TryNormalize(
            100, 200, 1920, 1080, 99, 200, out _, out _));
        Assert.False(PreProviderAboutInputPolicy.TryNormalize(
            100, 200, 1, 1080, 100, 200, out _, out _));
    }

    [Fact]
    public void PreservesDownReleaseAndSubPollTapEdgesExactlyOnce()
    {
        var down = PreProviderAboutInputPolicy.EvaluateLeftButton(
            eligible: true, down: true, pressedSinceLastPoll: true, previousDown: false);
        Assert.True(down.Pressed);
        Assert.False(down.Released);
        Assert.True(down.NextDown);

        var release = PreProviderAboutInputPolicy.EvaluateLeftButton(
            eligible: true, down: false, pressedSinceLastPoll: false, previousDown: down.NextDown);
        Assert.False(release.Pressed);
        Assert.True(release.Released);
        Assert.False(release.NextDown);

        var shortTap = PreProviderAboutInputPolicy.EvaluateLeftButton(
            eligible: true, down: false, pressedSinceLastPoll: true, previousDown: false);
        Assert.True(shortTap.Pressed);
        Assert.True(shortTap.Released);
        Assert.False(shortTap.NextDown);
    }

    [Fact]
    public void IneligibleBoundaryResetsHeldStateWithoutForwardingAnEdge()
    {
        var reset = PreProviderAboutInputPolicy.EvaluateLeftButton(
            eligible: false, down: true, pressedSinceLastPoll: true, previousDown: true);
        Assert.False(reset.Pressed);
        Assert.False(reset.Released);
        Assert.False(reset.NextDown);
    }
}
