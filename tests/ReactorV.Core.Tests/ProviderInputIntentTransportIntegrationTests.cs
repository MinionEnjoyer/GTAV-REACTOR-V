using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using ReactorV.BootstrapHost;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ProviderInputIntentTransportIntegrationTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task TypedPipeFramesRemainOneShotAndFailClosedAtHostLedger()
    {
        const int processId = 4242;
        var pipeName = "ReactorV.IntentTest." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        var wait = server.WaitForConnectionAsync();
        await client.ConnectAsync(2000);
        await wait;

        var gate = new ProviderInputIntentGate(processId);
        Assert.True(gate.BeginProviderSession(1));
        var results = new List<bool>();
        var clock = Stopwatch.StartNew();
        var host = Task.Run(() =>
        {
            for (var index = 0; index < 8; index++)
            {
                var frame = BootstrapHostWire.Read(server)!;
                var type = frame.Value<string>("type");
                var now = clock.ElapsedMilliseconds;
                switch (type)
                {
                    case "provider_input_intent_arm":
                        results.Add(gate.TryArm(
                            new ProviderInputIntentToken(
                                frame.Value<int>("pid"),
                                frame.Value<long>("epoch"),
                                frame.Value<int>("lifetimeMs")),
                            now,
                            1));
                        break;
                    case "provider_input_intent_bind":
                        results.Add(gate.TryBind(
                            frame.Value<int>("pid"),
                            frame.Value<long>("epoch"),
                            frame.Value<string>("presentationId"),
                            now,
                            1));
                        break;
                    case "provider_input_intent_cancel":
                        gate.Cancel(
                            frame.Value<int>("pid"),
                            frame.Value<long>("epoch"));
                        results.Add(true);
                        break;
                }
            }
        });

        Write(client, "provider_input_intent_bind", processId, 1, "startup");
        Write(client, "provider_input_intent_arm", processId + 1, 1, null, 1000);
        Write(client, "provider_input_intent_arm", processId, 3, null, 1000);
        Write(client, "provider_input_intent_arm", processId, 2, null, 1000);
        Write(client, "provider_input_intent_cancel", processId, 3);
        Write(client, "provider_input_intent_bind", processId, 3, "closed-menu");
        Write(client, "provider_input_intent_arm", processId, 4, null, 10);
        Thread.Sleep(25);
        Write(client, "provider_input_intent_bind", processId, 4, "expired-menu");

        await host.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
            new[] { false, false, true, false, true, false, true, false },
            results);
    }

    private static void Write(
        NamedPipeClientStream pipe,
        string type,
        int processId,
        long epoch,
        string? presentationId = null,
        int? lifetimeMilliseconds = null)
    {
        var frame = new JObject
        {
            ["type"] = type,
            ["pid"] = processId,
            ["epoch"] = epoch,
        };
        if (presentationId != null)
            frame["presentationId"] = presentationId;
        if (lifetimeMilliseconds.HasValue)
            frame["lifetimeMs"] = lifetimeMilliseconds.Value;
        BootstrapHostWire.Write(pipe, frame);
    }
}
