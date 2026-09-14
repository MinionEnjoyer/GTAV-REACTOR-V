// Real browser/composition/capture integration; not injected into GTA.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;

internal sealed class PresentationForm : Form
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly string runtime;
    private readonly string output;
    private readonly bool software;
    private readonly bool rawPromotion;
    private readonly bool flip;
    private FlipBackdrop? backdrop;
    private readonly StreamWriter log;
    private object? host;
    private Type? hostType;
    private Assembly? assembly;
    private readonly Stopwatch timer = Stopwatch.StartNew();
    private int checks;
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; public override string ToString() => $"{Left},{Top},{Right},{Bottom}"; }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x20 | 0x80 | 0x00200000 | 0x00080000; return p; }
    }
    internal PresentationForm(string runtimeDirectory, string results, string mode)
    {
        if (mode != "software" && mode != "gpu" && mode != "raw-software" && mode != "raw-gpu" &&
            mode != "flip-software" && mode != "flip-gpu")
            throw new ArgumentException("Unknown browser presentation fixture mode.");
        runtime = runtimeDirectory; output = results;
        software = !mode.Contains("gpu"); rawPromotion = mode.StartsWith("raw-", StringComparison.Ordinal);
        flip = mode.StartsWith("flip-", StringComparison.Ordinal);
        if (Directory.Exists(results)) throw new InvalidOperationException("Use a fresh results directory.");
        Directory.CreateDirectory(results);
        log = new StreamWriter(new FileStream(Path.Combine(results, "results.txt"), FileMode.CreateNew)) { AutoFlush = true };
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000);
        ClientSize = new Size(640, 360);
        Shown += (_, __) => BeginInvoke(new Action(async () => await Run()));
    }
    private object? Call(string method, params object[] args) =>
        hostType!.GetMethod(method, Hidden)!.Invoke(host, args);
    private static object Get(object instance, string name) => instance.GetType().GetProperty(name, Hidden)!.GetValue(instance)!;
    private void Check(bool pass, string detail)
    {
        log.WriteLine($"{timer.ElapsedMilliseconds}ms {(pass ? "PASS" : "FAIL")} {detail}");
        if (!pass) throw new InvalidOperationException(detail);
        checks++;
    }
    private static async Task<T> Bounded<T>(Task<T> work, int ms)
    {
        if (await Task.WhenAny(work, Task.Delay(ms)) != work) throw new TimeoutException("Fixture stage exceeded " + ms + "ms");
        return await work;
    }
    private async Task<object> Witness(Rectangle target, object evidence, string backend)
    {
        var samples = (IEnumerable)Get(evidence, "DesktopSamples");
        var wire = new List<object>();
        foreach (var sample in samples)
        {
            var color = (Color)Get(sample, "Expected");
            wire.Add(new { x = (double)Get(sample, "NormalizedX"), y = (double)Get(sample, "NormalizedY"),
                r = color.R, g = color.G, b = color.B });
        }
        Check(wire.Count == 8, "complete-marker-sample-count");
        var client = assembly!.GetType("RageWebUI.Runtime.DesktopPresentationProbeClient", true)!;
        Action<string, string?> trace = (stage, detail) => { lock (log) log.WriteLine(stage + " " + detail); };
        var task = (Task)client.GetMethod("RunAttemptAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { Path.Combine(runtime, "ReactorV.Preloader.exe"), target, wire, 900, backend, trace })!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        log.WriteLine($"desktop backend={backend} concrete={Get(result,"IsConcrete")} " +
            $"matches={Get(result,"MatchingSampleCount")} rgb={Get(result,"ObservedRgb")} source={Get(result,"Source")} error={Get(result,"Error")}");
        return result;
    }
    private async Task Run()
    {
        try
        {
            if (flip)
            {
                backdrop = new FlipBackdrop();
                backdrop.StartRendering();
                Owner = backdrop;
                ClientSize = backdrop.ClientSize;
                backdrop.Activate();
                await Task.Delay(500);
                log.WriteLine($"flip_start frames={backdrop.Frames} failure={backdrop.RenderFailure} foreground_target={GetForegroundWindow() == backdrop.Handle} foreground_overlay={GetForegroundWindow() == Handle} target_visible={backdrop.Visible}");
                Check(backdrop.Frames >= 3 && backdrop.RenderFailure == null &&
                    GetForegroundWindow() == backdrop.Handle, "owned full-screen flip target is rendering in foreground");
                log.WriteLine($"backdrop=d3d11-flip-discard size={ClientSize} exclusive_fullscreen=False actual_scanout_mode=unmeasured");
            }
            log.WriteLine($"before_browser_exstyle=0x{GetWindowLong(Handle,-20):X8}");
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
                var path = Path.Combine(runtime, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            assembly = Assembly.LoadFrom(Path.Combine(runtime, "RageWebUI.Runtime.dll"));
            assembly.GetType("RageWebUI.Runtime.LayeredWindowInput", true)!
                .GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { Handle, new Action<string,string?>((stage, detail) => log.WriteLine(stage + " " + detail)) });
            Check((GetWindowLong(Handle,-20) & 0x08280020) == 0x08280020,
                "layered transparent no-activate no-redirection host styles");
            hostType = assembly.GetType("RageWebUI.Runtime.CompositionWebViewHost", true)!;
            var analyze = assembly.GetType("RageWebUI.Runtime.OverlayWindow", true)!
                .GetMethod("AnalyzePresentationPixels", BindingFlags.NonPublic | BindingFlags.Static)!;
            Action<string, string?> hostTrace = (stage, detail) => { lock (log) log.WriteLine(stage + " " + detail); };
            host = Activator.CreateInstance(hostType, Hidden, null, new object[] { this, hostTrace }, CultureInfo.InvariantCulture)!;
            var environment = await Bounded(CoreWebView2Environment.CreateAsync(null, Path.Combine(output, "profile"),
                new CoreWebView2EnvironmentOptions(software ? "--disable-gpu --disable-gpu-compositing" : "", "en-US")), 15000);
            var initialize = (Task)Call("EnsureCoreWebView2Async", environment)!;
            if (await Task.WhenAny(initialize, Task.Delay(15000)) != initialize) throw new TimeoutException("Controller initialization");
            await initialize;
            var controller = (CoreWebView2CompositionController)hostType.GetField("_controller", Hidden)!.GetValue(host)!;
            controller.ShouldDetectMonitorScaleChanges = false;
            log.WriteLine($"runtime_mvid={assembly.ManifestModule.ModuleVersionId} browser={environment.BrowserVersionString} synthetic_content=True software={software} raw_promotion={rawPromotion}");
            var navigation = new TaskCompletionSource<bool>();
            controller.CoreWebView2.NavigationCompleted += (_, e) => navigation.TrySetResult(e.IsSuccess);
            controller.CoreWebView2.NavigateToString(@"<!doctype html><style>
html,body{margin:0;background:transparent;overflow:hidden;font-family:system-ui;color:white}
#card{position:fixed;left:20px;top:20px;background:#203247;padding:20px;width:220px;font-size:22px}
#hud{position:fixed;right:30px;bottom:65px;font-size:55px;background:transparent}
#marker{position:fixed;right:0;bottom:0;display:flex;gap:4px;height:6px;width:92px}
#marker i{width:8px;height:6px;flex-shrink:0}</style>
<div id='card'>Reactor presentation regression</div><div id='hud'>42 MPH</div><div id='marker'></div>
<script>window.paint=(colors,hud)=>{document.querySelector('#card').hidden=hud;document.querySelector('#hud').hidden=!hud;
document.querySelector('#marker').innerHTML=colors.map(c=>'<i style=""background:'+c+'""></i>').join('');};</script>");
            Check(await Bounded(navigation.Task, 10000), "navigation");
            var initialHandle = Handle;
            object? previous = null;
            foreach (var scale in new[] { 1d, 1.25d, 1.5d })
            foreach (var hud in new[] { false, true })
            {
                var id = hud ? 0xF83A38AAB229AF24UL : 0x894AFD87511DE5E7UL;
                var colors = new string[8];
                for (var i = 0; i < 8; i++) { var b = (byte)(id >> (i * 8)); colors[i] = $"rgb({64+(b>>4)*12},{64+(b&15)*12},208)"; }
                Hide(); Location = new Point(-32000, -32000);
                controller.RasterizationScale = scale;
                Check((bool)Call("SynchronizeBounds")!, "bounds synchronized");
                Call("RebindRootVisual");
                Show();
                await controller.CoreWebView2.ExecuteScriptAsync("paint(" + JsonConvert.SerializeObject(colors) + "," + (hud ? "true" : "false") + ")");
                await Task.Delay(100);
                var png = await Bounded((Task<byte[]>)Call("CapturePreviewAsync")!, 5000);
                File.WriteAllBytes(Path.Combine(output, $"synthetic-{scale}-{hud}.png"), png);
                var evidence = analyze.Invoke(null, new object[] { png, id, ClientSize, hud })!;
                Check((bool)Get(evidence,"PaintIdentityMarkerMatched") && (bool)Get(evidence,"IsConcrete"), $"browser paint scale={scale} hud={hud}");
                var target = flip ? backdrop!.Bounds : new Rectangle(Screen.PrimaryScreen.WorkingArea.Left + 24, Screen.PrimaryScreen.WorkingArea.Top + 24, Width, Height);
                var promoted = rawPromotion
                    ? SetWindowPos(Handle, new IntPtr(-1), target.X, target.Y, target.Width, target.Height, 0x0010)
                    : (bool)assembly.GetType("RageWebUI.Runtime.VerifiedWindowPromotion", true)!
                        .GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, new object[] { Handle, target, true, hostTrace })!;
                Check(promoted, "native topmost promotion");
                log.WriteLine($"immediate_exstyle=0x{GetWindowLong(Handle,-20):X8}");
                Check((bool)Call("NotifyParentWindowPositionChanged")!, "promoted parent notified");
                Check((int)Call("WaitForCommitCompletion")! >= 0, "composition commit fence");
                await Task.Delay(80);
                log.WriteLine($"geometry expected={target} actual={Bounds} client={ClientSize} controller={controller.Bounds} dpi={DeviceDpi} raster={controller.RasterizationScale} visible={Visible} topmost={TopMost}");
                GetWindowRect(Handle, out var nativeRect);
                log.WriteLine($"native_rect={nativeRect} native_dpi={GetDpiForWindow(Handle)} primary={Screen.PrimaryScreen.Bounds}");
                log.WriteLine($"initial_hwnd={initialHandle} current_hwnd={Handle} native_visible={IsWindowVisible(Handle)} exstyle=0x{GetWindowLong(Handle,-20):X8}");
                var cloakHr = DwmGetWindowAttribute(Handle, 14, out var cloak, 4);
                log.WriteLine($"dwm_cloak_hr={cloakHr:X8} cloak={cloak}");
                Check(Bounds == target && nativeRect.Left == target.Left && nativeRect.Top == target.Top &&
                    nativeRect.Right == target.Right && nativeRect.Bottom == target.Bottom && initialHandle == Handle,
                    "promotion preserves HWND and physical bounds");
                Check((GetWindowLong(Handle,-20) & 0x08280020) == 0x08280020,
                    "layered input styles survive hide/show and promotion");
                var bothVisible = true;
                foreach (var backend in new[] { "auto", "dxgi" })
                {
                    var framesBefore = backdrop?.Frames ?? 0;
                    bothVisible &= (bool)Get(await Witness(target,evidence,backend),"IsConcrete");
                    if (flip)
                        Check(backdrop!.RenderFailure == null && backdrop.Frames > framesBefore &&
                            GetForegroundWindow() == backdrop.Handle,
                            $"flip target remained rendering and foreground during {backend} witness frames={framesBefore}->{backdrop.Frames}");
                }
                Check(bothVisible, $"visible browser scale={scale} hud={hud} both-backends");
                if (previous != null)
                    Check(!(bool)Get(await Witness(target,previous,"dxgi"),"IsConcrete"), "stale previous identity rejected");
                previous = evidence;
                Hide(); await Task.Delay(80);
                Check(!(bool)Get(await Witness(target,evidence,"dxgi"),"IsConcrete"), "hidden browser rejected");
            }
            log.WriteLine($"COMPLETE checks={checks} installation_changed=False game_launched=False input_ownership_tested=False");
        }
        catch (Exception e) { log.WriteLine("FAILED " + e); Environment.ExitCode = 1; }
        finally { Hide(); (host as IDisposable)?.Dispose(); Owner = null; backdrop?.Dispose(); log.Dispose(); Close(); }
    }
}
internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [STAThread] private static void Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 3) { Environment.ExitCode = 64; return; }
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new PresentationForm(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]),
            args.Length == 3 ? args[2] : "software"));
    }
}
