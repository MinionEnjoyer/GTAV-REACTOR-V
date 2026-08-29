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
