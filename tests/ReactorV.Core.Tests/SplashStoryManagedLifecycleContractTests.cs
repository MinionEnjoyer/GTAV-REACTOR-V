using System;
using System.IO;
using RageWebUI.Core;
using RageWebUI.Script;
using ReactorV.BootstrapHost;
using Xunit;

namespace RageWebUI.Core.Tests;

/// <summary>
/// Pins the explicit bootstrap lifecycle exercised by the packaged and live
/// harnesses. These tests intentionally combine pure policy checks with source
/// contracts: OverlayWindow and the external preloader cannot be instantiated
/// safely in the unit-test process without a real compositor/GTA HWND.
/// </summary>
public sealed class SplashStoryManagedLifecycleContractTests
{
    [Fact]
    public void NativeCloseIsTerminalBeforeSamePollStoryOrF9Signals()
    {
        var preloader = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs");
        AssertOrdered(
            preloader,
            "var closeRequested = _hostClose?.WaitOne(0) == true;",
            "var initializerPromotionRequested =",
            "var signalAction = HostSurfaceIntentPolicy.EvaluateSignalBatch(",
            "if (signalAction == BootstrapHostSignalAction.Close)",
            "terminal_epoch=True",
            "return true;",
            "if (signalAction == BootstrapHostSignalAction.ToggleAbout)",
            "if (signalAction == BootstrapHostSignalAction.PromoteInitializer)");
    }

    [Fact]
    public void HarnessClosesFrontendAboutBeforeRequestingTheStoryInitializer()
    {
        var live = ReadRepositoryFile(
            "src", "ReactorV.Harness", "LiveAcceptanceHarness.cs");
        var synthetic = ReadRepositoryFile(
            "src", "ReactorV.Harness", "BootstrapHostHarness.cs");

        AssertOrdered(
            live,
            "frontend-about-final-close",
            "story-transition",
            "story-early-preloader",
            "story-preloader-desktop.png");
        AssertOrdered(
            synthetic,
            "var aboutClosed =",
            "var earlyToggleSignaled =",
            "startup-initializing.png",
            "WaitForStartupSurface(");
    }

    [Fact]
    public void OptionalFrontendAboutMayBeSkippedBeforeObjectiveStoryInitialization()
    {
        var receipt = new LiveAcceptanceSurfaceLifecycleReceipt("no-about");
        Assert.True(receipt.TryAdvance(
            LiveAcceptanceSurfaceLifecycleState.StoryInitializing,
            new LiveAcceptanceLifecycleIdentity(4, null),
            At(1),
            LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
            "objective Story transition",
            out var failure), failure);

        Assert.False(HostSurfaceIntentPolicy.ShouldPromoteToInitializer(
            objectiveStoryEvidence: false));
        Assert.True(HostSurfaceIntentPolicy.ShouldPromoteToInitializer(
            objectiveStoryEvidence: true));
    }

    [Fact]
    public void AboutPaintMayBePreservedDuringStoryPromotionButInitializerProofParksIt()
    {
        Assert.True(
            HostSurfaceIntentPolicy.ShouldPreserveVisibleSurfaceDuringPromotion(
                actuallyVisible: true,
                currentMode: "about",
                requestedMode: "initializing"));
        Assert.True(
            HostSurfaceIntentPolicy.ShouldParkForBootstrapPixelProof(
                requestedMode: "initializing"));

        var preloader = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs");
        AssertOrdered(
            preloader,
            "ShouldPreserveVisibleSurfaceDuringPromotion(",
            "ShouldParkForBootstrapPixelProof(",
            "SetBrowserVisible(window, false);",
            "VerifyBootstrapSurfacePixelsAsync(");
    }

