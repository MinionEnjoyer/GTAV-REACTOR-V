using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using RageWebUI.DirectX;
using RageWebUI.DirectX.Native;

namespace RageWebUI.Harness
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var exitCode = 2;
            try
            {
                var options = HarnessOptions.Parse(args);
                exitCode = Run(options);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                exitCode = 2;
            }
            finally
            {
                // CEF creates a full browser cache for several harness
                // scenarios. These directories are disposable test state and
                // can otherwise grow by hundreds of megabytes per run. Keep a
                // small, explicitly marked set of failed runs so a packaging
                // failure still has actionable trace evidence.
                HarnessRunDirectory.CompleteCurrentRun(exitCode == 0);
            }
            return exitCode;
        }

        private static int Run(HarnessOptions options)
        {
            if (options.Scenario == HarnessScenario.ShvdnFallback)
                return SecondaryAppDomainHarness.Run(options);
            if (options.Scenario == HarnessScenario.BootstrapHost)
                return BootstrapHostHarness.Run(options);
            if (options.Scenario == HarnessScenario.GbayLifecycle)
                return GbayLifecycleHarness.Run(options);
            if (options.Scenario == HarnessScenario.LiveAcceptance)
                return LiveAcceptanceHarness.Run(options);
            if (options.Scenario == HarnessScenario.ExternalGpuConsumer)
                return ExternalGpuBrowserConsumerHarness.Run(options);
            if (options.Scenario == HarnessScenario.ApiContract)
                return RunApiContract();
            return RunDirectX(options);
        }

        private static int RunApiContract()
        {
            var visible = true;
            using var router = new HarnessApiRouter(value => visible = value, () => visible);
            try
            {
                var handshake = RequireResult(router.Dispatch(Request("runtime.handshake")));
                Require(handshake.Value<int>("apiVersion") == 2, "runtime handshake did not select API v2");
                var startup = RequireResult(router.Dispatch(Request(StartupStatusContract.Method)));
                Require(
                    startup.Value<int>("schemaVersion") == StartupStatusContract.SchemaVersion &&
                    startup.Value<bool>("providerConnected") &&
                    startup.Value<bool>("allIn1Loaded") &&
                    startup.Value<string>("gameplayReadiness") == "not-reported",
                    "startup status invented readiness or omitted explicit provider state");
                Require(
                    (startup["console"]?["entries"] as JArray)?.Count <= StartupTrace.MaximumConsoleEntries,
                    "startup console exceeded its bounded entry limit");

                var extensionIndex = RequireResult(router.Dispatch(Request("extensions.list")));
                var extensionItems = extensionIndex["items"] as JArray ?? new JArray();
                Require(extensionItems.OfType<JObject>().Any(item =>
                    item.Value<string>("id") == "allin1.fixture"), "ALLIN1 fixture missing from extension index");

                var extension = RequireResult(router.Dispatch(Request(
                    "extensions.get",
                    new JObject { ["extensionId"] = "allin1.fixture" })));
                Require((extension["actions"] as JArray)?.Count >= 2, "extension detail omitted actions");

                var menuIndex = RequireResult(router.Dispatch(Request(
                    "menu.list",
                    new JObject { ["extensionId"] = "allin1.fixture" })));
                Require((menuIndex["items"] as JArray)?.Count == 1, "menu index is incorrect");
                var menu = RequireResult(router.Dispatch(Request(
                    "menu.get",
                    new JObject { ["extensionId"] = "allin1.fixture", ["menuId"] = "gbay" })));
                Require((menu["nodes"] as JArray)?.Count == 3, "menu detail omitted nodes");

                var purchase = new JObject
                {
                    ["extensionId"] = "allin1.fixture",
                    ["actionId"] = "gbay.purchase",
                    ["parameters"] = new JObject { ["listing"] = "fixture-bus" },
                };
                var unconfirmed = RequireResult(router.Dispatch(Request("extensions.invoke", purchase)));
                Require(unconfirmed.Value<bool>("confirmationRequired"), "persistent action bypassed confirmation");

                purchase["confirmed"] = true;
                purchase["idempotencyKey"] = "fixture-purchase-1";
                var committed = RequireResult(router.Dispatch(Request("extensions.invoke", purchase)));
                Require(committed.Value<bool>("succeeded") && !committed.Value<bool>("replayed"),
                    "confirmed persistent action did not execute");
                var replayed = RequireResult(router.Dispatch(Request("extensions.invoke", purchase)));
                Require(replayed.Value<bool>("succeeded") && replayed.Value<bool>("replayed"),
                    "persistent idempotency replay failed");

                var menuResult = RequireResult(router.Dispatch(Request(
                    "menu.invoke",
                    new JObject
                    {
                        ["extensionId"] = "allin1.fixture",
                        ["menuId"] = "gbay",
                        ["nodeId"] = "traffic",
                        ["interaction"] = "set-value",
                        ["value"] = false,
                        ["confirmed"] = true,
                        ["idempotencyKey"] = "fixture-traffic-1",
                    })));
                Require(menuResult.Value<bool>("succeeded"), "typed menu invocation failed");

                var subscription = RequireResult(router.Dispatch(Request(
                    "events.subscribe",
                    new JObject { ["events"] = new JArray("allin1.fixture.gbay.orderchanged") })));
                var subscriptionId = subscription.Value<string>("id") ?? string.Empty;
                Require(subscriptionId.StartsWith("sub-", StringComparison.Ordinal), "subscription id missing");
                var removed = RequireResult(router.Dispatch(Request(
                    "events.unsubscribe",
                    new JObject { ["subscriptionId"] = subscriptionId })));
                Require(removed.Value<bool>("removed"), "subscription could not be removed");

                var hidden = RequireResult(router.Dispatch(Request(
                    "overlay.setState",
                    new JObject
                    {
                        ["visibility"] = "hidden",
                        ["inputMode"] = "game",
                    })));
                Require(!hidden.Value<bool>("visible") && !visible, "overlay visibility did not change");

                var unsafeVisible = router.Dispatch(Request(
                    "overlay.setVisibility",
                    new JObject { ["visibility"] = "visible" }));
                Require(
                    unsafeVisible.Error != null && !visible,
                    "legacy visibility exposed a surface in game input mode");

                var interactive = RequireResult(router.Dispatch(Request(
                    "overlay.setState",
                    new JObject
                    {
                        ["visibility"] = "visible",
                        ["inputMode"] = "interactive-menu",
                    })));
                Require(
                    interactive.Value<bool>("visible") &&
                    interactive.Value<string>("inputMode") == "interactive-menu" &&
                    visible,
                    "atomic overlay state did not acquire input before visibility");

                var unsafeGameMode = router.Dispatch(Request(
                    "overlay.setInputMode",
                    new JObject { ["mode"] = "game" }));
                Require(
                    unsafeGameMode.Error != null && visible,
                    "visible overlay released input ownership without hiding");

                var unknown = router.Dispatch(Request("fixture.unknown"));
                Require(unknown.Error?.Code == "method_not_found", "unknown method did not fail closed");

                Console.WriteLine("RESULT PASS: API v2 handshake, bounded startup status, extensions, menus, confirmation, idempotency, events, and atomic overlay ownership validated.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("RESULT FAIL: " + error.Message);
                return 5;
            }
        }

        private static BridgeRequest Request(string method, JObject? parameters = null)
        {
            var json = new JObject
            {
                ["kind"] = "request",
                ["id"] = "h-" + Guid.NewGuid().ToString("N"),
                ["method"] = method,
                ["params"] = parameters ?? new JObject(),
                ["protocolVersion"] = 2,
                ["minimumProtocolVersion"] = 1,
            }.ToString(Newtonsoft.Json.Formatting.None);
            if (!BridgeProtocol.TryParseRequest(json, out var request, out var error) || request == null)
                throw new InvalidOperationException(error?.Message ?? "Could not create harness request.");
            return request;
        }

        private static JObject RequireResult(BridgeResponse response)
        {
            if (response.Error != null)
                throw new InvalidOperationException(response.Error.Code + ": " + response.Error.Message);
            return response.Result as JObject ??
                throw new InvalidOperationException("Harness response was not an object.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static int RunDirectX(HarnessOptions options)
        {
            var runtimeDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var uiDirectory = options.UiDirectory ?? Path.Combine(runtimeDirectory, "ui");
            // A Chromium root cache is single-process state. Reusing the same
            // directory from two concurrent harness runs makes the second CEF
            // instance attach to the first process and can fail browser creation
            // inside libcef. Keep every harness process isolated while leaving
            // the production runtime's persistent profile behavior unchanged.
            var cacheDirectory = HarnessRunDirectory.For(options.Api.ToString());
            var runtimeTracePath = Path.Combine(
                Path.GetDirectoryName(cacheDirectory) ?? cacheDirectory,
                "reactorv-runtime.log");
            var broker = new BridgeBroker();
            using var session = new DirectXOverlaySession(
                IntPtr.Zero,
                uiDirectory,
                runtimeDirectory,
                cacheDirectory,
                broker,
                options.Width,
                options.Height,
                frameRate: 60,
                enableDevTools: true);
            if (!session.StartHarness(options.Api, $"REACTOR V Preview — {options.Api}"))
            {
                Console.Error.WriteLine($"Could not start the {options.Api} test surface.");
                return 3;
            }

            using var router = new HarnessApiRouter(session.SetVisible, () => session.IsVisible);
            var stopwatch = Stopwatch.StartNew();
            var nextTelemetry = TimeSpan.Zero;
            var nextReport = TimeSpan.Zero;
            var nextSetupSurface = TimeSpan.Zero;
            var handledRequests = 0;
            var setupSurfaceGeneration = 0;
            Console.WriteLine($"Harness started: {options.Api}, {options.Width}x{options.Height}");
            Console.WriteLine($"React UI: {uiDirectory}");

            while (session.IsHarnessRunning &&
                (!options.Duration.HasValue || stopwatch.Elapsed < options.Duration.Value))
            {
                session.PumpInput();
                if (handledRequests < 2 && stopwatch.Elapsed >= nextSetupSurface)
                {
                    // Production intentionally boots transparent. The visual
                    // compositor harness explicitly selects the installer
                    // status surface so bridge traffic is part of this test,
                    // independent of whatever the product idle state is.
                    session.PostEvent(
                        "host.provider",
                        new JObject { ["connected"] = true });
                    session.PostEvent(
                        "host.surface",
                        new JObject
                        {
                            ["mode"] = HostSurfaceMode.SetupStatus,
                            ["generation"] = ++setupSurfaceGeneration,
                        });
                    nextSetupSurface = stopwatch.Elapsed + TimeSpan.FromMilliseconds(250);
                }
                for (var index = 0; index < 32 && broker.TryDequeue(out var request); index++)
                {
                    if (request == null) continue;
                    session.PostResponse(router.Dispatch(request));
                    handledRequests++;
                }

                if (stopwatch.Elapsed >= nextTelemetry)
                {
                    session.PostEvent("game.state", router.State);
                    nextTelemetry = stopwatch.Elapsed + TimeSpan.FromMilliseconds(100);
                }
                if (stopwatch.Elapsed >= nextReport)
                {
                    var stats = session.Stats;
                    Console.WriteLine(
                        $"[{stopwatch.Elapsed.TotalSeconds,5:0.0}s] API={stats.Api} submitted={stats.SubmittedFrames} " +
                        $"rendered={stats.RenderedFrames} dropped={stats.DroppedFrames} requests={handledRequests}");
                    nextReport = stopwatch.Elapsed + TimeSpan.FromSeconds(1);
                }
                Thread.Sleep(4);
            }

            var finalStats = session.Stats;
            var runtimeTrace = File.Exists(runtimeTracePath)
                ? File.ReadAllText(runtimeTracePath)
                : string.Empty;
            var contextReadyAt = runtimeTrace.IndexOf(
                "stage=cef_context_initialized",
                StringComparison.Ordinal);
            var browserCreateCompleteAt = runtimeTrace.IndexOf(
                "stage=browser_create_complete",
                StringComparison.Ordinal);
            var contextBarrierPassed = contextReadyAt >= 0 &&
                browserCreateCompleteAt > contextReadyAt;
            var passed = finalStats.Api == options.Api &&
                finalStats.SubmittedFrames > 0 &&
                finalStats.RenderedFrames > 0 &&
                handledRequests >= 2 &&
                contextBarrierPassed;
            Console.WriteLine(
                $"RESULT {(passed ? "PASS" : "FAIL")}: API={finalStats.Api}, submitted={finalStats.SubmittedFrames}, " +
                $"rendered={finalStats.RenderedFrames}, dropped={finalStats.DroppedFrames}, requests={handledRequests}, " +
                $"contextBarrier={contextBarrierPassed}");
            return passed ? 0 : 4;
        }

    }

    internal sealed class HarnessOptions
    {
        public HarnessScenario Scenario { get; private set; } = HarnessScenario.DirectX;
        public RenderApi Api { get; private set; } = RenderApi.Direct3D11;
        public int Width { get; private set; } = 1280;
        public int Height { get; private set; } = 720;
        public TimeSpan? Duration { get; private set; }
        public string? UiDirectory { get; private set; }
        public string? LocalDataDirectory { get; private set; }
        public TimeSpan BootstrapWarmDelay { get; private set; } = TimeSpan.FromMilliseconds(3500);
        public TimeSpan GbayColdReadyBudget { get; private set; } = TimeSpan.FromMilliseconds(3500);
        public TimeSpan GbayFirstPresentationBudget { get; private set; } = TimeSpan.FromMilliseconds(1000);
        public TimeSpan GbayWarmPresentationBudget { get; private set; } = TimeSpan.FromMilliseconds(500);
        public TimeSpan GbayCloseBudget { get; private set; } = TimeSpan.FromMilliseconds(500);
        public TimeSpan LiveProcessTimeout { get; private set; } = TimeSpan.FromMinutes(20);
        public TimeSpan LiveStepTimeout { get; private set; } = TimeSpan.FromSeconds(45);
        public string? LiveReceiptPath { get; private set; }

        public static HarnessOptions Parse(string[] args)
        {
            var result = new HarnessOptions();
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index].ToLowerInvariant())
                {
                    case "--scenario":
                        var scenario = args[++index].ToLowerInvariant();
                        result.Scenario = scenario == "directx" ? HarnessScenario.DirectX :
                            scenario == "shvdn" || scenario == "shvdn-fallback"
                                ? HarnessScenario.ShvdnFallback
                                : scenario == "bootstrap" || scenario == "bootstrap-host"
                                ? HarnessScenario.BootstrapHost
                                : scenario == "api" || scenario == "api-contract"
                                    ? HarnessScenario.ApiContract
                                : scenario == "gbay" || scenario == "gbay-lifecycle"
                                    ? HarnessScenario.GbayLifecycle
                                : scenario == "live" || scenario == "live-acceptance"
                                    ? HarnessScenario.LiveAcceptance
                                : scenario == "external-gpu" ||
                                    scenario == "external-gpu-consumer"
                                    ? HarnessScenario.ExternalGpuConsumer
                                : throw new ArgumentException(
                                    "--scenario must be directx, shvdn-fallback, bootstrap-host, api-contract, gbay-lifecycle, live-acceptance, or external-gpu-consumer.");
                        break;
                    case "--api":
                        var api = args[++index].ToLowerInvariant();
                        result.Api = api == "d3d12" ? RenderApi.Direct3D12 : api == "d3d11" ? RenderApi.Direct3D11
                            : throw new ArgumentException("--api must be d3d11 or d3d12.");
                        break;
                    case "--width": result.Width = int.Parse(args[++index]); break;
                    case "--height": result.Height = int.Parse(args[++index]); break;
                    case "--duration":
                        result.Duration = TimeSpan.FromSeconds(double.Parse(args[++index], CultureInfo.InvariantCulture));
                        break;
                    case "--smoke": result.Duration = TimeSpan.FromSeconds(6); break;
                    case "--ui": result.UiDirectory = Path.GetFullPath(args[++index]); break;
                    case "--local-data-dir":
                        result.LocalDataDirectory = Path.GetFullPath(args[++index]);
                        break;
                    case "--bootstrap-warm-delay-ms":
                        var delay = int.Parse(args[++index], CultureInfo.InvariantCulture);
                        if (delay < 0 || delay > 30000)
                            throw new ArgumentOutOfRangeException(
                                nameof(args),
                                "--bootstrap-warm-delay-ms must be between 0 and 30000.");
                        result.BootstrapWarmDelay = TimeSpan.FromMilliseconds(delay);
                        break;
                    case "--gbay-cold-ready-budget-ms":
                        result.GbayColdReadyBudget = Budget(args[++index], args[index - 1]);
                        break;
                    case "--gbay-first-presentation-budget-ms":
                        result.GbayFirstPresentationBudget = Budget(args[++index], args[index - 1]);
                        break;
                    case "--gbay-warm-presentation-budget-ms":
                        result.GbayWarmPresentationBudget = Budget(args[++index], args[index - 1]);
                        break;
                    case "--gbay-close-budget-ms":
                        result.GbayCloseBudget = Budget(args[++index], args[index - 1]);
                        break;
                    case "--live-process-timeout-seconds":
                        result.LiveProcessTimeout = LiveTimeout(args[++index], args[index - 1], 30, 7200);
                        break;
                    case "--live-step-timeout-seconds":
                        result.LiveStepTimeout = LiveTimeout(args[++index], args[index - 1], 5, 300);
                        break;
                    case "--receipt":
                        result.LiveReceiptPath = Path.GetFullPath(args[++index]);
                        break;
                    default: throw new ArgumentException($"Unknown harness argument '{args[index]}'.");
                }
            }
            if (result.Width < 320 || result.Width > 8192 || result.Height < 240 || result.Height > 8192)
                throw new ArgumentOutOfRangeException(nameof(args), "Harness dimensions are outside the supported range.");
            return result;
        }

        private static TimeSpan Budget(string value, string option)
        {
            var milliseconds = int.Parse(value, CultureInfo.InvariantCulture);
            if (milliseconds < 50 || milliseconds > 30000)
                throw new ArgumentOutOfRangeException(nameof(value), $"{option} must be between 50 and 30000.");
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        private static TimeSpan LiveTimeout(
            string value,
            string option,
            int minimumSeconds,
            int maximumSeconds)
        {
            var seconds = int.Parse(value, CultureInfo.InvariantCulture);
            if (seconds < minimumSeconds || seconds > maximumSeconds)
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    $"{option} must be between {minimumSeconds} and {maximumSeconds} seconds.");
            return TimeSpan.FromSeconds(seconds);
        }
    }

    internal enum HarnessScenario
    {
        DirectX,
        ShvdnFallback,
        BootstrapHost,
        ApiContract,
        GbayLifecycle,
        LiveAcceptance,
        ExternalGpuConsumer,
    }

    internal static class HarnessRunDirectory
    {
        private const string FailureMarkerFileName = ".failed-run";
        private const int MaximumRetainedFailedRuns = 3;

        private static readonly string RunsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReactorV",
            "Harness",
            "Runs");

        private static readonly string RunId = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyyMMddHHmmssfff}-{1}-{2}",
            DateTime.UtcNow,
            Process.GetCurrentProcess().Id,
            Guid.NewGuid().ToString("N"));

        private static readonly string RunRoot = Path.Combine(RunsRoot, RunId);

        public static string For(string scenario)
        {
            var path = Path.Combine(RunRoot, scenario);
            Directory.CreateDirectory(path);
            return path;
        }

        public static void CompleteCurrentRun(bool succeeded)
        {
            if (!IsOwnedRunDirectory(RunRoot) || !Directory.Exists(RunRoot))
                return;

            if (!succeeded)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(RunRoot, FailureMarkerFileName),
                        DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                PruneCompletedFailureEvidence();
                return;
            }

            TryDeleteOwnedRunDirectory(RunRoot);
        }

        private static void PruneCompletedFailureEvidence()
        {
            if (!Directory.Exists(RunsRoot))
                return;

            try
            {
                var staleFailures = new DirectoryInfo(RunsRoot)
                    .EnumerateDirectories()
                    .Where(directory =>
                        IsOwnedRunDirectory(directory.FullName) &&
                        File.Exists(Path.Combine(
                            directory.FullName,
                            FailureMarkerFileName)))
                    .OrderByDescending(directory => directory.LastWriteTimeUtc)
                    .Skip(MaximumRetainedFailedRuns)
                    .ToArray();
                foreach (var staleFailure in staleFailures)
                    TryDeleteOwnedRunDirectory(staleFailure.FullName);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteOwnedRunDirectory(string runDirectory)
        {
            if (!IsOwnedRunDirectory(runDirectory) || !Directory.Exists(runDirectory))
                return;

            // CEF helper processes normally stop before the harness returns,
            // but antivirus or a delayed file handle can briefly retain the
            // cache. Cleanup is best-effort so it can never hide a test result.
            var retryDelaysMilliseconds = new[] { 0, 50, 150, 400 };
            foreach (var retryDelayMilliseconds in retryDelaysMilliseconds)
            {
                if (retryDelayMilliseconds > 0)
                    Thread.Sleep(retryDelayMilliseconds);
                try
                {
                    Directory.Delete(runDirectory, recursive: true);
                    return;
                }
                catch (DirectoryNotFoundException)
                {
                    return;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private static bool IsOwnedRunDirectory(string candidate)
        {
            var fullRunsRoot = Path.GetFullPath(RunsRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var fullCandidate = Path.GetFullPath(candidate);
            return fullCandidate.StartsWith(
                fullRunsRoot,
                StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    fullCandidate.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    fullRunsRoot.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}
