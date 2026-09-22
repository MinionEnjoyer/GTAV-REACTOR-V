using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ProviderInputIntentSourceContractTests
{
    [Fact]
    public void FailedOrHiddenPresentationRevokesItsExactInputEvenAfterRegistryRemoval()
    {
        var script = ReadRepositoryFile("src", "ReactorV.Script", "RageWebUiScript.cs");
        var abort = Region(script, "private bool AbortPresentationTransfer(", "private string CurrentHostSurface");
        Assert.Contains("RevokeProviderPresentationInputIntent(exactPresentationId, reason);", abort);
        Assert.True(abort.IndexOf("RevokeProviderPresentationInputIntent(", StringComparison.Ordinal) <
            abort.IndexOf("if (dismissal == null)", StringComparison.Ordinal));
        var pending = Region(script, "private void CancelPendingProviderPresentation(", "private bool AbortPresentationTransfer(");
        Assert.Contains("RevokeProviderPresentationInputIntent(presentationId, reason);", pending);
        var publish = Region(script, "private void PublishActiveMenuDismissed(", "private void TraceRuntime(");
        Assert.Contains("RevokeProviderPresentationInputIntent(expectedPresentationId, reason);", publish);
        Assert.Contains("RevokeProviderPresentationInputIntent(dismissal.Value<string>(\"presentationId\"), reason);", publish);
    }

    [Fact]
    public void ExactCleanupCannotCancelPendingInputOrMutateGlobalMenuState()
    {
        var script = ReadRepositoryFile("src", "ReactorV.Script", "RageWebUiScript.cs");
        var cleanup = Region(script, "private void RevokeProviderPresentationInputIntent(",
            "private void ExpirePendingProviderInputIntent(");
        Assert.Contains("ProviderPresentationInputCleanup.TryRevoke(", cleanup);
        Assert.Contains("ref _boundProviderInputIntentEpoch", cleanup);
        Assert.Contains("ref _boundProviderInputIntentPresentationId", cleanup);
        Assert.Contains("ref _userIntentFallbackPresentationId", cleanup);
        Assert.Contains("if (revokedEpoch > 0", cleanup);
        Assert.Contains("CancelProviderInputIntent(Process.GetCurrentProcess().Id, revokedEpoch)", cleanup);
        Assert.DoesNotContain("CancelProviderInputIntent();", cleanup);
        foreach (var forbidden in new[] { "_pendingProviderInputIntent", "_inputMode =", "_overlayRequestedVisible =",
            "SetVisible(", "CloseOverlay(", "PublishActiveMenuDismissed(", "_menuInputLease", "PostCoreEvent(" })
            Assert.DoesNotContain(forbidden, cleanup);
    }

    [Fact]
    public void DelayedDefaultOwnerCloseEdgeRespectsTheExistingReleaseLease()
    {
        var script = ReadRepositoryFile("src", "ReactorV.Script", "RageWebUiScript.cs");
        var key = Region(script, "private void OnKeyDown(", "private void ArmProviderInputIntent(");
        var resolution = Region(key, "var managedF9Disposition =", "if (managedF9Disposition ==");
        Assert.Contains("_menuInputLease.SuppressGameInput", resolution);
    }

    [Fact]
    public void UnboundIntentExpiryIsServicedWithoutAnotherPresentation()
    {
        var script = ReadRepositoryFile("src", "ReactorV.Script", "RageWebUiScript.cs");
        var tick = Region(script, "private void OnTickCore()", "private void OnKeyDown(");
        var key = Region(script, "private void OnKeyDown(", "private void ArmProviderInputIntent(");
        Assert.Contains("ExpirePendingProviderInputIntent(", tick);
        Assert.Contains("ExpirePendingProviderInputIntent(", key);
        Assert.True(tick.IndexOf("ExpirePendingProviderInputIntent(", StringComparison.Ordinal) <
            tick.IndexOf("DrainMenuPresentations();", StringComparison.Ordinal));
        Assert.True(key.IndexOf("ExpirePendingProviderInputIntent(", StringComparison.Ordinal) <
            key.IndexOf("var defaultOwnerPresentationOrIntentActive =", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyIntentEscapeAndExpiryCannotMutateMenuVisibility()
    {
        var script = ReadRepositoryFile("src", "ReactorV.Script", "RageWebUiScript.cs");
        var emptyEscape = Region(script,
            "if (escapeDisposition == ProviderIntentEscapeDisposition.CancelUnboundIntent)",
            "if (!_storyModeReady)");
        var expiry = Region(script, "private void ExpirePendingProviderInputIntent(", "private void CloseOverlay(");
        foreach (var region in new[] { emptyEscape, expiry }) {
            Assert.Contains("CancelProviderInputIntent();", region);
            Assert.DoesNotContain("CloseOverlay(", region);
            Assert.DoesNotContain("SetVisible(", region);
            Assert.DoesNotContain("PublishActiveMenuDismissed(", region);
        }
        Assert.DoesNotContain("SET_PAUSE_MENU_ACTIVE", script);
        Assert.DoesNotContain("ENABLE_ALL_CONTROL_ACTIONS", script);
        Assert.Contains("evidence=managed-keydown-not-frontend-delivery", script);
        Assert.Contains("inputElapsedMilliseconds + 250", script);
    }

    [Fact]
    public void DefaultOwnerRoutesPhysicalF9BeforeGenericCloseAndBindsOnlyThatOwner()
    {
        var script = ReadRepositoryFile(
            "src", "ReactorV.Script", "RageWebUiScript.cs");
        var keyHandler = Region(
            script,
            "private void OnKeyDown(",
            "private void ArmProviderInputIntent(");
        var bind = Region(
            script,
            "private void TryBindProviderInputIntent(",
            "private void CancelProviderInputIntent(");
        var ownerActivity = Region(
            keyHandler,
            "var defaultOwnerPresentationOrIntentActive =",
            "var managedF9Disposition =");

        var resolveOwner = keyHandler.IndexOf(
            "ResolveManagedF9Edge(",
            StringComparison.Ordinal);
        var yieldToOwner = keyHandler.IndexOf(
            "ManagedF9EdgeDisposition.YieldToDefaultOwner",
            StringComparison.Ordinal);
        var genericClose = keyHandler.IndexOf(
            "CloseOverlay(\"toggle\")",
            StringComparison.Ordinal);
        var storyReadyGate = keyHandler.IndexOf(
            "if (!_storyModeReady)",
            StringComparison.Ordinal);
        var debounceGate = keyHandler.IndexOf(
            "Game.GameTime < _nextToggleAt",
            StringComparison.Ordinal);
        var armOwnerIntent = keyHandler.IndexOf(
            "ManagedF9EdgeDisposition.ArmDefaultOwnerInputIntent",
            StringComparison.Ordinal);
        Assert.True(resolveOwner >= 0);
        Assert.True(yieldToOwner > resolveOwner);
        Assert.True(genericClose > yieldToOwner);
        Assert.True(storyReadyGate > genericClose);
        Assert.True(debounceGate > storyReadyGate);
        Assert.True(armOwnerIntent > debounceGate);
        Assert.Contains("ManagedF9EdgeDisposition.GenericToggle", keyHandler);
        Assert.Contains("action=yield-no-mutation", keyHandler);
        Assert.Contains("CloseOverlay(\"toggle\")", keyHandler);
        Assert.Contains("ArmProviderInputIntent();", keyHandler);
        Assert.Contains("escape-user-intent-fallback", keyHandler);
        Assert.DoesNotContain("_overlay.IsVisible", ownerActivity);
        Assert.Contains("ExtensionHasCapability(", bind);
        Assert.Contains("DefaultF9MenuOwner", bind);
        Assert.Contains("BindProviderInputIntent(", bind);
        Assert.Contains("_boundProviderInputIntentEpoch", keyHandler);
    }

    [Fact]
    public void DispatchSamplesTrustedPhysicalStateAfterRegistryAcceptanceBeforeBinding()
    {
        var script = ReadRepositoryFile("src", "ReactorV.Script", "RageWebUiScript.cs");
        var dispatch = Region(script, "private void DrainMenuPresentations()",
            "private void TryAdvancePendingProviderPresentation(");
        Assert.True(dispatch.IndexOf("ShouldArmDefaultOwnerAtDispatch(", StringComparison.Ordinal) >
            dispatch.IndexOf("MarkMenuPresentationActive(", StringComparison.Ordinal));
        Assert.True(dispatch.IndexOf("TryBindProviderInputIntent(", StringComparison.Ordinal) >
            dispatch.IndexOf("ShouldArmDefaultOwnerAtDispatch(", StringComparison.Ordinal));
        Assert.Contains("NativeMethods.IsPhysicalF9Down()", dispatch);
        Assert.Contains("NativeMethods.IsGameForeground(_gtaWindow)", dispatch);
        Assert.Contains("PreloadHandoff.ManagedOwnsF9", dispatch);
        Assert.Contains("hasSupersededPresentation: superseded != null", dispatch);
        Assert.Contains("_pendingProviderInputIntentEpoch > 0", dispatch);
        Assert.Contains("source=physical-f9-held", dispatch);
    }

    [Fact]
    public void PipeCarriesProcessEpochAndExactPresentationWithoutBroadAuthority()
    {
        var client = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "BootstrapOverlayRuntime.cs");
        var server = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "BootstrapOverlayServer.cs");

        Assert.Contains("provider_input_intent_arm", client);
        Assert.Contains("provider_input_intent_bind", client);
        Assert.Contains("provider_input_intent_cancel", client);
        Assert.Contains("[\"pid\"] = token.ProcessId", client);
        Assert.Contains("[\"epoch\"] = token.Epoch", client);
        Assert.Contains("[\"presentationId\"] = presentationId", client);
        Assert.Contains("message.Value<int>(\"pid\") == _gtaProcessId", server);
        Assert.Contains("ProviderPresentationCommitContract.IsValidPresentationId(", server);
        Assert.Contains("ProviderInputIntentBindRequested", server);
        Assert.Contains("providerPresentationUserIntent", server);
    }

    [Fact]
    public void PhysicalIntentCannotReplaceDesktopProof()
    {
        var overlay = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayWindow.cs");
        var probe = Region(
            overlay,
            "private async void BeginDesktopPresentationCommit(",
            "private void CompleteQualifiedReveal(");
        Assert.Contains("HandleDesktopPresentationFailure(", probe);
        Assert.Contains("DesktopPresentationVerified", probe);
        Assert.DoesNotContain("TryCompleteExplicitUserIntentReveal", overlay);
        Assert.DoesNotContain("OverlayTransferPhase.ExplicitUserIntentAuthorized", overlay);
        Assert.Contains("string presentationId) => false;", overlay);
        Assert.True(probe.IndexOf("_providerInputIntentGate.TryConsume(", StringComparison.Ordinal) >
            probe.IndexOf("_desktopPresentationPixelsVerified = true;", StringComparison.Ordinal));
    }

    [Fact]
    public void BothRevealAndInputCommitRequireDesktopProofAndReconnectResetsAuthority()
    {
        var overlay = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayWindow.cs");

        var reveal = Region(overlay, "private void CompleteQualifiedReveal(",
            "private void HandleDesktopPresentationFailure(");
        var commit = Region(overlay, "private void CommitProviderInputAfterRevealFence()",
            "private void PublishProviderPresentationCommitted(");
        Assert.Contains("!_desktopPresentationPixelsVerified", reveal);
        Assert.Contains("!_desktopPresentationPixelsVerified", commit);
        Assert.DoesNotContain("ExplicitUserIntentInputLeaseMilliseconds", overlay);
        Assert.Contains("_providerInputIntentGate.BeginProviderSession(", overlay);
        Assert.Contains("_providerInputIntentGate.RevokeProviderSession(", overlay);
    }

    [Fact]
    public void AuthoritativeBootstrapCloseEmitsOneHostSurfaceBoundary()
    {
        var script = ReadRepositoryFile(
            "src", "ReactorV.Script", "RageWebUiScript.cs");
        var close = Region(
            script,
            "private void CloseOverlay(",
            "private void ShowOverlay(");
        var runtime = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayRuntime.cs");
        var bootstrap = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "BootstrapOverlayRuntime.cs");

        Assert.Contains("if (!HasAuthoritativeHostSurfaceBoundary())", close);
        Assert.Contains("_overlay.PostEvent(\n                    \"host.surface\"", close);
        Assert.Contains("[\"mode\"] = HostSurfaceMode.None", close);
        Assert.Contains("[\"generation\"] = NextHostSurfaceGeneration()", close);
        Assert.Contains("_overlay.SetVisible(false);", close);
        Assert.Contains("IAuthoritativeHostSurfaceRuntime", runtime);
        Assert.Contains("HasAuthoritativeHostSurfaceBoundary", runtime);
        Assert.Contains("IAuthoritativeHostSurfaceRuntime", bootstrap);
        Assert.Contains(
            "public bool HasAuthoritativeHostSurfaceBoundary => true;",
            bootstrap);
    }

    [Fact]
    public void EveryNonAuthoritativeHostSurfaceResetCarriesTheMonotonicGeneration()
    {
        var script = ReadRepositoryFile(
            "src", "ReactorV.Script", "RageWebUiScript.cs");

        // Windowed/direct renderers have no external bootstrap publisher.
        // Their close, abort, and bootstrap-retirement resets must therefore
        // cross OverlayWindow's high-watermark guard as new messages rather
        // than relying on an unversioned compatibility escape hatch.
        Assert.DoesNotContain(
            "new JObject { [\"mode\"] = \"none\" }",
            script);
        Assert.Equal(
            4,
            CountOccurrences(
                script,
                "[\"mode\"] = HostSurfaceMode.None"));
        Assert.Equal(
            5,
            CountOccurrences(
                script,
                "[\"generation\"] = NextHostSurfaceGeneration()"));
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string Region(string source, string startMarker, string endMarker)
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
        return File.ReadAllText(
            Path.Combine(current!.FullName, Path.Combine(parts))).Replace("\r\n", "\n");
    }
}
