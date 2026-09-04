using System;

namespace RageWebUI.Core
{
    /// <summary>
    /// Selects exactly one pointer owner when the persistent WebView bootstrap
    /// host and the external GPU renderer coexist.
    /// </summary>
    public static class DualBrowserInputAuthority
    {
        public static bool UseExternalGpuRenderer(
            bool externalGpuRendererActive,
            string? hostSurfaceMode) =>
            externalGpuRendererActive &&
            string.Equals(
                HostSurfaceMode.Normalize(hostSurfaceMode),
                HostSurfaceMode.None,
                StringComparison.Ordinal);
    }
}
