namespace RageWebUI.DirectX
{
    internal enum AdapterLuidDiscoveryDecision
    {
        Continue,
        StartBrowser,
        DisableExternalGpuPath,
        Stop,
    }

    internal static class AdapterLuidDiscoveryWaitPolicy
    {
        public static AdapterLuidDiscoveryDecision Evaluate(
            bool adapterDiscovered,
            bool deadlineReached,
            bool sessionStopping)
        {
            if (sessionStopping) return AdapterLuidDiscoveryDecision.Stop;
            if (adapterDiscovered) return AdapterLuidDiscoveryDecision.StartBrowser;
            return deadlineReached
                ? AdapterLuidDiscoveryDecision.DisableExternalGpuPath
                : AdapterLuidDiscoveryDecision.Continue;
        }
    }
}
