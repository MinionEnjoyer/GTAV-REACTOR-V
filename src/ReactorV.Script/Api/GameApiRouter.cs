using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using GTA;
using GTA.Math;
using GTA.UI;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using ReactorV.Integration;

namespace RageWebUI.Script.Api
{
    internal sealed class GameApiRouter
    {
        private const int MaximumSubscriptions = 128;
        private const int MinimumEventCadenceMilliseconds = 16;
        private const int MaximumEventCadenceMilliseconds = 60_000;

        private readonly Action<bool> _setOverlayVisible;
        private readonly Func<bool> _overlayVisible;
        private readonly Action<string> _setInputMode;
        private readonly Func<string> _inputMode;
        private readonly Func<string> _rendererName;
        private readonly Action<string, Exception> _logFailure;
        private readonly Action _browserReady;
        private readonly Dictionary<string, EventSubscriptionState> _subscriptions =
            new Dictionary<string, EventSubscriptionState>(StringComparer.Ordinal);
        private readonly Dictionary<string, JToken> _latestEvents =
            new Dictionary<string, JToken>(StringComparer.Ordinal);
        private readonly Queue<OutboundEvent> _replayEvents = new Queue<OutboundEvent>();

        public GameApiRouter(Action closeOverlay, Func<string>? rendererName = null)
            : this(
                visible =>
                {
                    if (!visible) closeOverlay();
                },
                () => false,
                _ => { },
                () => "exclusive",
                rendererName,
                null,
                null)
        {
        }

        public GameApiRouter(
            Action<bool> setOverlayVisible,
            Func<bool> overlayVisible,
            Action<string> setInputMode,
            Func<string> inputMode,
            Func<string>? rendererName = null,
            Action<string, Exception>? logFailure = null,
            Action? browserReady = null)
        {
            _setOverlayVisible = setOverlayVisible ?? throw new ArgumentNullException(nameof(setOverlayVisible));
            _overlayVisible = overlayVisible ?? throw new ArgumentNullException(nameof(overlayVisible));
            _setInputMode = setInputMode ?? throw new ArgumentNullException(nameof(setInputMode));
            _inputMode = inputMode ?? throw new ArgumentNullException(nameof(inputMode));
            _rendererName = rendererName ?? (() => "Unknown");
            _logFailure = logFailure ?? ((_, __) => { });
            _browserReady = browserReady ?? (() => { });
        }

