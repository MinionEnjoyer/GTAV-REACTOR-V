using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using ReactorV.Integration;

namespace RageWebUI.Harness
{
    internal sealed class HarnessApiRouter : IDisposable
    {
        private readonly Action<bool> _setVisible;
        private readonly Func<bool> _isVisible;
        private readonly JObject _state;
        private readonly IReactorExtensionHandle _fixture;
        private readonly HashSet<string> _subscriptions = new HashSet<string>(StringComparer.Ordinal);
        private string _inputMode = "exclusive";

        public HarnessApiRouter(Action close)
            : this(visible => { if (!visible) close(); }, () => true)
        {
        }

        public HarnessApiRouter(Action<bool> setVisible, Func<bool> isVisible)
        {
            _setVisible = setVisible;
            _isVisible = isVisible;
            _state = new JObject
            {
                ["gameTime"] = 42420,
                ["paused"] = false,
                ["player"] = new JObject
                {
                    ["health"] = 200,
                    ["maxHealth"] = 200,
                    ["armor"] = 72,
                    ["wantedLevel"] = 2,
                    ["invincible"] = false,
                    ["position"] = new JObject { ["x"] = -75.3, ["y"] = -818.9, ["z"] = 326.2 },
                    ["heading"] = 182.4,
                },
                ["vehicle"] = new JObject
                {
                    ["handle"] = 1042,
                    ["displayName"] = "Buffalo STX",
                    ["speedMps"] = 18.6,
                    ["engineHealth"] = 842,
                },
                ["world"] = new JObject { ["time"] = "21:48", ["weather"] = "Clear" },
            };

            ReactorHostApi.Reset();
            _fixture = RegisterAllIn1Fixture();
        }

        public JObject State
        {
            get
            {
                _state["gameTime"] = _state.Value<int>("gameTime") + 16;
                return (JObject)_state.DeepClone();
            }
        }

