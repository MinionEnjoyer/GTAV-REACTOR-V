using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class HarnessCacheLifecycleSourceContractTests
{
    [Fact]
    public void Harness_removes_success_cache_and_keeps_only_bounded_failed_evidence()
    {
        var source = ReadRepositoryFile(
            "src",
            "ReactorV.Harness",
            "Program.cs");

        Assert.Contains("finally", source, StringComparison.Ordinal);
        Assert.Contains(
            "HarnessRunDirectory.CompleteCurrentRun(exitCode == 0);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryDeleteOwnedRunDirectory(RunRoot);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FailureMarkerFileName = \".failed-run\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaximumRetainedFailedRuns = 3",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".Skip(MaximumRetainedFailedRuns)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOwnedRunDirectory(runDirectory)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Directory.Delete(RunsRoot, recursive: true)",
            source,
            StringComparison.Ordinal);
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
