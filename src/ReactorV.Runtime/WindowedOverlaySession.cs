using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using ReactorV.WebView2Host;

namespace RageWebUI.Runtime
{
    internal sealed class WindowedOverlaySession :
        IOverlayRuntime,
        IProviderPresentationCommitRuntime,
        IProviderInputIntentRuntime
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
        private string? _committedProviderPresentationId;
        private string? _userIntentAuthorizedProviderPresentationId;
        private int _disposed;
        private readonly object _cursorSync = new object();
        private readonly Queue<PointerSample> _cursorEdges = new Queue<PointerSample>();
        private PointerSample _pendingCursorMove;
        private bool _hasPendingCursorMove;
        private bool _cursorDispatchQueued;
        private bool _hasCursor;
        private float _lastCursorX;
        private float _lastCursorY;

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

        public bool IsProviderPresentationCommitted(string presentationId) =>
            ProviderPresentationCommitContract.Matches(
                Volatile.Read(ref _committedProviderPresentationId),
                presentationId);

        public bool IsProviderPresentationAuthorizedByUserIntent(
            string presentationId) =>
            ProviderPresentationCommitContract.Matches(
                Volatile.Read(ref _userIntentAuthorizedProviderPresentationId),
                presentationId);

        public bool ArmProviderInputIntent(ProviderInputIntentToken token) =>
            InvokeWindow(window => window.ArmProviderInputIntent(token));

        public bool BindProviderInputIntent(
            int processId,
            long epoch,
            string presentationId) =>
            InvokeWindow(window => window.BindProviderInputIntent(
                processId,
                epoch,
                presentationId));

