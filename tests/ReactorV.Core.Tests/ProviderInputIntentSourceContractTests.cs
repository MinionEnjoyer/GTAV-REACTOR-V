using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ProviderInputIntentSourceContractTests
{
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
    public void CompositionFallbackConsumesExactIntentOnlyAfterPassiveCommit()
    {
        var overlay = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayWindow.cs");
        var fallback = Region(
            overlay,
            "private bool TryCompleteExplicitUserIntentReveal(",
            "private void CompleteQualifiedReveal(");

        Assert.Contains("OverlayTransferOwner.Provider", fallback);
        Assert.Contains("CompositionCommittedVisible", fallback);
        Assert.Contains("_providerInputIntentGate.TryConsume(", fallback);
        Assert.Contains("transferIdentity.PresentationId", fallback);
        Assert.Contains("ExplicitUserIntentAuthorized", fallback);
        Assert.Contains("input_enabled=True close_contract=f9-or-escape", fallback);
        Assert.DoesNotContain("DesktopPresentationVerified", fallback);
    }

    [Fact]
    public void ExplicitInputHasBoundedFailClosedLeaseAndReconnectResetsAuthority()
    {
        var overlay = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayWindow.cs");

        Assert.Contains("ExplicitUserIntentInputLeaseMilliseconds", overlay);
        Assert.Contains("BeginExplicitUserIntentInputLease(transferIdentity)", overlay);
        Assert.Contains("webview_provider_input_intent_lease_expired", overlay);
        Assert.Contains("action=fail-closed-hide", overlay);
        Assert.Contains("_desiredVisible = false;", overlay);
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
        Assert.Contains("_overlay.SetVisible(false);", close);
        Assert.Contains("IAuthoritativeHostSurfaceRuntime", runtime);
        Assert.Contains("HasAuthoritativeHostSurfaceBoundary", runtime);
        Assert.Contains("IAuthoritativeHostSurfaceRuntime", bootstrap);
        Assert.Contains(
            "public bool HasAuthoritativeHostSurfaceBoundary => true;",
            bootstrap);
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
            Path.Combine(current!.FullName, Path.Combine(parts)));
    }
}
