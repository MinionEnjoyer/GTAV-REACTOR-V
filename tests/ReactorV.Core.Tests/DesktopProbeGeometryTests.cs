using System;
using System.Drawing;
using System.Linq;
using ReactorV.WebView2Host;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class DesktopProbeGeometryTests
{
    [Theory]
    [InlineData(0, 0, 2560, 1440)]
    [InlineData(-2560, -240, 2560, 1440)]
    [InlineData(1920, 0, 3840, 2160)]
    [InlineData(0, 0, 1, 1)]
    public void CropPreservesFullFramePixelCoordinates(int x, int y, int width, int height)
    {
        var target = new Rectangle(x, y, width, height);
        var random = new Random(17);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var points = Enumerable.Range(0, 8).Select(_ =>
            {
                var nx = random.NextDouble();
                var ny = random.NextDouble();
                var point = DesktopProbeGeometry.SamplePoint(target, nx, ny);
                Assert.Equal(x + Math.Clamp((int)Math.Round(nx * width - .5), 0, width - 1), point.X);
                Assert.Equal(y + Math.Clamp((int)Math.Round(ny * height - .5), 0, height - 1), point.Y);
                return point;
            }).ToArray();
            var crop = DesktopProbeGeometry.CaptureBounds(target, points);
            Assert.True(target.Contains(crop));
            foreach (var point in points)
            {
                Assert.True(crop.Contains(point));
                Assert.Equal(point, new Point(crop.X + (point.X - crop.X), crop.Y + (point.Y - crop.Y)));
            }
        }
    }

    [Fact]
    public void IdentityStripCapturesOnlyExactEightCellSpanAt125PercentScale()
    {
        var target = new Rectangle(-2560, 0, 2560, 1440);
        var points = Enumerable.Range(0, 8).Select(i => DesktopProbeGeometry.SamplePoint(
            target, (10 + i * 4 + .5) / 2048, (12 + .5) / 1152)).ToArray();
        var crop = DesktopProbeGeometry.CaptureBounds(target, points);
        Assert.Equal(36, crop.Width);
        Assert.Equal(1, crop.Height);
        Assert.True(crop.Width * crop.Height < target.Width * target.Height / 10000);
    }

    [Fact]
    public void EdgesDuplicatesAndSinglePixelRemainReadable()
    {
        var target = new Rectangle(-10, -20, 10, 20);
        Assert.Equal(new Point(-10, -20), DesktopProbeGeometry.SamplePoint(target, 0, 0));
        Assert.Equal(new Point(-1, -1), DesktopProbeGeometry.SamplePoint(target, 1, 1));
        var point = new Point(-5, -7);
        Assert.Equal(new Rectangle(-5, -7, 1, 1), DesktopProbeGeometry.CaptureBounds(target, new[] { point, point }));
        Assert.Throws<ArgumentException>(() => DesktopProbeGeometry.CaptureBounds(target, Array.Empty<Point>()));
        Assert.Throws<ArgumentException>(() => DesktopProbeGeometry.CaptureBounds(target, new[] { new Point(0, 0) }));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-.01)]
    [InlineData(1.01)]
    public void InvalidCoordinatesFailClosed(double coordinate) => Assert.Throws<ArgumentException>(() =>
        DesktopProbeGeometry.SamplePoint(new Rectangle(0, 0, 2560, 1440), coordinate, .5));

    [Theory]
    [InlineData("reactorv-probe-stage=dispatch ms=0", true)]
    [InlineData("reactorv-probe-stage=gdi-capture ms=120", true)]
    [InlineData("reactorv-probe-stage=gdi-get-dc ms=120", true)]
    [InlineData("reactorv-probe-stage=gdi-create-dc ms=120", true)]
    [InlineData("reactorv-probe-stage=gdi-create-bitmap ms=120", true)]
    [InlineData("reactorv-probe-stage=gdi-select-bitmap ms=120", true)]
    [InlineData("reactorv-probe-stage=gdi-bitblt ms=120", true)]
    [InlineData("reactorv-probe-stage=gdi-read-bitmap ms=120", true)]
    [InlineData("reactorv-probe-stage=gdi-cleanup ms=120", true)]
    [InlineData("reactorv-probe-stage=dxgi-evaluate ms=60000", true)]
    [InlineData("reactorv-probe-stage=result-write ms=60001", false)]
    [InlineData("reactorv-probe-stage=result-write ms=-1", false)]
    [InlineData("reactorv-probe-stage=result-write ms=12 trailing", false)]
    [InlineData("reactorv-probe-stage=secret-data ms=12", false)]
    [InlineData("untrusted stderr", false)]
    [InlineData(null, false)]
    public void ProgressAcceptsOnlyBoundedProtocol(string? line, bool expected) =>
        Assert.Equal(expected, DesktopProbeProgress.TryParse(line, out _, out _));
}
