using System;

namespace RageWebUI.Core
{
    /// <summary>
    /// Names the one desktop presenter allowed to publish Reactor pixels.
    /// WebView2 remains the bridge/readiness authority even while its HWND is
    /// parked and the native external-GPU compositor owns provider pixels.
    /// </summary>
    public enum BrowserPresentationOwner
    {
        None = 0,
        WebViewBootstrap = 1,
        ExternalGpuProvider = 2,
        ExternalGpuBootstrap = 3,
    }

    public readonly struct BrowserPresentationDecision
    {
        public BrowserPresentationDecision(
            BrowserPresentationOwner owner,
            bool webViewVisible,
            bool externalGpuVisible,
            string reason)
        {
            if (webViewVisible && externalGpuVisible)
                throw new ArgumentException(
                    "WebView2 and the external GPU compositor cannot be visible together.");

            Owner = owner;
            WebViewVisible = webViewVisible;
            ExternalGpuVisible = externalGpuVisible;
            Reason = reason ?? string.Empty;
        }

        public BrowserPresentationOwner Owner { get; }

        public bool WebViewVisible { get; }

        public bool ExternalGpuVisible { get; }

        public bool IsVisible => WebViewVisible || ExternalGpuVisible;

        public string Reason { get; }

        public string OwnerTraceValue
        {
            get
            {
                switch (Owner)
                {
                    case BrowserPresentationOwner.WebViewBootstrap:
                        return "webview-bootstrap";
                    case BrowserPresentationOwner.ExternalGpuProvider:
                        return "external-gpu-provider";
                    case BrowserPresentationOwner.ExternalGpuBootstrap:
                        return "external-gpu-bootstrap";
                    default:
                        return "none";
                }
            }
        }
    }

    /// <summary>
    /// Resolves a single presentation owner without changing browser message
    /// delivery. Interactive bootstrap surfaces normally remain on WebView2;
    /// the opt-in native-only route includes them in the native readiness gate.
    /// A bootstrap surface may use the in-game compositor after its
    /// exact surface generation has produced a fresh acknowledged frame. A
    /// connected provider with no bootstrap surface uses that same compositor.
    /// </summary>
    public static class ExclusiveBrowserPresentationPolicy
    {
        /// <summary>
        /// A replacement that arrives while a retained refresh is still
        /// waiting must be coalesced behind that refresh. Starting it as a
        /// cold refresh would hide the last qualified native texture.
        /// </summary>
        public static bool ShouldQueueRapidReplacement(
            bool replacementPending,
            bool externalGpuActive,
            bool retainedRefreshSupported,
            bool externalGpuPresentationReady,
            bool externalGpuVisible) =>
            replacementPending &&
            externalGpuActive &&
            retainedRefreshSupported &&
            !externalGpuPresentationReady &&
            externalGpuVisible;

