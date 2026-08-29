using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using RageWebUI.Runtime;

namespace RageWebUI.Harness
{
    /// <summary>
    /// Exercises the same hosting constraint as ScriptHookVDotNet: the managed
    /// script runs in a secondary AppDomain, where CefSharp must not start and
    /// the WebView2 renderer must preserve visibility requests made before its
    /// UI thread has created a window handle.
    /// </summary>
    internal static class SecondaryAppDomainHarness
    {
        private const string OverlayWindowTitle = "REACTOR V Overlay";

        public static int Run(HarnessOptions options)
        {
            var runtimeDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var uiDirectory = options.UiDirectory ?? Path.Combine(runtimeDirectory, "ui");
            if (!File.Exists(Path.Combine(uiDirectory, "index.html")))
            {
                Console.Error.WriteLine($"React UI was not found at '{uiDirectory}'.");
                return 3;
            }

            // Isolate WebView2 state and the trace just like the DirectX/CEF
            // harness profile. This allows build and developer harnesses to run
            // side-by-side without sharing browser locks or log contents.
            var localDataDirectory = options.LocalDataDirectory ??
                HarnessRunDirectory.For("ShvdnFallback");
            Directory.CreateDirectory(localDataDirectory);
            var logPath = Path.Combine(localDataDirectory, "reactorv-runtime.log");
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }

            using var host = new Form
            {
                ClientSize = new System.Drawing.Size(options.Width, options.Height),
                StartPosition = FormStartPosition.CenterScreen,
                Text = "REACTOR V SHVDN fallback harness",
            };
            host.Show();
            Application.DoEvents();

            // The production overlay deliberately remains hidden unless GTA
            // (or one of its owned windows) is foreground. Developer and
            // packaged smoke tests are often launched by a background terminal
            // or CI-style runner, so merely calling Form.Show() is racy: the
            // browser can become ready while another application still owns
            // foreground activation. Give the synthetic GTA host foreground
            // deterministically instead of weakening the runtime gate.
            var hostForeground = WindowProbe.EnsureForeground(
                host.Handle,
                TimeSpan.FromSeconds(3));
            Console.WriteLine($"Harness host foreground: {hostForeground}");
            if (!hostForeground)
            {
                Console.Error.WriteLine(
                    "Could not activate the synthetic GTA host window; " +
                    "the production foreground visibility gate cannot be tested deterministically.");
                return 5;
            }

