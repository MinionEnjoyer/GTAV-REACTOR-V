// Disposable .NET Framework fixture. Never loads GTA or installed product files.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

internal static class ProbeFixture
{
    private static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--desktop-presentation-probe")
        {
            Console.Error.WriteLine("reactorv-probe-stage=dispatch ms=0");
            var mode = Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location);
            var request = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(args[1])));
            var backend = request.Value<string>("backend") ?? "auto";
            if (mode == "slow-startup" || mode == "slow-gdi") Thread.Sleep(450);
            if (mode == "slow-gdi")
            {
                Console.Error.WriteLine("reactorv-probe-stage=gdi-bitblt ms=450");
                Console.Error.Flush();
                Thread.Sleep(80);
            }
            if (mode == "startup-timeout") Thread.Sleep(4000);
            if (mode == "timeout" || (mode.StartsWith("fallback", StringComparison.Ordinal) && backend == "auto"))
            {
                Console.Error.WriteLine("reactorv-probe-stage=" + (backend == "dxgi" ? "dxgi-capture" : "gdi-bitblt") + " ms=1");
                Console.Error.Flush();
                Thread.Sleep(4000);
            }
            if (mode == "fallback-real")
            {
                SetProcessDpiAwarenessContext(new IntPtr(-4));
                var runtimeDirectory = Environment.GetEnvironmentVariable("REACTOR_PROBE_FIXTURE_RUNTIME");
                var production = Assembly.LoadFrom(Path.Combine(runtimeDirectory, "ReactorV.Preloader.exe"));
                var entry = production.GetType("ReactorV.Preloader.DesktopPresentationProbeChild", true)
                    .GetMethod("TryRun", BindingFlags.Static | BindingFlags.NonPublic);
                var invokeArgs = new object[] { args, 0 };
                if (!(bool)entry.Invoke(null, invokeArgs)) throw new Exception("Production child dispatch rejected");
                return (int)invokeArgs[1];
            }
            if (mode == "ReactorV.Preloader") Thread.Sleep(1500);
            if (mode == "exit" || mode == "fallback-exit") return 5;
            if (mode == "malformed" || mode == "fallback-malformed") { Console.Write("invalid JSON"); return 0; }
            Console.Error.WriteLine("untrusted arbitrary stderr must not appear in trace");
            Console.Error.WriteLine("reactorv-probe-stage=result-write ms=2");
            Console.Write("{\"readable\":8,\"matching\":" + (mode == "quorum" || mode == "fallback-quorum" ? "5" : "8") +
                ",\"concrete\":true,\"source\":\"fixture-only\",\"error\":null}");
            return 0;
        }
        if (args.Length > 0 && args[0] == "--unexpected-role") { Thread.Sleep(1500); return 0; }
        if (args.Length > 0 && args[0].StartsWith("--collector-", StringComparison.Ordinal))
        {
            using (var child = Process.Start(new ProcessStartInfo {
                FileName = Assembly.GetExecutingAssembly().Location,
                Arguments = args[0] == "--collector-good" ? "--desktop-presentation-probe e30=" : "--unexpected-role",
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            }))
                return child.WaitForExit(6000) ? child.ExitCode : 2;
        }
        try { Run(args[0], args[1]); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static object Get(object value, string property) { return value.GetType().GetProperty(property, Hidden).GetValue(value, null); }
    private static object Verify(string runtimeDirectory, string executable, Rectangle bounds, Color[] colors,
        List<string> traces, bool pump, Point sampleOrigin, string backend = null)
    {
        var assembly = Assembly.LoadFrom(Path.Combine(runtimeDirectory, "RageWebUI.Runtime.dll"));
        var sampleType = assembly.GetType("RageWebUI.Runtime.DesktopPresentationProbeSample", true);
        var samples = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sampleType));
        for (var i = 0; i < 8; i++)
            samples.Add(Activator.CreateInstance(sampleType, Hidden, null,
                new object[] { (sampleOrigin.X + 8 + i * 24 + .5) / bounds.Width, (sampleOrigin.Y + 8.5) / bounds.Height, colors[i] }, null));
        var client = assembly.GetType("RageWebUI.Runtime.DesktopPresentationProbeClient", true);
        Action<string, string> trace = (stage, detail) => { lock (traces) traces.Add(stage + " " + detail); };
        var wire = new List<object>();
        for (var i = 0; i < 8; i++) wire.Add(new {
            x = (sampleOrigin.X + 8 + i * 24 + .5) / bounds.Width,
            y = (sampleOrigin.Y + 8.5) / bounds.Height,
            r = colors[i].R, g = colors[i].G, b = colors[i].B });
        var task = (Task)client.GetMethod(backend == null ? "VerifyAsync" : "RunAttemptAsync",
            BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,
            backend == null ? new object[] { executable, bounds, samples, 900, trace } :
                new object[] { executable, bounds, wire, 900, backend, trace });
        while (pump && !task.IsCompleted) { Application.DoEvents(); Thread.Sleep(5); }
        task.GetAwaiter().GetResult();
        return task.GetType().GetProperty("Result").GetValue(task, null);
    }

    private static void Run(string runtimeDirectory, string output)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        Environment.SetEnvironmentVariable("REACTOR_PROBE_FIXTURE_RUNTIME", runtimeDirectory);
        ValidatePixelDiagnostics(runtimeDirectory);
        var results = new List<object>();
        var allTraces = new List<string>();
        var colors = new[] { Color.Red, Color.Blue, Color.Lime, Color.Magenta, Color.Yellow, Color.Cyan, Color.White, Color.Orange };
        var modes = new[] { "success", "quorum", "malformed", "exit", "timeout",
            "fallback", "fallback-quorum", "fallback-exit", "fallback-malformed", "startup-timeout",
            "slow-startup", "slow-gdi" };
        foreach (var mode in modes)
        {
            var trace = new List<string>();
            var result = Verify(runtimeDirectory, Path.Combine(output, mode + ".exe"), new Rectangle(0, 0, 256, 80), colors, trace, false, Point.Empty);
            var error = (string)Get(result, "Error");
            var expected = mode == "timeout" || mode == "startup-timeout" ? "hard-timeout" :
                mode.EndsWith("exit", StringComparison.Ordinal) ? "preloader-exit-5" :
                mode.EndsWith("malformed", StringComparison.Ordinal) ? "invalid-json-result" : null;
            if (error != expected || (bool)Get(result, "IsConcrete") != (mode == "success" || mode == "fallback" || mode.StartsWith("slow-", StringComparison.Ordinal))) throw new Exception("Wrong outcome: " + mode);
            if ((int)Get(result, "ChildPid") <= 0 || Get(result, "ChildExitCode") == null) throw new Exception("Child lifecycle missing: " + mode);
            if (mode == "timeout" && (!(bool)Get(result, "TerminationRequested") ||
                (string)Get(result, "LastChildStage") != "dxgi-capture" || (long)Get(result, "TotalElapsedMilliseconds") > 1200))
                throw new Exception("Deadline stage/bound missing");
            var attempts = mode == "timeout" || mode.StartsWith("fallback", StringComparison.Ordinal) ? 2 : 1;
            if ((int)Get(result, "AttemptCount") != attempts || trace.Count != (attempts == 2 ? 5 : 2) ||
                string.Join("\n", trace).Contains("untrusted arbitrary")) throw new Exception("Trace protocol failed: " + mode);
            if (attempts == 2 && (!trace[1].Contains("child_finished") || !trace[1].Contains("termination_requested=True") ||
                trace[1].Contains("exit_code=unobserved") || !trace[2].Contains("backend_fallback") || !trace[3].Contains("backend=dxgi")))
                throw new Exception("Fallback started before owned predecessor exit: " + mode);
            results.Add(Snapshot(mode, result)); allTraces.AddRange(trace);
        }
        // Small owned, nonactivating checkerboard. The production child captures
        // only its identity strip; no desktop screenshot is written to disk.
        using (var form = new WitnessForm())
        {
            form.FormBorderStyle = FormBorderStyle.None;
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = new Rectangle(Screen.PrimaryScreen.Bounds.Left + 24, Screen.PrimaryScreen.Bounds.Top + 24, 256, 80);
            form.TopMost = true;
            form.ShowInTaskbar = false;
            form.Paint += (_, e) => {
                e.Graphics.Clear(Color.Black);
                for (var i = 0; i < 8; i++) using (var brush = new SolidBrush(colors[i])) e.Graphics.FillRectangle(brush, i * 24, 0, 20, 20);
            };
            form.Show(); form.Refresh(); Application.DoEvents(); Thread.Sleep(150);
            // Model a game-sized target, with the only sampled pixels inside
            // the small owned fixture. No surrounding screen pixels are saved.
            var bounds = Screen.PrimaryScreen.Bounds;
            var origin = form.PointToScreen(Point.Empty);
            var sampleOrigin = new Point(origin.X - bounds.X, origin.Y - bounds.Y);
            for (var i = 0; i < 5; i++)
            {
                var trace = new List<string>();
                var result = Verify(runtimeDirectory, Path.Combine(runtimeDirectory, "ReactorV.Preloader.exe"), bounds, colors, trace, true, sampleOrigin);
                results.Add(Snapshot("real-desktop-" + i, result)); allTraces.AddRange(trace);
            }
            var wrongTrace = new List<string>();
            var wrongColors = new[] { Color.Black, Color.Black, Color.Black, Color.Black, Color.Black, Color.Black, Color.Black, Color.Black };
            var rejected = Verify(runtimeDirectory, Path.Combine(runtimeDirectory, "ReactorV.Preloader.exe"), bounds, wrongColors, wrongTrace, true, sampleOrigin);
            results.Add(Snapshot("real-desktop-wrong-identity", rejected)); allTraces.AddRange(wrongTrace);
            if ((bool)Get(rejected, "IsConcrete") || (int)Get(rejected, "ReadableSampleCount") != 8 || (int)Get(rejected, "MatchingSampleCount") != 0)
                throw new Exception("Production desktop probe accepted the wrong identity");
            foreach (var backend in new[] { "dxgi", "invalid-backend" })
            {
                var trace = new List<string>();
                var result = Verify(runtimeDirectory, Path.Combine(runtimeDirectory, "ReactorV.Preloader.exe"), bounds, colors, trace, true, sampleOrigin, backend);
                results.Add(Snapshot("real-desktop-" + backend, result)); allTraces.AddRange(trace);
                File.WriteAllText(Path.Combine(output, "probe-results.json"), JsonConvert.SerializeObject(results, Formatting.Indented));
                File.WriteAllLines(Path.Combine(output, "probe-trace.txt"), allTraces);
                if (backend == "dxgi" && (!(bool)Get(result, "IsConcrete") ||
                    (int)Get(result, "ReadableSampleCount") != 8 || (int)Get(result, "MatchingSampleCount") != 8))
                    throw new Exception("Real DXGI witness failed: " + JsonConvert.SerializeObject(Snapshot(backend, result)));
                if (backend != "dxgi" && (bool)Get(result, "IsConcrete"))
                    throw new Exception("Unknown backend accepted");
            }
            var dxgiWrongTrace = new List<string>();
            var dxgiWrong = Verify(runtimeDirectory, Path.Combine(runtimeDirectory, "ReactorV.Preloader.exe"), bounds, wrongColors, dxgiWrongTrace, true, sampleOrigin, "dxgi");
            results.Add(Snapshot("real-desktop-dxgi-wrong-identity", dxgiWrong)); allTraces.AddRange(dxgiWrongTrace);
            if ((bool)Get(dxgiWrong, "IsConcrete") || (int)Get(dxgiWrong, "ReadableSampleCount") != 8 ||
                (int)Get(dxgiWrong, "MatchingSampleCount") != 0)
                throw new Exception("DXGI did not reject wrong identity");
            // Exercise the actual client timeout-to-production-DXGI route in
            // owned processes. The replacement fixture invokes the production
            // child in-process: no untracked grandchild or orphan on timeout.
            for (var i = 0; i < 3; i++)
            {
                var trace = new List<string>();
                var result = Verify(runtimeDirectory, Path.Combine(output, "fallback-real.exe"), bounds, colors, trace, true, sampleOrigin);
                results.Add(Snapshot("real-fallback-" + i, result)); allTraces.AddRange(trace);
                File.WriteAllText(Path.Combine(output, "probe-results.json"), JsonConvert.SerializeObject(results, Formatting.Indented));
                File.WriteAllLines(Path.Combine(output, "probe-trace.txt"), allTraces);
                if (!(bool)Get(result, "IsConcrete") || (int)Get(result, "AttemptCount") != 2 ||
                    (int)Get(result, "ReadableSampleCount") != 8 || (int)Get(result, "MatchingSampleCount") != 8 ||
                    (long)Get(result, "TotalElapsedMilliseconds") > 1200)
                    throw new Exception("Real bounded fallback failed: " + JsonConvert.SerializeObject(Snapshot("real-fallback", result)));
            }
            // Replay test1 and all four failed Test 4 palettes. This verifies
            // pixel matching on an owned desktop witness, not GTA composition
            // or its later hang. Primary colours alone can hide HDR clipping.
            foreach (var identity in new[] { 0x894AFD87511DE5E7UL, 0xF83A38AAB229AF24UL, 0x5AB00CBF19D04392UL,
                0xAF3B42FBDE605CD5UL, 0x6A497CB9DB57D7B6UL, 0x0D9D636DDF587AE3UL, 0x9866F5DADB71009AUL })
            {
                for (var i = 0; i < 8; i++)
                {
                    var value = (byte)(identity >> (i * 8));
                    colors[i] = Color.FromArgb(64 + (value >> 4) * 12, 64 + (value & 15) * 12, 208);
                }
                form.Refresh(); Application.DoEvents(); Thread.Sleep(80);
                foreach (var backend in new[] { "auto", "dxgi" })
                {
                    var trace = new List<string>();
                    var result = Verify(runtimeDirectory, Path.Combine(runtimeDirectory, "ReactorV.Preloader.exe"),
                        bounds, colors, trace, true, sampleOrigin, backend);
                    results.Add(Snapshot("palette-" + identity.ToString("X16") + "-" + backend, result));
                    allTraces.AddRange(trace);
                    File.WriteAllText(Path.Combine(output, "probe-results.json"), JsonConvert.SerializeObject(results, Formatting.Indented));
                    if (!(bool)Get(result, "IsConcrete") || (int)Get(result, "MatchingSampleCount") != 8)
                        throw new Exception("Recorded marker palette failed: " + identity.ToString("X16") + " " + backend);
                }
            }
            var staleColors = new Color[8];
            for (var i = 0; i < 8; i++)
            {
                var value = (byte)(0x894AFD87511DE5E7UL >> (i * 8));
                staleColors[i] = Color.FromArgb(64 + (value >> 4) * 12, 64 + (value & 15) * 12, 208);
            }
            var staleTrace = new List<string>();
            var staleResult = Verify(runtimeDirectory, Path.Combine(runtimeDirectory, "ReactorV.Preloader.exe"),
                bounds, staleColors, staleTrace, true, sampleOrigin, "dxgi");
            results.Add(Snapshot("palette-stale-generation-dxgi", staleResult)); allTraces.AddRange(staleTrace);
            if ((bool)Get(staleResult, "IsConcrete")) throw new Exception("Stale marker identity accepted after HDR normalization");
            form.Close();
        }
        File.WriteAllText(Path.Combine(output, "probe-results.json"), JsonConvert.SerializeObject(results, Formatting.Indented));
        File.WriteAllLines(Path.Combine(output, "probe-trace.txt"), allTraces);
        var desktop = results.GetRange(modes.Length, 5);
        foreach (dynamic result in desktop)
            if (!result.concrete || result.readable != 8 || result.matching != 8 || result.exitCode != 0)
                throw new Exception("A real desktop witness failed; inspect probe-results.json");
        Console.WriteLine("PASS: pixel diagnostic parser, twelve protocol/deadline cases, five real GDI witnesses, forced DXGI, three real timeout-to-DXGI recoveries, all seven recorded palettes at 8/8, stale/wrong-identity and unknown-backend rejection. GTA presentation/hang not tested.");
    }

    private static void ValidatePixelDiagnostics(string runtimeDirectory)
    {
        var type = Assembly.LoadFrom(Path.Combine(runtimeDirectory, "RageWebUI.Runtime.dll"))
            .GetType("RageWebUI.Runtime.DesktopPresentationProbeClient", true);
        var parse = type.GetMethod("ParseResult", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (var pixels in new[] {
            "[0,1,2,3,4,5,6,16777215]", "[null,null,null,null,null,null,null,null]", "null",
            "[]", "[0,1,2,3,4,5,6]", "[0,1,2,3,4,5,6,7,8]", "\"injected log text\"",
            "[0,1,2,3,4,5,6,-1]", "[0,1,2,3,4,5,6,16777216]",
            "[0,1,2,3,4,5,6,1.2]", "[0,1,2,3,4,5,6,\"FF0000\"]",
            "[0,1,2,3,4,5,6,999999999999999999999999999999]" })
        {
            var valid = pixels == "[0,1,2,3,4,5,6,16777215]" ||
                pixels == "[null,null,null,null,null,null,null,null]" || pixels == "null";
            var json = "{\"readable\":8,\"matching\":8,\"concrete\":true,\"source\":\"fixture\",\"observedRgb\":" + pixels + "}";
            var result = parse.Invoke(null, new object[] { json, 8 });
            if ((bool)Get(result, "IsConcrete") != valid) throw new Exception("Pixel diagnostics parser case failed: " + pixels);
            if (valid && pixels[1] == '0' && (string)Get(result, "ObservedRgb") != "000000,000001,000002,000003,000004,000005,000006,FFFFFF")
                throw new Exception("Pixel diagnostics were not canonically formatted");
        }
        // Optional telemetry never overrides an insufficient identity quorum.
        var mismatch = parse.Invoke(null, new object[] {
            "{\"readable\":8,\"matching\":2,\"concrete\":true,\"source\":\"fixture\",\"observedRgb\":[1,2,3,4,5,6,7,8]}", 8 });
        if ((bool)Get(mismatch, "IsConcrete")) throw new Exception("Telemetry overrode identity quorum");
    }

    private static object Snapshot(string name, object result)
    {
        return new { name, concrete = (bool)Get(result, "IsConcrete"), readable = (int)Get(result, "ReadableSampleCount"),
            matching = (int)Get(result, "MatchingSampleCount"), error = (string)Get(result, "Error"),
            observedRgb = (string)Get(result, "ObservedRgb"),
            source = (string)Get(result, "Source"), childPid = (int)Get(result, "ChildPid"),
            elapsedMs = (long)Get(result, "ElapsedMilliseconds"), firstProgressMs = (long)Get(result, "FirstProgressMilliseconds"),
            totalElapsedMs = (long)Get(result, "TotalElapsedMilliseconds"), attempts = (int)Get(result, "AttemptCount"),
            stage = (string)Get(result, "LastChildStage"), terminationRequested = (bool)Get(result, "TerminationRequested"),
            exitCode = (int?)Get(result, "ChildExitCode") };
    }
    private sealed class WitnessForm : Form { protected override bool ShowWithoutActivation { get { return true; } } }
}