        public static BrowserPresentationDecision Resolve(
            bool requestedVisible,
            bool providerConnected,
            string? hostSurfaceMode,
            bool externalGpuActive,
            bool externalGpuPresentationReady,
            bool externalGpuBootstrapRequested = false,
            bool externalGpuBootstrapReady = false,
            bool externalProviderReplacementPending = false,
            bool externalProviderReplacementReady = false,
            BrowserPresentationOwner retainedExternalOwner =
                BrowserPresentationOwner.None,
            bool failClosedInitializerFallback = false,
            bool requireNativePresenter = false)
        {
            if (!requestedVisible)
            {
                return new BrowserPresentationDecision(
                    BrowserPresentationOwner.None,
                    webViewVisible: false,
                    externalGpuVisible: false,
                    reason: "hidden");
            }

            var normalizedSurface = HostSurfaceMode.Normalize(hostSurfaceMode);
            var bootstrapSurfaceActive = !string.Equals(
                normalizedSurface,
                HostSurfaceMode.None,
                StringComparison.Ordinal);
            if (requireNativePresenter && !externalGpuActive)
                return new BrowserPresentationDecision(BrowserPresentationOwner.None, false, false,
                    "required-native-presenter-unavailable");
            if (externalGpuActive && providerConnected &&
                externalProviderReplacementPending)
            {
                if (externalProviderReplacementReady)
                {
                    // A matching exact-ID frame supersedes the passive
                    // initializer directly. The provider commit then retires
                    // the logical bootstrap surface; requiring retirement
                    // first creates a circular handoff and a blank frame.
                    return new BrowserPresentationDecision(
                        BrowserPresentationOwner.ExternalGpuProvider,
                        webViewVisible: false,
                        externalGpuVisible: true,
                        reason: "fresh-provider-replacement");
                }

                if (retainedExternalOwner ==
                        BrowserPresentationOwner.ExternalGpuBootstrap ||
                    retainedExternalOwner ==
                        BrowserPresentationOwner.ExternalGpuProvider)
                {
                    return new BrowserPresentationDecision(
                        retainedExternalOwner,
                        webViewVisible: false,
                        externalGpuVisible: true,
                        reason: "retained-external-frame");
                }
            }
            var nativeInitializerRequested =
                externalGpuActive &&
                externalGpuBootstrapRequested &&
                IsNativeBootstrapSurface(normalizedSurface, requireNativePresenter);
            if (nativeInitializerRequested && !externalGpuBootstrapReady)
            {
                return new BrowserPresentationDecision(
                    BrowserPresentationOwner.None,
                    webViewVisible: false,
                    externalGpuVisible: false,
                    reason: "external-gpu-bootstrap-not-ready");
            }
            if (nativeInitializerRequested)
            {
                return new BrowserPresentationDecision(
                    BrowserPresentationOwner.ExternalGpuBootstrap,
                    webViewVisible: false,
                    externalGpuVisible: true,
                    reason: HostSurfaceMode.IsInitializing(normalizedSurface) ? "fresh-initializer-frame" : "fresh-bootstrap-frame");
            }
            if (failClosedInitializerFallback &&
                HostSurfaceMode.IsInitializing(normalizedSurface))
            {
                // Enhanced loading transitions must never promote a fullscreen
                // topmost WebView HWND after the native producer was requested
                // but became unavailable. Keep the surface hidden and allow
                // the bounded readiness/retry path to recover. Interactive
                // About and provider-menu surfaces retain their established
                // WebView fallback below.
                return new BrowserPresentationDecision(
                    BrowserPresentationOwner.None,
                    webViewVisible: false,
                    externalGpuVisible: false,
                    reason: "initializer-native-presenter-unavailable");
            }
            if (externalGpuActive && providerConnected && !bootstrapSurfaceActive &&
                !externalGpuPresentationReady)
            {
                return new BrowserPresentationDecision(
                    BrowserPresentationOwner.None,
                    webViewVisible: false,
                    externalGpuVisible: false,
                    reason: "external-gpu-not-ready");
            }
            if (externalGpuActive && providerConnected && !bootstrapSurfaceActive)
            {
                return new BrowserPresentationDecision(
                    BrowserPresentationOwner.ExternalGpuProvider,
                    webViewVisible: false,
                    externalGpuVisible: true,
                    reason: "connected-provider-menu");
            }

            if (requireNativePresenter)
                return new BrowserPresentationDecision(BrowserPresentationOwner.None, false, false,
                    "required-native-presenter-not-qualified");

            return new BrowserPresentationDecision(
                BrowserPresentationOwner.WebViewBootstrap,
                webViewVisible: true,
                externalGpuVisible: false,
                reason: bootstrapSurfaceActive
                    ? "bootstrap-surface"
                    : externalGpuActive
                        ? "provider-not-connected"
                        : "external-gpu-unavailable");
        }

        public static bool IsNativeBootstrapSurface(string? mode, bool includeInteractiveBootstrap) =>
            HostSurfaceMode.IsInitializing(mode) || (includeInteractiveBootstrap && HostSurfaceMode.RequiresPaintProof(mode));
    }

    /// <summary>
    /// Generation gate for native bootstrap pixels. Every
    /// browser acknowledgement and transport frame must belong to the same
    /// current host-surface generation; a DOM-ready or stale-size texture is
    /// never sufficient by itself.
    /// </summary>
    public static class ExternalBootstrapPresentationGate
    {
        public static bool IsReady(
            string? hostSurfaceMode,
            int currentGeneration,
            int webViewReadyGeneration,
            int externalAckGeneration,
            int externalRefreshGeneration,
            int externalFreshGeneration,
            bool externalPresentationReady,
            bool exactSurfaceSize,
            bool includeInteractiveBootstrap = false)
        {
            return ExclusiveBrowserPresentationPolicy.IsNativeBootstrapSurface(hostSurfaceMode, includeInteractiveBootstrap) &&
                currentGeneration > 0 &&
                webViewReadyGeneration == currentGeneration &&
                externalAckGeneration == currentGeneration &&
                externalRefreshGeneration == currentGeneration &&
                externalFreshGeneration == currentGeneration &&
                externalPresentationReady &&
                exactSurfaceSize;
        }
    }
}