        public BridgeResponse Dispatch(BridgeRequest request)
        {
            if (request.IsExpired)
            {
                return Failure(
                    request,
                    "deadline_exceeded",
                    "The request expired before the game thread could execute it.",
                    retryable: true);
            }

            try
            {
                JToken result;
                switch (request.Method)
                {
                    case "overlay.ready":
                        _browserReady();
                        result = GetRuntimeStatus();
                        break;
                    case "overlay.close":
                        _setOverlayVisible(false);
                        result = new JObject { ["visible"] = false };
                        break;
                    case "runtime.handshake":
                        _browserReady();
                        result = Handshake(request.Parameters, request.ProtocolVersion);
                        break;
                    case "runtime.describe":
                        result = DescribeRuntime();
                        break;
                    case "overlay.setVisibility":
                        result = SetOverlayVisibility(request.Parameters);
                        break;
                    case "overlay.setInputMode":
                        result = SetOverlayInputMode(request.Parameters);
                        break;
                    case "extensions.list":
                        result = ReactorHostApi.DescribeExtensionSummaries();
                        break;
                    case "extensions.get":
                        result = GetExtension(request.Parameters);
                        break;
                    case "extensions.invoke":
                        result = InvokeExtension(request);
                        break;
                    case "menu.list":
                        result = ListMenus(request.Parameters);
                        break;
                    case "menu.get":
                        result = GetMenu(request.Parameters);
                        break;
                    case "menu.invoke":
                        result = InvokeMenu(request);
                        break;
                    case "events.subscribe":
                        result = Subscribe(request.Parameters);
                        break;
                    case "events.unsubscribe":
                        result = Unsubscribe(request.Parameters);
                        break;
                    case "game.getState":
                        result = GetState();
                        break;
                    case "ui.notify":
                        result = Notify(request.Parameters);
                        break;
                    case "player.heal":
                        result = HealPlayer();
                        break;
                    case "player.setInvincible":
                        result = SetInvincible(request.Parameters);
                        break;
                    case "player.setWantedLevel":
                        result = SetWantedLevel(request.Parameters);
                        break;
                    case "player.teleport":
                        result = Teleport(request.Parameters);
                        break;
                    case "vehicle.repair":
                        result = RepairVehicle();
                        break;
                    case "vehicle.spawn":
                        result = SpawnVehicle(request.Parameters);
                        break;
                    case "world.setTime":
                        result = SetTime(request.Parameters);
                        break;
                    case "world.setWeather":
                        result = SetWeather(request.Parameters);
                        break;
                    default:
                        return Failure(
                            request,
                            "method_not_found",
                            $"Unknown API method '{request.Method}'.");
                }

                return BridgeResponse.Success(request.Id, result, request.ProtocolVersion);
            }
            catch (ApiException exception)
            {
                return Failure(request, exception.Code, exception.Message);
            }
            catch (Exception exception)
            {
                var errorId = Guid.NewGuid().ToString("N").Substring(0, 12);
                _logFailure(errorId, exception);
                return BridgeResponse.Failure(
                    request.Id,
                    new BridgeError(
                        "game_error",
                        $"The game action failed. See the Reactor log with error id {errorId}.",
                        details: new JObject { ["errorId"] = errorId }),
                    request.ProtocolVersion);
            }
        }

        public JObject GetState()
        {
            var player = Game.Player;
            var character = player.Character;
            if (character == null || !character.Exists())
            {
                throw new ApiException("not_ready", "The player character is not available.");
            }

            var position = character.Position;
            var vehicle = character.CurrentVehicle;
            return new JObject
            {
                ["gameTime"] = Game.GameTime,
                ["paused"] = Game.IsPaused,
                ["player"] = new JObject
                {
                    ["health"] = character.Health,
                    ["maxHealth"] = character.MaxHealth,
                    ["armor"] = character.Armor,
                    ["wantedLevel"] = player.WantedLevel,
                    ["invincible"] = character.IsInvincible,
                    ["position"] = VectorToJson(position),
                    ["heading"] = character.Heading,
                },
                ["vehicle"] = vehicle != null && vehicle.Exists()
                    ? new JObject
                    {
                        ["handle"] = vehicle.Handle,
                        ["displayName"] = vehicle.LocalizedName,
                        ["speedMps"] = vehicle.Speed,
                        ["engineHealth"] = vehicle.EngineHealth,
                    }
                    : JValue.CreateNull(),
                ["world"] = new JObject
                {
                    ["time"] = World.CurrentTimeOfDay.ToString(@"hh\:mm"),
                    ["weather"] = World.Weather.ToString(),
                },
            };
        }

        public JObject GetSnapshot() => new JObject
        {
            ["runtime"] = GetRuntimeStatus(),
            ["state"] = GetState(),
        };

        public void RememberEvent(string eventName, JToken? payload)
        {
            if (!BridgeProtocol.IsValidEventName(eventName))
            {
                return;
            }

            _latestEvents[eventName] = payload?.DeepClone() ?? JValue.CreateNull();
        }

        public bool ShouldPublishEvent(string eventName, JToken? payload)
        {
            RememberEvent(eventName, payload);
            return _subscriptions.Values.Any(subscription =>
                subscription.ShouldDeliver(eventName, payload, Game.GameTime));
        }

        public bool TryDequeueReplayEvent(out string? eventName, out JToken? payload)
        {
            if (_replayEvents.Count == 0)
            {
                eventName = null;
                payload = null;
                return false;
            }

            var value = _replayEvents.Dequeue();
            eventName = value.Name;
            payload = value.Payload;
            return true;
        }