    [Fact]
    public void StaleSurfaceRootAndPresentationIdentitiesFailClosed()
    {
        var receipt = ReceiptThroughPending();
        Assert.False(receipt.TryAdvance(
            LiveAcceptanceSurfaceLifecycleState.MenuInteractive,
            new LiveAcceptanceLifecycleIdentity(4, "menu-current"),
            At(5),
            LiveAcceptanceLifecycleEvidenceSource.DesktopPixels,
            "stale generation",
            out var generationFailure));
        Assert.Equal("lifecycle_generation_regressed", generationFailure);

        var gate = new ProviderPresentationCommitGate(timeoutMilliseconds: 100);
        gate.Begin("session-1:menu-old", 0, 1);
        gate.Begin("session-2:menu-current", 10, 1);
        Assert.False(gate.TryCommit("session-1:menu-old", 20, out _, out _));
        Assert.True(gate.TryCommit(
            "session-2:menu-current", 20, out _, out _));

        var overlay = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayWindow.cs");
        Assert.Contains("_finalRevealOffscreenLeaseSurfaceGeneration", overlay);
        Assert.Contains("_finalRevealOffscreenLeaseRootVisualRevision", overlay);
        Assert.Contains("_finalRevealOffscreenLeaseRootVisualRevision ==", overlay);
        Assert.Contains("_webView.RootVisualRevision", overlay);
    }

    [Fact]
    public void NativeVisibilityAndCapturePreviewDoNotGrantCommittedInputOwnership()
    {
        var receipt = ReceiptThroughPending();
        var identity = new LiveAcceptanceLifecycleIdentity(5, "menu-current");
        Assert.True(receipt.TryAdvance(
            LiveAcceptanceSurfaceLifecycleState.MenuInteractive,
            identity,
            At(5),
            LiveAcceptanceLifecycleEvidenceSource.BrowserCapture,
            "CapturePreview succeeded while native HWND was visible",
            out var advanceFailure), advanceFailure);
        Assert.True(receipt.TryAdvance(
            LiveAcceptanceSurfaceLifecycleState.Closing,
            identity,
            At(6),
            LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
            "closed without DWM proof",
            out var closingFailure), closingFailure);
        Assert.True(receipt.TryAdvance(
            LiveAcceptanceSurfaceLifecycleState.Closed,
            identity,
            At(7),
            LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
            "hidden",
            out var closedFailure), closedFailure);
        Assert.False(receipt.TryValidateSurfaceLifecycleCompleted(out var failure));
        Assert.Equal("interactive_desktop_pixels_missing", failure);

        var overlay = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayWindow.cs");
        var commit = MethodRegion(
            overlay,
            "private void CommitVerifiedRevealAfterPixelProof(",
            "private void HandleFinalRevealPixelProofFailure(");
        AssertOrdered(
            commit,
            "OwnsFinalRevealOffscreenLease(generation)",
            "NativeMethods.SetWindowPos(",
            "CommitFinalRevealOffscreenLease(generation)",
            "CommitProviderInputAfterRevealFence();");
        Assert.DoesNotContain("CapturePreviewAsync", commit);
    }

    [Fact]
    public void MissingPaintAndProviderAcknowledgementsHaveBoundedFailClosedPaths()
    {
        var gate = new ProviderPresentationCommitGate(timeoutMilliseconds: 100);
        gate.Begin("menu-timeout", 1000, 12);
        Assert.True(gate.TryExpire(1100, out var expired, out _));
        Assert.Equal("menu-timeout", expired);
        Assert.False(gate.TryCommit("menu-timeout", 1101, out _, out _));

        var preloader = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs");
        var completion = MethodRegion(
            preloader,
            "private async void CompleteHostSurfaceReveal(",
            "private void CancelPendingHostSurfaceReveal(");
        AssertOrdered(
            completion,
            "if (!verified)",
            "CancelPendingHostSurfaceReveal(\"surface-pixels-unverified\")",
            "SetBrowserVisible(window, false);",
            "PostHostSurface(window, \"none\");");

        var script = ReadRepositoryFile(
            "src", "ReactorV.Script", "RageWebUiScript.cs");
        Assert.Contains("_menuRevealGate.TryExpire(", script);
        Assert.Contains("AbortPresentationTransfer(", script);
        Assert.Contains("\"presentation-ready-timeout\"", script);
        Assert.Contains("_providerPresentationCommitGate.TryExpire(", script);
        Assert.Contains("\"provider-paint-timeout\"", script);
        Assert.DoesNotContain(
            "CloseOverlay(\"presentation-ready-timeout\")",
            script);
    }

    [Fact]
    public void MissingSurfaceReadyDeadlineUsesABoundedFreshGenerationRetry()
    {
        Assert.Equal(
            HostSurfaceReadyDeadlineAction.FailClosedAndRetry,
            HostSurfaceIntentPolicy.EvaluateReadyDeadline(
                pendingGeneration: 9,
                deadlineArmed: true,
                deadlineExpired: true));

        var preloader = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs");
        Assert.Contains("HostSurfaceReadyDeadline", preloader);
        Assert.Contains("HostSurfaceReadyDeadlineAction.FailClosedAndRetry", preloader);
        Assert.Contains("bootstrap_host_surface_ready_timeout", preloader);
        Assert.Contains("bootstrap_host_surface_ready_abandoned", preloader);
    }

