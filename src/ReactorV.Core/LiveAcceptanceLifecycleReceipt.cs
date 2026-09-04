using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace RageWebUI.Core
{
    /// <summary>
    /// One authoritative surface lifecycle used by live acceptance receipts.
    /// The generation identifies the native/WebView surface while the optional
    /// presentation id identifies the typed menu rendered on that surface.
    /// </summary>
    public enum LiveAcceptanceSurfaceLifecycleState
    {
        None = 0,
        FrontendAbout = 1,
        StoryInitializing = 2,
        ProviderReady = 3,
        MenuPendingPaint = 4,
        MenuInteractive = 5,
        Closing = 6,
        Closed = 7,
    }

    public enum LiveAcceptanceLifecycleEvidenceSource
    {
        RuntimeTrace = 0,
        Dom = 1,
        UiAutomation = 2,
        BrowserCapture = 3,
        DesktopPixels = 4,
        ForegroundObservation = 5,
        InputEdge = 6,
    }

    public enum LiveAcceptanceStartupMilestone
    {
        HarnessArmed = 0,
        GtaProcessObserved = 1,
        GtaWindowObserved = 2,
        FrontendAboutObserved = 3,
        StoryInitializingObserved = 4,
        ProviderReadyObserved = 5,
        MenuPendingPaintObserved = 6,
        MenuInteractiveObserved = 7,
        HarnessCompleted = 8,
    }

    public enum LiveAcceptanceShutdownMilestone
    {
        MenuClosingObserved = 0,
        MenuClosedObserved = 1,
        QuitRequested = 2,
        ScriptAbortObserved = 3,
        ScriptHookUninitialized = 4,
        GtaWindowDestroyed = 5,
        GtaProcessExited = 6,
        WebViewProcessExited = 7,
    }

    public sealed class LiveAcceptanceLifecycleIdentity
    {
        private static readonly Regex PresentationPattern = new Regex(
            @"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public LiveAcceptanceLifecycleIdentity(
            int generation,
            string? presentationId)
        {
            if (generation <= 0)
                throw new ArgumentOutOfRangeException(nameof(generation));
            if (!string.IsNullOrWhiteSpace(presentationId) &&
                !PresentationPattern.IsMatch(presentationId!))
                throw new ArgumentException(
                    "The presentation id is not a bounded receipt identity.",
                    nameof(presentationId));
            Generation = generation;
            PresentationId = presentationId ?? string.Empty;
        }

        public int Generation { get; }
        public string PresentationId { get; }
        public bool HasPresentation => !string.IsNullOrWhiteSpace(PresentationId);
        public string Key => Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ":" + (HasPresentation ? PresentationId : "none");
    }

    public sealed class LiveAcceptanceLifecycleObservation
    {
        internal LiveAcceptanceLifecycleObservation(
            int sequence,
            LiveAcceptanceSurfaceLifecycleState state,
            LiveAcceptanceLifecycleIdentity identity,
            DateTimeOffset observedUtc,
            LiveAcceptanceLifecycleEvidenceSource source,
            string evidence)
        {
            Sequence = sequence;
            State = state;
            Generation = identity.Generation;
            PresentationId = identity.PresentationId;
            LifecycleKey = identity.Key;
            ObservedUtc = observedUtc;
            Source = source;
            Evidence = evidence;
        }

        public int Sequence { get; }
        public LiveAcceptanceSurfaceLifecycleState State { get; }
        public int Generation { get; }
        public string PresentationId { get; }
        public string LifecycleKey { get; }
        public DateTimeOffset ObservedUtc { get; }
        public LiveAcceptanceLifecycleEvidenceSource Source { get; }
        public string Evidence { get; }
    }

    public sealed class LiveAcceptanceInputEdgeReceipt
    {
        internal LiveAcceptanceInputEdgeReceipt(
            int sequence,
            DateTimeOffset observedUtc,
            bool pressed,
            bool released,
            double x,
            double y,
            string route,
            bool forwarded,
            long foregroundWindow,
            int foregroundProcessId,
            bool gtaForeground)
        {
            Sequence = sequence;
            ObservedUtc = observedUtc;
            Pressed = pressed;
            Released = released;
            X = x;
            Y = y;
            Route = route;
            Forwarded = forwarded;
            ForegroundWindow = foregroundWindow;
            ForegroundProcessId = foregroundProcessId;
            GtaForeground = gtaForeground;
        }

        public int Sequence { get; }
        public DateTimeOffset ObservedUtc { get; }
        public bool Pressed { get; }
        public bool Released { get; }
        public double X { get; }
        public double Y { get; }
        public string Route { get; }
        public bool Forwarded { get; }
        public long ForegroundWindow { get; }
        public int ForegroundProcessId { get; }
        public bool GtaForeground { get; }
    }

    public sealed class LiveAcceptanceStartupTimestamps
    {
        public DateTimeOffset? HarnessArmedUtc { get; internal set; }
        public DateTimeOffset? GtaProcessObservedUtc { get; internal set; }
        public DateTimeOffset? GtaWindowObservedUtc { get; internal set; }
        public DateTimeOffset? FrontendAboutObservedUtc { get; internal set; }
        public DateTimeOffset? StoryInitializingObservedUtc { get; internal set; }
        public DateTimeOffset? ProviderReadyObservedUtc { get; internal set; }
        public DateTimeOffset? MenuPendingPaintObservedUtc { get; internal set; }
        public DateTimeOffset? MenuInteractiveObservedUtc { get; internal set; }
        public DateTimeOffset? HarnessCompletedUtc { get; internal set; }
    }

    public sealed class LiveAcceptanceShutdownTimestamps
    {
        public DateTimeOffset? MenuClosingObservedUtc { get; internal set; }
        public DateTimeOffset? MenuClosedObservedUtc { get; internal set; }
        public DateTimeOffset? QuitRequestedUtc { get; internal set; }
        public DateTimeOffset? ScriptAbortObservedUtc { get; internal set; }
        public DateTimeOffset? ScriptHookUninitializedUtc { get; internal set; }
        public DateTimeOffset? GtaWindowDestroyedUtc { get; internal set; }
        public DateTimeOffset? GtaProcessExitedUtc { get; internal set; }
        public DateTimeOffset? WebViewProcessExitedUtc { get; internal set; }
    }

    /// <summary>
    /// Fail-closed receipt state machine. It records one canonical startup
    /// presentation through its first close. Reopen stress cycles remain
    /// separate acceptance steps and cannot rewrite this authoritative chain.
    /// </summary>
    public sealed class LiveAcceptanceSurfaceLifecycleReceipt
    {
        private readonly List<LiveAcceptanceLifecycleObservation> _observations =
            new List<LiveAcceptanceLifecycleObservation>();
        private readonly List<LiveAcceptanceInputEdgeReceipt> _inputEdges =
            new List<LiveAcceptanceInputEdgeReceipt>();
        private int _sequence;

        public LiveAcceptanceSurfaceLifecycleReceipt(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId) || runId.Length > 128)
                throw new ArgumentException("A bounded run id is required.", nameof(runId));
            RunId = runId;
        }

        public string RunId { get; }
        public LiveAcceptanceSurfaceLifecycleState CurrentState { get; private set; }
        public int CurrentGeneration { get; private set; }
        public string CurrentPresentationId { get; private set; } = string.Empty;
        public LiveAcceptanceStartupTimestamps Startup { get; } =
            new LiveAcceptanceStartupTimestamps();
        public LiveAcceptanceShutdownTimestamps Shutdown { get; } =
            new LiveAcceptanceShutdownTimestamps();
        public IReadOnlyList<LiveAcceptanceLifecycleObservation> Observations =>
            new ReadOnlyCollection<LiveAcceptanceLifecycleObservation>(_observations);
        public IReadOnlyList<LiveAcceptanceInputEdgeReceipt> InputEdges =>
            new ReadOnlyCollection<LiveAcceptanceInputEdgeReceipt>(_inputEdges);

        public bool TryAdvance(
            LiveAcceptanceSurfaceLifecycleState next,
            LiveAcceptanceLifecycleIdentity identity,
            DateTimeOffset observedUtc,
            LiveAcceptanceLifecycleEvidenceSource source,
            string evidence,
            out string failure)
        {
            failure = string.Empty;
            if (identity == null) return Fail("lifecycle_identity_missing", out failure);
            if (string.IsNullOrWhiteSpace(evidence))
                return Fail("lifecycle_evidence_missing", out failure);
            if (!IsAllowedTransition(CurrentState, next))
                return Fail(
                    "lifecycle_transition_invalid:" + CurrentState + "->" + next,
                    out failure);
            if (CurrentGeneration > 0 && identity.Generation < CurrentGeneration)
                return Fail("lifecycle_generation_regressed", out failure);
            if (RequiresPresentation(next) && !identity.HasPresentation)
                return Fail("lifecycle_presentation_missing", out failure);
            if ((next == LiveAcceptanceSurfaceLifecycleState.MenuInteractive ||
                    next == LiveAcceptanceSurfaceLifecycleState.Closing ||
                    next == LiveAcceptanceSurfaceLifecycleState.Closed) &&
                !string.Equals(
                    identity.PresentationId,
                    CurrentPresentationId,
                    StringComparison.Ordinal))
                return Fail("lifecycle_presentation_changed", out failure);

            CurrentState = next;
            CurrentGeneration = identity.Generation;
            if (identity.HasPresentation)
                CurrentPresentationId = identity.PresentationId;
            _observations.Add(new LiveAcceptanceLifecycleObservation(
                ++_sequence,
                next,
                identity,
                observedUtc,
                source,
                evidence));
            MarkStateTimestamp(next, observedUtc);
            return true;
        }

        public bool TryRecordEvidence(
            LiveAcceptanceSurfaceLifecycleState state,
            LiveAcceptanceLifecycleIdentity identity,
            DateTimeOffset observedUtc,
            LiveAcceptanceLifecycleEvidenceSource source,
            string evidence,
            out string failure)
        {
            failure = string.Empty;
            if (identity == null) return Fail("lifecycle_identity_missing", out failure);
            if (state != CurrentState)
                return Fail("lifecycle_evidence_state_mismatch", out failure);
            if (identity.Generation != CurrentGeneration ||
                !string.Equals(
                    identity.PresentationId,
                    CurrentPresentationId,
                    StringComparison.Ordinal))
                return Fail("lifecycle_evidence_identity_mismatch", out failure);
            if (string.IsNullOrWhiteSpace(evidence))
                return Fail("lifecycle_evidence_missing", out failure);
            _observations.Add(new LiveAcceptanceLifecycleObservation(
                ++_sequence,
                state,
                identity,
                observedUtc,
                source,
                evidence));
            return true;
        }

        public bool TryRecordInputEdge(
            LiveAcceptancePointerEdge edge,
            DateTimeOffset observedUtc,
            long foregroundWindow,
            int foregroundProcessId,
            int gtaProcessId,
            out string failure)
        {
            failure = string.Empty;
            if (CurrentState != LiveAcceptanceSurfaceLifecycleState.MenuInteractive)
                return Fail("input_edge_outside_interactive_lifecycle", out failure);
            if (edge.Pressed == edge.Released)
                return Fail("input_edge_not_singular", out failure);
            if (!LiveAcceptanceContract.IsValidPoint(edge.X, edge.Y))
                return Fail("input_edge_coordinates_invalid", out failure);
            if (foregroundWindow == 0 || foregroundProcessId <= 0 || gtaProcessId <= 0)
                return Fail("input_edge_foreground_identity_missing", out failure);
            _inputEdges.Add(new LiveAcceptanceInputEdgeReceipt(
                _inputEdges.Count + 1,
                observedUtc,
                edge.Pressed,
                edge.Released,
                edge.X,
                edge.Y,
                edge.Route,
                edge.Forwarded,
                foregroundWindow,
                foregroundProcessId,
                foregroundProcessId == gtaProcessId));
            return true;
        }

        public void MarkStartup(
            LiveAcceptanceStartupMilestone milestone,
            DateTimeOffset observedUtc)
        {
            switch (milestone)
            {
                case LiveAcceptanceStartupMilestone.HarnessArmed:
                    Startup.HarnessArmedUtc = First(Startup.HarnessArmedUtc, observedUtc); break;
                case LiveAcceptanceStartupMilestone.GtaProcessObserved:
                    Startup.GtaProcessObservedUtc = First(Startup.GtaProcessObservedUtc, observedUtc); break;
                case LiveAcceptanceStartupMilestone.GtaWindowObserved:
                    Startup.GtaWindowObservedUtc = First(Startup.GtaWindowObservedUtc, observedUtc); break;
                case LiveAcceptanceStartupMilestone.FrontendAboutObserved:
                    Startup.FrontendAboutObservedUtc = First(Startup.FrontendAboutObservedUtc, observedUtc); break;
                case LiveAcceptanceStartupMilestone.StoryInitializingObserved:
                    Startup.StoryInitializingObservedUtc = First(Startup.StoryInitializingObservedUtc, observedUtc); break;
                case LiveAcceptanceStartupMilestone.ProviderReadyObserved:
                    Startup.ProviderReadyObservedUtc = First(Startup.ProviderReadyObservedUtc, observedUtc); break;
                case LiveAcceptanceStartupMilestone.MenuPendingPaintObserved:
                    Startup.MenuPendingPaintObservedUtc = First(Startup.MenuPendingPaintObservedUtc, observedUtc); break;
                case LiveAcceptanceStartupMilestone.MenuInteractiveObserved:
                    Startup.MenuInteractiveObservedUtc = First(Startup.MenuInteractiveObservedUtc, observedUtc); break;
                case LiveAcceptanceStartupMilestone.HarnessCompleted:
                    Startup.HarnessCompletedUtc = First(Startup.HarnessCompletedUtc, observedUtc); break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(milestone));
            }
        }

        public void MarkShutdown(
            LiveAcceptanceShutdownMilestone milestone,
            DateTimeOffset observedUtc)
        {
            switch (milestone)
            {
                case LiveAcceptanceShutdownMilestone.MenuClosingObserved:
                    Shutdown.MenuClosingObservedUtc = First(Shutdown.MenuClosingObservedUtc, observedUtc); break;
                case LiveAcceptanceShutdownMilestone.MenuClosedObserved:
                    Shutdown.MenuClosedObservedUtc = First(Shutdown.MenuClosedObservedUtc, observedUtc); break;
                case LiveAcceptanceShutdownMilestone.QuitRequested:
                    Shutdown.QuitRequestedUtc = First(Shutdown.QuitRequestedUtc, observedUtc); break;
                case LiveAcceptanceShutdownMilestone.ScriptAbortObserved:
                    Shutdown.ScriptAbortObservedUtc = First(Shutdown.ScriptAbortObservedUtc, observedUtc); break;
                case LiveAcceptanceShutdownMilestone.ScriptHookUninitialized:
                    Shutdown.ScriptHookUninitializedUtc = First(Shutdown.ScriptHookUninitializedUtc, observedUtc); break;
                case LiveAcceptanceShutdownMilestone.GtaWindowDestroyed:
                    Shutdown.GtaWindowDestroyedUtc = First(Shutdown.GtaWindowDestroyedUtc, observedUtc); break;
                case LiveAcceptanceShutdownMilestone.GtaProcessExited:
                    Shutdown.GtaProcessExitedUtc = First(Shutdown.GtaProcessExitedUtc, observedUtc); break;
                case LiveAcceptanceShutdownMilestone.WebViewProcessExited:
                    Shutdown.WebViewProcessExitedUtc = First(Shutdown.WebViewProcessExitedUtc, observedUtc); break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(milestone));
            }
        }

        public bool TryValidateSurfaceLifecycleCompleted(out string failure)
        {
            failure = string.Empty;
            if (CurrentState != LiveAcceptanceSurfaceLifecycleState.Closed)
                return Fail("lifecycle_not_closed", out failure);
            var requiredStates = new[]
            {
                LiveAcceptanceSurfaceLifecycleState.StoryInitializing,
                LiveAcceptanceSurfaceLifecycleState.ProviderReady,
                LiveAcceptanceSurfaceLifecycleState.MenuPendingPaint,
                LiveAcceptanceSurfaceLifecycleState.MenuInteractive,
                LiveAcceptanceSurfaceLifecycleState.Closing,
                LiveAcceptanceSurfaceLifecycleState.Closed,
            };
            foreach (var state in requiredStates)
            {
                if (!_observations.Exists(item => item.State == state))
                    return Fail("lifecycle_state_missing:" + state, out failure);
            }
            if (!_observations.Exists(item =>
                    item.State == LiveAcceptanceSurfaceLifecycleState.MenuInteractive &&
                    item.Source == LiveAcceptanceLifecycleEvidenceSource.DesktopPixels))
                return Fail("interactive_desktop_pixels_missing", out failure);
            if (_inputEdges.Exists(edge => !edge.GtaForeground))
                return Fail("input_foreground_left_gta", out failure);
            if (!Startup.HarnessArmedUtc.HasValue ||
                !Startup.GtaProcessObservedUtc.HasValue ||
                !Startup.GtaWindowObservedUtc.HasValue ||
                !Startup.MenuInteractiveObservedUtc.HasValue)
                return Fail("startup_timestamps_incomplete", out failure);
            return true;
        }

        /// <summary>
        /// Validates an actual process shutdown, not merely a menu close. Live
        /// UI acceptance normally leaves GTA running and must report this phase
        /// as not exercised. A dedicated shutdown run can opt into this gate
        /// and must supply every external/runtime boundary before it passes.
        /// </summary>
        public bool TryValidateShutdownCompleted(out string failure)
        {
            failure = string.Empty;
            if (!TryValidateSurfaceLifecycleCompleted(out failure))
                return false;
            if (!Shutdown.QuitRequestedUtc.HasValue)
                return Fail("shutdown_quit_request_missing", out failure);
            if (!Shutdown.ScriptAbortObservedUtc.HasValue)
                return Fail("shutdown_script_abort_missing", out failure);
            if (!Shutdown.ScriptHookUninitializedUtc.HasValue)
                return Fail("shutdown_scripthook_uninitialize_missing", out failure);
            if (!Shutdown.GtaWindowDestroyedUtc.HasValue)
                return Fail("shutdown_gta_window_destroyed_missing", out failure);
            if (!Shutdown.GtaProcessExitedUtc.HasValue)
                return Fail("shutdown_gta_process_exit_missing", out failure);
            if (!Shutdown.WebViewProcessExitedUtc.HasValue)
                return Fail("shutdown_webview_process_exit_missing", out failure);

            var quitRequested = Shutdown.QuitRequestedUtc.Value;
            var scriptAbort = Shutdown.ScriptAbortObservedUtc.Value;
            var scriptHookUninitialized = Shutdown.ScriptHookUninitializedUtc.Value;
            var windowDestroyed = Shutdown.GtaWindowDestroyedUtc.Value;
            var processExited = Shutdown.GtaProcessExitedUtc.Value;
            var webViewExited = Shutdown.WebViewProcessExitedUtc.Value;
            if (quitRequested > scriptAbort ||
                scriptAbort > scriptHookUninitialized ||
                scriptHookUninitialized > windowDestroyed ||
                windowDestroyed > processExited ||
                processExited > webViewExited)
            {
                return Fail("shutdown_milestone_order_invalid", out failure);
            }
            return true;
        }

        private static bool RequiresPresentation(LiveAcceptanceSurfaceLifecycleState state) =>
            state == LiveAcceptanceSurfaceLifecycleState.MenuPendingPaint ||
            state == LiveAcceptanceSurfaceLifecycleState.MenuInteractive ||
            state == LiveAcceptanceSurfaceLifecycleState.Closing ||
            state == LiveAcceptanceSurfaceLifecycleState.Closed;

        private static bool IsAllowedTransition(
            LiveAcceptanceSurfaceLifecycleState current,
            LiveAcceptanceSurfaceLifecycleState next)
        {
            switch (current)
            {
                case LiveAcceptanceSurfaceLifecycleState.None:
                    return next == LiveAcceptanceSurfaceLifecycleState.FrontendAbout ||
                        next == LiveAcceptanceSurfaceLifecycleState.StoryInitializing;
                case LiveAcceptanceSurfaceLifecycleState.FrontendAbout:
                    return next == LiveAcceptanceSurfaceLifecycleState.StoryInitializing;
                case LiveAcceptanceSurfaceLifecycleState.StoryInitializing:
                    return next == LiveAcceptanceSurfaceLifecycleState.ProviderReady;
                case LiveAcceptanceSurfaceLifecycleState.ProviderReady:
                    return next == LiveAcceptanceSurfaceLifecycleState.MenuPendingPaint;
                case LiveAcceptanceSurfaceLifecycleState.MenuPendingPaint:
                    return next == LiveAcceptanceSurfaceLifecycleState.MenuInteractive;
                case LiveAcceptanceSurfaceLifecycleState.MenuInteractive:
                    return next == LiveAcceptanceSurfaceLifecycleState.Closing;
                case LiveAcceptanceSurfaceLifecycleState.Closing:
                    return next == LiveAcceptanceSurfaceLifecycleState.Closed;
                default:
                    return false;
            }
        }

        private void MarkStateTimestamp(
            LiveAcceptanceSurfaceLifecycleState state,
            DateTimeOffset observedUtc)
        {
            switch (state)
            {
                case LiveAcceptanceSurfaceLifecycleState.FrontendAbout:
                    MarkStartup(LiveAcceptanceStartupMilestone.FrontendAboutObserved, observedUtc); break;
                case LiveAcceptanceSurfaceLifecycleState.StoryInitializing:
                    MarkStartup(LiveAcceptanceStartupMilestone.StoryInitializingObserved, observedUtc); break;
                case LiveAcceptanceSurfaceLifecycleState.ProviderReady:
                    MarkStartup(LiveAcceptanceStartupMilestone.ProviderReadyObserved, observedUtc); break;
                case LiveAcceptanceSurfaceLifecycleState.MenuPendingPaint:
                    MarkStartup(LiveAcceptanceStartupMilestone.MenuPendingPaintObserved, observedUtc); break;
                case LiveAcceptanceSurfaceLifecycleState.MenuInteractive:
                    MarkStartup(LiveAcceptanceStartupMilestone.MenuInteractiveObserved, observedUtc); break;
                case LiveAcceptanceSurfaceLifecycleState.Closing:
                    MarkShutdown(LiveAcceptanceShutdownMilestone.MenuClosingObserved, observedUtc); break;
                case LiveAcceptanceSurfaceLifecycleState.Closed:
                    MarkShutdown(LiveAcceptanceShutdownMilestone.MenuClosedObserved, observedUtc); break;
            }
        }

        private static DateTimeOffset First(DateTimeOffset? current, DateTimeOffset value) =>
            current ?? value;

        private static bool Fail(string reason, out string failure)
        {
            failure = reason;
            return false;
        }
    }
}
