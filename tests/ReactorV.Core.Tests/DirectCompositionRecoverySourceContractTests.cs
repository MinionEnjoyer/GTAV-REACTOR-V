using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class DirectCompositionRecoverySourceContractTests
{
    [Fact]
    public void HealthyTargetRecoveryRebindsInsteadOfCreatingASecondHwndTarget()
    {
        var host = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "CompositionWebViewHost.cs");
        var recovery = Region(
            host,
            "internal CompositionDeviceRecoveryResult ForceRecreateCompositionDevice()",
            "internal RootVisualRebindResult RebindRootVisual()");

        Assert.Contains(
            "ApplyCompositionMutation(\n                CompositionMutation.RootVisualRebind)",
            recovery.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("ExistingTargetRebound", recovery);
        Assert.DoesNotContain("DirectCompositionDevice.Create", recovery);
        Assert.DoesNotContain("CreateTargetForHwnd", recovery);
    }

    [Fact]
    public void LostDeviceRetiresTheOldTargetBeforeCreatingExactlyOneReplacement()
    {
        var host = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "CompositionWebViewHost.cs");
        var recovery = Region(
            host,
            "private CompositionDeviceRecoveryResult RecoverCompositionDeviceCore(",
            "internal bool SendMouseInput(");

        var clearPublishedReference = recovery.IndexOf(
            "_composition = null;",
            StringComparison.Ordinal);
        var retire = recovery.IndexOf(
            "retired.RetireForReplacement(_controller)",
            StringComparison.Ordinal);
        var replacement = recovery.IndexOf(
            "DirectCompositionDevice.Create(_owner.Handle)",
            StringComparison.Ordinal);

        Assert.True(clearPublishedReference >= 0);
        Assert.True(retire > clearPublishedReference);
        Assert.True(replacement > retire);
        Assert.Equal(1, Count(recovery, "DirectCompositionDevice.Create("));
        Assert.Contains("if (!retirement.ReplacementSafe)", recovery);
        Assert.Contains("TargetRetiredAndRecreated", recovery);
    }

    [Fact]
    public void RetirementDetachesWebViewRootCommitsAndReleasesBeforeReplacement()
    {
        var host = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "CompositionWebViewHost.cs");
        var retirement = Region(
            host,
            "internal CompositionTargetRetirement RetireForReplacement(",
            "internal readonly struct CompositionTargetRetirement");
        var disposal = Region(
            host,
            "private int DisposeCore()",
            "private static void Release(object? value)");

        Assert.Contains("controller.RootVisualTarget = null;", retirement);
        Assert.Contains("var retirementHResult = DisposeCore();", retirement);
        Assert.Contains("ReplacementBlocked", retirement);

        var detachTarget = disposal.IndexOf("target.SetRoot(null)", StringComparison.Ordinal);
        var commit = disposal.IndexOf("_device.Commit()", StringComparison.Ordinal);
        var releaseRoot = disposal.IndexOf("Release(_root)", StringComparison.Ordinal);
        var releaseTarget = disposal.IndexOf("Release(target)", StringComparison.Ordinal);
        var releaseDevice = disposal.IndexOf("Release(_device)", StringComparison.Ordinal);
        Assert.True(detachTarget >= 0);
        Assert.True(commit > detachTarget);
        Assert.True(releaseRoot > commit);
        Assert.True(releaseTarget > releaseRoot);
        Assert.True(releaseDevice > releaseTarget);
    }

    [Fact]
    public void GbayStressHarnessRejectsAlreadyComposedRecoveryRegressions()
    {
        var harness = ReadRepositoryFile(
            "src", "ReactorV.Harness", "GbayLifecycleHarness.cs");

        Assert.Contains("rootRebindCount", harness);
        Assert.Contains("0x88980800", harness);
        Assert.Contains("targetReuseQualified", harness);
        Assert.Contains("targetReuseQualified &&", harness);
        Assert.Contains("targetReuse=", harness);
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
        return File.ReadAllText(Path.Combine(
            current!.FullName,
            Path.Combine(parts)));
    }
}