    [Fact]
    public void HarnessExercisesLateProviderDisconnectReconnectAndStaleSessionRejection()
    {
        var harness = ReadRepositoryFile(
            "src", "ReactorV.Harness", "BootstrapHostHarness.cs");
        Assert.Contains("transientProviderReconnected", harness);
        Assert.Contains("reconnectObservedPreservedIntent", harness);
        Assert.Contains("providerDisconnected", harness);
        Assert.Contains("aboutPreservedAcrossDisconnect", harness);
        Assert.Contains("staleAcknowledgements == 0", harness);
        Assert.Contains("cancelledDispatchRejected", harness);
        Assert.Contains("cancelledClaimRejected", harness);
    }

    [Fact]
    public void F9HasDistinctPreparingCommittedAndManagedTransferActions()
    {
        Assert.Equal(
            BootstrapSurfaceToggleAction.Close,
            HostSurfaceIntentPolicy.EvaluateBootstrapToggle(logicallyOpen: true));
        Assert.Equal(
            BootstrapSurfaceToggleAction.Show,
            HostSurfaceIntentPolicy.EvaluateBootstrapToggle(logicallyOpen: false));
        Assert.Equal(
            NativeHostToggleAction.ShowBootstrapSurface,
            HostSurfaceIntentPolicy.EvaluateNativeToggle(
                runtimeReadyLeaseSignaled: false));
        Assert.Equal(
            NativeHostToggleAction.ForwardDefaultMenuIntentHidden,
            HostSurfaceIntentPolicy.EvaluateNativeToggle(
                runtimeReadyLeaseSignaled: true));
        Assert.True(HostSurfaceIntentPolicy.ShouldConsumeOpeningInitializerToggle(
            openingEdgePending: true,
            initializerLogicallyOpen: true));
        Assert.False(HostSurfaceIntentPolicy.ShouldConsumeOpeningInitializerToggle(
            openingEdgePending: true,
            initializerLogicallyOpen: false));
    }

    [Fact]
    public void ClaimedCancelledAndExpiredStartupIntentsCannotBeRestoredByLateWork()
    {
        var claimedProcessId =
            81000 + Math.Abs(Guid.NewGuid().GetHashCode() % 1000);
        using var claimedIntent =
            PreloadHandoff.CreateDefaultMenuIntentWaitHandle(claimedProcessId);
        using var claimedAck =
            PreloadHandoff.CreateDefaultMenuIntentClaimedWaitHandle(claimedProcessId);
        using var claimedActive =
            PreloadHandoff.CreateDefaultMenuIntentActiveWaitHandle(claimedProcessId);
        using var claimedCancelled =
            PreloadHandoff.CreateDefaultMenuIntentCancelledWaitHandle(claimedProcessId);

        Assert.True(PreloadHandoff.TryArmDefaultMenuIntent(claimedProcessId));
        Assert.True(PreloadHandoff.TryConsumeDefaultMenuIntent(claimedProcessId));
        Assert.True(PreloadHandoff.TryCommitDefaultMenuIntentClaim(claimedProcessId));
        Assert.True(PreloadHandoff.TryTakeDefaultMenuIntentClaim(claimedProcessId));
        Assert.False(PreloadHandoff.TryRestoreDefaultMenuIntent(claimedProcessId));
        Assert.False(PreloadHandoff.TryCommitDefaultMenuIntentClaim(claimedProcessId));

        var cancelledProcessId = claimedProcessId + 2000;
        using var cancelledIntent =
            PreloadHandoff.CreateDefaultMenuIntentWaitHandle(cancelledProcessId);
        using var cancelledAck =
            PreloadHandoff.CreateDefaultMenuIntentClaimedWaitHandle(cancelledProcessId);
        using var cancelledActive =
            PreloadHandoff.CreateDefaultMenuIntentActiveWaitHandle(cancelledProcessId);
        using var cancelled =
            PreloadHandoff.CreateDefaultMenuIntentCancelledWaitHandle(cancelledProcessId);

        Assert.True(PreloadHandoff.TryArmDefaultMenuIntent(cancelledProcessId));
        Assert.True(PreloadHandoff.TryCancelDefaultMenuIntent(cancelledProcessId));
        Assert.False(PreloadHandoff.TryRestoreDefaultMenuIntent(cancelledProcessId));
        Assert.False(PreloadHandoff.TryCommitDefaultMenuIntentClaim(cancelledProcessId));
        Assert.False(PreloadHandoff.CanDispatchDefaultMenuIntent(cancelledProcessId));

        // Expiry is represented by the same terminal cancellation primitive.
        // Pin that source contract so a late provider cannot reinterpret an
        // expired request as a fresh presentation.
        var preloader = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs");
        Assert.Contains(
            "CancelDefaultMenuIntent(\"deadline-expired\")",
            preloader);
        var cancellation = MethodRegion(
            preloader,
            "private void CancelDefaultMenuIntent(string reason)",
            "private void CompleteDefaultMenuIntentClaim()");
        Assert.Contains("PreloadHandoff.TryCancelDefaultMenuIntent(", cancellation);
    }