        private JObject Handshake(JObject parameters, int negotiatedProtocolVersion)
        {
            var requestedVersions = parameters["apiVersions"] as JArray;
            var selectedVersion = negotiatedProtocolVersion;
            if (parameters["apiVersions"] != null && requestedVersions == null)
            {
                throw new ApiException("invalid_params", "'apiVersions' must be an array of integers.");
            }
            if (requestedVersions != null)
            {
                var offered = new List<int>();
                foreach (var value in requestedVersions)
                {
                    if (value.Type != JTokenType.Integer)
                    {
                        throw new ApiException("invalid_params", "'apiVersions' must contain only integers.");
                    }
                    offered.Add(value.Value<int>());
                }
                selectedVersion = offered
                    .Where(value => value >= BridgeProtocol.MinimumSupportedProtocolVersion &&
                        value <= BridgeProtocol.CurrentProtocolVersion)
                    .DefaultIfEmpty(0)
                    .Max();
                if (selectedVersion == 0)
                {
                    throw new ApiException("unsupported_protocol", "The page and Reactor do not share an API version.");
                }
            }

            ValidateClientIdentity(parameters["client"]);
            var capabilities = RuntimeCapabilities();
            var requestedCapabilities = ReadStringArray(
                parameters,
                "requestedCapabilities",
                required: false,
                maximumCount: 64);
            var acceptedCapabilities = requestedCapabilities.Count == 0
                ? capabilities
                : capabilities.Where(value => requestedCapabilities.Contains(value, StringComparer.Ordinal)).ToArray();

            var status = GetRuntimeStatus();
            status["apiVersion"] = selectedVersion;
            status["supportedApiVersions"] = new JArray(
                Enumerable.Range(
                    BridgeProtocol.MinimumSupportedProtocolVersion,
                    BridgeProtocol.CurrentProtocolVersion - BridgeProtocol.MinimumSupportedProtocolVersion + 1));
            status["sessionId"] = StartupTrace.SessionId;
            status["runtimeVersion"] = typeof(GameApiRouter).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            status["capabilities"] = new JArray(acceptedCapabilities);
            status["extensionApiVersion"] = ReactorApi.ExtensionApiVersion;
            return status;
        }

        private JObject DescribeRuntime()
        {
            var events = new JArray(
                EventDescriptor("overlay.snapshot", "game.state", replay: true),
                EventDescriptor("game.state", "game.state", replay: true),
                EventDescriptor("runtime.lifecycle", "events.lifecycle", replay: true),
                EventDescriptor("input.action", "input.semantic", replay: false));
            return new JObject
            {
                ["apiVersion"] = BridgeProtocol.CurrentProtocolVersion,
                ["extensionApiVersion"] = ReactorApi.ExtensionApiVersion,
                ["sessionId"] = StartupTrace.SessionId,
                ["capabilities"] = new JArray(RuntimeCapabilities()),
                ["methods"] = new JArray(RuntimeMethods()),
                ["events"] = events,
                ["limits"] = new JObject
                {
                    ["requestBytes"] = BridgeProtocol.MaximumMessageLength,
                    ["queueDepth"] = BridgeBroker.MaximumPendingRequests,
                    ["requestsPerFrame"] = 32,
                    ["subscriptions"] = MaximumSubscriptions,
                },
            };
        }

        private JObject SetOverlayVisibility(JObject parameters)
        {
            var visibility = RequestParameters.RequiredString(parameters, "visibility", 16).ToLowerInvariant();
            bool visible;
            switch (visibility)
            {
                case "visible":
                    visible = true;
                    break;
                case "hidden":
                    visible = false;
                    break;
                case "toggle":
                    visible = !_overlayVisible();
                    break;
                default:
                    throw new ApiException(
                        "invalid_params",
                        "'visibility' must be 'visible', 'hidden', or 'toggle'.");
            }
            _setOverlayVisible(visible);
            return OverlayState(visible);
        }

