using System;
using System.Runtime.InteropServices;
using RageWebUI.DirectX.Browser;

namespace RageWebUI.DirectX.Native
{
    internal static class NativeAdapterLuidDiscovery
    {
        private const string LibraryName = "RageWebUI.Native.dll";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_QueryTargetAdapterLuid(
            uint targetProcessId,
            out int highPart,
            out uint lowPart);

        public static bool TryQuery(
            uint targetProcessId,
            out GpuAdapterLuid adapterLuid)
        {
            adapterLuid = default;
            if (targetProcessId == 0) return false;
            try
            {
                if (RWUI_QueryTargetAdapterLuid(
                        targetProcessId,
                        out var highPart,
                        out var lowPart) == 0)
                {
                    return false;
                }
                adapterLuid = new GpuAdapterLuid(highPart, lowPart);
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }
    }
}