    [Fact]
    public void TerminalIntentNeedsAGenerationToSeparateFreshRearmFromStaleRevival()
    {
        // The current named-event contract has no request generation. A fresh
        // F9 edge is therefore represented by resetting the same terminal
        // events, which means stale work cannot be distinguished from a new
        // request by identity alone. This pins the missing production seam:
        // claim/restore/commit need a monotonically increasing intent token.
        var processId = 85000 + Math.Abs(Guid.NewGuid().GetHashCode() % 1000);
        using var intent =
            PreloadHandoff.CreateDefaultMenuIntentWaitHandle(processId);
        using var claimed =
            PreloadHandoff.CreateDefaultMenuIntentClaimedWaitHandle(processId);
        using var active =
            PreloadHandoff.CreateDefaultMenuIntentActiveWaitHandle(processId);
        using var cancelled =
            PreloadHandoff.CreateDefaultMenuIntentCancelledWaitHandle(processId);

        Assert.True(PreloadHandoff.TryArmDefaultMenuIntent(processId));
        Assert.True(PreloadHandoff.TryCancelDefaultMenuIntent(processId));
        Assert.True(PreloadHandoff.TryArmDefaultMenuIntent(processId));
        Assert.True(PreloadHandoff.CanDispatchDefaultMenuIntent(processId));
    }

    private static LiveAcceptanceSurfaceLifecycleReceipt ReceiptThroughPending()
    {
        var receipt = new LiveAcceptanceSurfaceLifecycleReceipt("lifecycle");
        Assert.True(receipt.TryAdvance(
            LiveAcceptanceSurfaceLifecycleState.FrontendAbout,
            new LiveAcceptanceLifecycleIdentity(2, null),
            At(1),
            LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
            "About",
            out var aboutFailure), aboutFailure);
        Assert.True(receipt.TryAdvance(
            LiveAcceptanceSurfaceLifecycleState.StoryInitializing,
            new LiveAcceptanceLifecycleIdentity(5, null),
            At(2),
            LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
            "Story",
            out var storyFailure), storyFailure);
        Assert.True(receipt.TryAdvance(
            LiveAcceptanceSurfaceLifecycleState.ProviderReady,
            new LiveAcceptanceLifecycleIdentity(5, null),
            At(3),
            LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
            "provider",
            out var providerFailure), providerFailure);
        Assert.True(receipt.TryAdvance(
            LiveAcceptanceSurfaceLifecycleState.MenuPendingPaint,
            new LiveAcceptanceLifecycleIdentity(5, "menu-current"),
            At(4),
            LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
            "pending",
            out var pendingFailure), pendingFailure);
        return receipt;
    }

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero)
            .AddSeconds(seconds);

    private static void AssertOrdered(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = source.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Missing or out-of-order marker: {marker}");
            previous = current;
        }
    }

    private static string MethodRegion(
        string source,
        string startMarker,
        string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source.Substring(start, end - start);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null &&
            !(File.Exists(Path.Combine(current.FullName, "ReactorV.json")) &&
              Directory.Exists(Path.Combine(current.FullName, "src"))))
        {
            current = current.Parent;
        }
        Assert.NotNull(current);
        return File.ReadAllText(Path.Combine(current!.FullName, Path.Combine(parts)));
    }
}