        private JObject SetOverlayInputMode(JObject parameters)
        {
            var mode = RequestParameters.RequiredString(parameters, "mode", 16).ToLowerInvariant();
            if (mode != "game" && mode != "menu" && mode != "pointer" && mode != "exclusive")
            {
                throw new ApiException(
                    "invalid_params",
                    "'mode' must be 'game', 'menu', 'pointer', or 'exclusive'.");
            }
            _setInputMode(mode);
            return OverlayState(_overlayVisible());
        }

        private JObject OverlayState(bool visible) => new JObject
        {
            ["visible"] = visible,
            ["inputMode"] = _inputMode(),
        };

        private static JToken InvokeExtension(BridgeRequest request)
        {
            var extensionId = RequestParameters.RequiredString(request.Parameters, "extensionId", 64);
            var actionId = RequestParameters.RequiredString(request.Parameters, "actionId", 64);
            var parameters = RequestParameters.OptionalObject(request.Parameters, "parameters");
            var confirmed = request.Parameters["confirmed"] == null
                ? request.Confirmed
                : RequestParameters.OptionalBoolean(request.Parameters, "confirmed");
            var idempotencyKey = RequestParameters.OptionalString(
                request.Parameters,
                "idempotencyKey",
                128) ?? request.IdempotencyKey;
            return ReactorHostApi.Invoke(
                extensionId,
                actionId,
                parameters,
                confirmed,
                idempotencyKey).ToJson();
        }

        private static JToken ListMenus(JObject parameters)
        {
            var extensionId = RequestParameters.OptionalString(parameters, "extensionId", 64);
            return ReactorHostApi.DescribeMenuSummaries(extensionId);
        }

        private static JToken GetExtension(JObject parameters)
        {
            var extensionId = RequestParameters.RequiredString(parameters, "extensionId", 64);
            return ReactorHostApi.DescribeExtension(extensionId) ??
                throw new ApiException(
                    "extension_not_found",
                    "The requested Reactor extension is not registered.");
        }

        private static JToken GetMenu(JObject parameters)
        {
            var extensionId = RequestParameters.RequiredString(parameters, "extensionId", 64);
            var menuId = RequestParameters.RequiredString(parameters, "menuId", 64);
            var matches = ReactorHostApi.DescribeMenus(extensionId, menuId);
            if (matches.Count == 0)
            {
                throw new ApiException("menu_not_found", "The requested extension menu is not registered.");
            }
            return matches[0]!.DeepClone();
        }

        private static JToken InvokeMenu(BridgeRequest request)
        {
            var extensionId = RequestParameters.RequiredString(request.Parameters, "extensionId", 64);
            var menuId = RequestParameters.RequiredString(request.Parameters, "menuId", 64);
            var nodeId = RequestParameters.RequiredString(request.Parameters, "nodeId", 64);
            var interaction = RequestParameters.RequiredString(request.Parameters, "interaction", 24).ToLowerInvariant();
            var parameters = RequestParameters.OptionalObject(request.Parameters, "parameters");
            if (parameters.Count == 0 && request.Parameters.TryGetValue("value", out var value))
            {
                parameters["value"] = value.DeepClone();
            }
            var confirmed = request.Parameters["confirmed"] == null
                ? request.Confirmed
                : RequestParameters.OptionalBoolean(request.Parameters, "confirmed");
            var idempotencyKey = RequestParameters.OptionalString(
                request.Parameters,
                "idempotencyKey",
                128) ?? request.IdempotencyKey;
            return ReactorHostApi.InvokeMenu(
                extensionId,
                menuId,
                nodeId,
                interaction,
                parameters,
                confirmed,
                idempotencyKey).ToJson();
        }

