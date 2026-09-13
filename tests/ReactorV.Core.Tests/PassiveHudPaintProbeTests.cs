using System;
using ReactorV.WebView2Host;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class PassiveHudPaintProbeTests
{
    [Theory]
    [InlineData(1280, 720, 1d)]
    [InlineData(1920, 1080, 1d)]
    [InlineData(2560, 1440, 1.25d)]
    [InlineData(3840, 2160, 1.5d)]
    [InlineData(3840, 2160, 2d)]
    public void SparseTransparentReadoutIsMeasuredWithinContentBounds(int width, int height, double scale)
    {
        var calls = 0;
        uint Read(int x, int y)
        {
            calls++;
            // Three narrow, separated glyph stems with a cross stroke.
            var localX = (x - (width - 300 * scale)) / scale;
            var localY = (y - (height - 260 * scale)) / scale;
            return localX >= 0 && localX < 120 && localY >= 0 && localY < 90 &&
                (localX % 40 < 8 || localY > 42 && localY < 50) ? 0xa0f5f8f6u : 0;
        }
        var evidence = PassiveHudPaintProbe.Analyze(width, height, Read);
        Assert.True(evidence.IsConcrete);
        Assert.InRange(evidence.Width, 16, (int)(200 * scale));
        Assert.InRange(evidence.Height, 8, (int)(180 * scale));
        Assert.InRange(calls, 1, 512 * 288 + 128 * 72);
    }

    [Fact]
    public void LoggedHudCountsRemainRejectedByMenuPolicy()
    {
        Assert.False(OverlayPresentationPolicy.HasConcreteBrowserPixels(9216, 35, 26));
        Assert.False(OverlayPresentationPolicy.HasConcreteBrowserPixels(9216, 44, 36));
        Assert.False(OverlayPresentationPolicy.HasConcreteBrowserPixels(9216, 34, 28));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0xff000000u)]
    [InlineData(0x1fffffffu)]
    public void TransparentBlackAndSubthresholdAlphaCannotQualify(uint color)
    {
        Assert.False(PassiveHudPaintProbe.Analyze(2560, 1440, (_, _) => color).IsConcrete);
    }

    [Fact]
    public void EvenCompletelyOpaqueMarkerSearchRegionCannotQualifyAsContent()
    {
        Assert.False(PassiveHudPaintProbe.Analyze(2560, 1440,
            (x, y) => x >= 2560 - 384 && y >= 1440 - 48 ? 0xffffffff : 0).IsConcrete);
    }

    [Fact]
    public void SingleScanlineCannotQualify()
    {
        Assert.False(PassiveHudPaintProbe.Analyze(2560, 1440,
            (_, y) => y == 1210 ? 0xffffffff : 0).IsConcrete);
    }

    [Theory]
    [InlineData("passive-hud", null, true)]
    [InlineData("passive-hud", "gbay-123", false)]
    [InlineData("initializing", null, false)]
    [InlineData("none", null, false)]
    [InlineData(null, null, false)]
    [InlineData("PASSIVE-HUD", null, false)]
    public void MenuCannotInheritRelaxedHudScope(string? surface, string? presentation, bool expected)
    {
        Assert.Equal(expected, PassiveHudPaintProbe.IsEligible(surface, presentation));
    }
}
