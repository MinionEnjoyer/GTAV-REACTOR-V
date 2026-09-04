using System.Collections.Generic;
using RageWebUI.Windowing;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class GameWindowSelectionPolicyTests
{
    [Fact]
    public void RejectsTransientMainWindowAndSelectsLargeRenderWindow()
    {
        var candidates = new List<GameWindowCandidate>
        {
            Candidate(1, 560, 68, preferred: true, foreground: true),
            Candidate(2, 2560, 1440, title: "Grand Theft Auto V Enhanced", className: "grcWindow"),
        };

        var selected = GameWindowSelectionPolicy.SelectBest(candidates, 42);

        Assert.NotNull(selected);
        Assert.Equal(2, selected!.Handle);
    }

    [Fact]
    public void PrefersGtaIdentityOverUnrelatedSameProcessWindow()
    {
        var candidates = new List<GameWindowCandidate>
        {
            Candidate(1, 3840, 2160, title: "Diagnostic surface"),
            Candidate(2, 1920, 1080, title: "Grand Theft Auto V", className: "grcWindow"),
        };

        var selected = GameWindowSelectionPolicy.SelectBest(candidates, 42);

        Assert.NotNull(selected);
        Assert.Equal(2, selected!.Handle);
    }

    [Fact]
    public void RejectsToolExcludedMinimizedAndForeignWindows()
    {
        var candidates = new List<GameWindowCandidate>
        {
            Candidate(1, 2560, 1440, toolWindow: true),
            Candidate(2, 2560, 1440, excluded: true),
            Candidate(3, 2560, 1440, minimized: true),
            Candidate(4, 2560, 1440, processId: 99),
            Candidate(5, 1280, 720),
        };

        var selected = GameWindowSelectionPolicy.SelectBest(candidates, 42);

        Assert.NotNull(selected);
        Assert.Equal(5, selected!.Handle);
    }

    [Fact]
    public void RejectsCandidatesWhenTargetProcessIsUnknown()
    {
        var selected = GameWindowSelectionPolicy.SelectBest(
            new[] { Candidate(1, 1920, 1080) },
            0);

        Assert.Null(selected);
    }

    [Theory]
    [InlineData(42u, 42u, true)]
    [InlineData(42u, 99u, false)]
    [InlineData(0u, 42u, false)]
    [InlineData(42u, 0u, false)]
    public void SameProcessForegroundFailsClosedForUnknownOrForeignProcesses(
        uint targetProcessId,
        uint foregroundProcessId,
        bool expected)
    {
        Assert.Equal(
            expected,
            GameWindowSelectionPolicy.IsSameProcessForeground(
                targetProcessId,
                foregroundProcessId));
    }

    [Theory]
    [InlineData(42u, 42u, 77u, false, true)]
    [InlineData(42u, 77u, 77u, true, true)]
    [InlineData(42u, 77u, 77u, false, false)]
    [InlineData(42u, 99u, 77u, true, false)]
    [InlineData(42u, 77u, 0u, true, false)]
    public void ReactorInteractionProcessIsTrustedOnlyDuringPointerCapture(
        uint targetProcessId,
        uint foregroundProcessId,
        uint interactionProcessId,
        bool interactionCaptureActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            GameWindowSelectionPolicy.IsInteractionForegroundProcess(
                targetProcessId,
                foregroundProcessId,
                interactionProcessId,
                interactionCaptureActive));
    }

    [Fact]
    public void ReusesEligibleForegroundPreferredWindow()
    {
        var candidate = Candidate(
            7,
            1920,
            1080,
            preferred: true,
            foreground: true);

        Assert.True(GameWindowSelectionPolicy.CanReusePreferred(candidate, 42));
    }

    [Theory]
    [InlineData(false, true, false, false, 42u)]
    [InlineData(true, false, false, false, 42u)]
    [InlineData(true, true, true, false, 42u)]
    [InlineData(true, true, false, true, 42u)]
    [InlineData(true, true, false, false, 99u)]
    public void PreferredFastPathFailsClosedWhenBindingIsNotAuthoritative(
        bool preferred,
        bool foreground,
        bool minimized,
        bool excluded,
        uint processId)
    {
        var candidate = Candidate(
            7,
            1920,
            1080,
            processId: processId,
            preferred: preferred,
            foreground: foreground,
            minimized: minimized,
            excluded: excluded);

        Assert.False(GameWindowSelectionPolicy.CanReusePreferred(candidate, 42));
    }

    private static GameWindowCandidate Candidate(
        long handle,
        int width,
        int height,
        uint processId = 42,
        bool preferred = false,
        bool foreground = false,
        bool toolWindow = false,
        bool excluded = false,
        bool minimized = false,
        string? title = null,
        string? className = null) =>
        new GameWindowCandidate
        {
            Handle = handle,
            ProcessId = processId,
            ClientWidth = width,
            ClientHeight = height,
            Visible = true,
            Minimized = minimized,
            ToolWindow = toolWindow,
            Excluded = excluded,
            Foreground = foreground,
            Preferred = preferred,
            Title = title,
            ClassName = className,
        };
}
