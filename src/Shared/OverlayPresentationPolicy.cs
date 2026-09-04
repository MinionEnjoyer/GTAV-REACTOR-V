using System;
using System.Globalization;
using System.Text;

namespace ReactorV.WebView2Host
{
    internal enum CompositionDeviceState
    {
        Unavailable,
        Ready,
        Lost,
        QueryFailed,
    }

    internal enum RevealCompositionRefresh
    {
        None,
        Synchronize,
        RebindRootVisual,
    }

    internal static class OverlayPresentationPolicy
    {
        // CompositionController preserves the browser's alpha channel. The
        // windowed renderer must never fall back to an opaque chroma key: a
        // WebView2 child HWND is not keyed by its parent WinForms surface.
        internal const int CompositionBackgroundArgb = 0x00000000;

        // GetPixel/GetDC desktop verification can synchronize with GTA's
        // presentation path and freeze the WebView STA. Browser
        // CapturePreviewAsync is the only live paint diagnostic.
        internal const bool UseLiveDesktopPixelSampling = false;
        internal const bool UseLiveBrowserCaptureDiagnostics = false;

        internal static bool ShouldPresent(
            bool requestedVisible,
            bool browserReady,
            bool gameMinimized,
            bool gameForeground,
            bool hasClientBounds)
        {
            return requestedVisible &&
                browserReady &&
                !gameMinimized &&
                gameForeground &&
                hasClientBounds;
        }

        internal static bool ShouldCommitReveal(
            bool requestedVisible,
            bool browserReady,
            bool revealPending)
        {
            return requestedVisible &&
                browserReady &&
                revealPending;
        }

        // PreserveSig COM calls report failure through a negative HRESULT.
        // Reactor must never expose a window whose DirectComposition commit
        // has not completed successfully.
        internal static bool DidCompositionCommitComplete(int hresult)
        {
            return hresult >= 0;
        }

        // The composition controller is created against an off-screen host.
        // Its first ordinary reveal needs one explicit root publication. A
        // cold Story initializer is different: its fresh root must be deferred
        // until the final proof lease has made the parent WS_VISIBLE off-screen.
        // Hidden preparation only synchronizes that path; replacing the root
        // there can leave CapturePreview readable while DWM presents nothing.
        // Warm reveals keep the proven root and synchronize as before.
        internal static RevealCompositionRefresh SelectRevealCompositionRefresh(
            bool browserReady,
            bool surfacePrepared,
            bool actualVisible,
            bool revealPending,
            bool surfaceWasPreviouslyPresented,
            bool deferFreshRootUntilVisibleLease = false)
        {
            if (!browserReady || !surfacePrepared || actualVisible || !revealPending)
                return RevealCompositionRefresh.None;
            if (deferFreshRootUntilVisibleLease)
                return RevealCompositionRefresh.Synchronize;
            return surfaceWasPreviouslyPresented
                ? RevealCompositionRefresh.Synchronize
                : RevealCompositionRefresh.RebindRootVisual;
        }

        internal static bool ShouldDismissForForegroundLoss(
            bool requestedVisible,
            bool visibleOrPending,
            bool gameForeground)
        {
            return requestedVisible && visibleOrPending && !gameForeground;
        }

        // GTA Enhanced can promote its renderer without changing the selected
        // top-level HWND or client bounds. A cached WS_EX_TOPMOST result is not
        // proof that Reactor still precedes GTA in the desktop z-order. The
        // visible bounds poll may repair only a positively observed overtake;
        // unknown ordering remains fail-closed and never creates a recurring
        // SetWindowPos loop.
        internal static bool ShouldReassertOverlayZOrder(
            bool requestedVisible,
            bool visibleOrPending,
            bool gameForeground,
            bool comparisonKnown,
            bool overlayAboveGame)
        {
            return requestedVisible &&
                visibleOrPending &&
                gameForeground &&
                comparisonKnown &&
                !overlayAboveGame;
        }

        // A cross-process owned window participates in the owner's window
        // lifetime. Reactor only needs that relationship while it is preparing
        // or presenting a surface. Keeping the hidden WebView host owned by GTA
        // during normal gameplay needlessly couples it to GTA's shutdown path.
        internal static bool ShouldAttachToGameWindow(
            bool actualVisible,
            bool revealPending)
        {
            // A logical request can wait on browser readiness, foreground, or
            // usable bounds for an arbitrary amount of time. It is not a real
            // HWND presentation boundary and must not couple the otherwise
            // hidden preload host to GTA's process/window lifetime.
            return actualVisible || revealPending;
        }

        // A layout acknowledgement alone can be emitted before Chromium has
        // produced a compositor frame. Require several opaque, non-dark sample
        // pixels before treating a captured browser surface as painted. The
        // threshold remains low enough for compact extension menus while a
        // fully transparent/black stale frame cannot pass.
        internal static bool HasConcreteBrowserPixels(
            int sampleCount,
            int opaqueSampleCount,
            int visibleColorSampleCount)
        {
            if (sampleCount <= 0 || opaqueSampleCount < 0 ||
                visibleColorSampleCount < 0 ||
                opaqueSampleCount > sampleCount ||
                visibleColorSampleCount > opaqueSampleCount)
            {
                return false;
            }

            var minimumOpaque = Math.Max(3, (sampleCount + 99) / 100);
            var minimumVisible = Math.Max(2, (sampleCount + 149) / 150);
            return opaqueSampleCount >= minimumOpaque &&
                visibleColorSampleCount >= minimumVisible;
        }

