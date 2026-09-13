using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ReactorV.Preloader
{
    // Read-only display query. Match the DXGI output's GDI device name to an
    // active source, then query its TARGET LUID/id (not the output index).
    internal static class DesktopSdrWhiteLevel
    {
        internal static double ReadScale(string deviceName)
        {
            const uint ActivePaths = 2;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var error = GetDisplayConfigBufferSizes(ActivePaths, out var pathCount, out var modeCount);
                if (error != 0 || pathCount == 0 || pathCount > 128 || modeCount == 0 || modeCount > 512)
                    throw new InvalidDataException("Display configuration sizes unavailable: " + error);
                var paths = new DisplayPath[pathCount];
                var modes = Marshal.AllocHGlobal(checked((int)modeCount * 64));
                try
                {
                    error = QueryDisplayConfig(ActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                    if (error == 122) continue; // topology changed between the two calls
                    if (error != 0) throw new InvalidDataException("Display configuration unavailable: " + error);
                    double? scale = null;
                    for (var i = 0; i < pathCount; i++)
                    {
                        var source = new SourceName { Header = Header.For(1, 84, paths[i].Source.Adapter, paths[i].Source.Id), Name = string.Empty };
                        if (GetSourceName(ref source) != 0 ||
                            !string.Equals(source.Name, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                        var white = new WhiteLevel { Header = Header.For(11, 24, paths[i].Target.Adapter, paths[i].Target.Id) };
                        error = GetWhiteLevel(ref white);
                        if (error != 0 || white.Value == 0 || white.Value > 100000)
                            throw new InvalidDataException("SDR reference white unavailable: " + error);
                        var current = white.Value / 1000d;
                        // Mirrored outputs with different white levels cannot
                        // safely share one normalization assumption.
                        if (scale.HasValue && scale.Value != current)
                            throw new InvalidDataException("Ambiguous mirrored SDR reference white.");
                        scale = current;
                    }
                    if (scale.HasValue) return scale.Value;
                    throw new InvalidDataException("DXGI output was not found in active display paths.");
                }
                finally { Marshal.FreeHGlobal(modes); }
            }
            throw new InvalidDataException("Display topology did not stabilize.");
        }

        [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] internal struct Source { public Luid Adapter; public uint Id, ModeIndex, Status; }
        [StructLayout(LayoutKind.Sequential)] internal struct Target
        {
            public Luid Adapter;
            public uint Id, ModeIndex, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLineOrdering;
            public int Available;
            public uint Status;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct DisplayPath { public Source Source; public Target Target; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] internal struct Header
        {
            public uint Type, Size;
            public Luid Adapter;
            public uint Id;
            internal static Header For(uint type, uint size, Luid adapter, uint id) => new Header { Type = type, Size = size, Adapter = adapter, Id = id };
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct SourceName
        {
            public Header Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct WhiteLevel { public Header Header; public uint Value; }
        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
            [Out] DisplayPath[] paths, ref uint modeCount, IntPtr modes, IntPtr topology);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
        private static extern int GetSourceName(ref SourceName source);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int GetWhiteLevel(ref WhiteLevel white);
    }
}