            var setup = new AppDomainSetup
            {
                ApplicationBase = runtimeDirectory,
                PrivateBinPath = runtimeDirectory,
                ShadowCopyFiles = "false",
            };
            var domain = AppDomain.CreateDomain(
                "ScriptHookVDotNet-ScriptDomain-Harness",
                null,
                setup);
            SecondaryAppDomainHarnessProxy? proxy = null;
            try
            {
                proxy = (SecondaryAppDomainHarnessProxy)domain.CreateInstanceAndUnwrap(
                    Assembly.GetExecutingAssembly().FullName,
                    typeof(SecondaryAppDomainHarnessProxy).FullName);
                var started = proxy.StartAndRequestEarlyVisibility(
                    host.Handle,
                    uiDirectory,
                    runtimeDirectory,
                    localDataDirectory,
                    options.Width,
                    options.Height);
                Console.WriteLine(
                    $"Secondary-domain harness started: domain={proxy.DomainName}, " +
                    $"default={proxy.IsDefaultAppDomain}, renderer={proxy.RendererName}");

                var duration = options.Duration ?? TimeSpan.FromMinutes(10);
                var stopwatch = Stopwatch.StartNew();
                var requests = 0;
                var browserReady = false;
                var raceVisible = false;
                var prematureVisible = false;
                while (host.Visible && stopwatch.Elapsed < duration)
                {
                    Application.DoEvents();
                    requests = proxy.Pump();
                    var log = ReadLog(logPath);
                    browserReady = log.Contains("webview_content_ready");
                    var currentlyVisible = WindowProbe.IsVisible(OverlayWindowTitle);
                    if (currentlyVisible && !browserReady)
                    {
                        prematureVisible = true;
                    }
                    raceVisible = currentlyVisible;
                    if (browserReady && raceVisible && requests >= 2)
                    {
                        break;
                    }
                    Thread.Sleep(10);
                }

                // Confirm that normal F10-style transitions still work after
                // the pre-handle visibility request has been replayed.
                proxy.SetVisible(false);
                var hideWorked = WaitForVisibility(OverlayWindowTitle, false, TimeSpan.FromSeconds(2));
                proxy.SetVisible(true);
                var showWorked = WaitForVisibility(OverlayWindowTitle, true, TimeSpan.FromSeconds(2));

                var trace = ReadLog(logPath);
                var skippedCef = trace.Contains("directx_skipped reason=cefsharp_requires_default_appdomain");
                var contentReadyAt = trace.IndexOf("webview_content_ready", StringComparison.Ordinal);
                var firstVisibleAt = trace.IndexOf("webview_visibility_applied visible=True", StringComparison.Ordinal);
                var replayedEarlyVisibility = contentReadyAt >= 0 && firstVisibleAt > contentReadyAt;
                var passed = started && hostForeground && !proxy.IsDefaultAppDomain &&
                    string.Equals(proxy.RendererName, "WebView2 window", StringComparison.Ordinal) &&
                    skippedCef && replayedEarlyVisibility && !prematureVisible && browserReady && raceVisible &&
                    hideWorked && showWorked && requests >= 2;
                Console.WriteLine(
                    $"RESULT {(passed ? "PASS" : "FAIL")}: scenario=shvdn-fallback " +
                    $"cefSkipped={skippedCef} earlyVisibility={replayedEarlyVisibility} " +
                    $"browserReady={browserReady} prematureVisible={prematureVisible} initiallyVisible={raceVisible} " +
                    $"hide={hideWorked} show={showWorked} requests={requests}");
                if (!passed)
                {
                    Console.Error.WriteLine($"Runtime trace: {logPath}");
                }
                return passed ? 0 : 4;
            }
            finally
            {
                try
                {
                    proxy?.DisposeRuntime();
                }
                finally
                {
                    AppDomain.Unload(domain);
                }
            }
        }