        internal static ulong HostPaintIdentity(string? mode, int surfaceGeneration)
        {
            if (string.IsNullOrWhiteSpace(mode) || surfaceGeneration < 0)
                return 0;
            return PaintIdentity(
                "reactor-v-paint/v1\0host\0" + mode + "\0" +
                surfaceGeneration.ToString(CultureInfo.InvariantCulture));
        }

        internal static ulong MenuPaintIdentity(
            int providerSessionGeneration,
            string? presentationId)
        {
            if (providerSessionGeneration < 0 ||
                string.IsNullOrWhiteSpace(presentationId))
            {
                return 0;
            }
            return PaintIdentity(
                "reactor-v-paint/v1\0menu\0" +
                providerSessionGeneration.ToString(CultureInfo.InvariantCulture) +
                "\0" + presentationId);
        }

        private static ulong PaintIdentity(string canonicalIdentity)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (var value in Encoding.UTF8.GetBytes(canonicalIdentity))
                {
                    hash ^= value;
                    hash *= 1099511628211UL;
                }
                return hash == 0 ? 1UL : hash;
            }
        }

        internal static bool HasExactBootstrapPixelProof(
            string? currentMode,
            int currentSurfaceGeneration,
            int currentControllerGeneration,
            int currentWidth,
            int currentHeight,
            string? proofMode,
            int proofSurfaceGeneration,
            int proofControllerGeneration,
            int proofWidth,
            int proofHeight,
            bool concrete,
            bool generationMarkerMatched)
        {
            return concrete && generationMarkerMatched &&
                string.Equals(currentMode, "initializing", StringComparison.Ordinal) &&
                string.Equals(proofMode, currentMode, StringComparison.Ordinal) &&
                currentSurfaceGeneration > 0 &&
                proofSurfaceGeneration == currentSurfaceGeneration &&
                currentControllerGeneration > 0 &&
                proofControllerGeneration == currentControllerGeneration &&
                currentWidth > 0 && currentHeight > 0 &&
                proofWidth == currentWidth && proofHeight == currentHeight;
        }

        /// <summary>
        /// An asynchronous bootstrap probe owns the temporary off-screen HWND
        /// lease only while every identity component and the hidden-state
        /// boundary still match. A provider handoff or newer surface is then
        /// free to reveal without an older continuation hiding its window.
        /// </summary>
        internal static bool OwnsBootstrapPixelProbeLease(
            int leaseProbeGeneration,
            int currentProbeGeneration,
            string? leaseMode,
            string? currentMode,
            int leaseSurfaceGeneration,
            int currentSurfaceGeneration,
            int leaseControllerGeneration,
            int currentControllerGeneration,
            bool desiredVisible,
            bool actualVisible,
            bool revealPending,
            bool hasMenuPresentation)
        {
            return leaseProbeGeneration > 0 &&
                leaseProbeGeneration == currentProbeGeneration &&
                string.Equals(leaseMode, currentMode, StringComparison.Ordinal) &&
                leaseSurfaceGeneration > 0 &&
                leaseSurfaceGeneration == currentSurfaceGeneration &&
                leaseControllerGeneration > 0 &&
                leaseControllerGeneration == currentControllerGeneration &&
                !desiredVisible && !actualVisible && !revealPending &&
                !hasMenuPresentation;
        }

        internal static bool ShouldRetryBootstrapPixelProbe(
            int completedAttempt,
            int maximumAttempts,
            bool leaseCurrent,
            bool concrete)
        {
            return completedAttempt > 0 &&
                maximumAttempts > completedAttempt &&
                leaseCurrent &&
                !concrete;
        }

        /// <summary>
        /// Encodes one byte of the exact 64-bit paint identity as two bright
        /// colour channels. Eight adjacent marker cells preserve the entire
        /// host/menu identity without accepting a generic or stale page.
        /// </summary>
        internal static void GetPaintIdentityMarkerColor(
            ulong paintIdentity,
            int byteIndex,
            out int red,
            out int green,
            out int blue)
        {
            if (paintIdentity == 0)
                throw new ArgumentOutOfRangeException(nameof(paintIdentity));
            if (byteIndex < 0 || byteIndex > 7)
                throw new ArgumentOutOfRangeException(nameof(byteIndex));

            var value = (byte)(paintIdentity >> (byteIndex * 8));
            red = 64 + ((value >> 4) * 12);
            green = 64 + ((value & 0x0F) * 12);
            blue = 208;
        }

        internal static bool PaintIdentityMarkerColorMatches(
            ulong paintIdentity,
            int byteIndex,
            int red,
            int green,
            int blue,
            int alpha,
            int tolerance = 10)
        {
            if (paintIdentity == 0 || byteIndex < 0 || byteIndex > 7 ||
                tolerance < 0 || alpha < 240)
            {
                return false;
            }

            GetPaintIdentityMarkerColor(
                paintIdentity,
                byteIndex,
                out var expectedRed,
                out var expectedGreen,
                out var expectedBlue);
            return Math.Abs(red - expectedRed) <= tolerance &&
                Math.Abs(green - expectedGreen) <= tolerance &&
                Math.Abs(blue - expectedBlue) <= tolerance;
        }

        internal const int PaintIdentityMarkerMinimumStride = 8;
        internal const int PaintIdentityMarkerMaximumStride = 48;

        /// <summary>
        /// Finds the complete eight-cell identity marker in a packed ARGB
        /// raster. CapturePreview reports physical pixels, so the search must
        /// cover the full marker width at high Windows DPI scaling rather than
        /// assuming the 92 CSS-pixel layout.
        /// </summary>
        internal static bool HasPaintIdentityMarker(
            int width,
            int height,
            ulong paintIdentity,
            Func<int, int, uint> readArgb)
        {
            return TryFindPaintIdentityMarker(
                width,
                height,
                paintIdentity,
                readArgb,
                out _,
                out _,
                out _);
        }

        internal static bool TryFindPaintIdentityMarker(
            int width,
            int height,
            ulong paintIdentity,
            Func<int, int, uint> readArgb,
            out int markerX,
            out int markerY,
            out int markerStride)
        {
            markerX = 0;
            markerY = 0;
            markerStride = 0;
            if (width < 57 || height < 1 || paintIdentity == 0 ||
                readArgb == null)
            {
                return false;
            }

            var scanWidth = PaintIdentityMarkerMaximumStride * 8;
            var scanLeft = Math.Max(0, width - scanWidth);
            var scanTop = Math.Max(0, height - PaintIdentityMarkerMaximumStride);
            for (var y = scanTop; y < height; y++)
            {
                for (var stride = PaintIdentityMarkerMinimumStride;
                    stride <= PaintIdentityMarkerMaximumStride;
                    stride++)
                {
                    for (var x = scanLeft; x + 7 * stride < width; x++)
                    {
                        var matched = true;
                        for (var byteIndex = 0; byteIndex < 8; byteIndex++)
                        {
                            var argb = readArgb(x + byteIndex * stride, y);
                            var alpha = (int)((argb >> 24) & 0xFF);
                            var red = (int)((argb >> 16) & 0xFF);
                            var green = (int)((argb >> 8) & 0xFF);
                            var blue = (int)(argb & 0xFF);
                            if (!PaintIdentityMarkerColorMatches(
                                    paintIdentity,
                                    byteIndex,
                                    red,
                                    green,
                                    blue,
                                    alpha))
                            {
                                matched = false;
                                break;
                            }
                        }
                        if (matched)
                        {
                            markerX = x;
                            markerY = y;
                            markerStride = stride;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // DWM may apply small colour conversions even to an opaque browser
        // pixel. A majority of deterministic browser/screen samples within a
        // bounded channel tolerance is sufficient evidence that the intended
        // window, rather than the game alone, reached the desktop compositor.
        internal static bool HasConcreteDesktopPixels(
            int readableSampleCount,
            int matchingSampleCount)
        {
            if (readableSampleCount <= 0 || matchingSampleCount < 0 ||
                matchingSampleCount > readableSampleCount)
            {
                return false;
            }

            return matchingSampleCount >= Math.Max(2, (readableSampleCount + 1) / 2);
        }

        internal static CompositionDeviceState ClassifyCompositionDeviceState(
            bool available,
            int queryHResult,
            bool valid)
        {
            if (!available)
                return CompositionDeviceState.Unavailable;
            if (queryHResult < 0)
                return CompositionDeviceState.QueryFailed;
            return valid
                ? CompositionDeviceState.Ready
                : CompositionDeviceState.Lost;
        }

        /// <summary>
        /// A failed desktop presentation may detach/rebind its root visual only
        /// once for a given host-surface generation. Recording the generation
        /// before attempting the operation makes failure fail closed rather
        /// than turning every bounds tick into a compositor recovery loop.
        /// </summary>
        internal static bool ShouldAttemptRootVisualRebind(
            int failedSurfaceGeneration,
            int? attemptedSurfaceGeneration,
            bool hostVisible,
            bool desktopPresentationConcrete)
        {
            return failedSurfaceGeneration >= 0 &&
                attemptedSurfaceGeneration != failedSurfaceGeneration &&
                hostVisible &&
                !desktopPresentationConcrete;
        }

        internal static string DescribePresentationEvidence(
            bool domReady,
            bool browserSurfaceCaptured,
            bool browserPixelsVerified,
            bool desktopPresentationVerified)
        {
            if (desktopPresentationVerified)
                return "desktop_presentation_verified";
            if (browserPixelsVerified)
                return "browser_surface_pixels_verified_not_desktop";
            if (browserSurfaceCaptured)
                return "browser_surface_captured_not_desktop";
            if (domReady)
                return "dom_ready_no_pixel_evidence";
            return "none";
        }
    }
}
