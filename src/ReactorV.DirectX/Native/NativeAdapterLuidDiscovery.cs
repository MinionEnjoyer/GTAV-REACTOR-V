using System;
using System.Runtime.InteropServices;
using RageWebUI.DirectX.Browser;

namespace RageWebUI.DirectX.Native
{
    internal enum AdapterLuidQueryResult
    {
        NotPublished,
        Found,
        NativeUnavailable,
    }

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
            return Query(targetProcessId, out adapterLuid) ==
                AdapterLuidQueryResult.Found;
        }

        public static AdapterLuidQueryResult Query(
            uint targetProcessId,
            out GpuAdapterLuid adapterLuid)
        {
            adapterLuid = default;
            if (targetProcessId == 0) return AdapterLuidQueryResult.NotPublished;
            try
            {
                if (RWUI_QueryTargetAdapterLuid(
                        targetProcessId,
                        out var highPart,
                        out var lowPart) == 0)
                {
                    return AdapterLuidQueryResult.NotPublished;
                }
                adapterLuid = new GpuAdapterLuid(highPart, lowPart);
                return AdapterLuidQueryResult.Found;
            }
            catch (DllNotFoundException)
            {
                return AdapterLuidQueryResult.NativeUnavailable;
            }
            catch (EntryPointNotFoundException)
            {
                return AdapterLuidQueryResult.NativeUnavailable;
            }
            catch (BadImageFormatException)
            {
                return AdapterLuidQueryResult.NativeUnavailable;
            }
        }
    }
}
