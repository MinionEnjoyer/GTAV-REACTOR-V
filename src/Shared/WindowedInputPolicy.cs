using System;
using Newtonsoft.Json.Linq;
using RageWebUI.Core.Protocol;

namespace ReactorV.WebView2Host
{
    internal static class WindowedInputPolicy
    {
        internal const float PositionEpsilon = 1f / 8192f;
        internal const string ProviderPointerEventName = "input.pointer";
        internal const string ProviderPointerResetEventName = "input.pointerReset";
        internal const string BootstrapPointerEventName = "input.bootstrapPointer";
        internal const string BootstrapPointerResetEventName =
            "input.bootstrapPointerReset";

        internal static float Normalize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return 0f;
            }

            return Math.Max(0f, Math.Min(1f, value));
        }

        /// <summary>
        /// Builds the one authoritative DOM pointer event used by both the
        /// WebView2 and external CEF presentation paths. Keeping the bounds and
        /// payload contract here prevents the GPU provider from falling back
        /// to CEF's native OSR mouse route, whose cursor is not rendered into
        /// the shared texture.
        /// </summary>
        internal static string SerializeProviderPointerEvent(
            float normalizedX,
            float normalizedY,
            bool pressed,
            bool released,
            int wheelDelta) =>
            BridgeProtocol.SerializeEvent(
                ProviderPointerEventName,
                new JObject
                {
                    ["x"] = Normalize(normalizedX),
                    ["y"] = Normalize(normalizedY),
                    ["pressed"] = pressed,
                    ["released"] = released,
                    ["wheelDelta"] = Math.Max(-1200, Math.Min(1200, wheelDelta)),
                });

        internal static bool ShouldForward(
            float previousX,
            float previousY,
            bool hasPrevious,
            float nextX,
            float nextY,
            bool pressed,
            bool released,
            int wheelDelta)
        {
            if (!hasPrevious || pressed || released || wheelDelta != 0)
            {
                return true;
            }

            return Math.Abs(Normalize(nextX) - Normalize(previousX)) >= PositionEpsilon ||
                Math.Abs(Normalize(nextY) - Normalize(previousY)) >= PositionEpsilon;
        }

        /// <summary>
        /// A provider-owned menu receives browser input through the typed DOM
        /// cursor bridge and requires pointer isolation. This flag describes
        /// the gameplay-input isolation lifetime; it never grants the external
        /// HWND permission to activate, focus, or participate in hit testing.
        /// </summary>
        internal static bool ShouldForwardProviderPointer(
            bool requestedVisible,
            bool actualVisible,
            bool revealPending,
            string? activePresentationId,
            string? acceptedPresentationId,
            string? committedPresentationId)
        {
            return requestedVisible &&
                actualVisible &&
                !revealPending &&
                !string.IsNullOrWhiteSpace(activePresentationId) &&
                string.Equals(
                    acceptedPresentationId,
                    activePresentationId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    committedPresentationId,
                    activePresentationId,
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// Neither bootstrap nor provider input may make the external host a
        /// physical Windows hit-test target. Both lanes are sampled while GTA
        /// owns foreground and delivered as typed DOM events.
        /// </summary>
        internal static bool ShouldCaptureHostHitTest(
            bool bootstrapCaptureRequested,
            bool providerPointerIsolationActive)
        {
            _ = bootstrapCaptureRequested;
            _ = providerPointerIsolationActive;
            return false;
        }

        /// <summary>
        /// The external HWND is permanently non-activating and hit-test
        /// transparent, so it is never a legitimate interaction foreground.
        /// A foreground transition to Reactor must be treated as a boundary,
        /// not repaired after the fact.
        /// </summary>
        internal static bool AllowsInteractionForeground(
            bool bootstrapCaptureRequested,
            bool providerPointerIsolationActive)
        {
            _ = bootstrapCaptureRequested;
            _ = providerPointerIsolationActive;
            return false;
        }

        /// <summary>
        /// The managed pointer sampler runs only while GTA owns foreground.
        /// The external host no longer has a trusted-focus exception because
        /// typed DOM delivery does not require a WebView2 focus transition.
        /// </summary>
        internal static bool AllowsManagedPointerSampling(
            bool gameForeground,
            bool interactiveLease,
            bool requestedVisible,
            bool actualVisible,
            bool trustedProviderForeground)
        {
            _ = interactiveLease;
            _ = requestedVisible;
            _ = actualVisible;
            _ = trustedProviderForeground;
            return gameForeground;
        }

        internal static bool IsTrustedProviderForeground(
            uint authenticatedHostProcessId,
            uint foregroundProcessId) =>
            authenticatedHostProcessId != 0 &&
            foregroundProcessId == authenticatedHostProcessId;

        /// <summary>
        /// Selects WebView2's input parent. The composition visual is rooted in
        /// Reactor's HWND, so ParentWindow must remain that same-process HWND
        /// for the controller's entire lifetime. GTA ownership and z-order are
        /// managed on the outer overlay window; browser pointer events continue
        /// through typed bridge events and never require cross-process
        /// re-parenting.
        /// </summary>
        internal static IntPtr ResolveInputParent(
            bool actualVisible,
            bool revealPending,
            IntPtr gameWindow,
            IntPtr overlayWindow)
        {
            if (overlayWindow == IntPtr.Zero)
                throw new ArgumentException("Overlay window is required.", nameof(overlayWindow));

            _ = actualVisible;
            _ = revealPending;
            _ = gameWindow;
            return overlayWindow;
        }
    }
}
