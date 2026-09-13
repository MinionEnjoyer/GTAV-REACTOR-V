// Manual/Computer Use input receiver: never generates or forwards desktop input.
// A separate owned process hosts the production OverlayWindow + composition WebView.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using RageWebUI.Runtime;

internal static class Program
{
    [STAThread] private static void Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 4 && args[0] == "--overlay")
        {
            Application.Run(new OverlayContext(new IntPtr(long.Parse(args[1])), uint.Parse(args[2]), args[3]));
            return;
        }
        if (args.Length != 1 || Directory.Exists(args[0])) { Environment.ExitCode = 2; return; }
        Directory.CreateDirectory(args[0]);
        Application.Run(new Receiver(Path.GetFullPath(args[0])));
    }
}

internal sealed class Receiver : Form
{
    private readonly string output;
    private readonly StreamWriter log;
    private Process? child;
    private string phase = "baseline";
    private int clicks, escapes;
    private readonly Label status = new Label { AutoSize = false, Bounds = new Rectangle(18, 80, 700, 52) };
    private readonly Rectangle pad = new Rectangle(18, 162, 680, 230);
    private readonly Timer diagnostics = new Timer { Interval = 250 };
    private string? previousDiagnostic;
    internal Receiver(string output)
    {
        this.output = output;
        log = new StreamWriter(Path.Combine(output, "receiver.txt")) { AutoFlush = true };
        foreach (var file in new[] { Application.ExecutablePath, typeof(OverlayWindow).Assembly.Location }) {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var bytes = File.OpenRead(file);
            log.WriteLine($"BINARY {Path.GetFileName(file)} sha256={BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "")}");
        }
        Text = "REACTOR Input Regression — Receiver"; StartPosition = FormStartPosition.Manual;
        Location = new Point(100, 100); ClientSize = new Size(720, 420); KeyPreview = true;
        BackColor = Color.FromArgb(30, 36, 45); ForeColor = Color.White;
        var names = new[] { "Baseline", "Current host", "Disabled host", "Layered host", "Hide overlay" };
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            var button = new Button { Text = name, Bounds = new Rectangle(18 + i * 138, 20, 130, 40), ForeColor = Color.White, BackColor = Color.FromArgb(65, 75, 90), UseVisualStyleBackColor = false };
            button.Click += (_, __) => ChangeMode(name); Controls.Add(button);
        }
        Controls.Add(status);
        diagnostics.Tick += (_, __) => {
            var bounds = RectangleToScreen(pad);
            var snapshot = InputDiagnostics.Capture(Handle, child?.Id ?? 0, bounds);
            if (snapshot != previousDiagnostic) { log.WriteLine($"DIAGNOSTIC phase={phase} {snapshot}"); previousDiagnostic = snapshot; }
        };
        diagnostics.Start();
        Shown += async (_, __) => await StartChild();
        FormClosing += (_, __) => { child?.StandardInput.WriteLine("quit"); child?.StandardInput.Flush(); };
        FormClosed += (_, __) => { diagnostics.Dispose(); if (child != null && !child.WaitForExit(3000)) child.Kill(); child?.Dispose(); log.Dispose(); };
        RefreshStatus("Starting isolated overlay process...");
    }
    private async Task StartChild()
    {
        try
        {
            child = new Process { StartInfo = new ProcessStartInfo {
                FileName = Application.ExecutablePath,
                Arguments = $"--overlay {Handle.ToInt64()} {Process.GetCurrentProcess().Id} \"{output}\"",
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true
            } };
            child.Start();
            string? line;
            while ((line = await child.StandardOutput.ReadLineAsync()) != null)
            {
                log.WriteLine("HOST " + line);
                RefreshStatus(line);
            }
        }
        catch (Exception error) { log.WriteLine(error); RefreshStatus(error.Message); }
    }
    private void ChangeMode(string name)
    {
        phase = name; clicks = escapes = 0;
        previousDiagnostic = null;
        var bounds = RectangleToScreen(pad);
        log.WriteLine($"PHASE {phase} receiver_pid={Process.GetCurrentProcess().Id} child_pid={child?.Id} target={bounds}");
        child?.StandardInput.WriteLine($"{(name == "Current host" ? "current" : name == "Disabled host" ? "disabled" : name == "Layered host" ? "layered" : "hide")} {bounds.X} {bounds.Y} {bounds.Width} {bounds.Height}");
        child?.StandardInput.Flush(); RefreshStatus("Mode requested");
    }
    private void RefreshStatus(string detail)
    {
        status.Text = $"Phase: {phase}     Received mouse-downs: {clicks}     ESC keys: {escapes}\n{detail}";
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.FillRectangle(Brushes.SteelBlue, pad);
        e.Graphics.DrawRectangle(Pens.White, pad);
        using var font = new Font("Segoe UI", 16);
        e.Graphics.DrawString("CLICK THIS RECEIVER AREA", font, Brushes.White, pad.X + 20, pad.Y + 35);
        e.Graphics.DrawString("Then press ESC. Counters must increase.", Font, Brushes.White, pad.X + 20, pad.Y + 80);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0201)
        {
            var value = m.LParam.ToInt64(); var p = new Point((short)value, (short)(value >> 16));
            if (pad.Contains(p)) { clicks++; log.WriteLine($"MOUSE_DOWN phase={phase} count={clicks} client={p}"); RefreshStatus("Mouse delivered to RECEIVER"); }
        }
        base.WndProc(ref m);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { escapes++; log.WriteLine($"ESC phase={phase} count={escapes}"); RefreshStatus("ESC delivered to RECEIVER"); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

internal sealed class OverlayContext : ApplicationContext
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly OverlayWindow window;
    private readonly string output;
    private readonly StreamWriter log;
    private readonly Timer start = new Timer { Interval = 1 };
    private object? host;
    private Type? hostType;
    private readonly System.Threading.SemaphoreSlim commandGate = new System.Threading.SemaphoreSlim(1, 1);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr w);
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr w, int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW", SetLastError=true)] private static extern IntPtr SetWindowLongPtr(IntPtr w, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private sealed class Sink : IBridgeMessageSink { public bool TryEnqueue(string json, out BridgeError? error) { error = null; return true; } }
    internal OverlayContext(IntPtr owner, uint ownerPid, string output)
    {
        this.output = output;
        log = new StreamWriter(Path.Combine(output, "overlay.txt")) { AutoFlush = true };
        window = new OverlayWindow(owner, ownerPid, output, Path.Combine(output, "profile"), new Sink(), false, false,
            (stage, detail) => log.WriteLine(stage + " " + detail), _ => {}, () => {}, () => {}, error => log.WriteLine(error));
        window.Text = "REACTOR Input Regression — Overlay";
        start.Tick += async (_, __) => { start.Stop(); await Initialize(); }; start.Start();
    }
    private object? Call(string name, params object[] args) => hostType!.GetMethod(name, Private)!.Invoke(host, args);
    private async Task Initialize()
    {
        try
        {
            var handle = window.Handle;
            host = typeof(OverlayWindow).GetField("_webView", Private)!.GetValue(window)!;
            hostType = host.GetType();
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(output, "profile"));
            await (Task)Call("EnsureCoreWebView2Async", environment)!;
            var core = (CoreWebView2)hostType.GetProperty("CoreWebView2", Private)!.GetValue(host)!;
            var ready = new TaskCompletionSource<bool>(); core.NavigationCompleted += (_, e) => ready.TrySetResult(e.IsSuccess);
            core.NavigateToString("<!doctype html><style>html,body{margin:0;background:transparent;color:white;font:32px Segoe UI}#hud{position:absolute;right:15px;bottom:15px;background:#183a2f;padding:12px}</style><div id='hud'>42 MPH</div>");
            if (!await ready.Task) throw new InvalidOperationException("Browser navigation failed");
            Console.WriteLine("READY: production overlay + real browser; separate process; no input forwarding."); Console.Out.Flush();
            _ = Task.Run(() => {
                string? line;
                while ((line = Console.ReadLine()) != null)
                {
                    var command = line;
                    window.BeginInvoke(new Action(async () => {
                        await commandGate.WaitAsync();
                        try { await Apply(command); }
                        catch (Exception error) {
                            log.WriteLine(error); window.Hide(); Console.WriteLine("FAILED " + error.Message); Console.Out.Flush();
                        }
                        finally { commandGate.Release(); }
                    }));
                    if (command == "quit") return;
                }
                window.BeginInvoke(new Action(Close));
            });
        }
        catch (Exception error) { log.WriteLine(error); Console.WriteLine("FAILED " + error.Message); Console.Out.Flush(); Close(); }
    }
    private async Task Apply(string command)
    {
        if (command == "quit") { Close(); return; }
        var parts = command.Split(' ');
        if (parts[0] == "hide") { window.Hide(); Console.WriteLine("Hidden"); Console.Out.Flush(); return; }
        window.Hide(); window.Enabled = parts[0] != "disabled";
        // Current host uses the production OnHandleCreated configuration, with
        // no fixture override on a fresh run. Disabled host deliberately removes
        // the layer to retain the earlier failed candidate as a negative control.
        var exStyle = GetWindowLong(window.Handle, -20);
        var desiredStyle = parts[0] == "disabled" ? exStyle & ~0x00080000 : exStyle | 0x00080000;
        if (desiredStyle != exStyle) {
            SetWindowLongPtr(window.Handle, -20, new IntPtr(desiredStyle));
            if (parts[0] != "disabled")
                typeof(OverlayWindow).Assembly.GetType("RageWebUI.Runtime.LayeredWindowInput", true)!
                    .GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, new object[] { window.Handle, new Action<string,string?>((stage, detail) => log.WriteLine(stage + " " + detail)) });
        }
        window.Bounds = new Rectangle(int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]), int.Parse(parts[4]));
        Call("SynchronizeBounds"); window.Show();
        var promotion = typeof(OverlayWindow).Assembly.GetType("RageWebUI.Runtime.VerifiedWindowPromotion", true)!;
        var promoted = (bool)promotion.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { window.Handle, window.Bounds, true,
                new Action<string,string?>((stage, detail) => log.WriteLine(stage + " " + detail)) })!;
        if (!promoted) throw new InvalidOperationException("Fixture topmost promotion failed");
        Call("NotifyParentWindowPositionChanged"); Call("WaitForCommitCompletion");
        await Task.Delay(100);
        var image = await (Task<byte[]>)Call("CapturePreviewAsync")!;
        File.WriteAllBytes(Path.Combine(output, parts[0] + "-browser.png"), image);
        var status = $"mode={parts[0]} enabled={IsWindowEnabled(window.Handle)} style=0x{GetWindowLong(window.Handle,-16):X8} exstyle=0x{GetWindowLong(window.Handle,-20):X8} foreground=0x{GetForegroundWindow().ToInt64():X} hwnd=0x{window.Handle.ToInt64():X}";
        log.WriteLine(status); Console.WriteLine(status); Console.Out.Flush();
    }
    private void Close() { window.Close(); start.Dispose(); log.Dispose(); ExitThread(); }
}