        public BridgeResponse Dispatch(BridgeRequest request)
        {
            if (request.IsExpired)
            {
                return Failure(request, "deadline_exceeded", "The harness request expired.");
            }

            try
            {
                JToken result;
                switch (request.Method)
                {
                    case "overlay.ready":
                    case "runtime.handshake":
                        result = RuntimeStatus();
                        break;
                    case "runtime.describe":
                        result = RuntimeDescription();
                        break;
                    case "overlay.close":
                        _setVisible(false);
                        result = new JObject { ["visible"] = false };
                        break;
                    case "overlay.setVisibility":
                        var visibility = RequireString(request.Parameters, "visibility");
                        var visible = visibility == "toggle" ? !_isVisible() : visibility == "visible";
                        if (visibility != "toggle" && visibility != "visible" && visibility != "hidden")
                            throw new InvalidOperationException("Invalid visibility.");
                        _setVisible(visible);
                        result = OverlayState(visible);
                        break;
                    case "overlay.setInputMode":
                        _inputMode = RequireString(request.Parameters, "mode");
                        result = OverlayState(_isVisible());
                        break;
                    case "extensions.list":
                        result = ReactorHostApi.DescribeExtensionSummaries();
                        break;
                    case "extensions.get":
                        result = ReactorHostApi.DescribeExtension(
                            RequireString(request.Parameters, "extensionId")) ??
                            throw new InvalidOperationException("Extension not found.");
                        break;
                    case "extensions.invoke":
                        result = ReactorHostApi.Invoke(
                            RequireString(request.Parameters, "extensionId"),
                            RequireString(request.Parameters, "actionId"),
                            request.Parameters["parameters"] as JObject ?? new JObject(),
                            request.Parameters.Value<bool?>("confirmed") ?? request.Confirmed,
                            request.Parameters.Value<string>("idempotencyKey") ?? request.IdempotencyKey).ToJson();
                        break;
                    case "menu.list":
                        result = ReactorHostApi.DescribeMenuSummaries(
                            request.Parameters.Value<string>("extensionId"));
                        break;
                    case "menu.get":
                        var menus = ReactorHostApi.DescribeMenus(
                            RequireString(request.Parameters, "extensionId"),
                            RequireString(request.Parameters, "menuId"));
                        result = menus.Count == 1
                            ? menus[0]!.DeepClone()
                            : throw new InvalidOperationException("Menu not found.");
                        break;
                    case "menu.invoke":
                        var actionParameters = request.Parameters["parameters"] as JObject ?? new JObject();
                        if (actionParameters.Count == 0 && request.Parameters.TryGetValue("value", out var value))
                            actionParameters["value"] = value.DeepClone();
                        result = ReactorHostApi.InvokeMenu(
                            RequireString(request.Parameters, "extensionId"),
                            RequireString(request.Parameters, "menuId"),
                            RequireString(request.Parameters, "nodeId"),
                            RequireString(request.Parameters, "interaction"),
                            actionParameters,
                            request.Parameters.Value<bool?>("confirmed") ?? request.Confirmed,
                            request.Parameters.Value<string>("idempotencyKey") ?? request.IdempotencyKey).ToJson();
                        break;
                    case "events.subscribe":
                        var subscriptionId = "sub-" + Guid.NewGuid().ToString("N");
                        _subscriptions.Add(subscriptionId);
                        result = new JObject
                        {
                            ["id"] = subscriptionId,
                            ["events"] = request.Parameters["events"]?.DeepClone() ?? new JArray(),
                        };
                        break;
                    case "events.unsubscribe":
                        result = new JObject
                        {
                            ["removed"] = _subscriptions.Remove(
                                RequireString(request.Parameters, "subscriptionId")),
                        };
                        break;
                    case "game.getState":
                        result = State;
                        break;
                    case "ui.notify":
                        result = new JObject { ["shown"] = true };
                        break;
                    case "player.heal":
                        ((JObject)_state["player"]!)["health"] = 200;
                        ((JObject)_state["player"]!)["armor"] = 100;
                        result = new JObject { ["health"] = 200, ["armor"] = 100 };
                        break;
                    case "player.setInvincible":
                        var invincible = request.Parameters.Value<bool>("enabled");
                        ((JObject)_state["player"]!)["invincible"] = invincible;
                        result = new JObject { ["enabled"] = invincible };
                        break;
                    case "player.setWantedLevel":
                        var level = request.Parameters.Value<int>("level");
                        ((JObject)_state["player"]!)["wantedLevel"] = level;
                        result = new JObject { ["level"] = level };
                        break;
                    case "player.teleport":
                        var position = new JObject
                        {
                            ["x"] = request.Parameters.Value<double>("x"),
                            ["y"] = request.Parameters.Value<double>("y"),
                            ["z"] = request.Parameters.Value<double>("z"),
                        };
                        ((JObject)_state["player"]!)["position"] = position;
                        result = new JObject { ["position"] = position };
                        break;
                    case "vehicle.repair":
                        ((JObject)_state["vehicle"]!)["engineHealth"] = 1000;
                        result = new JObject { ["repaired"] = true, ["engineHealth"] = 1000 };
                        break;
                    case "vehicle.spawn":
                        var model = request.Parameters.Value<string>("model") ?? "unknown";
                        _state["vehicle"] = new JObject
                        {
                            ["handle"] = 2048,
                            ["displayName"] = model.ToUpperInvariant(),
                            ["speedMps"] = 0,
                            ["engineHealth"] = 1000,
                        };
                        result = ((JObject)_state["vehicle"]!).DeepClone();
                        break;
                    case "world.setTime":
                        var time = $"{request.Parameters.Value<int>("hour"):00}:{request.Parameters.Value<int>("minute"):00}";
                        ((JObject)_state["world"]!)["time"] = time;
                        result = new JObject { ["time"] = time };
                        break;
                    case "world.setWeather":
                        var weather = request.Parameters.Value<string>("weather") ?? "Clear";
                        ((JObject)_state["world"]!)["weather"] = weather;
                        result = new JObject { ["weather"] = weather };
                        break;
                    default:
                        return Failure(request, "method_not_found", $"Unknown harness API method '{request.Method}'.");
                }
                return BridgeResponse.Success(request.Id, result, request.ProtocolVersion);
            }
            catch
            {
                return Failure(request, "harness_error", "The harness request failed validation.");
            }
        }

        public void Dispose()
        {
            _fixture.Dispose();
            ReactorHostApi.Reset();
        }

        private JObject OverlayState(bool visible) => new JObject
        {
            ["visible"] = visible,
            ["inputMode"] = _inputMode,
        };

        private static JObject RuntimeStatus() => new JObject
        {
            ["apiVersion"] = BridgeProtocol.CurrentProtocolVersion,
            ["supportedApiVersions"] = new JArray(1, 2),
            ["sessionId"] = StartupTrace.SessionId,
            ["runtime"] = "DirectX harness",
            ["runtimeVersion"] = "0.2.0",
            ["renderer"] = "Native test surface",
            ["edition"] = "Enhanced",
            ["extensionApiVersion"] = ReactorApi.ExtensionApiVersion,
            ["capabilities"] = new JArray(
                "runtime.discovery", "overlay.visibility", "overlay.input",
                "extension.discovery", "extension.actions", "menu.discovery",
                "menu.actions", "events.subscriptions", "events.lifecycle", "input.semantic"),
            ["dependencies"] = new JArray
            {
                Status("scripthookv", "Script Hook V"),
                Status("scripthookdotnet", "ScriptHookVDotNet Enhanced"),
                Status("allin1", "ALLIN1 client"),
                Status("compositor", "REACTOR V compositor"),
                Status("chromium", "Chromium runtime"),
            },
        };

