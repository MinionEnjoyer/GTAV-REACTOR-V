namespace RageWebUI.Core
{
    // Only the explicit synthetic-host switch may select this policy.
    // A shadow refresh cannot revoke pixels already committed by the actual
    // WebView presenter. Cold reveals still require the shadow readiness gate.
    public static class BootstrapHarnessPresentationPolicy
    {
        public static bool UseWebView(bool enabled, bool requestedVisible,
            bool providerConnected, string? hostSurfaceMode, bool externalGpuActive,
            bool externalGpuReady, bool currentProviderPixelsCommitted) =>
            enabled && requestedVisible && providerConnected && externalGpuActive &&
            HostSurfaceMode.Normalize(hostSurfaceMode) == HostSurfaceMode.None &&
            (externalGpuReady || currentProviderPixelsCommitted);
    }
}
