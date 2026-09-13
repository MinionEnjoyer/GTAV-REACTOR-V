// Production OverlayWindow + real WinForms timer/HWND; off-screen, no game,
// browser, simulated desktop input, or synthetic claim of pixel verification.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using RageWebUI.Runtime;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static StreamWriter log = null!;
    private static int checks;
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint color, out byte alpha, out uint flags);
    private static bool LayeredInputReady(IntPtr window) =>
        (GetWindowLong(window, -20) & 0x08280020) == 0x08280020 &&
        GetLayeredWindowAttributes(window, out _, out var alpha, out var flags) && alpha == 255 && flags == 2;
    private static bool RejectsUnsafeInputWindow(IntPtr handle)
    {
        try {
            typeof(OverlayWindow).Assembly.GetType("RageWebUI.Runtime.LayeredWindowInput", true)!
                .GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { handle, new Action<string,string?>((_, __) => {}) });
            return false;
        } catch (TargetInvocationException error) { return error.InnerException is InvalidOperationException; }
    }
    private sealed class Sink : IBridgeMessageSink
    {
        public bool TryEnqueue(string json, out BridgeError? error) { error = null; return true; }
    }
    private static void Check(bool pass, string name)
    {
        log.WriteLine((pass ? "PASS " : "FAIL ") + name);
        if (!pass) throw new InvalidOperationException(name);
        checks++;
    }
    private static object? Call(OverlayWindow window, string name, params object?[] args) =>
        typeof(OverlayWindow).GetMethod(name, Private)!.Invoke(window, args);
    private static void Set(OverlayWindow window, string name, object value) =>
        typeof(OverlayWindow).GetField(name, Private)!.SetValue(window, value);
    private static T Get<T>(OverlayWindow window, string name) =>
        (T)typeof(OverlayWindow).GetField(name, Private)!.GetValue(window)!;
    private static string Snapshot(IntPtr overlay, IntPtr owner) =>
        (string)typeof(OverlayWindow).Assembly.GetType("RageWebUI.Runtime.DesktopWindowState", true)!
            .GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { overlay, owner })!;
    private static void Event(OverlayWindow window, string name, JObject payload) =>
        Call(window, "ObserveHostMessage", new JObject {
            ["kind"] = "event", ["event"] = name, ["payload"] = payload
        }.ToString());
    private static void Surface(OverlayWindow window, int generation, string mode = HostSurfaceMode.PassiveHud) =>
        Event(window, "host.surface", new JObject { ["mode"] = mode, ["generation"] = generation });
    private static void Frame(OverlayWindow window, int generation, bool visible = true, int age = 0) =>
        Event(window, PassiveHudContract.EventId, PassiveHudHostLease.CreateFrame(new JObject {
            ["schema"] = 1, ["visible"] = visible, ["kind"] = "speedometer", ["speed"] = 42,
            ["units"] = "MPH", ["gear"] = "3", ["manual"] = false, ["notice"] = ""
        }, generation, DateTime.UtcNow.AddMilliseconds(-age)));

    // Setup only: simulate a composition-qualified HWND while deliberately
    // withholding desktop proof, then exercise actual production expiry/hiding.
    private static void ShowPending(OverlayWindow window)
    {
        window.Location = new Point(-32000, -32000);
        window.Show();
        Set(window, "_desiredVisible", true);
        Set(window, "_actualVisible", true);
        Set(window, "_visibilityPublished", true);
        Set(window, "_revealPending", true);
    }

    private static async Task Run(ApplicationContext context, string output)
    {
        try
        {
            using var owner = new Form { ShowInTaskbar = false, Location = new Point(-32000, -32000) };
            int hidden = 0, verified = 0, inputInitializations = 0;
            using var window = new OverlayWindow(owner.Handle, (uint)Process.GetCurrentProcess().Id,
                output, Path.Combine(output, "unused-profile"), new Sink(), false, false,
                (stage, detail) => { if (stage == "webview_layered_input_initialized") inputInitializations++; log.WriteLine(stage + " " + detail); },
                visible => { if (!visible) hidden++; }, () => {}, () => {}, error => throw error);
            window.HostSurfacePresentationChanged += receipt => { if (receipt != null) verified++; };
            var handle = window.Handle;
            Check(LayeredInputReady(handle), "production handle has full-opacity layered transparent non-activating composition style");
            Check(RejectsUnsafeInputWindow(IntPtr.Zero), "invalid input host fails closed");
            Check(RejectsUnsafeInputWindow(owner.Handle), "ordinary unlayered window fails closed");
            foreach (var bootstrap in new[] { false, true })
            foreach (var provider in new[] { false, true }) {
                Set(window, "_providerPointerShieldRequested", provider);
                window.SetBootstrapPointerCapture(bootstrap);
                Check(LayeredInputReady(handle), $"logical pointer leases preserve native passthrough bootstrap={bootstrap} provider={provider}");
            }
            Set(window, "_providerPointerShieldRequested", false);
            window.SetBootstrapPointerCapture(false);
            typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            handle = window.Handle;
            Check(inputInitializations == 2 && LayeredInputReady(handle), "handle recreation repeats verified layer initialization");
            Check(Snapshot(IntPtr.Zero, IntPtr.Zero).Contains("overlay_valid=False game_valid=False"),
                "window-state diagnostics handle absent windows");
            Check(!Get<Timer>(window, "_boundsTimer").Enabled, "bounds timer is stopped for expiry regression");

            Surface(window, 1);
            var mapping = new PassiveHudGenerationMap();
            mapping.BeginRequest(new JObject { ["mode"] = HostSurfaceMode.PassiveHud, ["generation"] = 101 });
            mapping.BindSurface(HostSurfaceMode.PassiveHud, 1);
            var mappedFrame = mapping.ToHostFrame(PassiveHudHostLease.CreateFrame(new JObject {
                ["schema"] = 1, ["visible"] = true, ["kind"] = "speedometer", ["speed"] = 42,
                ["units"] = "MPH", ["gear"] = "3", ["manual"] = false, ["notice"] = ""
            }, 101, DateTime.UtcNow))!;
            Event(window, PassiveHudContract.EventId, mappedFrame);
            Check(Get<PassiveHudHostLease>(window, "_passiveHudLease").AllowsPresentation(
                (long)(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency))) &&
                !Get<bool>(window, "_passiveHudFrameRejectionLogged"),
                "mapped provider 101 frame accepted by actual host generation 1");
            ShowPending(window);
            var reveal = Get<int>(window, "_revealGeneration");
            var expiredGeneration = 0;
            window.PassiveHudLeaseExpired += generation => expiredGeneration = generation;
            Check(IsWindowVisible(handle), "real off-screen native HWND initially visible");
            Check(Snapshot(handle, owner.Handle).Contains("overlay=[visible:True"),
                "window-state diagnostics observe actual native visibility");
            for (int i = 0; i < 7; i++) { await Task.Delay(150); Frame(window, 1); }
            Check(IsWindowVisible(handle), "fresh HUD stays alive without any GBay request");
            Check(verified == 0, "native visibility alone never emits verified receipt");
            // Do not call the watchdog, a game tick or a browser timer here.
            await Task.Delay(1300);
            Check(!IsWindowVisible(handle), "independent production timer hides native HWND on stopped producer");
            Check(Snapshot(handle, owner.Handle).Contains("overlay=[visible:False"),
                "window-state diagnostics observe actual native hide");
            Check(!Get<bool>(window, "_desiredVisible"), "expiry revokes requested visibility");
            Check(!Get<bool>(window, "_revealPending"), "expiry cancels in-flight reveal");
            Check(Get<int>(window, "_revealGeneration") > reveal, "late proof is invalidated by generation advance");
            Check(hidden == 1, "hide callback published once");
            Check(expiredGeneration == 1, "expiry reports authoritative generation for parent/native retirement");
            Frame(window, 1);
            Surface(window, 1);
            ShowPending(window); // Even a repeated stale native show is retired.
            await Task.Delay(200);
            Check(!IsWindowVisible(handle), "same-generation replay cannot revive expired HUD");

            Surface(window, 2);
            Frame(window, 2);
            ShowPending(window);
            await Task.Delay(100);
            Check(IsWindowVisible(handle), "fresh HUD activation recovers independently of GBay");
            Frame(window, 1, false);
            await Task.Delay(100);
            Check(IsWindowVisible(handle), "old-generation hide cannot dismiss replacement HUD");
            Frame(window, 2, false);
            Check(!IsWindowVisible(handle), "current-generation explicit hide immediately retires HWND");

            Surface(window, 1);
            Frame(window, 1);
            ShowPending(window);
            Event(window, "host.provider", new JObject { ["connected"] = false, ["sessionGeneration"] = 1 });
            Check(!IsWindowVisible(handle), "provider disconnect hides native HUD immediately");
            Event(window, "host.provider", new JObject { ["connected"] = true, ["sessionGeneration"] = 2 });
            Surface(window, 1); // Reloaded script restarts its generation counter.
            Frame(window, 1);
            ShowPending(window);
            await Task.Delay(150);
            Check(IsWindowVisible(handle), "new provider can reuse first generation without stale-lease lockout");

            Surface(window, 3);
            Frame(window, 3);
            ShowPending(window);
            Event(window, "menu.presentation", new JObject { ["presentationId"] = "fixture-menu" });
            Surface(window, 4, HostSurfaceMode.None);
            // Simulate the new menu owning this same HWND after surface transfer.
            ShowPending(window);
            await Task.Delay(1300);
            Check(IsWindowVisible(handle), "old HUD expiry does not hide replacement menu");
            Check(verified == 0, "fixture never claims desktop or input verification");
            window.Close();
            Check(!IsWindowVisible(handle), "fixture window closed");
            Check(Snapshot(handle, owner.Handle).Contains("overlay_valid=False"),
                "window-state diagnostics do not revive a closed HWND");
            await RunPipe(output);
            log.WriteLine("RESULT PASS checks=" + checks);
            Environment.ExitCode = 0;
        }
        catch (Exception error)
        {
            log.WriteLine(error);
            Environment.ExitCode = 1;
        }
        finally { context.ExitThread(); }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--server")
        {
            RunServer(int.Parse(args[1]), args[2]);
            return;
        }
        if (args.Length != 1 || Directory.Exists(args[0])) { Environment.ExitCode = 2; return; }
        Directory.CreateDirectory(args[0]);
        using (log = new StreamWriter(Path.Combine(args[0], "results.txt")) { AutoFlush = true })
        using (var context = new ApplicationContext())
        using (var launch = new Timer { Interval = 1 })
        {
            launch.Tick += async (_, __) => { launch.Stop(); await Run(context, args[0]); };
            launch.Start();
            Application.Run(context);
        }
    }

    private static void RunServer(int clientPid, string output)
    {
        using var trace = new StreamWriter(Path.Combine(output, "pipe-server.txt")) { AutoFlush = true };
        var type = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReactorV.Preloader.exe"))
            .GetType("ReactorV.Preloader.BootstrapOverlayServer", true)!;
        Action<string, string?> write = (stage, detail) => { lock (trace) trace.WriteLine(stage + " " + detail); };
        using var server = (IDisposable)Activator.CreateInstance(type, new object[] { clientPid, write, false })!;
        object? Invoke(string method, params object?[] parameters) => type.GetMethod(method)!.Invoke(server, parameters);
        Invoke("Start");
        Invoke("MarkContentReady");
        Console.WriteLine("READY"); Console.Out.Flush();
        string? command;
        while ((command = Console.ReadLine()) != null && command != "stop")
        {
            switch (command)
            {
                case "visible": Invoke("PublishVisibility", true); break;
                case "verify1": Invoke("PublishHostSurfacePresentation", new HostSurfacePresentation(HostSurfaceMode.PassiveHud, 1)); break;
                case "verify2": Invoke("PublishHostSurfacePresentation", new HostSurfacePresentation(HostSurfaceMode.PassiveHud, 2)); break;
                case "clear": Invoke("PublishHostSurfacePresentation", new object?[] { null }); break;
                case "hide": Invoke("PublishVisibility", false); break;
                case "unavailable": Invoke("MarkContentUnavailable"); break;
                case "ready": Invoke("MarkContentReady"); break;
                case "failed": Invoke("MarkPresentationUnavailable", "fixture-failure"); break;
                default: throw new InvalidOperationException("Unknown fixture command");
            }
            Console.WriteLine("ACK " + command); Console.Out.Flush();
        }
    }

    private static async Task RunPipe(string output)
    {
        // Injected receipts test the real authenticated cross-process server,
        // wire, reader and public runtime interface, not desktop pixels.
        using var child = new Process { StartInfo = new ProcessStartInfo {
            FileName = Application.ExecutablePath,
            Arguments = "--server " + Process.GetCurrentProcess().Id + " \"" + output + "\"",
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        child.Start();
        IOverlayRuntime? proxy = null;
        async Task<string?> ReadLine()
        {
            var read = child.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(read, Task.Delay(3000)) != read) throw new TimeoutException("Host fixture response");
            return await read;
        }
        async Task Send(string command)
        {
            child.StandardInput.WriteLine(command); child.StandardInput.Flush();
            Check(await ReadLine() == "ACK " + command, "server command: " + command);
        }
        async Task Until(Func<bool> predicate, string name)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.ElapsedMilliseconds < 2000) await Task.Delay(10);
            Check(predicate(), name);
        }
        try
        {
            Check(await ReadLine() == "READY", "separate host process started");
            var type = typeof(OverlayWindow).Assembly.GetType("RageWebUI.Runtime.BootstrapOverlayRuntime", true)!;
            proxy = (IOverlayRuntime)Activator.CreateInstance(type,
                new object[] { Process.GetCurrentProcess().Id, output, new BridgeBroker(), false })!;
            Check(proxy.Start(), "real authenticated provider pipe attached");
            var receipt = (IHostSurfacePresentationRuntime)proxy;
            bool One() => receipt.IsHostSurfacePresented(HostSurfaceMode.PassiveHud, 1);
            bool Two() => receipt.IsHostSurfacePresented(HostSurfaceMode.PassiveHud, 2);
            await Send("visible");
            await Until(() => proxy.IsVisible, "native visibility received");
            Check(!One(), "closeable native visibility is not verified HUD presentation over IPC");
            await Send("verify1");
            await Until(One, "exact generation receipt delivered through production reader");
            Check(!Two(), "old receipt cannot authorize replacement generation");
            await Send("clear");
            await Until(() => !One(), "explicit evidence reset propagated");
            await Send("verify2");
            await Until(Two, "new generation received");
            await Send("hide");
            await Until(() => !proxy.IsVisible && !Two(), "native hide revokes verified receipt");
            await Send("visible");
            await Until(() => proxy.IsVisible, "native reopened");
            Check(!Two(), "reopen cannot resurrect old proof");
            await Send("verify2"); await Until(Two, "receipt before content loss");
            await Send("unavailable"); await Until(() => !Two(), "content loss invalidates receipt");
            await Send("ready");
            await Until(() => ((IContentGenerationRuntime)proxy).TryGetReadyContentGeneration(out _), "content recovered");
            Check(!Two(), "content recovery does not revive old proof");
            await Send("verify2"); await Until(Two, "receipt before presentation failure");
            await Send("failed"); await Until(() => !Two(), "presentation failure clears proof without dropping pipe");
            await Send("verify2"); await Until(Two, "receipt before disconnect");
            child.StandardInput.WriteLine("stop"); child.StandardInput.Flush();
            await Until(() => !Two() && !proxy.IsVisible, "disconnect revokes visibility and proof");
            Check(child.WaitForExit(2000) && child.ExitCode == 0, "owned host exited cleanly");
        }
        finally
        {
            proxy?.Dispose();
            if (!child.HasExited) { child.Kill(); child.WaitForExit(2000); }
        }
    }
}
