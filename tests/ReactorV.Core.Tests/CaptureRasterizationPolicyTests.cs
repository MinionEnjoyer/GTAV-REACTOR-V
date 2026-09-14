using ReactorV.WebView2Host;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class CaptureRasterizationPolicyTests
{
    [Theory]
    [InlineData(3440, 1440, 1, true)]
    [InlineData(3440, 1440, 1.25, true)]
    [InlineData(3441, 1440, 1.5, true)]
    [InlineData(3441, 1441, 1.75, true)]
    [InlineData(3440, 1440, 2, true)]
    [InlineData(3441, 1440, 1, false)]
    [InlineData(3441, 1440, 1.25, false)]
    [InlineData(3440, 1441, 1.5, false)]
    [InlineData(3442, 1440, 1.5, false)]
    [InlineData(3439, 1440, 1.5, false)]
    [InlineData(3441, 1440, 2, false)]
    [InlineData(3441, 1440, 5.5, false)]
    public void Only_exact_or_scale_explained_dimensions_pass(int width, int height, double scale, bool expected)
        => Assert.Equal(expected, OverlayPresentationPolicy.CaptureSizeMatchesTarget(
            width, height, 3440, 1440, scale, scale));

    [Theory]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(1.5, 1.75)]
    public void Unknown_or_changed_scale_rejects_even_an_exact_size(double before, double after)
        => Assert.False(OverlayPresentationPolicy.CaptureSizeMatchesTarget(
            3440, 1440, 3440, 1440, before, after));

    [Fact]
    public void Odd_raw_bounds_can_round_at_integer_scale_but_even_bounds_cannot()
    {
        Assert.True(OverlayPresentationPolicy.CaptureSizeMatchesTarget(1920, 1080, 1919, 1079, 2, 2));
        Assert.False(OverlayPresentationPolicy.CaptureSizeMatchesTarget(1921, 1081, 1920, 1080, 2, 2));
    }

    [Fact]
    public void Invalid_and_overflow_sized_targets_do_not_gain_a_tolerance()
    {
        Assert.False(OverlayPresentationPolicy.CaptureSizeMatchesTarget(0, 1, 0, 1, 1.5, 1.5));
        Assert.False(OverlayPresentationPolicy.CaptureSizeMatchesTarget(int.MaxValue, 1, 1, 1, 1.5, 1.5));
    }
}
