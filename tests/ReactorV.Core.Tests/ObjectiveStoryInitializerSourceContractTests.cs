using System;
using System.IO;
using ReactorV.BootstrapHost;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ObjectiveStoryInitializerSourceContractTests
{
    [Fact]
    public void ObjectivePromotionShowsOnlyTheInitializerWithoutADefaultMenuIntent()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ReactorV.Preloader",
            "Program.cs"));
        var promotion = SourceRegion(
            source,
            "if (signalAction == BootstrapHostSignalAction.PromoteInitializer)",
            "if (signalAction != BootstrapHostSignalAction.ToggleInitializer)");

        int request = promotion.IndexOf(
            "RequestHostSurface(window, \"initializing\", true);",
            StringComparison.Ordinal);
        int releaseOpeningEdge = promotion.IndexOf(
            "_initializerOpeningEdgePending = false;",
            StringComparison.Ordinal);

        Assert.True(request >= 0);
        Assert.True(releaseOpeningEdge > request);
        Assert.DoesNotContain(
            "ArmDefaultMenuIntent();",
            promotion,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_initializerOpeningEdgePending = true;",
            promotion,
            StringComparison.Ordinal);

        // The source boundary supplies false to this policy after an
        // objective (non-keyboard) promotion, leaving the next real F9 free
        // to perform the ordinary open -> close transition.
        Assert.False(
            HostSurfaceIntentPolicy.ShouldConsumeOpeningInitializerToggle(
                openingEdgePending: false,
                initializerLogicallyOpen: true));
        Assert.Equal(
            BootstrapSurfaceToggleAction.Close,
            HostSurfaceIntentPolicy.EvaluateBootstrapToggle(
                logicallyOpen: true));
    }

    [Fact]
    public void ExplicitF9PathsStillArmTheDefaultMenuIntent()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ReactorV.Preloader",
            "Program.cs"));
        var toggle = SourceRegion(
            source,
            "if (signalAction != BootstrapHostSignalAction.ToggleInitializer)",
            "private bool IsHostSurfaceLogicallyOpen");

        Assert.Contains(
            "ArmDefaultMenuIntent();",
            toggle,
            StringComparison.Ordinal);
        Assert.Contains(
            "destination=default-menu",
            toggle,
            StringComparison.Ordinal);
    }

    private static string SourceRegion(
        string source,
        string startMarker,
        string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find source marker '{startMarker}'.");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find source marker '{endMarker}'.");
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
            "Could not locate the ReactorV source root.");
    }
}
