// Offline verification only. Runs the existing managed runtime in a secondary
// AppDomain with the native compositor physically absent from a disposable copy.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;

namespace ReactorV.Issue1Isolation
{
    internal static class WindowedProbe
    {
        public static int Run(string sourceRuntime, string output)
        {
            sourceRuntime = Isolation.Full(sourceRuntime); output = Isolation.Full(output);
            Isolation.SafePath(sourceRuntime); Isolation.SafePath(output);
            if (Directory.Exists(output)) throw new IOException("Use a new probe output directory.");
            Directory.CreateDirectory(output);
            var runtime = Path.Combine(output, "runtime"); Directory.CreateDirectory(runtime);
            foreach (var name in new[] { "RageWebUI.Runtime.dll", "RageWebUI.DirectX.dll", "RageWebUI.Core.dll", "Newtonsoft.Json.dll",
                "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll", "SharpDX.dll", "SharpDX.Direct3D11.dll", "SharpDX.DXGI.dll" })
                File.Copy(Path.Combine(sourceRuntime, name), Path.Combine(runtime, name), false);
            var gameRoot = Path.GetFullPath(Path.Combine(sourceRuntime, "../.."));
            File.Copy(Path.Combine(gameRoot, Isolation.Script), Path.Combine(runtime, "RageWebUI.Script.dll"), false);
            File.Copy(Path.Combine(gameRoot, "ScriptHookVDotNet3.dll"), Path.Combine(runtime, "ScriptHookVDotNet3.dll"), false);
            Directory.CreateDirectory(Path.Combine(output, "ui"));
            File.WriteAllText(Path.Combine(output, "ui/index.html"), "<!doctype html><html><head><title>Offline windowed probe</title></head><body><div id='root'>Reactor windowed isolation probe</div></body></html>");
            var domain = AppDomain.CreateDomain("ReactorV_Offline_SecondaryDomain", null, new AppDomainSetup { ApplicationBase = AppDomain.CurrentDomain.BaseDirectory });
            var worker = (WindowedProbeWorker)domain.CreateInstanceFromAndUnwrap(typeof(WindowedProbeWorker).Assembly.Location, typeof(WindowedProbeWorker).FullName);
            var passed = worker.Run(runtime, output);
            // The disposable process exits after managed window disposal. Do not
            // unload a domain while third-party COM callbacks could still unwind.
            return passed ? 0 : 1;
        }
    }

    public sealed class WindowedProbeWorker : MarshalByRefObject
    {
        public override object InitializeLifetimeService() => null!;
        public bool Run(string runtime, string output)
        {
            object? overlay = null; bool passed = false; string? error = null;
            string[] moduleNames = Array.Empty<string>(); string renderer = "";
            var trace = Path.Combine(output, "reactorv-runtime.log");
            try
            {
                if (AppDomain.CurrentDomain.IsDefaultAppDomain()) throw new Exception("Probe must run outside the default AppDomain.");
                if (File.Exists(Path.Combine(runtime, "RageWebUI.Native.dll"))) throw new Exception("Native compositor must be absent.");
                AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
                    var name = new AssemblyName(e.Name).Name;
                    var path = Path.Combine(runtime, name + ".dll");
                    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
                };
                var core = Assembly.LoadFrom(Path.Combine(runtime, "RageWebUI.Core.dll"));
                var broker = Activator.CreateInstance(core.GetType("RageWebUI.Core.BridgeBroker", true)!);
                var script = Assembly.LoadFrom(Path.Combine(runtime, "RageWebUI.Script.dll"));
                var loader = script.GetType("RageWebUI.Script.Browser.ExternalRuntimeLoader", true)!;
                loader.GetMethod("Prepare", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { runtime, output, "windowed" });
                var type = Assembly.LoadFrom(Path.Combine(runtime, "RageWebUI.Runtime.dll")).GetType("RageWebUI.Runtime.OverlayRuntime", true)!;
                overlay = Activator.CreateInstance(type, new object[] { "windowed", IntPtr.Zero, Path.Combine(output, "ui"), runtime, output, broker!, 640, 360, 30, false, false });
                if (!(bool)type.GetMethod("Start")!.Invoke(overlay, null)) throw new Exception("Runtime Start failed.");
                renderer = (string)type.GetProperty("RendererName")!.GetValue(overlay);
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 20000)
                {
                    Thread.Sleep(100);
                    if (File.Exists(trace) && File.ReadAllText(trace).Contains("stage=webview_controller_ready")) break;
                }
                var log = File.Exists(trace) ? File.ReadAllText(trace) : "";
                var bootstrapTrace = Path.Combine(output, "reactorv-bootstrap.log");
                var bootstrapLog = File.Exists(bootstrapTrace) ? File.ReadAllText(bootstrapTrace) : "";
                using var process = Process.GetCurrentProcess();
                moduleNames = process.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToArray();
                passed = renderer == "WebView2 window" && bootstrapLog.Contains("stage=cefsharp_deferred") &&
                    log.Contains("stage=webview_controller_ready") &&
                    !moduleNames.Any(n => n.Equals("RageWebUI.Native.dll", StringComparison.OrdinalIgnoreCase) || n.Equals("libcef.dll", StringComparison.OrdinalIgnoreCase));
                if (!passed) error = "Windowed route, WebView2 initialization, or no-native assertion failed.";
            }
            catch (Exception e) { error = e.ToString(); }
            finally { (overlay as IDisposable)?.Dispose(); }
            File.WriteAllText(Path.Combine(output, "probe-result.json"), JsonConvert.SerializeObject(new {
                passed, renderer, secondaryAppDomain = !AppDomain.CurrentDomain.IsDefaultAppDomain(),
                helperSha256 = Isolation.Hash(typeof(WindowedProbeWorker).Assembly.Location),
                nativeCompositorFileAbsent = !File.Exists(Path.Combine(runtime, "RageWebUI.Native.dll")),
                observedModules = moduleNames, error, gameLaunched = false,
                limitation = "Offline managed loader/windowed route only, not in-game acceptance or proof of a crash fix."
            }, Formatting.Indented));
            return passed;
        }
    }
}
