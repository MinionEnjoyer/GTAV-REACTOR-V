using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;

namespace RageWebUI.Runtime
{
    internal sealed class WindowedOverlaySession : IOverlayRuntime
    {
        private readonly Thread _uiThread;
        private readonly IntPtr _gtaWindow;
        private readonly string _uiDirectory;
        private readonly string _userDataDirectory;
        private readonly BridgeBroker _broker;
        private readonly bool _enableDevTools;
        private readonly string _logDirectory;
        private OverlayWindow? _window;
        private int _requestedVisible;
        private int _actualVisible;
        private int _disposed;

        public WindowedOverlaySession(
            IntPtr gtaWindow,
            string uiDirectory,
            string userDataDirectory,
            BridgeBroker broker,
            bool enableDevTools,
            bool startVisible)
        {
            _gtaWindow = gtaWindow;
            _uiDirectory = uiDirectory;
            _userDataDirectory = userDataDirectory;
            _broker = broker;
            _enableDevTools = enableDevTools;
            _logDirectory = Path.GetDirectoryName(_userDataDirectory) ?? _userDataDirectory;
            _requestedVisible = startVisible ? 1 : 0;
            _uiThread = new Thread(RunUiThread)
            {
                IsBackground = true,
                Name = "REACTOR V WebView2",
            };
            _uiThread.SetApartmentState(ApartmentState.STA);
        }

        public bool IsVisible => Volatile.Read(ref _actualVisible) == 1;

        private bool RequestedVisible => Volatile.Read(ref _requestedVisible) == 1;

        public string RendererName => "WebView2 window";

        public bool Start()
        {
            _uiThread.Start();
            return true;
        }

        public void SetVisible(bool visible)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _requestedVisible, visible ? 1 : 0);
            RuntimeTrace.Write(
                _logDirectory,
                "webview_visibility_queued",
                $"visible={visible}");
            InvokeWindow(window => window.SetOverlayVisible(visible));
        }

        public void PostResponse(BridgeResponse response) => PostJson(BridgeProtocol.SerializeResponse(response));

        public void PostEvent(string eventName, JToken? payload) =>
            PostJson(BridgeProtocol.SerializeEvent(eventName, payload));

        public void PumpInput()
        {
        }

        public void UpdateCursor(float normalizedX, float normalizedY, bool pressed, bool released, int wheelDelta)
        {
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            InvokeWindow(window => window.Close());
            if (_uiThread.IsAlive)
            {
                _uiThread.Join(2000);
            }
        }

        private void PostJson(string json) => InvokeWindow(window => window.PostJson(json));

        private void InvokeWindow(Action<OverlayWindow> action)
        {
            var window = _window;
            if (window == null || window.IsDisposed || !window.IsHandleCreated)
            {
                return;
            }

            try
            {
                window.BeginInvoke(action, window);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void RunUiThread()
        {
            try
            {
                RuntimeTrace.Write(_logDirectory, "webview_ui_thread_start");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var window = new OverlayWindow(
                    _gtaWindow,
                    _uiDirectory,
                    _userDataDirectory,
                    _broker,
                    _enableDevTools,
                    RequestedVisible,
                    (stage, detail) => RuntimeTrace.Write(_logDirectory, stage, detail),
                    visible => Interlocked.Exchange(ref _actualVisible, visible ? 1 : 0),
                    () =>
                    {
                        var gtaProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
                        var signaled = PreloadHandoff.TrySignal(gtaProcessId);
                        RuntimeTrace.Write(
                            _logDirectory,
                            "webview_preload_handoff",
                            $"pid={gtaProcessId} signaled={signaled}");
                    },
                    error =>
                    {
                        Interlocked.Exchange(ref _requestedVisible, 0);
                        Interlocked.Exchange(ref _actualVisible, 0);
                        RuntimeTrace.Write(
                            _logDirectory,
                            "webview_failed",
                            $"type={error.GetType().FullName} message={error.Message}");
                    });
                _window = window;

                // Create the HWND and preload WebView2 without asking WinForms
                // to show the Form. Application.Run(Form) briefly exposed a
                // blank full-screen surface during GTA startup.
                var context = new ApplicationContext();
                window.FormClosed += (_, __) => context.ExitThread();
                var unusedHandle = window.Handle;
                // A visibility request can arrive after the constructor read
                // _requestedVisible but before the HWND existed. Replay the
                // atomic session state once the handle is ready so that race
                // cannot leave the window permanently out of sync.
                window.SetOverlayVisible(RequestedVisible);
                window.BeginInvoke(new Action(window.BeginPreload));
                Application.Run(context);
                RuntimeTrace.Write(_logDirectory, "webview_ui_thread_stop");
            }
            catch (Exception error)
            {
                Interlocked.Exchange(ref _requestedVisible, 0);
                Interlocked.Exchange(ref _actualVisible, 0);
                RuntimeTrace.Write(
                    _logDirectory,
                    "webview_ui_thread_failed",
                    $"type={error.GetType().FullName} message={error.Message}");
            }
        }
    }
}
