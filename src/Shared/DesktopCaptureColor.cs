using System;
using System.IO;

namespace ReactorV.WebView2Host
{
    internal static class DesktopCaptureColor
    {
        // FP16 desktop pixels are linear scRGB (1 = 80 nits). Reverse the
        // OS's SDR-reference-white boost before comparing an SDR UI marker.
        // This is not a guessed exposure or a looser matching tolerance.
        internal static byte ToSdrByte(ushort half, double sdrWhiteScale)
        {
            if (double.IsNaN(sdrWhiteScale) || double.IsInfinity(sdrWhiteScale) ||
                sdrWhiteScale <= 0 || sdrWhiteScale > 100)
                throw new InvalidDataException("Invalid SDR reference white level.");
            var exponent = (half >> 10) & 31;
            var fraction = half & 1023;
            if (exponent == 31) throw new InvalidDataException("Non-finite scRGB pixel.");
            var linear = exponent == 0
                ? fraction / 16777216d
                : (1d + fraction / 1024d) * Math.Pow(2, exponent - 15);
            if ((half & 0x8000) != 0) linear = -linear;
            linear = Math.Max(0, Math.Min(1, linear / sdrWhiteScale));
            var srgb = linear <= 0.0031308 ? 12.92 * linear
                : 1.055 * Math.Pow(linear, 1d / 2.4d) - 0.055;
            return (byte)Math.Round(Math.Max(0, Math.Min(255, srgb * 255)));
        }
    }
}
