using System;
using System.IO;
using System.Threading;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class StartupTraceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ReactorV.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void WritesAggregateAndPerProcessTimelineWithElapsedStageData()
    {
        Assert.True(StartupTrace.Write(
            _root,
            "reactorv-runtime.log",
            "script",
            "construction_begin",
            "domain=test"));

        var aggregate = File.ReadAllText(Path.Combine(_root, "reactorv-runtime.log"));
        var session = File.ReadAllText(Path.Combine(_root, StartupTrace.SessionFileName));

        Assert.Contains($"session={StartupTrace.SessionId}", aggregate);
        Assert.Contains("elapsed_ms=", aggregate);
        Assert.Contains("source=script stage=construction_begin domain=test", aggregate);
        Assert.Equal(aggregate, session);
    }

    [Fact]
    public void AppendsWhileAReaderAllowsSharedWrites()
    {
        Directory.CreateDirectory(_root);
        var aggregatePath = Path.Combine(_root, "reactorv-runtime.log");
        File.WriteAllText(aggregatePath, "existing" + Environment.NewLine);
        using var reader = new FileStream(
            aggregatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        Assert.True(StartupTrace.Write(
            _root,
            "reactorv-runtime.log",
            "runtime",
            "overlay_start"));

        Assert.Contains("stage=overlay_start", File.ReadAllText(aggregatePath));
    }

    [Fact]
    public void PreservesSessionTraceWhenAggregateLogIsExclusivelyLocked()
    {
        Directory.CreateDirectory(_root);
        var aggregatePath = Path.Combine(_root, "reactorv-runtime.log");
        using var locked = new FileStream(
            aggregatePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.True(StartupTrace.Write(
            _root,
            "reactorv-runtime.log",
            "webview",
            "content_ready",
            "duration_ms=125.0"));

        var session = File.ReadAllText(Path.Combine(_root, StartupTrace.SessionFileName));
        Assert.Contains("source=webview stage=content_ready duration_ms=125.0", session);
    }

    [Fact]
    public void CreatesUniqueFallbackWhenBothNormalLogsAreLocked()
    {
        Directory.CreateDirectory(_root);
        var aggregatePath = Path.Combine(_root, "reactorv-runtime.log");
        var sessionPath = Path.Combine(_root, StartupTrace.SessionFileName);
        using var aggregateLock = new FileStream(
            aggregatePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var sessionLock = new FileStream(
            sessionPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.True(StartupTrace.Write(
            _root,
            "reactorv-runtime.log",
            "script",
            "construction_failed"));

        var fallbacks = Directory.GetFiles(
            _root,
            $"reactorv-session-{StartupTrace.SessionId}-fallback-*.log");
        var fallback = Assert.Single(fallbacks);
        Assert.Contains("stage=construction_failed", File.ReadAllText(fallback));
    }

    [Fact]
    public void PrunesOnlyOldSessionLogsBeyondTheBoundedNewestSet()
    {
        Directory.CreateDirectory(_root);
        var now = DateTime.UtcNow;
        for (var index = 0;
            index < StartupTrace.MaximumRetainedSessionFiles + 12;
            index++)
        {
            var path = Path.Combine(
                _root,
                $"reactorv-session-fixture-{index:D3}.log");
            File.WriteAllText(path, "fixture");
            File.SetLastWriteTimeUtc(path, now - TimeSpan.FromHours(index + 1));
        }
        var recent = Path.Combine(_root, "reactorv-session-recent.log");
        File.WriteAllText(recent, "active");
        File.SetLastWriteTimeUtc(recent, now);

        var removed = StartupTrace.PruneSessionLogs(_root, now);

        Assert.Equal(13, removed);
        Assert.True(File.Exists(recent));
        Assert.Equal(
            StartupTrace.MaximumRetainedSessionFiles,
            Directory.GetFiles(_root, "reactorv-session-*.log").Length);
    }

    [Fact]
    public void SignalsOnlyTheProcessSpecificPreloadHandoff()
    {
        var firstProcessId = Math.Max(1, Environment.TickCount & 0x3fffffff);
        var secondProcessId = firstProcessId + 1;
        using var first = PreloadHandoff.CreateWaitHandle(firstProcessId);
        using var second = PreloadHandoff.CreateWaitHandle(secondProcessId);

        Assert.True(PreloadHandoff.TrySignal(firstProcessId));
        Assert.True(first.WaitOne(0));
        Assert.False(second.WaitOne(0));
        Assert.Contains(firstProcessId.ToString(), PreloadHandoff.EventName(firstProcessId));
    }

    [Fact]
    public void DoesNotInventACompletedHandoffWhenNoPreloaderIsWaiting()
    {
        var processId = 1_500_000_000 + Random.Shared.Next(1, 100_000_000);

        Assert.False(PreloadHandoff.TrySignal(processId));
    }

    [Fact]
    public void SignalsOnlyTheProcessSpecificRuntimeReadyHandoff()
    {
        var firstProcessId = 1_200_000_000 + Random.Shared.Next(1, 50_000_000);
        var secondProcessId = firstProcessId + 1;
        using var first = PreloadHandoff.CreateRuntimeReadyWaitHandle(firstProcessId);
        using var second = PreloadHandoff.CreateRuntimeReadyWaitHandle(secondProcessId);

        Assert.True(PreloadHandoff.TrySignalRuntimeReady(firstProcessId));
        Assert.True(first.WaitOne(0));
        Assert.False(second.WaitOne(0));
        Assert.Contains(
            firstProcessId.ToString(),
            PreloadHandoff.RuntimeReadyEventName(firstProcessId));
    }

    [Fact]
    public void RuntimeReadyHandoff_is_a_monotonic_story_ownership_boundary()
    {
        var processId = 1_250_000_000 + Random.Shared.Next(1, 50_000_000);
        using var runtimeReady = PreloadHandoff.CreateRuntimeReadyWaitHandle(processId);

        Assert.False(runtimeReady.WaitOne(0));
        Assert.True(PreloadHandoff.TrySignalRuntimeReady(processId));
        Assert.True(runtimeReady.WaitOne(0));
        Assert.True(PreloadHandoff.TrySignalRuntimeReady(processId));
        Assert.True(runtimeReady.WaitOne(0));
    }

    [Fact]
    public void F9OwnershipRelease_is_distinct_and_process_specific()
    {
        var firstProcessId = 1_260_000_000 + Random.Shared.Next(1, 50_000_000);
        var secondProcessId = firstProcessId + 1;
        using var runtimeReady = PreloadHandoff.CreateRuntimeReadyWaitHandle(
            firstProcessId);
        using var first = PreloadHandoff.CreateF9OwnershipReleasedWaitHandle(
            firstProcessId);
        using var second = PreloadHandoff.CreateF9OwnershipReleasedWaitHandle(
            secondProcessId);

        Assert.False(first.WaitOne(0));
        Assert.False(second.WaitOne(0));
        Assert.True(PreloadHandoff.TrySignalRuntimeReady(firstProcessId));
        Assert.True(runtimeReady.WaitOne(0));
        Assert.False(first.WaitOne(0));
        first.Set();
        Assert.True(first.WaitOne(0));
        Assert.False(second.WaitOne(0));
        Assert.NotEqual(
            PreloadHandoff.RuntimeReadyEventName(firstProcessId),
            PreloadHandoff.F9OwnershipReleasedEventName(firstProcessId));
    }

    [Fact]
    public void ManagedF9_fails_closed_only_while_native_bootstrap_owns_the_key()
    {
        var processId = 1_270_000_000 + Random.Shared.Next(1, 50_000_000);

        Assert.True(PreloadHandoff.ManagedOwnsF9(processId));
        using var ownership = PreloadHandoff.CreateF9OwnershipReleasedWaitHandle(
            processId);
        Assert.False(PreloadHandoff.ManagedOwnsF9(processId));
        ownership.Set();
        Assert.True(PreloadHandoff.ManagedOwnsF9(processId));
    }

    [Fact]
    public void Default_menu_intent_is_process_scoped_cancellable_and_consumed_once()
    {
        var firstProcessId = 73000 + Math.Abs(Guid.NewGuid().GetHashCode() % 1000);
        var secondProcessId = firstProcessId + 1001;
        using var first = PreloadHandoff.CreateDefaultMenuIntentWaitHandle(firstProcessId);
        using var second = PreloadHandoff.CreateDefaultMenuIntentWaitHandle(secondProcessId);
        using var firstClaimed =
            PreloadHandoff.CreateDefaultMenuIntentClaimedWaitHandle(firstProcessId);
        using var firstActive =
            PreloadHandoff.CreateDefaultMenuIntentActiveWaitHandle(firstProcessId);
        using var firstCancelled =
            PreloadHandoff.CreateDefaultMenuIntentCancelledWaitHandle(firstProcessId);
        using var secondClaimed =
            PreloadHandoff.CreateDefaultMenuIntentClaimedWaitHandle(secondProcessId);
        using var secondActive =
            PreloadHandoff.CreateDefaultMenuIntentActiveWaitHandle(secondProcessId);
        using var secondCancelled =
            PreloadHandoff.CreateDefaultMenuIntentCancelledWaitHandle(secondProcessId);

        Assert.Equal(
            $@"Local\ReactorV.DefaultMenuIntent.{firstProcessId}",
            PreloadHandoff.DefaultMenuIntentEventName(firstProcessId));
        Assert.False(PreloadHandoff.TryConsumeDefaultMenuIntent(firstProcessId));

        Assert.True(PreloadHandoff.TryArmDefaultMenuIntent(firstProcessId));
        Assert.False(PreloadHandoff.TryConsumeDefaultMenuIntent(secondProcessId));
        Assert.True(PreloadHandoff.TryConsumeDefaultMenuIntent(firstProcessId));
        Assert.False(PreloadHandoff.TryConsumeDefaultMenuIntent(firstProcessId));

        Assert.True(PreloadHandoff.TryRestoreDefaultMenuIntent(firstProcessId));
        Assert.True(PreloadHandoff.TryCancelDefaultMenuIntent(firstProcessId));
        Assert.False(PreloadHandoff.TryConsumeDefaultMenuIntent(firstProcessId));
    }

    [Fact]
    public void Default_menu_claim_acknowledgement_is_process_scoped_and_one_shot()
    {
        var firstProcessId = 75000 + Math.Abs(Guid.NewGuid().GetHashCode() % 1000);
        var secondProcessId = firstProcessId + 1001;
        using var firstIntent = PreloadHandoff.CreateDefaultMenuIntentWaitHandle(
            firstProcessId);
        using var first = PreloadHandoff.CreateDefaultMenuIntentClaimedWaitHandle(
            firstProcessId);
        using var firstActive = PreloadHandoff.CreateDefaultMenuIntentActiveWaitHandle(
            firstProcessId);
        using var firstCancelled =
            PreloadHandoff.CreateDefaultMenuIntentCancelledWaitHandle(firstProcessId);
        using var second = PreloadHandoff.CreateDefaultMenuIntentClaimedWaitHandle(
            secondProcessId);

        Assert.Equal(
            $@"Local\ReactorV.DefaultMenuIntentClaimed.{firstProcessId}",
            PreloadHandoff.DefaultMenuIntentClaimedEventName(firstProcessId));
        Assert.True(PreloadHandoff.TryArmDefaultMenuIntent(firstProcessId));
        Assert.True(PreloadHandoff.TryConsumeDefaultMenuIntent(firstProcessId));
        Assert.True(PreloadHandoff.TryCommitDefaultMenuIntentClaim(firstProcessId));
        Assert.False(second.WaitOne(0));
        Assert.True(PreloadHandoff.TryTakeDefaultMenuIntentClaim(firstProcessId));
        Assert.False(PreloadHandoff.TryTakeDefaultMenuIntentClaim(firstProcessId));
        Assert.False(PreloadHandoff.IsDefaultMenuIntentActive(firstProcessId));
    }

    [Fact]
    public void Failed_presentation_restores_intent_unless_close_won()
    {
        var processId = 76000 + Math.Abs(Guid.NewGuid().GetHashCode() % 1000);
        using var intent = PreloadHandoff.CreateDefaultMenuIntentWaitHandle(processId);
        using var claimed = PreloadHandoff.CreateDefaultMenuIntentClaimedWaitHandle(processId);
        using var active = PreloadHandoff.CreateDefaultMenuIntentActiveWaitHandle(processId);
        using var cancelled =
            PreloadHandoff.CreateDefaultMenuIntentCancelledWaitHandle(processId);

        Assert.True(PreloadHandoff.TryArmDefaultMenuIntent(processId));
        Assert.True(PreloadHandoff.CanDispatchDefaultMenuIntent(processId));
        Assert.True(PreloadHandoff.TryConsumeDefaultMenuIntent(processId));
        Assert.True(PreloadHandoff.TryRestoreDefaultMenuIntent(processId));
        Assert.True(PreloadHandoff.TryConsumeDefaultMenuIntent(processId));

        Assert.True(PreloadHandoff.TryCancelDefaultMenuIntent(processId));
        Assert.False(PreloadHandoff.CanDispatchDefaultMenuIntent(processId));
        Assert.False(PreloadHandoff.TryRestoreDefaultMenuIntent(processId));
        Assert.True(PreloadHandoff.IsDefaultMenuIntentCancelled(processId));
        Assert.False(PreloadHandoff.IsDefaultMenuIntentActive(processId));
    }

    [Fact]
    public void KeepsContentAndRuntimeReadyHandoffsIndependent()
    {
        var processId = 1_300_000_000 + Random.Shared.Next(1, 50_000_000);
        using var contentReady = PreloadHandoff.CreateWaitHandle(processId);
        using var runtimeReady = PreloadHandoff.CreateRuntimeReadyWaitHandle(processId);

        Assert.True(PreloadHandoff.TrySignalRuntimeReady(processId));
        Assert.False(contentReady.WaitOne(0));
        Assert.True(runtimeReady.WaitOne(0));
        Assert.NotEqual(
            PreloadHandoff.EventName(processId),
            PreloadHandoff.RuntimeReadyEventName(processId));
    }

    [Fact]
    public void DoesNotInventRuntimeReadinessWithoutANativeWaitHandle()
    {
        var processId = 1_400_000_000 + Random.Shared.Next(1, 50_000_000);

        Assert.False(PreloadHandoff.TrySignalRuntimeReady(processId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
