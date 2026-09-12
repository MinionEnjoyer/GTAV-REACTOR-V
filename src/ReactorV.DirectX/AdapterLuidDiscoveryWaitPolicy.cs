namespace RageWebUI.DirectX
{
    internal enum AdapterLuidDiscoveryDecision
    {
        Continue,
        Defer,
        StartBrowser,
        DisableExternalGpuPath,
        Stop,
    }

    internal static class AdapterLuidDiscoveryWaitPolicy
    {
        public static AdapterLuidDiscoveryDecision Evaluate(
            bool adapterDiscovered,
            bool fastDeadlineReached,
            bool sessionStopping,
            bool nativeQueryUnavailable = false)
        {
            if (sessionStopping) return AdapterLuidDiscoveryDecision.Stop;
            if (nativeQueryUnavailable)
                return AdapterLuidDiscoveryDecision.DisableExternalGpuPath;
            if (adapterDiscovered) return AdapterLuidDiscoveryDecision.StartBrowser;
            // Device creation during the game's loading phase can legitimately
            // happen after the initial fast window.  The session/host lifetime
            // remains the bounded cancellation authority; this deadline only
            // changes polling cadence and must never permanently disable the
            // optional GPU presenter.
            return fastDeadlineReached
                ? AdapterLuidDiscoveryDecision.Defer
                : AdapterLuidDiscoveryDecision.Continue;
        }
    }
}
