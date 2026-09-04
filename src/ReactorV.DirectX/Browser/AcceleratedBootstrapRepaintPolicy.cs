namespace RageWebUI.DirectX.Browser
{
    internal enum AcceleratedBootstrapRepaintDecision
    {
        RequestInvalidate,
        StopNotPending,
        StopReady,
        StopUnavailable,
        StopDeadline,
        StopBudgetExhausted
    }

    /// <summary>
    /// Pure policy for the accelerated bootstrap repaint pump. Keeping the
    /// stop rules separate makes it impossible for a repeating CLR timer to
    /// outlive the bounded bootstrap attempt by accident.
    /// </summary>
    internal static class AcceleratedBootstrapRepaintPolicy
    {
        internal static AcceleratedBootstrapRepaintDecision EvaluateSurface(
            bool bootstrapPending, bool transportReady, bool surfaceReady,
            bool unavailable, bool deadlineReached, int attempts, int maximumAttempts) =>
            Evaluate(bootstrapPending || !surfaceReady, transportReady && surfaceReady,
                unavailable, deadlineReached, attempts, maximumAttempts);

        internal static AcceleratedBootstrapRepaintDecision Evaluate(
            bool bootstrapPending,
            bool transportReady,
            bool unavailable,
            bool deadlineReached,
            int attempts,
            int maximumAttempts)
        {
            if (maximumAttempts < 1)
                throw new System.ArgumentOutOfRangeException(nameof(maximumAttempts));
            if (attempts < 0)
                throw new System.ArgumentOutOfRangeException(nameof(attempts));
            if (unavailable)
                return AcceleratedBootstrapRepaintDecision.StopUnavailable;
            if (transportReady)
                return AcceleratedBootstrapRepaintDecision.StopReady;
            if (deadlineReached)
                return AcceleratedBootstrapRepaintDecision.StopDeadline;
            if (attempts >= maximumAttempts)
                return AcceleratedBootstrapRepaintDecision.StopBudgetExhausted;
            if (!bootstrapPending)
                return AcceleratedBootstrapRepaintDecision.StopNotPending;
            return AcceleratedBootstrapRepaintDecision.RequestInvalidate;
        }
    }
}
