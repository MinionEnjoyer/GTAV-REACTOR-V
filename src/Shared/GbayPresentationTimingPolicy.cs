using System;
using System.Collections.Generic;

namespace RageWebUI.Harness
{
    /// <summary>
    /// Keeps the GBAY release timing gate on production lifecycle boundaries.
    /// Desktop pixel capture remains an independent correctness proof and is
    /// intentionally excluded from the performance result.
    /// </summary>
    internal static class GbayPresentationTimingPolicy
    {
        public const double StableHandoffSettleMilliseconds = 400d;

        public static double ElapsedBetween(
            double requestedAtMilliseconds,
            double committedAtMilliseconds)
        {
            if (!IsFiniteNonNegative(requestedAtMilliseconds) ||
                !IsFiniteNonNegative(committedAtMilliseconds) ||
                committedAtMilliseconds < requestedAtMilliseconds)
            {
                return double.PositiveInfinity;
            }

            return committedAtMilliseconds - requestedAtMilliseconds;
        }

        public static double Maximum(IReadOnlyCollection<double> latencies)
        {
            if (latencies == null || latencies.Count == 0)
                return double.PositiveInfinity;

            var maximum = 0d;
            foreach (var latency in latencies)
            {
                if (!IsFiniteNonNegative(latency))
                    return double.PositiveInfinity;
                maximum = Math.Max(maximum, latency);
            }
            return maximum;
        }

        public static bool MeetsBudget(
            IReadOnlyCollection<double> endToEndLatencies,
            double budgetMilliseconds) =>
            IsFiniteNonNegative(budgetMilliseconds) &&
            Maximum(endToEndLatencies) <= budgetMilliseconds;

        /// <summary>
        /// Startup is a one-way visual phase. Once GBAY has painted, showing
        /// the initializer again is a regression rather than a permitted
        /// transition frame.
        /// </summary>
        public static bool IsInitializerFramePermitted(
            bool allowStartupTransition,
            bool gbayPhaseEntered,
            bool isStartupTransition) =>
            allowStartupTransition &&
            !gbayPhaseEntered &&
            isStartupTransition;

        /// <summary>
        /// Requires a continuously qualified GBAY presentation to survive a
        /// short observation window. This catches one-frame black, hidden, or
        /// initializer regressions immediately after the ready signal.
        /// </summary>
        public static bool HasStableHandoffSettled(
            double qualifiedAtMilliseconds,
            double observedAtMilliseconds)
        {
            var elapsed = ElapsedBetween(
                qualifiedAtMilliseconds,
                observedAtMilliseconds);
            return !double.IsInfinity(elapsed) &&
                elapsed >= StableHandoffSettleMilliseconds;
        }

        private static bool IsFiniteNonNegative(double value) =>
            !double.IsNaN(value) &&
            !double.IsInfinity(value) &&
            value >= 0d;
    }
}