        private JObject Subscribe(JObject parameters)
        {
            if (_subscriptions.Count >= MaximumSubscriptions)
            {
                throw new ApiException("subscription_limit", "The Reactor event subscription limit was reached.");
            }
            var events = ReadStringArray(parameters, "events", required: true, maximumCount: 64);
            var known = KnownEvents();
            foreach (var eventName in events)
            {
                if (!BridgeProtocol.IsValidEventName(eventName) || !known.Contains(eventName))
                {
                    throw new ApiException("invalid_event", $"Event '{eventName}' is not registered.");
                }
            }

            var filters = RequestParameters.OptionalObject(parameters, "filters");
            var cadence = RequestParameters.OptionalInteger(
                parameters,
                "cadenceMs",
                MinimumEventCadenceMilliseconds,
                MaximumEventCadenceMilliseconds,
                100);
            var replayLatest = RequestParameters.OptionalBoolean(parameters, "replayLatest", false);
            var id = "sub-" + Guid.NewGuid().ToString("N");
            var subscription = new EventSubscriptionState(id, events, filters, cadence);
            _subscriptions.Add(id, subscription);

            if (replayLatest)
            {
                foreach (var eventName in events)
                {
                    if (_latestEvents.TryGetValue(eventName, out var payload) &&
                        subscription.Matches(eventName, payload))
                    {
                        _replayEvents.Enqueue(new OutboundEvent(eventName, payload));
                    }
                }
            }

            return new JObject
            {
                ["id"] = id,
                ["events"] = new JArray(events),
                ["cadenceMs"] = cadence,
            };
        }

        private JObject Unsubscribe(JObject parameters)
        {
            var id = RequestParameters.RequiredString(parameters, "subscriptionId", 64);
            return new JObject { ["removed"] = _subscriptions.Remove(id) };
        }

        private JObject GetRuntimeStatus()
        {
            var process = Process.GetCurrentProcess();
            var scriptHookV = NativeStatus("scripthookv", "Script Hook V", "ScriptHookV.dll");
            var shvdn = ManagedStatus(
                "scripthookvdotnet",
                "ScriptHookVDotNet Enhanced",
                typeof(GTA.Script).Assembly,
                required: true);
            var allin1 = ManagedStatus("allin1", "ALLIN1 client", FindManagedAssembly("ALLIN1"));
            var lemonUi = ManagedStatus("lemonui", "LemonUI", FindManagedAssembly("LemonUI.SHVDN3"));
            var renderer = _rendererName();
            var rendererDependencies = renderer.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) >= 0
                ? new JToken[] { NativeStatus("webview2", "WebView2 runtime", "WebView2Loader.dll") }
                : new JToken[]
                {
                    NativeStatus("compositor", "REACTOR V compositor", "RageWebUI.Native.dll"),
                    NativeStatus("chromium", "Chromium runtime", "libcef.dll"),
                };
            var dependencies = new JArray(scriptHookV, shvdn, allin1, lemonUi);
            foreach (var dependency in rendererDependencies)
            {
                dependencies.Add(dependency);
            }

            return new JObject
            {
                ["apiVersion"] = BridgeProtocol.CurrentProtocolVersion,
                ["runtime"] = "ScriptHookVDotNet3",
                ["renderer"] = renderer,
                ["edition"] = process.ProcessName.IndexOf("Enhanced", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "Enhanced"
                    : "Legacy",
                ["dependencies"] = dependencies,
            };
        }

        private static string[] RuntimeCapabilities() => new[]
        {
            "events.lifecycle",
            "events.subscriptions",
            "extension.actions",
            "extension.discovery",
            "extension.events",
            "game.actions",
            "game.state",
            "input.semantic",
            "menu.actions",
            "menu.discovery",
            "overlay.input",
            "overlay.visibility",
            "runtime.discovery",
        };

