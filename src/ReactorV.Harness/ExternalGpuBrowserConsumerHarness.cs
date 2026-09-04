using System;
using System.Diagnostics;
using System.Threading;
using RageWebUI.DirectX.Native;
using ReactorV.BootstrapHost;

namespace RageWebUI.Harness
{
    /// <summary>
    /// Non-GTA target for the packaged external CEF producer. The native test
    /// surface supplies a real D3D11 or D3D12 swap chain while the early hook
    /// entry point arms only the same shared-frame consumer used in GTA. No
    /// in-process browser or CPU mailbox frame is created, so a non-zero
    /// rendered generation is direct proof that a cross-process GPU frame made
    /// the complete producer/discovery/import/composite path.
    /// </summary>
    internal static class ExternalGpuBrowserConsumerHarness
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan TeardownGracePeriod = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SharedFrameContinuityPeriod =
            TimeSpan.FromMilliseconds(400);

        private static string FrameReadyEventName(int processId) =>
            @"Local\ReactorV.ExternalGpuConsumerHarness.FrameReady." + processId;

        private static string TeardownCompleteEventName(int processId) =>
            @"Local\ReactorV.ExternalGpuConsumerHarness.TeardownComplete." + processId;

        public static int Run(HarnessOptions options)
        {
            var timeout = options.Duration ?? DefaultTimeout;
            var processId = Process.GetCurrentProcess().Id;
            var armed = false;
            var testStarted = false;

            using var aboutToggle = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                BootstrapHostNames.AboutToggleEvent(processId));
            using var frameReady = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                FrameReadyEventName(processId));
            using var teardownComplete = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                TeardownCompleteEventName(processId));

            try
            {
                // Arm before the standalone surface creates its swap chain so
                // the hook observes the same target-creation boundary used in
                // GTA. Starting first made this qualification race-dependent.
                armed = NativeCompositor.ArmEnhancedHook();
                if (!armed)
                {
                    Console.Error.WriteLine(
                        "RESULT FAIL: scenario=external-gpu-consumer " +
                        "reason=consumer-arm-rejected");
                    return 6;
                }

                testStarted = NativeCompositor.StartTest(
                    options.Api,
                    options.Width,
                    options.Height,
                    $"REACTOR V External GPU Consumer — {options.Api}");
                if (!testStarted)
                {
                    Console.Error.WriteLine(
                        "RESULT FAIL: scenario=external-gpu-consumer " +
                        "reason=test-surface-start-rejected");
                    return 7;
                }

                var surfaceDeadline = Stopwatch.StartNew();
                RenderStats initializedStats = default;
                while (surfaceDeadline.Elapsed < TimeSpan.FromSeconds(5) &&
                    NativeCompositor.IsTestRunning)
                {
                    if (NativeCompositor.TryGetStats(out initializedStats) &&
                        initializedStats.Api == options.Api)
                    {
                        break;
                    }
                    Thread.Sleep(10);
                }
                if (!NativeCompositor.IsTestRunning ||
                    initializedStats.Api != options.Api)
                {
                    Console.Error.WriteLine(
                        "RESULT FAIL: scenario=external-gpu-consumer " +
                        "reason=test-surface-prepare-failed");
                    return 6;
                }

                NativeCompositor.SetVisible(true);
                // The package gate launches the producer after this process.
                // Auto-reset event state is retained until Preloader consumes
                // this single edge, making the About surface visible without
                // a polling/toggle race.
                aboutToggle.Set();

                Console.WriteLine(
                    $"CONSUMER READY: scenario=external-gpu-consumer " +
                    $"api={options.Api} pid={processId} " +
                    $"timeoutMs={timeout.TotalMilliseconds:0}");

                var timer = Stopwatch.StartNew();
                var nextReport = TimeSpan.Zero;
                Stopwatch? continuityTimer = null;
                ulong continuityFirstRenderedFrames = 0;
                ulong continuityLastRenderedFrames = 0;
                ulong continuityFirstGeneration = 0;
                while (timer.Elapsed < timeout && NativeCompositor.IsTestRunning)
                {
                    if (NativeCompositor.TryGetStats(out var stats))
                    {
                        if (timer.Elapsed >= nextReport)
                        {
                            Console.WriteLine(
                                $"[{timer.Elapsed.TotalSeconds,5:0.0}s] " +
                                $"API={stats.Api} submitted={stats.SubmittedFrames} " +
                                $"rendered={stats.RenderedFrames} " +
                                $"lastGeneration={stats.LastFrameGeneration}");
                            nextReport = timer.Elapsed + TimeSpan.FromSeconds(1);
                        }

                        var sharedFrameQualified = stats.Api == options.Api &&
                            stats.SubmittedFrames == 0 &&
                            stats.RenderedFrames > 0 &&
                            stats.LastFrameGeneration > 0;
                        if (sharedFrameQualified)
                        {
                            if (continuityTimer == null)
                            {
                                continuityTimer = Stopwatch.StartNew();
                                continuityFirstRenderedFrames =
                                    stats.RenderedFrames;
                                continuityLastRenderedFrames =
                                    stats.RenderedFrames;
                                continuityFirstGeneration =
                                    stats.LastFrameGeneration;
                                Console.WriteLine(
                                    $"SHARED FRAME OBSERVED: scenario=external-gpu-consumer " +
                                    $"api={stats.Api} rendered={stats.RenderedFrames} " +
                                    $"lastGeneration={stats.LastFrameGeneration}");
                            }
                            else if (stats.RenderedFrames <
                                    continuityLastRenderedFrames ||
                                stats.LastFrameGeneration <
                                    continuityFirstGeneration)
                            {
                                Console.Error.WriteLine(
                                    "RESULT FAIL: scenario=external-gpu-consumer " +
                                    "reason=shared-frame-counter-regression");
                                return 11;
                            }

                            continuityLastRenderedFrames = stats.RenderedFrames;
                            if (continuityTimer.Elapsed >=
                                SharedFrameContinuityPeriod)
                            {
                                if (stats.RenderedFrames <=
                                    continuityFirstRenderedFrames)
                                {
                                    Console.Error.WriteLine(
                                        "RESULT FAIL: scenario=external-gpu-consumer " +
                                        "reason=shared-frame-did-not-remain-renderable");
                                    return 11;
                                }

                                Console.WriteLine(
                                    $"SHARED FRAME READY: scenario=external-gpu-consumer " +
                                    $"api={stats.Api} rendered={stats.RenderedFrames} " +
                                    $"lastGeneration={stats.LastFrameGeneration}");
                                frameReady.Set();
                                // Keep the fake GTA process and its consumer alive
                                // while the package gate closes Preloader cleanly.
                                // This distinguishes graceful producer disposal
                                // from a target-loss fault that merely happened
                                // after the first successful frame.
                                if (!teardownComplete.WaitOne(TeardownGracePeriod))
                                {
                                    Console.Error.WriteLine(
                                        "RESULT FAIL: scenario=external-gpu-consumer " +
                                        "reason=clean-teardown-not-acknowledged");
                                    return 10;
                                }
                                Console.WriteLine(
                                    $"RESULT PASS: scenario=external-gpu-consumer " +
                                    $"api={stats.Api} submitted={stats.SubmittedFrames} " +
                                    $"rendered={stats.RenderedFrames} " +
                                    $"lastGeneration={stats.LastFrameGeneration} " +
                                    $"elapsedMs={timer.Elapsed.TotalMilliseconds:0.###}");
                                return 0;
                            }
                        }
                        else if (continuityTimer != null)
                        {
                            Console.Error.WriteLine(
                                "RESULT FAIL: scenario=external-gpu-consumer " +
                                "reason=shared-frame-continuity-lost");
                            return 11;
                        }
                    }
                    else if (continuityTimer != null)
                    {
                        Console.Error.WriteLine(
                            "RESULT FAIL: scenario=external-gpu-consumer " +
                            "reason=shared-frame-stats-unavailable");
                        return 11;
                    }

                    Thread.Sleep(10);
                }

                var finalStats = NativeCompositor.TryGetStats(out var final)
                    ? final
                    : default;
                Console.Error.WriteLine(
                    $"RESULT FAIL: scenario=external-gpu-consumer " +
                    $"reason=shared-frame-timeout api={finalStats.Api} " +
                    $"submitted={finalStats.SubmittedFrames} " +
                    $"rendered={finalStats.RenderedFrames} " +
                    $"lastGeneration={finalStats.LastFrameGeneration}");
                return 8;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    "RESULT FAIL: scenario=external-gpu-consumer " +
                    $"reason={error.GetType().Name} message={error.Message}");
                return 9;
            }
            finally
            {
                NativeCompositor.SetVisible(false);
                if (testStarted)
                    NativeCompositor.StopTest();
                if (armed)
                    NativeCompositor.Shutdown();
                Console.WriteLine(
                    "CONSUMER STOPPED: scenario=external-gpu-consumer");
            }
        }
    }
}