        public void CancelProviderInputIntent(int processId, long epoch) =>
            InvokeWindow(window => window.CancelProviderInputIntent(
                processId,
                epoch));

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
            if (!visible)
            {
                lock (_cursorSync)
                {
                    _hasCursor = false;
                    _hasPendingCursorMove = false;
                    _cursorEdges.Clear();
                }
            }
            RuntimeTrace.Write(
                _logDirectory,
                "webview_visibility_queued",
                $"visible={visible}");
            InvokeWindow(
                window => window.SetOverlayVisible(visible),
                visible ? null : window =>
                {
                    window.SignalRevealIngress();
                    return true;
                });
        }

        public void PostResponse(BridgeResponse response) => PostJson(BridgeProtocol.SerializeResponse(response));

        public void PostEvent(string eventName, JToken? payload) =>
            PostJson(BridgeProtocol.SerializeEvent(eventName, payload));

        public void PumpInput()
        {
        }

        public void UpdateCursor(float normalizedX, float normalizedY, bool pressed, bool released, int wheelDelta)
        {
            if (Volatile.Read(ref _disposed) != 0 || !RequestedVisible)
            {
                return;
            }

            normalizedX = WindowedInputPolicy.Normalize(normalizedX);
            normalizedY = WindowedInputPolicy.Normalize(normalizedY);
            lock (_cursorSync)
            {
                if (!WindowedInputPolicy.ShouldForward(
                        _lastCursorX,
                        _lastCursorY,
                        _hasCursor,
                        normalizedX,
                        normalizedY,
                        pressed,
                        released,
                        wheelDelta))
                {
                    return;
                }

                _hasCursor = true;
                _lastCursorX = normalizedX;
                _lastCursorY = normalizedY;
                var sample = new PointerSample(
                    normalizedX,
                    normalizedY,
                    pressed,
                    released,
                    wheelDelta);
                if (pressed || released || wheelDelta != 0)
                {
                    // Preserve every button/wheel edge in order. Only pure
                    // movement is coalesced while the WebView UI thread is
                    // occupied so a quick click can never become "release only".
                    if (_hasPendingCursorMove)
                    {
                        _cursorEdges.Enqueue(_pendingCursorMove);
                        _hasPendingCursorMove = false;
                    }
                    _cursorEdges.Enqueue(sample);
                }
                else
                {
                    _pendingCursorMove = sample;
                    _hasPendingCursorMove = true;
                }
            }

            SchedulePendingCursorDispatch();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _committedProviderPresentationId, null);
            Interlocked.Exchange(
                ref _userIntentAuthorizedProviderPresentationId,
                null);

            InvokeWindow(
                window => window.Close(),
                window =>
                {
                    window.SignalRevealIngress();
                    return true;
                });
            if (_uiThread.IsAlive)
            {
                _uiThread.Join(2000);
            }
        }

        private void PostJson(string json) => InvokeWindow(
            window => window.PostJson(json),
            window => window.SignalHostMessageIngress(json));

        private void DispatchPendingCursor(OverlayWindow window)
        {
            PointerSample[] samples;
            lock (_cursorSync)
            {
                if (_hasPendingCursorMove)
                {
                    _cursorEdges.Enqueue(_pendingCursorMove);
                    _hasPendingCursorMove = false;
                }
                samples = _cursorEdges.ToArray();
                _cursorEdges.Clear();
                _cursorDispatchQueued = false;
            }
            foreach (var sample in samples)
            {
                window.PostPointerInput(
                    sample.X,
                    sample.Y,
                    sample.Pressed,
                    sample.Released,
                    sample.WheelDelta);
            }
        }

        private void SchedulePendingCursorDispatch()
        {
            lock (_cursorSync)
            {
                if (_cursorDispatchQueued ||
                    (!_hasPendingCursorMove && _cursorEdges.Count == 0))
                {
                    return;
                }
                _cursorDispatchQueued = true;
            }

            if (InvokeWindow(DispatchPendingCursor))
            {
                return;
            }

            // The first game-cursor sample can arrive before WinForms creates
            // the HWND. A failed BeginInvoke must not strand the dispatch gate;
            // handle creation below replays the retained samples, while any
            // later input is also free to schedule the drain again.
            lock (_cursorSync)
            {
                _cursorDispatchQueued = false;
            }
        }

        private bool InvokeWindow(
            Action<OverlayWindow> action,
            Func<OverlayWindow, bool>? ingress = null)
        {
            var window = _window;
            if (window == null || window.IsDisposed || !window.IsHandleCreated)
            {
                return false;
            }

            var ingressAnnounced = false;
            try
            {
                ingressAnnounced = ingress?.Invoke(window) == true;
                window.BeginInvoke(
                    (Action<OverlayWindow>)(queuedWindow =>
                    {
                        if (ingressAnnounced)
                            queuedWindow.ApplyRevealIngress();
                        try
                        {
                            action(queuedWindow);
                        }
                        finally
                        {
                            if (ingressAnnounced)
                                queuedWindow.ResumeRevealAfterIngress();
                        }
                    }),
                    window);
                return true;
            }
            catch (Exception error) when (
                error is InvalidOperationException ||
                error is ObjectDisposedException)
            {
                // BeginInvoke failed after the ingress announcement. Do not
                // leave a surviving window permanently blocked by a token that
                // can no longer reach its STA.
                if (ingressAnnounced)
                    window.ApplyRevealIngress();
                return false;
            }
        }

        private void RunUiThread()
        {
            try
            {
                Interlocked.Exchange(ref _committedProviderPresentationId, null);
                RuntimeTrace.Write(_logDirectory, "webview_ui_thread_start");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var window = new OverlayWindow(
                    _gtaWindow,
                    (uint)System.Diagnostics.Process.GetCurrentProcess().Id,
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
                    () => { },
                    error =>
                    {
                        Interlocked.Exchange(ref _requestedVisible, 0);
                        Interlocked.Exchange(ref _actualVisible, 0);
                        Interlocked.Exchange(
                            ref _committedProviderPresentationId,
                            null);
                        RuntimeTrace.Write(
                            _logDirectory,
                            "webview_failed",
                            $"type={error.GetType().FullName} message={error.Message}");
                    });
                window.ProviderPresentationCommitted += presentationId =>
                {
                    Interlocked.Exchange(
                        ref _committedProviderPresentationId,
                        ProviderPresentationCommitContract.IsValidPresentationId(
                            presentationId)
                            ? presentationId
                            : null);
                    Interlocked.Exchange(
                        ref _userIntentAuthorizedProviderPresentationId,
                        window.IsProviderPresentationAuthorizedByUserIntent(
                            presentationId)
                            ? presentationId
                            : null);
                };
                _window = window;

                // Create the HWND and preload WebView2 without asking WinForms
                // to show the Form. Application.Run(Form) briefly exposed a
                // blank full-screen surface during GTA startup.
                var context = new ApplicationContext();
                window.FormClosed += (_, __) =>
                {
                    Interlocked.Exchange(
                        ref _committedProviderPresentationId,
                        null);
                    Interlocked.Exchange(
                        ref _userIntentAuthorizedProviderPresentationId,
                        null);
                    context.ExitThread();
                };
                var unusedHandle = window.Handle;
                // A visibility request can arrive after the constructor read
                // _requestedVisible but before the HWND existed. Replay the
                // atomic session state once the handle is ready so that race
                // cannot leave the window permanently out of sync.
                window.SetOverlayVisible(RequestedVisible);
                SchedulePendingCursorDispatch();
                window.BeginInvoke(new Action(window.BeginPreload));
                Application.Run(context);
                RuntimeTrace.Write(_logDirectory, "webview_ui_thread_stop");
            }
            catch (Exception error)
            {
                Interlocked.Exchange(ref _requestedVisible, 0);
                Interlocked.Exchange(ref _actualVisible, 0);
                Interlocked.Exchange(ref _committedProviderPresentationId, null);
                RuntimeTrace.Write(
                    _logDirectory,
                    "webview_ui_thread_failed",
                    $"type={error.GetType().FullName} message={error.Message}");
            }
        }

        private readonly struct PointerSample
        {
            internal PointerSample(
                float x,
                float y,
                bool pressed,
                bool released,
                int wheelDelta)
            {
                X = x;
                Y = y;
                Pressed = pressed;
                Released = released;
                WheelDelta = wheelDelta;
            }

            internal float X { get; }
            internal float Y { get; }
            internal bool Pressed { get; }
            internal bool Released { get; }
            internal int WheelDelta { get; }
        }
    }
}