        private static JObject RuntimeDescription() => new JObject
        {
            ["apiVersion"] = BridgeProtocol.CurrentProtocolVersion,
            ["sessionId"] = StartupTrace.SessionId,
            ["capabilities"] = RuntimeStatus()["capabilities"]!.DeepClone(),
            ["methods"] = new JArray(
                Method("runtime.handshake", "runtime.discovery"),
                Method("runtime.describe", "runtime.discovery"),
                Method("overlay.setVisibility", "overlay.visibility"),
                Method("overlay.setInputMode", "overlay.input"),
                Method("extensions.list", "extension.discovery"),
                Method("extensions.get", "extension.discovery"),
                Method("extensions.invoke", "extension.actions", "optional"),
                Method("menu.list", "menu.discovery"),
                Method("menu.get", "menu.discovery"),
                Method("menu.invoke", "menu.actions", "optional"),
                Method("events.subscribe", "events.subscriptions"),
                Method("events.unsubscribe", "events.subscriptions")),
            ["events"] = new JArray(
                Event("runtime.lifecycle", "events.lifecycle", true),
                Event("input.action", "input.semantic", false),
                Event("game.state", "game.state", true)),
            ["limits"] = new JObject
            {
                ["requestBytes"] = BridgeProtocol.MaximumMessageLength,
                ["queueDepth"] = BridgeBroker.MaximumPendingRequests,
                ["subscriptions"] = 128,
            },
        };

        private static IReactorExtensionHandle RegisterAllIn1Fixture() => ReactorApi.RegisterExtension(
            new ReactorExtensionDescriptor(
                "allin1.fixture",
                "ALLIN1 integration fixture",
                "1.0.0",
                "Exercises GBAY, traffic, garage, and settings-style Reactor integration.",
                new[] { "gbay", "garages", "traffic", "settings" }),
            builder =>
            {
                builder.AddAction(
                    new ReactorActionDescriptor(
                        "gbay.purchase",
                        "Purchase vehicle",
                        ReactorActionRisk.Persistent,
                        new[]
                        {
                            new ReactorParameterDescriptor("listing", ReactorValueType.String, required: true),
                        }),
                    (_, parameters) => ReactorActionResult.Success(new JObject
                    {
                        ["receipt"] = "fixture-" + parameters.Value<string>("listing"),
                        ["savePending"] = true,
                    }));
                builder.AddAction(
                    new ReactorActionDescriptor(
                        "traffic.set-enabled",
                        "Spawn in traffic",
                        ReactorActionRisk.Persistent,
                        new[]
                        {
                            new ReactorParameterDescriptor("value", ReactorValueType.Boolean, required: true),
                        }),
                    (_, parameters) => ReactorActionResult.Success(new JObject
                    {
                        ["enabled"] = parameters.Value<bool>("value"),
                    }));
                builder.AddEvent(new ReactorEventDescriptor("gbay.orderchanged"));
                builder.AddMenu(new ReactorMenuDescriptor(
                    "gbay",
                    "GBAY",
                    new ReactorMenuNode[]
                    {
                        new ReactorActionNode("purchase", "Purchase vehicle", "gbay.purchase"),
                        new ReactorToggleNode("traffic", "Spawn in traffic", "traffic.set-enabled", true),
                        new ReactorStatusNode("save-status", "Save status", "Pending until the next GTA save"),
                    },
                    "Example storefront and vehicle integration."));
            });

        private static string RequireString(JObject parameters, string name)
        {
            var value = parameters.Value<string>(name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"'{name}' is required.");
            return value!;
        }

        private static JObject Method(string name, string capability, string idempotency = "none") => new JObject
        {
            ["method"] = name,
            ["capability"] = capability,
            ["confirmed"] = false,
            ["idempotency"] = idempotency,
        };

        private static JObject Event(string name, string capability, bool replay) => new JObject
        {
            ["event"] = name,
            ["capability"] = capability,
            ["replay"] = replay,
        };

        private static BridgeResponse Failure(BridgeRequest request, string code, string message) =>
            BridgeResponse.Failure(request.Id, new BridgeError(code, message), request.ProtocolVersion);

        private static JObject Status(string id, string name) => new JObject
        {
            ["id"] = id,
            ["name"] = name,
            ["loaded"] = true,
            ["required"] = true,
            ["detail"] = "Harness verification",
        };
    }
}
