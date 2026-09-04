using System;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class LiveAcceptanceLifecycleReceiptTests
    {
        [Fact]
        public void CompleteLifecyclePreservesGenerationAndPresentationIdentity()
        {
            var receipt = CompleteLifecycle();

            Assert.True(
                receipt.TryValidateSurfaceLifecycleCompleted(out var failure),
                failure);
            Assert.Equal(LiveAcceptanceSurfaceLifecycleState.Closed, receipt.CurrentState);
            Assert.Equal("menu-17", receipt.CurrentPresentationId);
            Assert.Contains(receipt.Observations, observation =>
                observation.State == LiveAcceptanceSurfaceLifecycleState.MenuInteractive &&
                observation.Source == LiveAcceptanceLifecycleEvidenceSource.DesktopPixels &&
                observation.LifecycleKey == "5:menu-17");
            Assert.NotNull(receipt.Startup.ProviderReadyObservedUtc);
            Assert.NotNull(receipt.Shutdown.MenuClosedObservedUtc);
        }

        [Fact]
        public void SplashSkippedRouteCompletesTheSameDesktopVerifiedLifecycle()
        {
            var receipt = new LiveAcceptanceSurfaceLifecycleReceipt("skip-about");
            receipt.MarkStartup(LiveAcceptanceStartupMilestone.HarnessArmed, At(0));
            receipt.MarkStartup(LiveAcceptanceStartupMilestone.GtaProcessObserved, At(1));
            receipt.MarkStartup(LiveAcceptanceStartupMilestone.GtaWindowObserved, At(1));
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.StoryInitializing,
                new LiveAcceptanceLifecycleIdentity(4, null),
                At(2), LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "objective Story initializer", out var storyFailure), storyFailure);
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.ProviderReady,
                new LiveAcceptanceLifecycleIdentity(4, null),
                At(3), LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "provider ready", out var providerFailure), providerFailure);
            var identity = new LiveAcceptanceLifecycleIdentity(4, "menu-direct");
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.MenuPendingPaint,
                identity, At(4), LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "presentation pending", out var pendingFailure), pendingFailure);
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.MenuInteractive,
                identity, At(5), LiveAcceptanceLifecycleEvidenceSource.DesktopPixels,
                "exact desktop identity verified", out var readyFailure), readyFailure);
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.Closing,
                identity, At(6), LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "close", out var closingFailure), closingFailure);
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.Closed,
                identity, At(7), LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "hidden", out var closedFailure), closedFailure);

            Assert.True(
                receipt.TryValidateSurfaceLifecycleCompleted(out var failure),
                failure);
            Assert.DoesNotContain(receipt.Observations, observation =>
                observation.State == LiveAcceptanceSurfaceLifecycleState.FrontendAbout);
        }

        [Fact]
        public void BrowserCaptureCannotProveInteractiveDesktopVisibility()
        {
            var receipt = LifecycleThroughPending();
            var identity = new LiveAcceptanceLifecycleIdentity(5, "menu-17");
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.MenuInteractive,
                identity,
                At(5),
                LiveAcceptanceLifecycleEvidenceSource.BrowserCapture,
                "capture-preview rendered",
                out var advanceFailure), advanceFailure);
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.Closing,
                identity,
                At(6),
                LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "hide requested",
                out var closingFailure), closingFailure);
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.Closed,
                identity,
                At(7),
                LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "hidden",
                out var closedFailure), closedFailure);

            Assert.False(
                receipt.TryValidateSurfaceLifecycleCompleted(out var failure));
            Assert.Equal("interactive_desktop_pixels_missing", failure);
        }

        [Fact]
        public void PresentationCannotChangeBetweenPendingAndInteractive()
        {
            var receipt = LifecycleThroughPending();

            Assert.False(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.MenuInteractive,
                new LiveAcceptanceLifecycleIdentity(5, "different-menu"),
                At(5),
                LiveAcceptanceLifecycleEvidenceSource.DesktopPixels,
                "desktop pixels",
                out var failure));
            Assert.Equal("lifecycle_presentation_changed", failure);
        }

        [Fact]
        public void InputEdgesRecordForegroundIdentityAndFailClosedWhenItLeavesGta()
        {
            var receipt = LifecycleThroughInteractive();
            Assert.True(receipt.TryRecordInputEdge(
                new LiveAcceptancePointerEdge(0.25, 0.5, true, false, "composition", true),
                At(6),
                0x1234,
                99,
                99,
                out var downFailure), downFailure);
            Assert.True(receipt.TryRecordInputEdge(
                new LiveAcceptancePointerEdge(0.25, 0.5, false, true, "composition", true),
                At(7),
                0x5678,
                100,
                99,
                out var upFailure), upFailure);
            Close(receipt);

            Assert.False(
                receipt.TryValidateSurfaceLifecycleCompleted(out var failure));
            Assert.Equal("input_foreground_left_gta", failure);
        }

        [Fact]
        public void StartupAndShutdownTimestampsPreserveFirstObservation()
        {
            var receipt = new LiveAcceptanceSurfaceLifecycleReceipt("run");
            receipt.MarkStartup(LiveAcceptanceStartupMilestone.HarnessArmed, At(2));
            receipt.MarkStartup(LiveAcceptanceStartupMilestone.HarnessArmed, At(8));
            receipt.MarkShutdown(LiveAcceptanceShutdownMilestone.GtaProcessExited, At(9));

            Assert.Equal(At(2), receipt.Startup.HarnessArmedUtc);
            Assert.Equal(At(9), receipt.Shutdown.GtaProcessExitedUtc);
        }

        [Fact]
        public void MenuCloseDoesNotPretendThatTheGameShutdownWasValidated()
        {
            var receipt = CompleteLifecycle();

            Assert.True(
                receipt.TryValidateSurfaceLifecycleCompleted(
                    out var lifecycleFailure),
                lifecycleFailure);
            Assert.False(receipt.TryValidateShutdownCompleted(out var failure));
            Assert.Equal("shutdown_quit_request_missing", failure);
        }

        [Fact]
        public void FullShutdownRequiresEveryOrderedRuntimeBoundary()
        {
            var receipt = CompleteLifecycle();
            receipt.MarkShutdown(
                LiveAcceptanceShutdownMilestone.QuitRequested,
                At(10));
            receipt.MarkShutdown(
                LiveAcceptanceShutdownMilestone.ScriptAbortObserved,
                At(11));
            receipt.MarkShutdown(
                LiveAcceptanceShutdownMilestone.ScriptHookUninitialized,
                At(12));
            receipt.MarkShutdown(
                LiveAcceptanceShutdownMilestone.GtaWindowDestroyed,
                At(13));
            receipt.MarkShutdown(
                LiveAcceptanceShutdownMilestone.GtaProcessExited,
                At(14));
            receipt.MarkShutdown(
                LiveAcceptanceShutdownMilestone.WebViewProcessExited,
                At(15));

            Assert.True(
                receipt.TryValidateShutdownCompleted(out var failure),
                failure);
        }

        [Theory]
        [InlineData(12, 11, 13, 14, 15)]
        [InlineData(11, 12, 13, 15, 14)]
        public void FullShutdownRejectsOutOfOrderRuntimeBoundaries(
            int scriptAbortSecond,
            int scriptHookSecond,
            int windowDestroyedSecond,
            int processExitedSecond,
            int webViewExitedSecond)
        {
            var receipt = CompleteLifecycle();
            receipt.MarkShutdown(LiveAcceptanceShutdownMilestone.QuitRequested, At(10));
            receipt.MarkShutdown(
                LiveAcceptanceShutdownMilestone.ScriptAbortObserved,
                At(scriptAbortSecond));
            receipt.MarkShutdown(
                LiveAcceptanceShutdownMilestone.ScriptHookUninitialized,
                At(scriptHookSecond));
            receipt.MarkShutdown(
                LiveAcceptanceShutdownMilestone.GtaWindowDestroyed,
                At(windowDestroyedSecond));
            receipt.MarkShutdown(
                LiveAcceptanceShutdownMilestone.GtaProcessExited,
                At(processExitedSecond));
            receipt.MarkShutdown(
                LiveAcceptanceShutdownMilestone.WebViewProcessExited,
                At(webViewExitedSecond));

            Assert.False(receipt.TryValidateShutdownCompleted(out var failure));
            Assert.Equal("shutdown_milestone_order_invalid", failure);
        }

        private static LiveAcceptanceSurfaceLifecycleReceipt CompleteLifecycle()
        {
            var receipt = LifecycleThroughInteractive();
            Assert.True(receipt.TryRecordInputEdge(
                new LiveAcceptancePointerEdge(0.25, 0.5, true, false, "composition", true),
                At(6),
                0x1234,
                99,
                99,
                out var downFailure), downFailure);
            Assert.True(receipt.TryRecordInputEdge(
                new LiveAcceptancePointerEdge(0.25, 0.5, false, true, "composition", true),
                At(7),
                0x1234,
                99,
                99,
                out var upFailure), upFailure);
            Close(receipt);
            return receipt;
        }

        private static LiveAcceptanceSurfaceLifecycleReceipt LifecycleThroughInteractive()
        {
            var receipt = LifecycleThroughPending();
            var identity = new LiveAcceptanceLifecycleIdentity(5, "menu-17");
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.MenuInteractive,
                identity,
                At(5),
                LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "menu ready",
                out var readyFailure), readyFailure);
            Assert.True(receipt.TryRecordEvidence(
                LiveAcceptanceSurfaceLifecycleState.MenuInteractive,
                identity,
                At(5).AddMilliseconds(1),
                LiveAcceptanceLifecycleEvidenceSource.DesktopPixels,
                "desktop capture classified",
                out var pixelFailure), pixelFailure);
            return receipt;
        }

        private static LiveAcceptanceSurfaceLifecycleReceipt LifecycleThroughPending()
        {
            var receipt = new LiveAcceptanceSurfaceLifecycleReceipt("run-17");
            receipt.MarkStartup(LiveAcceptanceStartupMilestone.HarnessArmed, At(0));
            receipt.MarkStartup(LiveAcceptanceStartupMilestone.GtaProcessObserved, At(1));
            receipt.MarkStartup(LiveAcceptanceStartupMilestone.GtaWindowObserved, At(1));
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.FrontendAbout,
                new LiveAcceptanceLifecycleIdentity(2, null),
                At(2),
                LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "about surface ready",
                out var aboutFailure), aboutFailure);
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.StoryInitializing,
                new LiveAcceptanceLifecycleIdentity(5, null),
                At(3),
                LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "initializer surface ready",
                out var storyFailure), storyFailure);
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.ProviderReady,
                new LiveAcceptanceLifecycleIdentity(5, null),
                At(4),
                LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "provider connected",
                out var providerFailure), providerFailure);
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.MenuPendingPaint,
                new LiveAcceptanceLifecycleIdentity(5, "menu-17"),
                At(4).AddMilliseconds(1),
                LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "typed presentation pending",
                out var pendingFailure), pendingFailure);
            return receipt;
        }

        private static void Close(LiveAcceptanceSurfaceLifecycleReceipt receipt)
        {
            var identity = new LiveAcceptanceLifecycleIdentity(5, "menu-17");
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.Closing,
                identity,
                At(8),
                LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "hide requested",
                out var closingFailure), closingFailure);
            Assert.True(receipt.TryAdvance(
                LiveAcceptanceSurfaceLifecycleState.Closed,
                identity,
                At(9),
                LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                "hidden",
                out var closedFailure), closedFailure);
        }

        private static DateTimeOffset At(int seconds) =>
            new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
    }
}
