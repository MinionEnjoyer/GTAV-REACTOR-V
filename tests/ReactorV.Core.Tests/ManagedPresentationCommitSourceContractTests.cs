using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ManagedPresentationCommitSourceContractTests
{
    [Fact]
    public void BrowserPreparedCallbackCannotAuthorizeInputOrRetireTheInitializer()
    {
        var script = ReadScript();
        var prepared = MethodRegion(
            script,
            "private bool MarkMenuPresentationReady(string presentationId)",
            "private void TryAdvancePendingProviderPresentation(");

        Assert.Contains("ReactorHostApi.CanMarkMenuPresentationReady(presentationId)", prepared);
        Assert.Contains("_providerPresentationCommitGate.Begin(", prepared);
        Assert.Contains("_providerRevealAfterCommitPresentationId", prepared);
        Assert.DoesNotContain("ShowOverlay(\"extension-menu\")", prepared);
        Assert.DoesNotContain("ReactorHostApi.MarkMenuPresentationReady(presentationId)", prepared);
        Assert.DoesNotContain("MenuPresentationPolicy.ReadyPresentationInputMode", prepared);
        Assert.DoesNotContain("RetireBootstrapSurfaceForPresentation()", prepared);
    }

    [Fact]
    public void ExactNativeCommitPrecedesRegistryReadinessInputAndInitializerRetirement()
    {
        var script = ReadScript();
        var commit = MethodRegion(
            script,
            "private void TryAdvancePendingProviderPresentation(",
            "private void CancelPendingProviderPresentation(");

        var exactNativeCommit = commit.IndexOf(
            "commitRuntime.IsProviderPresentationCommitted(presentationId)",
            StringComparison.Ordinal);
        var exactGate = commit.IndexOf(
            "_providerPresentationCommitGate.TryCommit(",
            StringComparison.Ordinal);
        var registryReady = commit.IndexOf(
            "ReactorHostApi.MarkMenuPresentationReady(presentationId)",
            StringComparison.Ordinal);
        var readyInput = commit.IndexOf(
            "MenuPresentationPolicy.ReadyPresentationInputMode",
            StringComparison.Ordinal);
        var reveal = commit.IndexOf(
            "ShowOverlay(\"extension-menu\")",
            StringComparison.Ordinal);
        var retireInitializer = commit.IndexOf(
            "RetireBootstrapSurfaceForPresentation()",
            StringComparison.Ordinal);

        Assert.True(exactNativeCommit >= 0);
        Assert.True(exactGate > exactNativeCommit);
        Assert.True(registryReady > exactGate);
        Assert.True(readyInput > registryReady);
        Assert.True(reveal > readyInput);
        Assert.True(retireInitializer > reveal);
    }

    [Fact]
    public void HiddenColdReopenConsumesExactProviderCommitBeforeReveal()
    {
        var script = ReadScript();
        var commit = MethodRegion(
            script,
            "private void TryAdvancePendingProviderPresentation(",
            "private void CancelPendingProviderPresentation(");

        var pendingMode = commit.IndexOf(
            "MenuPresentationPolicy.PendingPresentationInputMode",
            StringComparison.Ordinal);
        var exactNativeCommit = commit.IndexOf(
            "commitRuntime.IsProviderPresentationCommitted(presentationId)",
            StringComparison.Ordinal);
        var registryReady = commit.IndexOf(
            "ReactorHostApi.MarkMenuPresentationReady(presentationId)",
            StringComparison.Ordinal);
        var reveal = commit.IndexOf(
            "ShowOverlay(\"extension-menu\")",
            StringComparison.Ordinal);

        Assert.True(pendingMode >= 0);
        Assert.True(exactNativeCommit > pendingMode);
        Assert.True(registryReady > exactNativeCommit);
        Assert.True(reveal > registryReady);
        Assert.DoesNotContain(
            "if (!_overlayRequestedVisible ||",
            commit,
            StringComparison.Ordinal);
        Assert.Contains(
            "_providerRevealAfterCommitPresentationId",
            commit,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TickAdvancesExactProviderCommitBeforeInputIsPumped()
    {
        var script = ReadScript();
        var tick = MethodRegion(
            script,
            "private void OnTick(object sender, EventArgs args)",
            "private void OnKeyDown(object sender, KeyEventArgs args)");

        var presentations = tick.IndexOf("DrainMenuPresentations();", StringComparison.Ordinal);
        var advance = tick.IndexOf("TryAdvancePendingProviderPresentation(", StringComparison.Ordinal);
        var pumpInput = tick.IndexOf("_overlay.PumpInput();", StringComparison.Ordinal);

        Assert.True(presentations >= 0);
        Assert.True(advance > presentations);
        Assert.True(pumpInput > advance);
    }

    private static string ReadScript() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "src",
        "ReactorV.Script",
        "RageWebUiScript.cs"));

    private static string MethodRegion(
        string source,
        string signature,
        string nextSignature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        var end = source.IndexOf(nextSignature, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method signature '{signature}'.");
        Assert.True(end > start, $"Could not find method boundary '{nextSignature}'.");
        return source.Substring(start, end - start);
    }

    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate != null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "ReactorV.json")) &&
                Directory.Exists(Path.Combine(candidate.FullName, "src")))
                return candidate.FullName;
            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the ReactorV source root for the managed presentation contract.");
    }
}
