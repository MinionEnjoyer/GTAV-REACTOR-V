using System.Runtime.InteropServices;

namespace RageWebUI.DirectX.Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SharedTextureCapabilities
    {
        public const uint ExpectedByteSize = 24;
        public const ushort SupportedMajorVersion = 1;

        public const uint Bgra8UnormFormat = 1u << 0;
        public const uint Bgra8UnormSrgbFormat = 1u << 1;

        public const uint SynchronousTransientCopy = 1u << 0;
        public const uint CrossProcessPersistentPool = 1u << 1;
        public const uint D3d11KeyedMutex = 1u << 2;
        public const uint D3d12SharedFence = 1u << 3;
        public const uint BootstrapProbe = 1u << 4;

        public uint ByteSize;
        public ushort Major;
        public ushort Minor;
        public uint MaxWidth;
        public uint MaxHeight;
        public uint SupportedFormatMask;
        public uint Flags;

        public bool SupportsBgra8Abi =>
            ByteSize >= ExpectedByteSize &&
            Major == SupportedMajorVersion &&
            (SupportedFormatMask & Bgra8UnormFormat) != 0;

        public bool SupportsSynchronousBgra8 =>
            SupportsBgra8Abi &&
            (Flags & SynchronousTransientCopy) != 0;

        public bool SupportsBootstrapProbe =>
            SupportsBgra8Abi &&
            (Flags & BootstrapProbe) != 0;

        public bool SupportsDimensions(int width, int height) =>
            width > 0 &&
            height > 0 &&
            (MaxWidth == 0 || (uint)width <= MaxWidth) &&
            (MaxHeight == 0 || (uint)height <= MaxHeight);

        public static SharedTextureCapabilities CreateRequest() => new SharedTextureCapabilities
        {
            ByteSize = checked((uint)Marshal.SizeOf<SharedTextureCapabilities>()),
        };
    }
}
