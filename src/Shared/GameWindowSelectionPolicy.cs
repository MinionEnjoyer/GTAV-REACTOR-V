using System;
using System.Collections.Generic;

namespace RageWebUI.Windowing
{
    /// <summary>
    /// Pure selection policy shared by the GTA script and windowed renderer.
    /// Win32 discovery stays at the edges so this policy can be regression
    /// tested without creating desktop windows.
    /// </summary>
    internal static class GameWindowSelectionPolicy
    {
        internal const int MinimumClientWidth = 640;
        internal const int MinimumClientHeight = 360;

        internal static GameWindowCandidate? SelectBest(
            IReadOnlyList<GameWindowCandidate> candidates,
            uint targetProcessId)
        {
            if (targetProcessId == 0)
            {
                return null;
            }

            GameWindowCandidate? best = null;
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (!IsEligible(candidate, targetProcessId))
                {
                    continue;
                }

                if (best == null || Compare(candidate, best) > 0)
                {
                    best = candidate;
                }
            }

            return best;
        }

        internal static bool IsEligible(GameWindowCandidate candidate, uint targetProcessId) =>
            candidate.Handle != 0 &&
            candidate.ProcessId == targetProcessId &&
            candidate.Visible &&
            !candidate.Minimized &&
            !candidate.ToolWindow &&
            !candidate.Excluded &&
            candidate.ClientWidth >= MinimumClientWidth &&
            candidate.ClientHeight >= MinimumClientHeight;

        internal static bool IsSameProcessForeground(
            uint targetProcessId,
            uint foregroundProcessId) =>
            targetProcessId != 0 &&
            foregroundProcessId != 0 &&
            targetProcessId == foregroundProcessId;

        /// <summary>
        /// Treats Reactor's own interactive host as part of GTA's foreground
        /// boundary only for the bounded lifetime in which that host has
        /// explicitly captured bootstrap pointer input. Without that lease,
        /// a same-process utility window must behave like any other foreground
        /// application and dismiss the overlay.
        /// </summary>
        internal static bool IsInteractionForegroundProcess(
            uint targetProcessId,
            uint foregroundProcessId,
            uint interactionProcessId,
            bool interactionCaptureActive) =>
            IsSameProcessForeground(targetProcessId, foregroundProcessId) ||
            (interactionCaptureActive &&
             interactionProcessId != 0 &&
             foregroundProcessId == interactionProcessId);

        /// <summary>
        /// A previously selected render window may bypass full top-level
        /// discovery only while it still satisfies the complete eligibility
        /// contract and remains the foreground root. This keeps the warm menu
        /// path free of cross-thread title/class reads without allowing a
        /// stale GTA window to survive a renderer-window replacement.
        /// </summary>
        internal static bool CanReusePreferred(
            GameWindowCandidate candidate,
            uint targetProcessId) =>
            candidate.Preferred &&
            candidate.Foreground &&
            IsEligible(candidate, targetProcessId);

        private static int Compare(GameWindowCandidate left, GameWindowCandidate right)
        {
            var gtaIdentity = GtaIdentityScore(left).CompareTo(GtaIdentityScore(right));
            if (gtaIdentity != 0)
            {
                return gtaIdentity;
            }

            var area = left.ClientArea.CompareTo(right.ClientArea);
            if (area != 0)
            {
                return area;
            }

            var foreground = left.Foreground.CompareTo(right.Foreground);
            if (foreground != 0)
            {
                return foreground;
            }

            return left.Preferred.CompareTo(right.Preferred);
        }

        private static int GtaIdentityScore(GameWindowCandidate candidate)
        {
            var score = 0;
            if (Contains(candidate.ClassName, "grcWindow"))
            {
                score += 2;
            }
            if (Contains(candidate.Title, "Grand Theft Auto"))
            {
                score += 1;
            }
            return score;
        }

        private static bool Contains(string? value, string expected) =>
            !string.IsNullOrWhiteSpace(value) &&
            value!.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal sealed class GameWindowCandidate
    {
        internal long Handle { get; set; }
        internal uint ProcessId { get; set; }
        internal int ClientWidth { get; set; }
        internal int ClientHeight { get; set; }
        internal bool Visible { get; set; }
        internal bool Minimized { get; set; }
        internal bool ToolWindow { get; set; }
        internal bool Excluded { get; set; }
        internal bool Foreground { get; set; }
        internal bool Preferred { get; set; }
        internal string? ClassName { get; set; }
        internal string? Title { get; set; }

        internal long ClientArea => (long)ClientWidth * ClientHeight;
    }
}