// Read-only, scoped to the fixture's pad and two processes. These APIs are
// diagnostic context, not injected input or evidence of actual event delivery.
internal static class InputDiagnostics
{
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; internal NativePoint(int x, int y) { X=x; Y=y; } }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo {
        internal uint Size, Flags;
        internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal NativeRect CaretRect;
    }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPhysicalPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    internal static string Capture(IntPtr receiver, int overlayPid, Rectangle pad)
    {
        var receiverThread = GetWindowThreadProcessId(receiver, out var receiverPid);
        string Describe(IntPtr handle) {
            if (handle == IntPtr.Zero) return "none";
            var thread = GetWindowThreadProcessId(handle, out var pid);
            if (pid != receiverPid && pid != overlayPid) return "other";
            return $"{(pid == receiverPid ? "receiver" : "overlay")}:0x{handle.ToInt64():X}:thread={thread}:enabled={IsWindowEnabled(handle)}:root=0x{GetAncestor(handle,2).ToInt64():X}";
        }
        var gui = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        var guiOk = GetGUIThreadInfo(receiverThread, ref gui);
        return $"receiver_enabled={IsWindowEnabled(receiver)} foreground={Describe(GetForegroundWindow())} " +
            $"pad_hit={Describe(WindowFromPhysicalPoint(new NativePoint(pad.X+pad.Width/3,pad.Y+pad.Height/2)))} " +
            $"hud_hit={Describe(WindowFromPhysicalPoint(new NativePoint(pad.Right-60,pad.Bottom-40)))} " +
            $"gui_ok={guiOk} focus={Describe(gui.Focus)} capture={Describe(gui.Capture)} evidence=queries-not-input";
    }
}
