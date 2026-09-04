using System;
using System.Globalization;

namespace RageWebUI.DirectX.Browser
{
    internal readonly struct GpuAdapterLuid : IEquatable<GpuAdapterLuid>
    {
        public GpuAdapterLuid(int highPart, uint lowPart)
        {
            HighPart = highPart;
            LowPart = lowPart;
        }

        public int HighPart { get; }
        public uint LowPart { get; }

        // Chromium's use-adapter-luid switch requires the signed high DWORD
        // followed by the unsigned low DWORD. Invariant decimal formatting is
        // intentional: hexadecimal and locale separators are not accepted.
        public string ToCefCommandLineValue() => string.Format(
            CultureInfo.InvariantCulture,
            "{0},{1}",
            HighPart,
            LowPart);

        public bool Equals(GpuAdapterLuid other) =>
            HighPart == other.HighPart && LowPart == other.LowPart;

        public override bool Equals(object? value) =>
            value is GpuAdapterLuid other && Equals(other);

        public override int GetHashCode() =>
            unchecked((HighPart * 397) ^ (int)LowPart);

        public static bool operator ==(
            GpuAdapterLuid left,
            GpuAdapterLuid right) => left.Equals(right);

        public static bool operator !=(
            GpuAdapterLuid left,
            GpuAdapterLuid right) => !left.Equals(right);

        public override string ToString() => ToCefCommandLineValue();
    }
}
