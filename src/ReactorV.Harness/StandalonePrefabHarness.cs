using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using RageWebUI.Runtime;
using ReactorV.Integration;
using ReactorV.Starter;

namespace RageWebUI.Harness
{
    // Real packaged React + WebView + typed Core callbacks; no GTA process.
    internal static class StandalonePrefabHarness
    {
        public static int Run(HarnessOptions options)
        {
            var directory = options.LocalDataDirectory ?? HarnessRunDirectory.For("StandalonePrefabs");
            Directory.CreateDirectory(directory);
            var runtimeDirectory = Path.GetDirectoryName(typeof(StandalonePrefabHarness).Assembly.Location)!;
            var ui = options.UiDirectory ?? Path.Combine(runtimeDirectory, "ui");
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(ui, "reactor-ui.json")));
            if (manifest.Value<string>("profile") != "reactor-runtime") throw new InvalidOperationException("Requires standalone UI.");
            using var capture = HarnessVisualCaptureSession.Enable(Color.FromArgb(72, 54, 88), directory);
            using var host = new Form { BackColor = Color.FromArgb(72, 54, 88), ClientSize = new Size(1280, 720),
                StartPosition = FormStartPosition.CenterScreen, Text = "REACTOR V standalone prefab test" };
            host.Show();
            Application.DoEvents();
            if (!WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromSeconds(3))) throw new InvalidOperationException("Test host not foreground.");
            capture.QualifyDesktop(host);
            var broker = new BridgeBroker();
            using var runtime = new OverlayRuntime("windowed", host.Handle, ui, runtimeDirectory, directory, broker, 1280, 720, 60, false, false);
            using var router = new HarnessApiRouter(runtime.SetVisible, () => runtime.IsVisible);
            // Reuse generic bridge plumbing, not its example consumer registration.
            ReactorHostApi.Reset();
            using var a = new StarterExtension("sample.prefabs-a", "Reactor prefab examples");
            using var b = new StarterExtension("sample.prefabs-b", "Independent sample");
            var expected = "";
            var accepted = "";
            var subscriptions = 0;
            var acknowledgements = 0;
            var confirmations = 0;
            var closes = 0;
            void Pump()
            {
                Application.DoEvents();
                for (var i = 0; i < 64 && broker.TryDequeue(out var request); i++)
                {
                    if (request == null) continue;
                    BridgeResponse response;
                    if (request.Method == "overlay.presentationReady")
                    {
                        var id = request.Parameters.Value<string>("presentationId");
                        var valid = id == expected;
                        if (valid) { accepted = id!; acknowledgements++; }
                        response = BridgeResponse.Success(request.Id, new JObject { ["accepted"] = valid, ["presentationId"] = id }, request.ProtocolVersion);
                    }
                    else if (request.Method == StartupStatusContract.Method)
                        response = BridgeResponse.Success(request.Id, StartupStatusContract.CreateRuntimeSnapshot(true, true, true), request.ProtocolVersion);
                    else response = router.Dispatch(request);
                    if (request.Method == "events.subscribe") subscriptions++;
                    if (request.Method == "overlay.close") closes++;
                    if (response.Result is JObject result && result.Value<bool?>("confirmationRequired") == true) confirmations++;
                    if (response.Error != null) throw new InvalidOperationException("Bridge error: " + response.Error.Message);
                    runtime.PostResponse(response);
                }
                Thread.Sleep(5);
            }
            void Until(Func<bool> done, string failure, int timeout = 5000)
            {
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < timeout) { Pump(); if (done()) return; }
                throw new InvalidOperationException(failure);
            }
            void Settle(int milliseconds = 200)
            {
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < milliseconds) Pump();
            }
            void Input(string action)
            {
                if (!WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromSeconds(3)))
                    throw new InvalidOperationException("Synthetic host lost foreground before input.");
                runtime.PostEvent("input.action", new JObject { ["action"] = action, ["phase"] = "pressed", ["source"] = "game" });
                Settle();
            }
            void Present(string menu, bool side = false)
            {
                expected = "prefab-" + Guid.NewGuid().ToString("N");
                runtime.PostEvent("menu.presentation", new JObject {
                    ["extensionId"] = "sample.prefabs-a", ["menuId"] = menu, ["presentationId"] = expected,
                    ["inputMode"] = "interactive-menu", ["context"] = new JObject { ["reactorLayout"] = side ? "side-editor" : "default" },
                });
                Until(() => accepted == expected, "No exact painted acknowledgement for " + menu);
                // Browser/desktop-witness startup can temporarily activate its
                // own window. Requalify the synthetic game, just as the visual
                // lifecycle harness does, before asking production to reveal.
                if (!WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromSeconds(3)))
                    throw new InvalidOperationException("Synthetic host lost foreground before reveal.");
                runtime.SetVisible(true);
                Until(() => runtime.IsVisible, "Menu did not become visible: " + menu);
                Settle(350);
                using var image = capture.Capture(host);
                var measured = GbayLifecycleHarness.VisualFrame.Measure(image);
                if (measured.ChangedFraction < .04 || measured.BlackFraction > .9)
                    throw new InvalidOperationException("Unpainted or black prefab: " + menu);
                image.Save(Path.Combine(directory, menu + (side ? "-side" : "") + ".png"));
            }
            if (!runtime.Start()) throw new InvalidOperationException("Runtime start failed.");
            Settle(1200);
            runtime.PostEvent("host.provider", new JObject { ["connected"] = true });
            runtime.PostEvent("host.surface", new JObject { ["mode"] = "none" });
            Until(() => subscriptions > 0, "No browser menu subscription.");
            foreach (var menu in new[] { "main", "settings", "list", "grid", "status", "catalog", "tabs", "checklist" }) Present(menu);
            Present("settings", true);
            Input("accept");
            Until(() => !a.Enabled, "Toggle did not invoke the owning sample.");
            if (!b.Enabled) throw new InvalidOperationException("Sample A changed sample B.");
            Input("navigate-down");
            Input("navigate-right");
            Until(() => a.Strength == 55, "Range callback not applied.");
            Input("navigate-down");
            Input("accept");
            Until(() => confirmations == 1, "No confirmation boundary.");
            if (a.Enabled) throw new InvalidOperationException("Reset executed without confirmation.");
            Input("back"); // cancel
            if (a.Enabled) throw new InvalidOperationException("Cancel applied the reset.");
            Input("accept");
            Until(() => confirmations == 2, "Confirmation did not reopen.");
            Input("accept");
            Until(() => a.Enabled && a.Strength == 50, "Confirmed reset did not apply.");
            Input("back");
            Until(() => closes > 0 && !runtime.IsVisible, "Close did not release the menu.");
            Present("settings", true);
            if (acknowledgements != 10) throw new InvalidOperationException("Unexpected presentation acknowledgement count.");
            Console.WriteLine("RESULT PASS: scenario=standalone-prefabs menus=8 exactAcks=10 typedSettings=True isolatedConsumers=True confirmation=True closeReopen=True contentProfile=reactor-runtime");
            return 0;
        }
    }
}
