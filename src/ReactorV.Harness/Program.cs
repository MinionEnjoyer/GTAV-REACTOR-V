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
            try
            {
                var options = HarnessOptions.Parse(args);
                return Run(options);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 2;
            }
        }

        private static int Run(HarnessOptions options)
        {
            if (options.Scenario == HarnessScenario.ShvdnFallback)
                return SecondaryAppDomainHarness.Run(options);
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

                var inputMode = RequireResult(router.Dispatch(Request(
                    "overlay.setInputMode",
                    new JObject { ["mode"] = "menu" })));
                Require(inputMode.Value<string>("inputMode") == "menu", "input mode did not change");
                var hidden = RequireResult(router.Dispatch(Request(
                    "overlay.setVisibility",
                    new JObject { ["visibility"] = "hidden" })));
                Require(!hidden.Value<bool>("visible") && !visible, "overlay visibility did not change");

                var unknown = router.Dispatch(Request("fixture.unknown"));
                Require(unknown.Error?.Code == "method_not_found", "unknown method did not fail closed");

                Console.WriteLine("RESULT PASS: API v2 handshake, extensions, menus, confirmation, idempotency, events, and overlay controls validated.");
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
            var handledRequests = 0;
            Console.WriteLine($"Harness started: {options.Api}, {options.Width}x{options.Height}");
            Console.WriteLine($"React UI: {uiDirectory}");

            while (session.IsHarnessRunning &&
                (!options.Duration.HasValue || stopwatch.Elapsed < options.Duration.Value))
            {
                session.PumpInput();
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
            var passed = finalStats.Api == options.Api &&
                finalStats.SubmittedFrames > 0 &&
                finalStats.RenderedFrames > 0 &&
                handledRequests >= 2;
            Console.WriteLine(
                $"RESULT {(passed ? "PASS" : "FAIL")}: API={finalStats.Api}, submitted={finalStats.SubmittedFrames}, " +
                $"rendered={finalStats.RenderedFrames}, dropped={finalStats.DroppedFrames}, requests={handledRequests}");
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
                                : scenario == "api" || scenario == "api-contract"
                                    ? HarnessScenario.ApiContract
                                : throw new ArgumentException(
                                    "--scenario must be directx, shvdn-fallback, or api-contract.");
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
                    default: throw new ArgumentException($"Unknown harness argument '{args[index]}'.");
                }
            }
            if (result.Width < 320 || result.Width > 8192 || result.Height < 240 || result.Height > 8192)
                throw new ArgumentOutOfRangeException(nameof(args), "Harness dimensions are outside the supported range.");
            return result;
        }
    }

    internal enum HarnessScenario
    {
        DirectX,
        ShvdnFallback,
        ApiContract,
    }

    internal static class HarnessRunDirectory
    {
        private static readonly string RunId = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyyMMddHHmmssfff}-{1}-{2}",
            DateTime.UtcNow,
            Process.GetCurrentProcess().Id,
            Guid.NewGuid().ToString("N"));

        public static string For(string scenario)
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ReactorV",
                "Harness",
                "Runs",
                RunId,
                scenario);
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
