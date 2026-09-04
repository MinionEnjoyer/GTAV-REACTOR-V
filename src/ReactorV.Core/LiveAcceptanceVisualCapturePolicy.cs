using System;
using System.Threading;
using System.Threading.Tasks;

namespace RageWebUI.Core
{
    /// <summary>
    /// Visual identity expected from one live-acceptance artifact. Evidence-only
    /// captures preserve diagnostics but do not claim that a Reactor route was
    /// painted. Route captures must produce two consecutive qualified frames.
    /// </summary>
    public enum LiveAcceptanceVisualExpectation
    {
        EvidenceOnly = 0,
        ReactorAbout = 1,
        Allin1Preloader = 2,
        GbayMenu = 3,
    }

    /// <summary>
    /// Resolution-independent pixel summary produced by the live window
    /// capture adapter. ContentFraction describes route-identifying palette
    /// pixels in the centered UI region, not arbitrary non-black world pixels.
    /// </summary>
    public readonly struct LiveAcceptanceVisualFrameMetrics
    {
        public LiveAcceptanceVisualFrameMetrics(
            double contentFraction,
            double blackFraction,
            double greenFraction,
            double blueFraction,
            double whiteFraction,
            double darkGreenFraction)
        {
            ContentFraction = RequireFraction(contentFraction, nameof(contentFraction));
            BlackFraction = RequireFraction(blackFraction, nameof(blackFraction));
            GreenFraction = RequireFraction(greenFraction, nameof(greenFraction));
            BlueFraction = RequireFraction(blueFraction, nameof(blueFraction));
            WhiteFraction = RequireFraction(whiteFraction, nameof(whiteFraction));
            DarkGreenFraction = RequireFraction(darkGreenFraction, nameof(darkGreenFraction));
        }

        public double ContentFraction { get; }
        public double BlackFraction { get; }
        public double GreenFraction { get; }
        public double BlueFraction { get; }
        public double WhiteFraction { get; }
        public double DarkGreenFraction { get; }

        private static double RequireFraction(double value, string parameter)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(parameter);
            return value;
        }
    }

    /// <summary>
    /// Pure route classifier and consecutive-frame gate shared by the live
    /// runner and deterministic unit tests.
    /// </summary>
    public static class LiveAcceptanceVisualCapturePolicy
    {
        public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Browser self-capture, DOM, and UI Automation can prove that a page
        /// exists, but not that DWM/GTA actually presented its pixels. Only a
        /// separately sampled desktop-composition frame may satisfy the live
        /// visibility gate.
        /// </summary>
        public static bool CanProveDesktopVisibility(
            LiveAcceptanceLifecycleEvidenceSource source) =>
            source == LiveAcceptanceLifecycleEvidenceSource.DesktopPixels;

        public static int RequiredConsecutiveFrames(
            LiveAcceptanceVisualExpectation expectation) =>
            expectation == LiveAcceptanceVisualExpectation.EvidenceOnly ? 1 : 2;

        public static bool RequiresRouteClassification(
            LiveAcceptanceVisualExpectation expectation) =>
            expectation != LiveAcceptanceVisualExpectation.EvidenceOnly;

        public static bool IsQualified(
            LiveAcceptanceVisualExpectation expectation,
            LiveAcceptanceVisualFrameMetrics frame)
        {
            switch (expectation)
            {
                case LiveAcceptanceVisualExpectation.EvidenceOnly:
                    return true;
                case LiveAcceptanceVisualExpectation.ReactorAbout:
                    // The Reactor splash has a bounded footprint, its blue V
                    // identity, readable white copy, and the dark green copy
                    // panel. A black/stale GTA frame cannot satisfy this.
                    return frame.ContentFraction >= 0.006d &&
                        frame.ContentFraction <= 0.35d &&
                        frame.BlueFraction >= 0.004d &&
                        frame.WhiteFraction >= 0.001d &&
                        frame.DarkGreenFraction >= 0.0005d;
                case LiveAcceptanceVisualExpectation.Allin1Preloader:
                    // The centered preloader is deliberately much smaller
                    // than GBAY while retaining ALLIN1's green/white palette.
                    return frame.ContentFraction >= 0.02d &&
                        frame.ContentFraction <= 0.55d &&
                        frame.GreenFraction >= 0.001d &&
                        frame.WhiteFraction >= 0.003d &&
                        frame.DarkGreenFraction >= 0.001d &&
                        frame.BlueFraction < 0.04d;
                case LiveAcceptanceVisualExpectation.GbayMenu:
                    // GBAY occupies most of the overlay client. Requiring its
                    // large light body and both greens prevents the compact
                    // preloader from being accepted after a late capture.
                    return frame.ContentFraction >= 0.35d &&
                        frame.GreenFraction >= 0.003d &&
                        frame.WhiteFraction >= 0.03d &&
                        frame.DarkGreenFraction >= 0.002d &&
                        frame.BlueFraction < 0.04d;
                default:
                    return false;
            }
        }

        public static bool IsStableTransition(
            LiveAcceptanceVisualExpectation expectation,
            double changedFraction)
        {
            if (double.IsNaN(changedFraction) || double.IsInfinity(changedFraction) ||
                changedFraction < 0.0 || changedFraction > 1.0)
                return false;
            switch (expectation)
            {
                case LiveAcceptanceVisualExpectation.EvidenceOnly:
                    return true;
                case LiveAcceptanceVisualExpectation.ReactorAbout:
                    return changedFraction <= 0.08d;
                case LiveAcceptanceVisualExpectation.Allin1Preloader:
                    // Service rows and the bounded console may advance while
                    // the preloader itself remains the stable route.
                    return changedFraction <= 0.12d;
                case LiveAcceptanceVisualExpectation.GbayMenu:
                    return changedFraction <= 0.06d;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Runs an operating-system capture call away from the acceptance thread
    /// and bounds the wait. Graphics drivers do not offer a safe cancellation
    /// primitive for CopyFromScreen/PrintWindow, so a late result is disposed
    /// when its background worker eventually returns.
    /// </summary>
    public static class LiveAcceptanceCaptureDeadline
    {
        public static T Execute<T>(
            Func<T> operation,
            TimeSpan timeout,
            Action<T>? disposeLateResult = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            var task = Task.Factory.StartNew(
                operation,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            try
            {
                if (task.Wait(timeout)) return task.GetAwaiter().GetResult();
            }
            catch (AggregateException error)
            {
                throw error.InnerException ?? error;
            }

            if (disposeLateResult != null)
            {
                task.ContinueWith(
                    completed => disposeLateResult(completed.Result),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously |
                        TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default);
            }
            throw new TimeoutException(
                $"The visual capture operation exceeded its {timeout.TotalMilliseconds:F0} ms deadline.");
        }
    }

    /// <summary>
    /// Stateful consecutive-frame counter. A wrong route or materially
    /// different transition resets the sequence instead of allowing two
    /// unrelated frames to combine into visual proof.
    /// </summary>
    public sealed class LiveAcceptanceVisualStabilityTracker
    {
        private readonly LiveAcceptanceVisualExpectation _expectation;

        public LiveAcceptanceVisualStabilityTracker(
            LiveAcceptanceVisualExpectation expectation)
        {
            _expectation = expectation;
        }

        public int ConsecutiveQualifiedFrames { get; private set; }
        public bool IsSatisfied => ConsecutiveQualifiedFrames >=
            LiveAcceptanceVisualCapturePolicy.RequiredConsecutiveFrames(_expectation);

        public bool Observe(
            LiveAcceptanceVisualFrameMetrics frame,
            double changedFractionFromPrevious)
        {
            var qualified = LiveAcceptanceVisualCapturePolicy.IsQualified(
                _expectation,
                frame);
            var stable = ConsecutiveQualifiedFrames == 0 ||
                LiveAcceptanceVisualCapturePolicy.IsStableTransition(
                    _expectation,
                    changedFractionFromPrevious);
            ConsecutiveQualifiedFrames = qualified && stable
                ? ConsecutiveQualifiedFrames + 1
                : qualified ? 1 : 0;
            return IsSatisfied;
        }
    }
}
