using System;

namespace RageWebUI.Core
{
    /// <summary>
    /// Deterministic retry policy for the narrow WebView2 shared-environment
    /// race. Other failures remain terminal so Reactor never hides a broken
    /// renderer behind an unbounded retry loop.
    /// </summary>
    public static class WebView2StartupPolicy
    {
        public const int ErrorInvalidState = unchecked((int)0x8007139F);

        private static readonly int[] RetryDelays =
        {
            200,
            400,
            800,
            1200,
            1600,
        };

        public static int MaximumAttempts => RetryDelays.Length + 1;

        public static bool CanRetry(int hresult, int failedAttempts) =>
            hresult == ErrorInvalidState &&
            failedAttempts > 0 &&
            failedAttempts < MaximumAttempts;

        public static int RetryDelayMilliseconds(int failedAttempts)
        {
            if (failedAttempts <= 0 || failedAttempts >= MaximumAttempts)
            {
                throw new ArgumentOutOfRangeException(nameof(failedAttempts));
            }
            return RetryDelays[failedAttempts - 1];
        }
    }
}
