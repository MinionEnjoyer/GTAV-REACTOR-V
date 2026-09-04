using System;
using RageWebUI.Core;

namespace ReactorV.BootstrapHost
{
    internal enum HostSurfaceReadyDeadlineAction
    {
        None = 0,
        FailClosedAndRetry = 1,
    }

    internal enum NativeHostToggleAction
    {
        ShowBootstrapSurface = 0,
        ForwardDefaultMenuIntentHidden = 1,
    }

    internal enum BootstrapSurfaceToggleAction
    {
        Show = 0,
        Close = 1,
    }

    internal enum BootstrapHostSignalAction
    {
        None = 0,
        Close = 1,
        PromoteInitializer = 2,
        ToggleAbout = 3,
        ToggleVerification = 4,
        ToggleInitializer = 5,
    }

    /// <summary>
    /// Pure ownership policy for a host surface whose browser pixels are
    /// intentionally hidden until React acknowledges the requested generation.
    /// Pending paint is still a logical open state, so a second toggle closes
    /// it and temporary renderer starvation cannot erase the user's intent.
    /// </summary>
    internal static class HostSurfaceIntentPolicy
    {
        internal const string PresentationHandoff = "presentation";

        // Named events are auto-reset and may coalesce into the same polling
        // epoch. Apply exactly one action so an objective Story promotion can
        // never be preceded by About or immediately closed by a stale toggle.
        internal static BootstrapHostSignalAction EvaluateSignalBatch(
            bool close,
            bool about,
            bool verify,
            bool promoteInitializer,
            bool toggleInitializer)
        {
            if (close) return BootstrapHostSignalAction.Close;
            if (promoteInitializer)
                return BootstrapHostSignalAction.PromoteInitializer;
            if (about) return BootstrapHostSignalAction.ToggleAbout;
            if (verify) return BootstrapHostSignalAction.ToggleVerification;
            if (toggleInitializer)
                return BootstrapHostSignalAction.ToggleInitializer;
            return BootstrapHostSignalAction.None;
        }

        internal static bool ShouldDeferInitializerAfterClose(
            bool close,
            bool initializerPromotion,
            bool runtimeReady) =>
            close && initializerPromotion && !runtimeReady;

        /// <summary>
        /// A logical provider handoff retains the last qualified bootstrap
        /// paint until the matching presentation is accepted. A hard
        /// retirement must remain immediate and carries no retention token.
        /// </summary>
        internal static string? RetirementHandoff(bool hide) =>
            hide ? null : PresentationHandoff;

        internal static NativeHostToggleAction EvaluateNativeToggle(
            bool runtimeReadyLeaseSignaled) =>
            runtimeReadyLeaseSignaled
                ? NativeHostToggleAction.ForwardDefaultMenuIntentHidden
                : NativeHostToggleAction.ShowBootstrapSurface;

        internal static BootstrapSurfaceToggleAction EvaluateBootstrapToggle(
            bool logicallyOpen) =>
            logicallyOpen
                ? BootstrapSurfaceToggleAction.Close
                : BootstrapSurfaceToggleAction.Show;

        internal static bool ShouldCancelDefaultMenuIntent(
            bool visible,
            HostVisibilityReason reason) =>
            !visible && reason != HostVisibilityReason.PresentationPreparation;

        internal static bool IsLogicallyOpen(
            bool actuallyVisible,
            string? currentMode,
            int currentGeneration,
            int pendingGeneration,
            string? pendingMode,
            string requestedMode)
        {
            if (!string.Equals(currentMode, requestedMode, StringComparison.Ordinal))
                return false;

            // The published generation is the authoritative presentation
            // identity.  A no-activate WebView can be compositor-hidden for a
            // frame while GTA changes foreground/monitor state; treating that
            // transient visibility sample as a closed surface turns the next
            // F9 into another open request.  That was the source of the
            // observed About open/open loop.  Once a non-idle mode has a
            // published generation, the matching F9 closes that exact logical
            // surface even if its pixels are temporarily not reported visible.
            return currentGeneration > 0 ||
                actuallyVisible ||
                (pendingGeneration > 0 &&
                 string.Equals(pendingMode, requestedMode, StringComparison.Ordinal));
        }

        /// <summary>
        /// Objective Story detection publishes the initializer before the
        /// user's first Story-mode F9 can reach managed code.  That first edge
        /// is an opening/ownership edge, not a request to close the surface
        /// that is already representing it.
        /// </summary>
        internal static bool ShouldConsumeOpeningInitializerToggle(
            bool openingEdgePending,
            bool initializerLogicallyOpen) =>
            openingEdgePending && initializerLogicallyOpen;

        internal static HostSurfaceReadyDeadlineAction EvaluateReadyDeadline(
            int pendingGeneration,
            bool deadlineArmed,
            bool deadlineExpired)
        {
            return pendingGeneration > 0 && deadlineArmed && deadlineExpired
                ? HostSurfaceReadyDeadlineAction.FailClosedAndRetry
                : HostSurfaceReadyDeadlineAction.None;
        }

        internal static bool ShouldPreserveVisibleSurfaceDuringPromotion(
            bool actuallyVisible,
            string? currentMode,
            string? requestedMode) =>
            actuallyVisible &&
            ((string.Equals(currentMode, HostSurfaceMode.Verifying, StringComparison.Ordinal) &&
              (string.Equals(requestedMode, HostSurfaceMode.About, StringComparison.Ordinal) ||
               string.Equals(requestedMode, HostSurfaceMode.Initializing, StringComparison.Ordinal))) ||
              (string.Equals(currentMode, HostSurfaceMode.About, StringComparison.Ordinal) &&
               string.Equals(requestedMode, HostSurfaceMode.Initializing, StringComparison.Ordinal)));

        // A preserved About/verifying frame may bridge the React state update,
        // but the generation-bound initializer capture needs an exclusively
        // owned hidden HWND lease. Park that prior frame before qualification.
        internal static bool ShouldParkForBootstrapPixelProof(
            string? requestedMode) =>
            string.Equals(
                requestedMode,
                HostSurfaceMode.Initializing,
                StringComparison.Ordinal);

        /// <summary>
        /// Objective Story-mode evidence is itself the authority for entering
        /// the initializer lifecycle. About is only a main-menu presentation;
        /// losing its window focus or closing that surface must not suppress
        /// the Story preloader transition.
        /// </summary>
        internal static bool ShouldPromoteToInitializer(
            bool objectiveStoryEvidence) => objectiveStoryEvidence;

        internal static bool IsVerificationActiveSurface(string? mode) =>
            string.Equals(
                HostSurfaceMode.Normalize(mode),
                HostSurfaceMode.Verifying,
                StringComparison.Ordinal);
    }
}
