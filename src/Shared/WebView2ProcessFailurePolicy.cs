namespace ReactorV.WebView2Host
{
    /// <summary>
    /// Keeps browser-process recovery bounded and selects the stable compositor
    /// for a persistent overlay. WebView2 151 can leave a GPU-composited surface
    /// logically visible but absent from the desktop after its host HWND is
    /// hidden and shown again. Reactor's persistent GTA overlay necessarily
    /// follows that lifecycle, so it starts on the software-composited browser
    /// path; short-lived, never-presented cache warmers can retain the GPU path.
    /// One failed browser may still be recreated once, but a second failure
    /// opens the circuit instead of crash-looping.
    /// </summary>
    internal static class WebView2ProcessFailurePolicy
    {
        internal const int MaximumRecoveryAttempts = 1;
        internal const int BrowserExitTimeoutMilliseconds = 2000;
        internal const int RendererReloadTimeoutMilliseconds = 2000;
        internal const string SoftwareCompositionArguments =
            "--disable-gpu --disable-gpu-compositing";

        internal static bool ShouldUseSoftwareComposition(
            bool persistentPresentedOverlay,
            bool recovering) =>
            persistentPresentedOverlay || recovering;

        internal static bool CanRecover(int completedAttempts) =>
            completedAttempts >= 0 &&
            completedAttempts < MaximumRecoveryAttempts;

        internal static bool ShouldAcceptFailure(
            bool recoveryQueuedOrInProgress,
            bool senderIsCurrentGeneration) =>
            !recoveryQueuedOrInProgress && senderIsCurrentGeneration;

        internal static bool IsCurrentControllerGeneration(
            int callbackGeneration,
            int currentGeneration,
            bool callbackCoreIsCurrent,
            bool callbackControlIsCurrent) =>
            callbackGeneration > 0 &&
            callbackGeneration == currentGeneration &&
            callbackCoreIsCurrent &&
            callbackControlIsCurrent;

        internal static bool CanRevealRecoveredSurface(
            bool recoveryInProgress,
            bool hasActiveMenuPresentation,
            bool currentPresentationPaintAcknowledged) =>
            !recoveryInProgress ||
            !hasActiveMenuPresentation ||
            currentPresentationPaintAcknowledged;
    }
}
