using System;
using System.IO;
using System.Runtime.InteropServices;
using ReactorV.Preloader;
using ReactorV.WebView2Host;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class DesktopCaptureColorTests
{
    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(2d)]
    [InlineData(3d)]
    [InlineData(5d)]
    public void EverySdrChannelRoundTripsThroughFp16AndKnownWhiteLevel(double scale)
    {
        for (var value = 0; value <= 255; value++)
        {
            var srgb = value / 255d;
            var linear = srgb <= .04045 ? srgb / 12.92 : Math.Pow((srgb + .055) / 1.055, 2.4);
            var half = BitConverter.HalfToUInt16Bits((Half)(linear * scale));
            Assert.InRange(Math.Abs(value - DesktopCaptureColor.ToSdrByte(half, scale)), 0, 1);
        }
    }

    [Fact]
    public void KnownThreeTimesWhiteLevelRestoresOrangeInsteadOfClippingItToYellow()
    {
        var half = BitConverter.HalfToUInt16Bits((Half)(Math.Pow((165 / 255d + .055) / 1.055, 2.4) * 3));
        Assert.Equal(255, DesktopCaptureColor.ToSdrByte(half, 1));
        Assert.Equal(165, DesktopCaptureColor.ToSdrByte(half, 3));
    }

    [Theory]
    [InlineData(0x7c00)]
    [InlineData(0xfc00)]
    [InlineData(0x7fff)]
    public void NonFiniteHalfNeverBecomesMatchingColor(int bits) =>
        Assert.Throws<InvalidDataException>(() => DesktopCaptureColor.ToSdrByte((ushort)bits, 1));

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(101d)]
    public void UnknownWhiteLevelIsNotGuessed(double scale) =>
        Assert.Throws<InvalidDataException>(() => DesktopCaptureColor.ToSdrByte(0x3800, scale));

    [Fact]
    public void HalfSubnormalsAndSignedValuesAreHandled()
    {
        Assert.Equal(0, DesktopCaptureColor.ToSdrByte(1, 1));
        Assert.Equal(0, DesktopCaptureColor.ToSdrByte(0x8000, 1));
        Assert.Equal(0, DesktopCaptureColor.ToSdrByte(0xbc00, 1));
        Assert.Equal(255, DesktopCaptureColor.ToSdrByte(0x3c00, 1));
    }

    [Fact]
    public void DisplayConfigInteropLayoutsMatchWindowsHeaders()
    {
        Assert.Equal(8, Marshal.SizeOf<DesktopSdrWhiteLevel.Luid>());
        Assert.Equal(20, Marshal.SizeOf<DesktopSdrWhiteLevel.Source>());
        Assert.Equal(48, Marshal.SizeOf<DesktopSdrWhiteLevel.Target>());
        Assert.Equal(72, Marshal.SizeOf<DesktopSdrWhiteLevel.DisplayPath>());
        Assert.Equal(20, Marshal.SizeOf<DesktopSdrWhiteLevel.Header>());
        Assert.Equal(84, Marshal.SizeOf<DesktopSdrWhiteLevel.SourceName>());
        Assert.Equal(24, Marshal.SizeOf<DesktopSdrWhiteLevel.WhiteLevel>());
    }
}