        private static bool WaitForVisibility(string title, bool expected, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                Application.DoEvents();
                if (WindowProbe.IsVisible(title) == expected)
                {
                    return true;
                }
                Thread.Sleep(10);
            }
            return false;
        }

        private static string ReadLog(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Marshal-by-reference boundary used to keep the production runtime and
    /// browser UI thread inside the simulated SHVDN AppDomain.
    /// </summary>
    public sealed class SecondaryAppDomainHarnessProxy : MarshalByRefObject
    {
        private BridgeBroker? _broker;
        private IOverlayRuntime? _runtime;
        private int _handledRequests;

        public string DomainName => AppDomain.CurrentDomain.FriendlyName;

        public bool IsDefaultAppDomain => AppDomain.CurrentDomain.IsDefaultAppDomain();

        public string RendererName => _runtime?.RendererName ?? "not started";

        public bool StartAndRequestEarlyVisibility(
            IntPtr hostWindow,
            string uiDirectory,
            string runtimeDirectory,
            string localDataDirectory,
            int width,
            int height)
        {
            _broker = new BridgeBroker();
            _runtime = new OverlayRuntime(
                "directx",
                hostWindow,
                uiDirectory,
                runtimeDirectory,
                localDataDirectory,
                _broker,
                width,
                height,
                60,
                false,
                false);
            var started = _runtime.Start();

            // This deliberately occurs immediately after Start, while the new
            // STA thread is normally still constructing its Form/WebView2.
            _runtime.SetVisible(true);
            return started;
        }

        public int Pump()
        {
            var runtime = _runtime;
            var broker = _broker;
            if (runtime == null || broker == null)
            {
                return _handledRequests;
            }

            runtime.PumpInput();
            for (var index = 0; index < 32 && broker.TryDequeue(out var request); index++)
            {
                if (request == null)
                {
                    continue;
                }
                runtime.PostResponse(Dispatch(request));
                _handledRequests++;
            }
            runtime.PostEvent(
                "game.state",
                new JObject
                {
                    ["gameTime"] = Environment.TickCount & int.MaxValue,
                    ["paused"] = false,
                });
            return _handledRequests;
        }

        public void SetVisible(bool visible) => _runtime?.SetVisible(visible);

        public void DisposeRuntime()
        {
            _runtime?.Dispose();
            _runtime = null;
            _broker = null;
        }

        public override object InitializeLifetimeService() => null!;

        private static BridgeResponse Dispatch(BridgeRequest request)
        {
            JToken result;
            switch (request.Method)
            {
                case "overlay.ready":
                    result = new JObject
                    {
                        ["apiVersion"] = 1,
                        ["runtime"] = "Secondary AppDomain harness",
                        ["renderer"] = "WebView2 window",
                        ["edition"] = "Enhanced",
                        ["dependencies"] = new JArray(),
                    };
                    break;
                case "game.getState":
                    result = new JObject
                    {
                        ["gameTime"] = 42420,
                        ["paused"] = false,
                        ["player"] = new JObject
                        {
                            ["health"] = 200,
                            ["maxHealth"] = 200,
                            ["armor"] = 0,
                            ["wantedLevel"] = 0,
                            ["invincible"] = false,
                            ["position"] = new JObject { ["x"] = 0, ["y"] = 0, ["z"] = 0 },
                            ["heading"] = 0,
                        },
                        ["vehicle"] = JValue.CreateNull(),
                        ["world"] = new JObject { ["time"] = "12:00", ["weather"] = "Clear" },
                    };
                    break;
                default:
                    result = new JObject { ["ok"] = true };
                    break;
            }
            return BridgeResponse.Success(request.Id, result);
        }
    }

    internal static class WindowProbe
    {
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint GaRootOwner = 3;

        public static bool EnsureForeground(IntPtr window, TimeSpan timeout)
        {
            if (window == IntPtr.Zero)
            {
                return false;
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                Application.DoEvents();
                if (IsForegroundOrOwnedBy(window))
                {
                    return true;
                }

                // Attach only for the duration of the activation attempt. This
                // lets a harness launched by a non-foreground terminal make the
                // same visible host window active without changing any
                // production OverlayWindow behavior.
                var foreground = GetForegroundWindow();
                var currentThread = GetCurrentThreadId();
                var foregroundThread = foreground == IntPtr.Zero
                    ? 0u
                    : GetWindowThreadProcessId(foreground, out _);
                var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                    AttachThreadInput(currentThread, foregroundThread, true);
                try
                {
                    ShowWindow(window, 9); // SW_RESTORE
                    BringWindowToTop(window);
                    SetForegroundWindow(window);
                    SetActiveWindow(window);
                    SetFocus(window);
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(currentThread, foregroundThread, false);
                    }
                }

                // Topmost is temporary and used only to make the synthetic
                // host visible during the smoke run. Immediately restore its
                // normal z-order so the harness cannot leave a sticky window.
                SetWindowPos(
                    window,
                    HwndTopmost,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
                SetWindowPos(
                    window,
                    HwndNotTopmost,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
                Thread.Sleep(25);
            }

            return IsForegroundOrOwnedBy(window);
        }

        public static bool IsForegroundOrOwnedBy(IntPtr window)
        {
            var foreground = GetForegroundWindow();
            return foreground == window ||
                (foreground != IntPtr.Zero && GetAncestor(foreground, GaRootOwner) == window);
        }

        public static bool IsVisible(string title)
        {
            var found = false;
            var processId = (uint)Process.GetCurrentProcess().Id;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var ownerProcessId);
                if (ownerProcessId == processId &&
                    string.Equals(GetTitle(window), title, StringComparison.Ordinal) &&
                    IsWindowVisible(window))
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static string GetTitle(IntPtr window)
        {
            var length = GetWindowTextLength(window);
            if (length <= 0)
            {
                return string.Empty;
            }
            var buffer = new System.Text.StringBuilder(length + 1);
            GetWindowText(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maximumCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr window);
    }
}
