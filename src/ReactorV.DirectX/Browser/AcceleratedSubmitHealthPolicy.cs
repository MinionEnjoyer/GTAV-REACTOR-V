namespace RageWebUI.DirectX.Browser
{
    internal enum AcceleratedSubmitHealth
    {
        Submitted,
        Backpressure,
        HardInvalidation,
        HardFailure
    }

    internal enum AcceleratedSubmitDecision
    {
        Accepted,
        Dropped,
        Disable
    }

    /// <summary>
    /// Separates bounded transport pressure from a persistently broken GPU session.
    /// This class is deliberately state-only so its behavior remains deterministic.
    /// </summary>
    internal sealed class AcceleratedSubmitHealthPolicy
    {
        internal const int DefaultConsecutiveHardFailureLimit = 8;

        private readonly int _consecutiveHardFailureLimit;
        private int _consecutiveHardFailures;

        internal AcceleratedSubmitHealthPolicy(
            int consecutiveHardFailureLimit = DefaultConsecutiveHardFailureLimit)
        {
            if (consecutiveHardFailureLimit < 1)
                throw new System.ArgumentOutOfRangeException(nameof(consecutiveHardFailureLimit));

            _consecutiveHardFailureLimit = consecutiveHardFailureLimit;
        }

        internal AcceleratedSubmitDecision Observe(AcceleratedSubmitHealth health)
        {
            switch (health)
            {
                case AcceleratedSubmitHealth.Submitted:
                    System.Threading.Interlocked.Exchange(ref _consecutiveHardFailures, 0);
                    return AcceleratedSubmitDecision.Accepted;
                case AcceleratedSubmitHealth.Backpressure:
                    // Pool pressure is expected and is not evidence that the
                    // adapter/session is unhealthy. It also breaks a consecutive
                    // hard-failure streak.
                    System.Threading.Interlocked.Exchange(ref _consecutiveHardFailures, 0);
                    return AcceleratedSubmitDecision.Dropped;
                case AcceleratedSubmitHealth.HardInvalidation:
                    return AcceleratedSubmitDecision.Disable;
                default:
                    return System.Threading.Interlocked.Increment(ref _consecutiveHardFailures) >=
                        _consecutiveHardFailureLimit
                        ? AcceleratedSubmitDecision.Disable
                        : AcceleratedSubmitDecision.Dropped;
            }
        }
    }
}
