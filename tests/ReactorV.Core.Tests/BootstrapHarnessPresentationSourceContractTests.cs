using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class BootstrapHarnessPresentationSourceContractTests
{
    [Fact]
    public void HarnessModelsTheSameTwoPhaseProviderCommitAsProduction()
    {
        var source = ReadHarness("SecondaryAppDomainHarness.cs");
        var pump = MethodRegion(
            source,
            "public int Pump()",
            "public void SetVisible(bool visible)");
        var finalize = MethodRegion(
            source,
            "private void TryFinalizeGbayPresentationHandoff()",
            "public int GbayStaleAcknowledgements");

        var response = pump.IndexOf("runtime.PostResponse(response);", StringComparison.Ordinal);
        var reveal = pump.IndexOf("runtime.SetVisible(true);", response, StringComparison.Ordinal);
        var exactCommit = finalize.IndexOf(
            "commitRuntime.IsProviderPresentationCommitted(",
            StringComparison.Ordinal);
        var retire = finalize.IndexOf(
            "bootstrapRuntime.RetireBootstrapSurface(hide: false);",
            StringComparison.Ordinal);

        Assert.True(response >= 0);
        Assert.True(reveal > response);
        Assert.True(exactCommit >= 0);
        Assert.True(retire > exactCommit);
        Assert.Contains("_runtime is IProviderPresentationCommitRuntime", source);
        Assert.Contains("TryFinalizeGbayPresentationHandoff();", pump);
    }

    [Fact]
    public void ReplacementCannotReuseThePreviousBrowserAcknowledgement()
    {
        var source = ReadHarness("GbayLifecycleHarness.cs");
        var expect = MethodRegion(
            source,
            "public void ExpectPresentation(string presentationId)",
            "public void HoldNextMenuGet()");

        Assert.Contains("ExpectedPresentation = presentationId;", expect);
        Assert.Contains("LastAcceptedPresentation = string.Empty;", expect);
    }

    [Fact]
    public void GbayRouteMatrixUsesOneShotReadOnlyInspectionActions()
    {
        var source = ReadHarness("GbayLifecycleHarness.cs");

        Assert.Contains("\"addons.inspect\"", source);
        Assert.Contains("\"diagnostics.inspect\"", source);
        Assert.Contains(
            "routeActions.All(action => router.InvocationCount(action) == 1)",
            source);
        Assert.DoesNotContain("\"addons.refresh\"", source);
        Assert.DoesNotContain("\"diagnostics.refresh\"", source);
        Assert.DoesNotContain(
            "router.InvocationCount(\"diagnostics-alpha\") >= 1",
            source);
    }

    [Fact]
    public void HandoffRequiresOneWayInitializerOrderingAndStableGbaySettle()
    {
        var source = ReadHarness("BootstrapHostHarness.cs");
        var handoff = MethodRegion(
            source,
            "private static HandoffObservation WaitForGbayHandoff(",
            "private static bool WaitForCloseWithoutStartupSurface(")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("var gbayPhaseEntered = false;", handoff);
        Assert.Contains(
            "GbayPresentationTimingPolicy.IsInitializerFramePermitted(",
            handoff);
        Assert.Contains("gbayPhaseEntered |= frame.IsGbay;", handoff);
        Assert.Contains("stableGbaySinceMilliseconds = null;", handoff);
        Assert.Contains(
            "GbayPresentationTimingPolicy.HasStableHandoffSettled(",
            handoff);

        var ready = handoff.IndexOf(
            "var exactPresentationReady =",
            StringComparison.Ordinal);
        var stableGate = handoff.IndexOf(
            "GbayPresentationTimingPolicy.HasStableHandoffSettled(",
            StringComparison.Ordinal);
        var success = handoff.IndexOf(
            "return new HandoffObservation(\n                        true,",
            StringComparison.Ordinal);

        Assert.True(ready >= 0);
        Assert.True(stableGate > ready);
        Assert.True(success > stableGate);
    }

    [Fact]
    public void Focus_recovery_is_before_provider_attachment_and_requalifies_pixels()
    {
        var source = ReadHarness("BootstrapHostHarness.cs");
        Assert.True(source.IndexOf("if (!PrepareInitializerForProvider(", StringComparison.Ordinal) <
            source.IndexOf("var started = proxy.StartForBootstrapGbayHandoff(", StringComparison.Ordinal));
        var prepare = MethodRegion(source, "private static bool PrepareInitializerForProvider(",
            "private static bool WaitForVisibleWithForeground(");
        Assert.Contains("stage=webview_visibility_dismissed reason=game_not_foreground", prepare);
        Assert.Contains("if (!focusLost || !TrySignalNamedEvent", prepare);
        Assert.Contains("WaitForStartupSurface(host, visualCapture,", prepare);
        Assert.Contains("observation.Qualified && observation.SinglePopup", prepare);
        Assert.Contains("PreloadHandoff.IsDefaultMenuIntentActive(processId)", prepare);
        Assert.Contains("focus lost after provider attachment; handoff not qualified", source);
        // Keep the visual gate strict even when the test desktop interferes.
        Assert.Contains("noTransparent &= frame.ChangedFraction > 0.10d;", source);
    }

    [Fact]
    public void Retained_webview_proof_requires_current_visible_accepted_and_committed_identity()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "ReactorV.Runtime", "OverlayWindow.cs"));
        var proof = MethodRegion(source, "public bool HasVisibleCommittedProviderPresentation(", "public void SignalRevealIngress()");
        Assert.Contains("_actualVisible && _desiredVisible", proof);
        Assert.Contains("Matches(_activeMenuPresentationId, presentationId)", proof);
        Assert.Contains("Matches(_acceptedMenuPresentationId, presentationId)", proof);
        Assert.Contains("Matches(_committedProviderInputPresentationId, presentationId)", proof);
    }

    private static string ReadHarness(string fileName) => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "src",
        "ReactorV.Harness",
        fileName));

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
            "Could not locate the ReactorV source root for the bootstrap harness contract.");
    }
}