        private static JObject[] RuntimeMethods() => new[]
        {
            MethodDescriptor("runtime.handshake", "runtime.discovery"),
            MethodDescriptor("runtime.describe", "runtime.discovery"),
            MethodDescriptor("overlay.ready", "runtime.discovery"),
            MethodDescriptor("overlay.close", "overlay.visibility"),
            MethodDescriptor("overlay.setVisibility", "overlay.visibility"),
            MethodDescriptor("overlay.setInputMode", "overlay.input"),
            MethodDescriptor("extensions.list", "extension.discovery"),
            MethodDescriptor("extensions.get", "extension.discovery"),
            MethodDescriptor("extensions.invoke", "extension.actions", idempotency: "optional"),
            MethodDescriptor("menu.list", "menu.discovery"),
            MethodDescriptor("menu.get", "menu.discovery"),
            MethodDescriptor("menu.invoke", "menu.actions", idempotency: "optional"),
            MethodDescriptor("events.subscribe", "events.subscriptions"),
            MethodDescriptor("events.unsubscribe", "events.subscriptions"),
            MethodDescriptor("game.getState", "game.state"),
            MethodDescriptor("ui.notify", "game.actions"),
            MethodDescriptor("player.heal", "game.actions"),
            MethodDescriptor("player.setInvincible", "game.actions"),
            MethodDescriptor("player.setWantedLevel", "game.actions"),
            MethodDescriptor("player.teleport", "game.actions"),
            MethodDescriptor("vehicle.repair", "game.actions"),
            MethodDescriptor("vehicle.spawn", "game.actions"),
            MethodDescriptor("world.setTime", "game.actions"),
            MethodDescriptor("world.setWeather", "game.actions"),
        };

        private static JObject MethodDescriptor(
            string method,
            string capability,
            bool confirmed = false,
            string idempotency = "none") => new JObject
        {
            ["method"] = method,
            ["capability"] = capability,
            ["confirmed"] = confirmed,
            ["idempotency"] = idempotency,
        };

        private static JObject EventDescriptor(string eventName, string capability, bool replay) => new JObject
        {
            ["event"] = eventName,
            ["capability"] = capability,
            ["replay"] = replay,
        };

