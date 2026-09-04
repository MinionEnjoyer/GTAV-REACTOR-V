using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

/// <summary>
/// Pins the failure observed in Enhanced when the persistent external
/// preloader was ready long before ScriptHookVDotNet, but an inconclusive
/// desktop witness (followed by DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED)
/// withdrew the IPC Ready event.  Presentation health may fail closed without
/// invalidating the warmed browser or the late-provider transport.
/// </summary>
public sealed class EnhancedLateProviderHandoffRegressionTests
{
    [Fact]
    public void PersistentHostDoesNotBindPresentationFailureToTransportReadiness()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs");
        var persistentHostConstructor = Region(
            program,
            "_hostWindow = new OverlayWindow(",
            "_hostWindow.ProviderPresentationCommitted +=");

        Assert.Contains("OnContentReady", persistentHostConstructor);
        Assert.Contains(
            "_hostServer.MarkContentUnavailable",
            persistentHostConstructor);

        var overlay = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayWindow.cs");
        Assert.Contains("Action browserContentUnavailable", overlay);
        Assert.Contains(
            "_browserContentUnavailable = browserContentUnavailable;",
            overlay);
        var desktopFailure = Region(
            overlay,
            "private void HandleDesktopPresentationFailure(",
            "private bool KeepCompositionQualifiedPresentationVisible(");
        Assert.DoesNotContain("_browserContentUnavailable();", desktopFailure);
        Assert.DoesNotContain(
            "InvalidateBrowserContentReadiness(",
            desktopFailure);
        Assert.Contains(
            "PreserveBrowserContentReadinessAfterPresentationFailure(",
            desktopFailure);

        var compositionFailure = Region(
            overlay,
            "private void FailCompositionReveal(",
            "private void ApplyOverlayTopMost(");
        Assert.DoesNotContain(
            "_browserContentUnavailable();",
            compositionFailure);
        Assert.DoesNotContain(
            "InvalidateBrowserContentReadiness(",
            compositionFailure);
        Assert.Contains(
            "PreserveBrowserContentReadinessAfterPresentationFailure(",
            compositionFailure);
    }

    [Fact]
    public void CompositionRecoveryCannotBlindlyCreateASecondHwndTarget()
    {
        var composition = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "CompositionWebViewHost.cs");
        var healthyRecovery = Region(
            composition,
            "internal CompositionDeviceRecoveryResult ForceRecreateCompositionDevice()",
            "internal RootVisualRebindResult RebindRootVisual()");
        Assert.Contains("CompositionMutation.RootVisualRebind", healthyRecovery);
        Assert.Contains("ExistingTargetRebound", healthyRecovery);
        Assert.DoesNotContain("DirectCompositionDevice.Create", healthyRecovery);

        var recovery = Region(
            composition,
            "private CompositionDeviceRecoveryResult RecoverCompositionDeviceCore(",
            "/// <summary>\n        /// Forwards GTA's normalized cursor state");
        var unpublished = recovery.IndexOf(
            "_composition = null;",
            StringComparison.Ordinal);
        var retired = recovery.IndexOf(
            "retired.RetireForReplacement(_controller)",
            StringComparison.Ordinal);
        var replacement = recovery.IndexOf(
            "DirectCompositionDevice.Create(_owner.Handle)",
            StringComparison.Ordinal);
        Assert.True(unpublished >= 0);
        Assert.True(retired > unpublished);
        Assert.True(replacement > retired);
        Assert.Contains("if (!retirement.ReplacementSafe)", recovery);
        Assert.Contains("TargetRetiredAndRecreated", recovery);
        Assert.Equal(1, Count(recovery, "DirectCompositionDevice.Create("));
    }

    [Fact]
    public void PackagedHarnessRetainsTransportAcrossLateEnhancedProviderHandoff()
    {
        var harness = ReadRepositoryFile(
            "src", "ReactorV.Harness", "BootstrapHostHarness.cs");

        AssertOrdered(
            harness,
            "var aboutVisible =",
            "var startupObservation =",
            "var preProviderTransportReady =",
            "var domain = AppDomain.CreateDomain(",
            "var connected = preProviderTransportReady && WaitForNamedEvent(",
            "var runtimeReadyInitializerPreserved =",
            "var managedOwnershipReleased = runtimeReadyInitializerPreserved &&",
            "f9OwnershipReleased.Set();",
            "var gbayPosted = providerMenuReady &&",
            "var handoffObservation = intentConsumedOnce");
        Assert.Contains(
            "preProviderTransportReady &&\n                    started && connected",
            harness.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains(
            "$\"preProviderTransportReady={preProviderTransportReady} \"",
            harness);

        var packageBuild = ReadRepositoryFile("build-package.ps1");
        Assert.Contains("'preProviderTransportReady=True'", packageBuild);
        Assert.Contains("'targetReuse=True'", packageBuild);
    }

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

    private static int Count(string source, string value)
    {
        var count = 0;
        var cursor = 0;
        while ((cursor = source.IndexOf(value, cursor, StringComparison.Ordinal)) >= 0)
        {
            count++;
            cursor += value.Length;
        }
        return count;
    }

    private static string Region(string source, string startMarker, string endMarker)
    {
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = normalized.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = normalized.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return normalized.Substring(start, end - start);
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
