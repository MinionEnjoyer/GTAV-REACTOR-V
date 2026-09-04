using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests
{
public sealed class StartupStatusContractTests
{
    [Fact]
    public void BootstrapStatusAuthorityEndsAtProviderConnection()
    {
        Assert.True(StartupStatusContract.IsBootstrapEventAuthority(
            providerConnected: false));
        Assert.False(StartupStatusContract.IsBootstrapEventAuthority(
            providerConnected: true));
    }

        [Fact]
        public void Snapshot_reports_only_explicit_readiness_signals()
        {
            var waiting = StartupStatusContract.CreateSnapshot(
                reactorReady: true,
                nativeBridgeReady: true,
                providerConnected: false,
                allIn1Loaded: false);

            Assert.Equal("waiting-for-provider", waiting.Value<string>("phase"));
            Assert.False(waiting.Value<bool>("providerConnected"));
            Assert.False(waiting.Value<bool>("allIn1Loaded"));
            Assert.Equal("not-reported", waiting.Value<string>("gameplayReadiness"));
            Assert.Equal("initializing", Component(waiting, "managed-bridge").Value<string>("state"));
            Assert.Equal("waiting", Component(waiting, "allin1").Value<string>("state"));

            var connected = StartupStatusContract.CreateSnapshot(
                reactorReady: true,
                nativeBridgeReady: true,
                providerConnected: true,
                allIn1Loaded: false);
            Assert.Equal("provider-connected", connected.Value<string>("phase"));
            Assert.Equal("ready", Component(connected, "managed-bridge").Value<string>("state"));
            Assert.Equal("initializing", Component(connected, "allin1").Value<string>("state"));

            var loaded = StartupStatusContract.CreateSnapshot(
                reactorReady: true,
                nativeBridgeReady: true,
                providerConnected: true,
                allIn1Loaded: true);
            Assert.Equal("ready", Component(loaded, "allin1").Value<string>("state"));
            Assert.Equal("not-reported", loaded.Value<string>("gameplayReadiness"));
        }

        [Fact]
        public void Automatic_menu_copy_is_backed_by_explicit_intent_state()
        {
            var deadline = new DateTime(
                2026, 8, 29, 18, 30, 0, DateTimeKind.Utc);
            var armed = StartupStatusContract.CreateSnapshot(
                reactorReady: true,
                nativeBridgeReady: true,
                providerConnected: false,
                allIn1Loaded: false,
                defaultMenuRequested: true,
                defaultMenuDeadlineUtc: deadline);
            Assert.True(armed.Value<bool>("defaultMenuRequested"));
            Assert.Equal(deadline.ToString("O"),
                armed.Value<string>("defaultMenuDeadlineUtc"));

            var neutral = StartupStatusContract.CreateSnapshot(
                true, true, false, false);
            Assert.False(neutral.Value<bool>("defaultMenuRequested"));
            Assert.Null(neutral.Value<string>("defaultMenuDeadlineUtc"));
            Assert.Contains(
                "\"defaultMenuDeadlineUtc\":null",
                neutral.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void Local_status_route_is_typed_and_does_not_capture_other_methods()
        {
            var snapshot = StartupStatusContract.CreateSnapshot(true, true, false, false);
            const string request =
                "{\"kind\":\"request\",\"id\":\"startup-1\",\"method\":\"startup.getStatus\"," +
                "\"protocolVersion\":2,\"minimumProtocolVersion\":1,\"params\":{}}";

            Assert.True(StartupStatusContract.TryCreateLocalResponse(
                request,
                snapshot,
                out var responseJson));
            var response = JObject.Parse(responseJson!);
            Assert.Equal("response", response.Value<string>("kind"));
            Assert.Equal("startup-1", response.Value<string>("id"));
            Assert.Equal(StartupStatusContract.SchemaVersion,
                response["result"]!.Value<int>("schemaVersion"));
            Assert.Equal(2, response.Value<int>("protocolVersion"));

            Assert.False(StartupStatusContract.TryCreateLocalResponse(
                "{\"kind\":\"request\",\"id\":\"other\",\"method\":\"runtime.describe\",\"params\":{}}",
                snapshot,
                out var unrelated));
            Assert.Null(unrelated);
        }

        [Fact]
        public void Local_status_route_rejects_parameters_with_a_typed_error()
        {
            var snapshot = StartupStatusContract.CreateSnapshot(true, true, false, false);
            const string request =
                "{\"kind\":\"request\",\"id\":\"startup-2\",\"method\":\"startup.getStatus\"," +
                "\"params\":{\"path\":\"C:\\\\private\"}}";

            Assert.True(StartupStatusContract.TryCreateLocalResponse(
                request,
                snapshot,
                out var responseJson));
            var response = JObject.Parse(responseJson!);
            Assert.Equal("invalid_params", response["error"]!.Value<string>("code"));
        }

        [Fact]
        public void Startup_console_is_bounded_and_redacts_local_identity_and_paths()
        {
            var user = Environment.UserName;
            var raw = $"user={user} source=D:\\Private Work\\secret.json next=ok";
            var sanitized = StartupTrace.SanitizeConsoleMessage(raw);
            Assert.DoesNotContain(user, sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("D:\\", sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<local-path>", sanitized);
            Assert.Contains("next=ok", sanitized);

            var temporary = Path.Combine(Path.GetTempPath(), "reactorv-status-" + Guid.NewGuid().ToString("N"));
            try
            {
                for (var index = 0; index < StartupTrace.MaximumConsoleEntries + 12; index++)
                {
                    StartupTrace.Write(
                        temporary,
                        "aggregate.log",
                        "status-test",
                        "entry-" + index,
                        raw);
                }
                var console = StartupTrace.CreateConsoleSnapshot();
                var entries = (JArray)console["entries"]!;
                Assert.Equal(StartupTrace.MaximumConsoleEntries, entries.Count);
                Assert.True(console.Value<long>("dropped") >= 12);
                Assert.All(entries.OfType<JObject>(), entry =>
                {
                    Assert.DoesNotContain(user, entry.Value<string>("message")!, StringComparison.OrdinalIgnoreCase);
                    Assert.True(entry.Value<string>("message")!.Length <= StartupTrace.MaximumConsoleMessageLength);
                });
            }
            finally
            {
                try { Directory.Delete(temporary, recursive: true); } catch { }
            }
        }

        private static JObject Component(JObject snapshot, string id) =>
            ((JArray)snapshot["components"]!).OfType<JObject>()
                .Single(value => value.Value<string>("id") == id);
    }
}