        private HashSet<string> KnownEvents()
        {
            var result = new HashSet<string>(StringComparer.Ordinal)
            {
                "overlay.snapshot",
                "game.state",
                "runtime.lifecycle",
                "input.action",
            };
            var summaries = ReactorHostApi.DescribeExtensionSummaries()["items"] as JArray ?? new JArray();
            foreach (var summary in summaries.OfType<JObject>())
            {
                var extensionId = summary.Value<string>("id") ?? string.Empty;
                var extension = ReactorHostApi.DescribeExtension(extensionId);
                if (extension == null)
                {
                    continue;
                }
                foreach (var descriptor in (extension["events"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var eventId = descriptor.Value<string>("id") ?? string.Empty;
                    var eventName = extensionId + "." + eventId;
                    if (BridgeProtocol.IsValidEventName(eventName))
                    {
                        result.Add(eventName);
                    }
                }
            }
            return result;
        }

        private static IReadOnlyList<string> ReadStringArray(
            JObject parameters,
            string name,
            bool required,
            int maximumCount)
        {
            var token = parameters[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                if (required)
                {
                    throw new ApiException("invalid_params", $"'{name}' is required.");
                }
                return Array.Empty<string>();
            }
            if (token.Type != JTokenType.Array)
            {
                throw new ApiException("invalid_params", $"'{name}' must be an array of strings.");
            }
            var result = new List<string>();
            foreach (var entry in (JArray)token)
            {
                if (entry.Type != JTokenType.String)
                {
                    throw new ApiException("invalid_params", $"'{name}' must contain only strings.");
                }
                var value = (entry.Value<string>() ?? string.Empty).Trim();
                if (value.Length == 0 || value.Length > 96)
                {
                    throw new ApiException("invalid_params", $"'{name}' contains an invalid value.");
                }
                if (!result.Contains(value, StringComparer.Ordinal))
                {
                    result.Add(value);
                }
                if (result.Count > maximumCount)
                {
                    throw new ApiException("invalid_params", $"'{name}' may contain at most {maximumCount} values.");
                }
            }
            if (required && result.Count == 0)
            {
                throw new ApiException("invalid_params", $"'{name}' must contain at least one value.");
            }
            return result;
        }

        private static void ValidateClientIdentity(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return;
            }
            if (token.Type != JTokenType.Object)
            {
                throw new ApiException("invalid_params", "'client' must be an object.");
            }
            var value = (JObject)token;
            RequestParameters.RequiredString(value, "id", 64);
            RequestParameters.RequiredString(value, "name", 96);
            RequestParameters.RequiredString(value, "version", 32);
            if (value.Properties().Any(property =>
                property.Name != "id" && property.Name != "name" && property.Name != "version"))
            {
                throw new ApiException("invalid_params", "'client' contains an unknown property.");
            }
        }

        private static BridgeResponse Failure(
            BridgeRequest request,
            string code,
            string message,
            bool retryable = false) => BridgeResponse.Failure(
                request.Id,
                new BridgeError(code, message, retryable),
                request.ProtocolVersion);

        private static JObject NativeStatus(string id, string name, string moduleName)
        {
            if (GetModuleHandle(moduleName) == IntPtr.Zero)
            {
                return Status(id, name, false, $"{moduleName} is not loaded", required: true);
            }
            return Status(id, name, true, "Loaded", required: true);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        private static JObject ManagedStatus(string id, string name, Assembly? assembly, bool required = false)
        {
            return assembly == null
                ? Status(id, name, false, "Managed assembly is not loaded", required)
                : Status(id, name, true, $"Loaded · {assembly.GetName().Version}", required);
        }

        private static JObject Status(
            string id,
            string name,
            bool loaded,
            string detail,
            bool required) => new JObject
        {
            ["id"] = id,
            ["name"] = name,
            ["loaded"] = loaded,
            ["required"] = required,
            ["detail"] = detail,
        };

        private static Assembly? FindManagedAssembly(string simpleName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                {
                    return assembly;
                }
            }
            return null;
        }

        private static JToken Notify(JObject parameters)
        {
            var message = RequestParameters.RequiredString(parameters, "message", 180);
            Notification.Show(message);
            return new JObject { ["shown"] = true };
        }

        private static JToken HealPlayer()
        {
            var character = RequireCharacter();
            character.Health = character.MaxHealth;
            character.Armor = 100;
            return new JObject { ["health"] = character.Health, ["armor"] = character.Armor };
        }

        private static JToken SetInvincible(JObject parameters)
        {
            var enabled = RequestParameters.OptionalBoolean(parameters, "enabled");
            var character = RequireCharacter();
            character.IsInvincible = enabled;
            return new JObject { ["enabled"] = character.IsInvincible };
        }

        private static JToken SetWantedLevel(JObject parameters)
        {
            var level = RequestParameters.RequiredInteger(parameters, "level", 0, 5);
            Game.Player.WantedLevel = level;
            return new JObject { ["level"] = Game.Player.WantedLevel };
        }

        private static JToken Teleport(JObject parameters)
        {
            var x = RequestParameters.RequiredNumber(parameters, "x", -100_000f, 100_000f);
            var y = RequestParameters.RequiredNumber(parameters, "y", -100_000f, 100_000f);
            var z = RequestParameters.RequiredNumber(parameters, "z", -10_000f, 10_000f);
            var keepVehicle = RequestParameters.OptionalBoolean(parameters, "keepVehicle", true);
            var character = RequireCharacter();
            Entity entity = keepVehicle && character.CurrentVehicle != null ? character.CurrentVehicle : character;
            entity.Position = new Vector3(x, y, z);
            return new JObject { ["position"] = VectorToJson(entity.Position) };
        }

        private static JToken RepairVehicle()
        {
            var vehicle = RequireCharacter().CurrentVehicle;
            if (vehicle == null || !vehicle.Exists())
            {
                throw new ApiException("no_vehicle", "The player is not in a vehicle.");
            }

            vehicle.Repair();
            return new JObject { ["repaired"] = true, ["engineHealth"] = vehicle.EngineHealth };
        }

        private static JToken SpawnVehicle(JObject parameters)
        {
            var modelName = RequestParameters.RequiredString(parameters, "model", 48);
            var warpIntoVehicle = RequestParameters.OptionalBoolean(parameters, "warpIntoVehicle", true);
            var character = RequireCharacter();
            var model = new Model(modelName);
            if (!model.IsInCdImage || !model.IsVehicle)
            {
                throw new ApiException("invalid_model", $"'{modelName}' is not a valid vehicle model.");
            }

            if (!model.Request(1000))
            {
                throw new ApiException("model_timeout", $"Vehicle model '{modelName}' did not load in time.");
            }

            try
            {
                var spawnPosition = character.Position + character.ForwardVector * 5f;
                var vehicle = World.CreateVehicle(model, spawnPosition, character.Heading);
                if (vehicle == null || !vehicle.Exists())
                {
                    throw new ApiException("spawn_failed", "GTA could not create the requested vehicle.");
                }

                if (warpIntoVehicle)
                {
                    character.SetIntoVehicle(vehicle, VehicleSeat.Driver);
                }

                return new JObject
                {
                    ["handle"] = vehicle.Handle,
                    ["displayName"] = vehicle.LocalizedName,
                };
            }
            finally
            {
                model.MarkAsNoLongerNeeded();
            }
        }

        private static JToken SetTime(JObject parameters)
        {
            var hour = RequestParameters.RequiredInteger(parameters, "hour", 0, 23);
            var minute = RequestParameters.RequiredInteger(parameters, "minute", 0, 59);
            World.CurrentTimeOfDay = new TimeSpan(hour, minute, 0);
            return new JObject { ["time"] = World.CurrentTimeOfDay.ToString(@"hh\:mm") };
        }

        private static JToken SetWeather(JObject parameters)
        {
            var name = RequestParameters.RequiredString(parameters, "weather", 32);
            if (!Enum.TryParse(name, true, out Weather weather))
            {
                throw new ApiException("invalid_weather", $"'{name}' is not a recognized weather preset.");
            }

            World.Weather = weather;
            return new JObject { ["weather"] = World.Weather.ToString() };
        }

        private static Ped RequireCharacter()
        {
            var character = Game.Player.Character;
            if (character == null || !character.Exists())
            {
                throw new ApiException("not_ready", "The player character is not available.");
            }

            return character;
        }

        private static JObject VectorToJson(Vector3 vector) => new JObject
        {
            ["x"] = vector.X,
            ["y"] = vector.Y,
            ["z"] = vector.Z,
        };

        private sealed class EventSubscriptionState
        {
            private readonly HashSet<string> _events;
            private readonly JObject _filters;
            private readonly Dictionary<string, int> _nextDeliveryAt =
                new Dictionary<string, int>(StringComparer.Ordinal);

            public EventSubscriptionState(
                string id,
                IEnumerable<string> events,
                JObject filters,
                int cadenceMilliseconds)
            {
                Id = id;
                _events = new HashSet<string>(events, StringComparer.Ordinal);
                _filters = (JObject)filters.DeepClone();
                CadenceMilliseconds = cadenceMilliseconds;
            }

            public string Id { get; }
            public int CadenceMilliseconds { get; }

            public bool Matches(string eventName, JToken? payload)
            {
                if (!_events.Contains(eventName))
                {
                    return false;
                }
                if (_filters.Count == 0)
                {
                    return true;
                }
                if (!(payload is JObject value))
                {
                    return false;
                }
                return _filters.Properties().All(filter =>
                    JToken.DeepEquals(value[filter.Name], filter.Value));
            }

            public bool ShouldDeliver(string eventName, JToken? payload, int gameTime)
            {
                if (!Matches(eventName, payload))
                {
                    return false;
                }
                if (_nextDeliveryAt.TryGetValue(eventName, out var next) && gameTime < next)
                {
                    return false;
                }
                _nextDeliveryAt[eventName] = gameTime + CadenceMilliseconds;
                return true;
            }
        }

        private sealed class OutboundEvent
        {
            public OutboundEvent(string name, JToken payload)
            {
                Name = name;
                Payload = payload.DeepClone();
            }

            public string Name { get; }
            public JToken Payload { get; }
        }
    }
}
