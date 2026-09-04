using System;
using Newtonsoft.Json.Linq;
using RageWebUI.Core.Protocol;

namespace RageWebUI.Core
{
    /// <summary>
    /// Typed, read-only contract used by the bootstrap page while the managed
    /// GTA provider is still starting. The snapshot is assembled exclusively
    /// from explicit host signals and the in-memory startup trace; it never
    /// reads an arbitrary log or path supplied by the page.
    /// </summary>
    public static class StartupStatusContract
    {
        public const int SchemaVersion = 1;
        public const string Method = "startup.getStatus";
        public const string EventName = "startup.status";

        /// <summary>Runtime-owned services only. Consumers own their own startup content.</summary>
        public static JObject CreateRuntimeSnapshot(bool reactorReady, bool nativeBridgeReady,
            bool providerConnected, bool defaultMenuRequested = false, DateTime? defaultMenuDeadlineUtc = null)
        {
            // Keep CreateSnapshot's v1 ABI for older consumers, but never invent a
            // required consumer for a standalone runtime installation.
            var snapshot = CreateSnapshot(reactorReady, nativeBridgeReady, providerConnected,
                false, defaultMenuRequested, defaultMenuDeadlineUtc);
            snapshot.Remove("allIn1Loaded");
            ((JArray)snapshot["components"]!).RemoveAt(3);
            return snapshot;
        }

        /// <summary>
        /// The bootstrap process is authoritative only until the authenticated
        /// managed provider connects. Afterwards it must stay silent so its
        /// necessarily incomplete ALLIN1 view cannot regress managed status.
        /// </summary>
        public static bool IsBootstrapEventAuthority(bool providerConnected) =>
            !providerConnected;

        public static JObject CreateSnapshot(
            bool reactorReady,
            bool nativeBridgeReady,
            bool providerConnected,
            bool allIn1Loaded,
            bool defaultMenuRequested = false,
            DateTime? defaultMenuDeadlineUtc = null)
        {
            var phase = !reactorReady
                ? "reactor-starting"
                : !providerConnected
                    ? "waiting-for-provider"
                    : "provider-connected";
            return new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["sequence"] = StartupTrace.ConsoleSequence,
                ["sessionId"] = StartupTrace.SessionId,
                ["phase"] = phase,
                ["providerConnected"] = providerConnected,
                ["allIn1Loaded"] = allIn1Loaded,
                ["defaultMenuRequested"] = defaultMenuRequested,
                ["defaultMenuDeadlineUtc"] = defaultMenuRequested &&
                    defaultMenuDeadlineUtc.HasValue
                        ? defaultMenuDeadlineUtc.Value.ToUniversalTime().ToString("O")
                        : null,
                // Loading a DLL or connecting the provider is not proof that
                // Story Mode and every gameplay service are ready.
                ["gameplayReadiness"] = "not-reported",
                ["components"] = new JArray(
                    Component(
                        "reactor",
                        "REACTOR V",
                        reactorReady ? "ready" : "initializing",
                        reactorReady
                            ? "Overlay runtime and local page are ready."
                            : "Preparing the overlay runtime and local page."),
                    Component(
                        "scripthook",
                        "ScriptHook / native bridge",
                        nativeBridgeReady ? "ready" : "initializing",
                        nativeBridgeReady
                            ? "The native bootstrap bridge is active."
                            : "Waiting for the native bootstrap bridge."),
                    Component(
                        "managed-bridge",
                        "Managed game bridge",
                        providerConnected ? "ready" : "initializing",
                        providerConnected
                            ? "The managed GTA provider is connected."
                            : "Waiting for ScriptHookVDotNet and the managed provider."),
                    Component(
                        "allin1",
                        "ALLIN1",
                        allIn1Loaded ? "ready" : providerConnected ? "initializing" : "waiting",
                        allIn1Loaded
                            ? "The ALLIN1 client assembly is loaded; gameplay readiness is reported separately."
                            : providerConnected
                                ? "Managed provider connected; waiting for ALLIN1 registration."
                                : "Waiting for the managed game bridge.")),
                ["console"] = StartupTrace.CreateConsoleSnapshot(),
            };
        }

        /// <summary>
        /// Handles exactly startup.getStatus and returns false for every other
        /// bridge method so the normal provider path remains authoritative.
        /// </summary>
        public static bool TryCreateLocalResponse(
            string json,
            JObject snapshot,
            out string? responseJson)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            responseJson = null;
            if (!BridgeProtocol.TryParseRequest(json, out var request, out _) ||
                request == null ||
                !string.Equals(request.Method, Method, StringComparison.Ordinal))
            {
                return false;
            }

            var response = request.Parameters.Count == 0
                ? BridgeResponse.Success(request.Id, snapshot.DeepClone(), request.ProtocolVersion)
                : BridgeResponse.Failure(
                    request.Id,
                    new BridgeError(
                        "invalid_params",
                        "startup.getStatus does not accept parameters."),
                    request.ProtocolVersion);
            responseJson = BridgeProtocol.SerializeResponse(response);
            return true;
        }

        public static string SerializeEvent(JObject snapshot, int protocolVersion = 1)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return BridgeProtocol.SerializeEvent(EventName, snapshot.DeepClone(), protocolVersion);
        }

        private static JObject Component(string id, string label, string state, string detail) =>
            new JObject
            {
                ["id"] = id,
                ["label"] = label,
                ["state"] = state,
                ["detail"] = detail,
            };
    }
}
