using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using CefSharp;
using CefSharp.Core;
using CefSharp.OffScreen;
using CefSharp.SchemeHandler;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using RageWebUI.DirectX.Native;

namespace RageWebUI.DirectX.Browser
{
    internal sealed class OffscreenBrowser : IDisposable
    {
        private const int MaxPendingPostJsonMessages = 256;

        private readonly IBridgeMessageSink _bridgeSink;
        private readonly ChromiumWebBrowser _browser;
        private readonly IRequestContext _requestContext;
        private readonly string _logDirectory;
        private readonly bool _acceleratedRendering;
        private readonly IAcceleratedFrameSubmitter? _acceleratedSubmitter;
        private readonly Stopwatch _startupTimer = Stopwatch.StartNew();
        private readonly object _postJsonSync = new object();
        private readonly Queue<string> _pendingPostJson = new Queue<string>();
        private ulong _frameGeneration;
        private bool _disposed;
        private bool _documentReady;
        private bool _desiredVisible;
        private bool _contentReadyLogged;
        private int _contentReady;
        private long _acceleratedPaintCallbackCount;
        private long _acceleratedProbeRejectedCount;
        private long _acceleratedProbeDeferredCount;
        private int _acceleratedSubmitLogged;
        private long _acceleratedSubmitFailureCount;
        private int _surfaceWidth;
        private int _surfaceHeight;

        public event Action? ContentReady;
        public event Action? ContentUnavailable;
        public event Action? AcceleratedTransportReady;
        public event Action? AcceleratedTransportUnavailable;
        public event Action<int, int, ulong>? AcceleratedFrameSubmitted;

        public bool IsContentReady => System.Threading.Volatile.Read(ref _contentReady) == 1;
        public bool IsAcceleratedTransportReady =>
            !_acceleratedRendering || _acceleratedSubmitter?.IsReady == true;
        public bool IsAcceleratedBootstrapPending =>
            _acceleratedRendering && _acceleratedSubmitter?.IsBootstrapPending == true;
        public long AcceleratedPaintCallbackCount =>
            System.Threading.Interlocked.Read(ref _acceleratedPaintCallbackCount);
        public long AcceleratedProbeRejectedCount =>
            System.Threading.Interlocked.Read(ref _acceleratedProbeRejectedCount);
        public long AcceleratedProbeDeferredCount =>
            System.Threading.Interlocked.Read(ref _acceleratedProbeDeferredCount);
        public int SurfaceWidth =>
            System.Threading.Volatile.Read(ref _surfaceWidth);
        public int SurfaceHeight =>
            System.Threading.Volatile.Read(ref _surfaceHeight);

        public OffscreenBrowser(
            IntPtr parentWindow,
            string uiDirectory,
            string runtimeDirectory,
            string cacheDirectory,
            IBridgeMessageSink bridgeSink,
            int width,
            int height,
            int frameRate,
            bool enableDevTools,
            bool startVisible,
            bool allowAcceleratedBootstrapProbe = false,
            bool forceCpuRendering = false,
            string browserRole = "primary",
            GpuAdapterLuid? adapterLuid = null)
        {
            _bridgeSink = bridgeSink ?? throw new ArgumentNullException(nameof(bridgeSink));
            _desiredVisible = startVisible;
            _logDirectory = Path.GetDirectoryName(cacheDirectory) ?? cacheDirectory;
            _surfaceWidth = Math.Max(1, width);
            _surfaceHeight = Math.Max(1, height);
            CefRuntime.EnsureInitialized(
                runtimeDirectory,
                cacheDirectory,
                adapterLuid);

            _requestContext = new RequestContext();
            if (!_requestContext.RegisterSchemeHandlerFactory(
                    "https",
                    "ragewebui.local",
                    new FolderSchemeHandlerFactory(uiDirectory, "https", "ragewebui.local")))
            {
                throw new InvalidOperationException("Could not register the RageWebUI local resource handler.");
            }

            var browserSettings = new BrowserSettings
            {
                BackgroundColor = Cef.ColorSetARGB(0, 0, 0, 0),
                WindowlessFrameRate = Math.Max(1, Math.Min(60, frameRate)),
            };
            _acceleratedRendering =
                !forceCpuRendering &&
                NativeAcceleratedFrameSubmitter.TryCreate(
                    allowAcceleratedBootstrapProbe,
                    out _acceleratedSubmitter);
            var trustedBrowserRole = string.Equals(
                browserRole,
                "gpu-renderer",
                StringComparison.Ordinal)
                    ? "gpu-renderer"
                    : "primary";
            _browser = new ChromiumWebBrowser(
                "https://ragewebui.local/index.html?reactorBrowserRole=" +
                trustedBrowserRole,
                browserSettings,
                _requestContext,
                automaticallyCreateBrowser: false,
                useLegacyRenderHandler: !_acceleratedRendering)
            {
                Size = new Size(_surfaceWidth, _surfaceHeight),
                RequestHandler = new LocalOnlyRequestHandler(),
            };
            if (_acceleratedRendering)
            {
                _acceleratedSubmitter!.Ready += OnAcceleratedTransportReady;
                _acceleratedSubmitter.Unavailable += OnAcceleratedTransportUnavailable;
                _browser.RenderHandler = new AcceleratedRenderHandler(
                    _browser,
                    _acceleratedSubmitter,
                    OnAcceleratedPaintObserved);
            }
            else
            {
                _browser.Paint += OnPaint;
            }
            _browser.JavascriptMessageReceived += OnJavascriptMessageReceived;
            _browser.LoadingStateChanged += OnLoadingStateChanged;

            var windowInfo = new WindowInfo();
            windowInfo.SetAsWindowless(parentWindow);
            windowInfo.SharedTextureEnabled = _acceleratedRendering;
            _browser.CreateBrowser(windowInfo, browserSettings);
            _browser.BrowserInitialized += (_, __) =>
            {
                if (_disposed || !_browser.IsBrowserInitialized) return;
                StartupTrace.Write(
                    _logDirectory,
                    "reactorv-runtime.log",
                    "directx",
                    "browser_initialized",
                    $"duration_ms={_startupTimer.Elapsed.TotalMilliseconds:F3}");
                var host = _browser.GetBrowser().GetHost();
                host.NotifyMoveOrResizeStarted();
                host.WasResized();
                host.WasHidden(!_desiredVisible);
                if (_desiredVisible) host.Invalidate(PaintElementType.View);
                // DevTools remain callable through CefSharp APIs when enabled;
                // no eager DevTools window is needed for preload.
            };
        }

        public bool Resize(int width, int height)
        {
            if (_disposed || width <= 0 || height <= 0) return false;
            var size = new Size(width, height);
            if (SurfaceWidth == width && SurfaceHeight == height)
                return true;

            try
            {
                // Chromium's windowless host owns the accelerated texture.
                // Marshal the size transition to CEF's UI thread so no caller
                // races a paint callback with a half-applied managed Size.
                return Cef.PostAction(CefThreadIds.TID_UI, () =>
                {
                    if (_disposed) return;
                    _browser.Size = size;
                    System.Threading.Volatile.Write(ref _surfaceWidth, width);
                    System.Threading.Volatile.Write(ref _surfaceHeight, height);
                    if (!_browser.IsBrowserInitialized) return;

                    var host = _browser.GetBrowser().GetHost();
                    host.NotifyMoveOrResizeStarted();
                    host.WasResized();
                    host.WasHidden(false);
                    host.Invalidate(PaintElementType.View);
                    QueueAcceleratedTrace(
                        "accelerated_surface_resize_applied",
                        $"size={width}x{height}");
                });
            }
            catch (Exception)
            {
                // CEF rejects new UI work during shutdown. The session keeps
                // the native output hidden and may fail open to WebView2.
                return false;
            }
        }

        public void SetVisible(bool visible)
        {
            // The external browser stays render-awake while native output is
            // hidden. Host arbitration may therefore repeat SetVisible(true)
            // at several readiness boundaries. Treat that as a no-op: forcing
            // WasHidden(false) plus Invalidate for an unchanged state creates
            // redundant 4K CEF paints during the exact handoff we are trying
            // to keep frame-continuous. Refresh/resize own explicit invalidates.
            if (_desiredVisible == visible) return;
            _desiredVisible = visible;
            if (_disposed || !_browser.IsBrowserInitialized) return;
            _browser.GetBrowser().GetHost().WasHidden(!visible);
            if (visible) _browser.GetBrowser().GetHost().Invalidate(PaintElementType.View);
        }

        /// <summary>
        /// Requests another accelerated paint while the native transport is
        /// still waiting for its first accepted shared-texture probe. The
        /// caller owns cadence and deadline bounds; this method only marshals
        /// one invalidate onto CEF's browser UI thread.
        /// </summary>
        public bool RequestAcceleratedBootstrapPaint()
        {
            if (_disposed ||
                !_acceleratedRendering ||
                _acceleratedSubmitter?.IsBootstrapPending != true)
            {
                return false;
            }

            try
            {
                return Cef.PostAction(CefThreadIds.TID_UI, () =>
                {
                    if (_disposed ||
                        !_browser.IsBrowserInitialized ||
                        _acceleratedSubmitter?.IsBootstrapPending != true)
                    {
                        return;
                    }

                    // The native producer visibility bit controls whether a
                    // frame reaches GTA. CEF itself stays awake so a static
                    // document can repaint after the consumer attaches.
                    var host = _browser.GetBrowser().GetHost();
                    host.WasHidden(false);
                    host.Invalidate(PaintElementType.View);
                });
            }
            catch (Exception)
            {
                // CEF may reject work during shutdown. The bounded session
                // pump will either retry or hit its existing fail-open deadline.
                return false;
            }
        }

        /// <summary>
        /// Forces one accelerated repaint for a new logical menu
        /// presentation. Unlike the bootstrap retry, this remains available
        /// after transport startup so a static document cannot reuse an
        /// acknowledged frame from the preceding presentation.
        /// </summary>
        public bool RequestAcceleratedPresentationPaint()
        {
            if (_disposed || !_acceleratedRendering)
                return false;

            try
            {
                return Cef.PostAction(CefThreadIds.TID_UI, () =>
                {
                    if (_disposed || !_browser.IsBrowserInitialized)
                        return;

                    var host = _browser.GetBrowser().GetHost();
                    host.WasHidden(false);
                    host.Invalidate(PaintElementType.View);
                });
            }
            catch (Exception)
            {
                // The caller keeps the native presenter hidden. A shutdown
                // race is therefore a rejected refresh, never stale output.
                return false;
            }
        }

        public void PostJson(string json)
        {
            lock (_postJsonSync)
            {
                if (_disposed) return;

                if (!_documentReady || !_browser.IsBrowserInitialized)
                {
                    if (_pendingPostJson.Count == MaxPendingPostJsonMessages)
                        _pendingPostJson.Dequeue();
                    _pendingPostJson.Enqueue(json);
                    return;
                }

                DispatchJsonUnsafe(json);
            }
        }

        public void SendInput(NativeInputEvent input)
        {
            if (_disposed || !_browser.IsBrowserInitialized) return;
            var host = _browser.GetBrowser().GetHost();
            var modifiers = ToCefModifiers(input.Modifiers);
            switch (input.Type)
            {
                case NativeInputType.MouseMove:
                    host.SendMouseMoveEvent(input.X, input.Y, false, modifiers);
                    break;
                case NativeInputType.MouseDown:
                case NativeInputType.MouseUp:
                    host.SendMouseClickEvent(
                        input.X,
                        input.Y,
                        ToMouseButton(input.Key),
                        input.Type == NativeInputType.MouseUp,
                        1,
                        modifiers);
                    break;
                case NativeInputType.MouseWheel:
                    host.SendMouseWheelEvent(input.X, input.Y, 0, input.Delta, modifiers);
                    break;
                case NativeInputType.KeyDown:
                    host.SendKeyEvent(0x0100, input.Key, 1 | (input.Delta << 16));
                    break;
                case NativeInputType.KeyUp:
                    host.SendKeyEvent(0x0101, input.Key, unchecked(1 | (input.Delta << 16) | (1 << 30) | (1 << 31)));
                    break;
                case NativeInputType.Character:
                    host.SendKeyEvent(0x0102, input.Key, 1);
                    break;
                case NativeInputType.Resize:
                    Resize(input.X, input.Y);
                    break;
            }
        }

        public void SendGameCursor(float normalizedX, float normalizedY, bool pressed, bool released, int wheelDelta)
        {
            if (_disposed || !_browser.IsBrowserInitialized) return;
            var width = Math.Max(1, _browser.Size.Width);
            var height = Math.Max(1, _browser.Size.Height);
            var x = Math.Max(0, Math.Min(width - 1, (int)(normalizedX * width)));
            var y = Math.Max(0, Math.Min(height - 1, (int)(normalizedY * height)));
            var host = _browser.GetBrowser().GetHost();
            host.SendMouseMoveEvent(x, y, false, CefEventFlags.None);
            if (pressed) host.SendMouseClickEvent(x, y, MouseButtonType.Left, false, 1, CefEventFlags.LeftMouseButton);
            if (released) host.SendMouseClickEvent(x, y, MouseButtonType.Left, true, 1, CefEventFlags.None);
            if (wheelDelta != 0) host.SendMouseWheelEvent(x, y, 0, wheelDelta, CefEventFlags.None);
        }

        public void Dispose()
        {
            lock (_postJsonSync)
            {
                if (_disposed) return;
                _disposed = true;
                _documentReady = false;
                _pendingPostJson.Clear();
            }
            if (_acceleratedSubmitter != null)
            {
                _acceleratedSubmitter.Ready -= OnAcceleratedTransportReady;
                _acceleratedSubmitter.Unavailable -= OnAcceleratedTransportUnavailable;
            }
            if (!_acceleratedRendering) _browser.Paint -= OnPaint;
            _browser.JavascriptMessageReceived -= OnJavascriptMessageReceived;
            _browser.LoadingStateChanged -= OnLoadingStateChanged;
            _browser.Dispose();
            _requestContext.Dispose();
        }

        private void OnPaint(object? sender, OnPaintEventArgs args)
        {
            if (args.IsPopup || _disposed) return;
            args.Handled = false;
            try
            {
                args.Handled = NativeCompositor.SubmitFrame(
                    args.BufferHandle,
                    args.Width,
                    args.Height,
                    checked(args.Width * 4),
                    ++_frameGeneration);
            }
            catch (Exception)
            {
                // Optional native packaging/ABI failures must not unwind into
                // CefSharp's paint callback. Leaving Handled false is the
                // deterministic fail-open behavior for this frame.
                args.Handled = false;
            }
        }

        private void OnLoadingStateChanged(object? sender, LoadingStateChangedEventArgs args)
        {
            if (_disposed)
            {
                return;
            }

            if (args.IsLoading)
            {
                lock (_postJsonSync)
                {
                    _documentReady = false;
                }
                if (System.Threading.Interlocked.Exchange(ref _contentReady, 0) == 1)
                    PublishSafely(ContentUnavailable);
                return;
            }

            lock (_postJsonSync)
            {
                _documentReady = true;
                while (_pendingPostJson.Count > 0)
                    DispatchJsonUnsafe(_pendingPostJson.Dequeue());
            }

            var becameReady = System.Threading.Interlocked.Exchange(ref _contentReady, 1) == 0;

            if (!_contentReadyLogged)
            {
                _contentReadyLogged = true;
                StartupTrace.Write(
                    _logDirectory,
                    "reactorv-runtime.log",
                    "directx",
                    "content_ready",
                    $"duration_ms={_startupTimer.Elapsed.TotalMilliseconds:F3}");
            }

            if (becameReady) PublishSafely(ContentReady);
        }

        // Callers hold _postJsonSync so a live message cannot overtake the
        // startup queue while LoadingStateChanged drains it.
        private void DispatchJsonUnsafe(string json)
        {
            var script = "window.dispatchEvent(new CustomEvent('ragewebui:message',{detail:" + json + "}));";
            _browser.GetMainFrame().ExecuteJavaScriptAsync(script, "ragewebui://bridge", 1);
        }

        private void OnJavascriptMessageReceived(object? sender, JavascriptMessageReceivedEventArgs args)
        {
            var json = JsonConvert.SerializeObject(args.Message);
            if (_bridgeSink.TryEnqueue(json, out var error)) return;

            var id = "invalid";
            try
            {
                var candidate = JObject.Parse(json).Value<string>("id");
                if (!string.IsNullOrWhiteSpace(candidate) && candidate!.Length <= 64) id = candidate;
            }
            catch (JsonException)
            {
                // The generic protocol error below is intentional.
            }
            PostJson(BridgeProtocol.SerializeResponse(BridgeResponse.Failure(
                id,
                error?.Code ?? "invalid_request",
                error?.Message ?? "The bridge request was rejected.")));
        }

        private void OnAcceleratedPaintObserved(AcceleratedPaintObservation observation)
        {
            if (observation.Result == AcceleratedFrameSubmitResult.CallbackStarted)
            {
                var startedCallbacks = System.Threading.Interlocked.Increment(
                    ref _acceleratedPaintCallbackCount);
                if (startedCallbacks == 1)
                {
                    QueueAcceleratedTrace(
                        "accelerated_paint_first_callback",
                        $"generation={observation.Generation} " +
                        $"handle_valid={observation.SharedTextureHandle != IntPtr.Zero} " +
                        $"size={observation.Width}x{observation.Height} " +
                        $"format={observation.ColorType} " +
                        $"duration_ms={_startupTimer.Elapsed.TotalMilliseconds:F3}");
                }
                return;
            }

            var callbackCount = AcceleratedPaintCallbackCount;
            switch (observation.Result)
            {
                case AcceleratedFrameSubmitResult.BootstrapProbeRejected:
                    var rejected = System.Threading.Interlocked.Increment(
                        ref _acceleratedProbeRejectedCount);
                    if (rejected == 1 || rejected % 8 == 0)
                    {
                        QueueAcceleratedTrace(
                            "accelerated_bootstrap_probe_rejected",
                            $"rejected={rejected} callbacks={callbackCount} " +
                            $"generation={observation.Generation} " +
                            $"probe_status={_acceleratedSubmitter?.LastStatus} " +
                            ProducerDiagnosticDetail());
                    }
                    break;
                case AcceleratedFrameSubmitResult.BootstrapProbeDeferred:
                    System.Threading.Interlocked.Increment(
                        ref _acceleratedProbeDeferredCount);
                    break;
                case AcceleratedFrameSubmitResult.Submitted:
                    PublishSafely(
                        AcceleratedFrameSubmitted,
                        observation.Width,
                        observation.Height,
                        observation.Generation);
                    if (System.Threading.Interlocked.Exchange(
                            ref _acceleratedSubmitLogged,
                            1) == 0)
                    {
                        QueueAcceleratedTrace(
                            "accelerated_transport_first_submit",
                            $"callbacks={callbackCount} generation={observation.Generation} " +
                            $"probe_rejected={AcceleratedProbeRejectedCount} " +
                            $"probe_deferred={AcceleratedProbeDeferredCount}");
                    }
                    break;
                case AcceleratedFrameSubmitResult.InvalidFrame:
                case AcceleratedFrameSubmitResult.Unavailable:
                case AcceleratedFrameSubmitResult.CallbackFaulted:
                    var failures = System.Threading.Interlocked.Increment(
                        ref _acceleratedSubmitFailureCount);
                    if (failures == 1 || failures % 8 == 0)
                    {
                        QueueAcceleratedTrace(
                            "accelerated_paint_submit_failure",
                            $"failures={failures} callbacks={callbackCount} " +
                            $"generation={observation.Generation} " +
                            $"result={observation.Result}");
                    }
                    break;
            }
        }

        private void QueueAcceleratedTrace(string stage, string detail)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ => StartupTrace.Write(
                _logDirectory,
                "reactorv-runtime.log",
                "directx",
                stage,
                detail));
        }

        private static string ProducerDiagnosticDetail()
        {
            return NativeCompositor.TryGetSharedTextureProducerDiagnostics(
                out var diagnostics)
                ? diagnostics.ToTraceDetail()
                : "producer_diagnostics=unavailable";
        }

        private void OnAcceleratedTransportReady() => PublishSafely(AcceleratedTransportReady);

        private void OnAcceleratedTransportUnavailable() =>
            PublishSafely(AcceleratedTransportUnavailable);

        private static void PublishSafely(Action? handlers)
        {
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try
                {
                    handler();
                }
                catch (Exception)
                {
                    // An observer cannot be allowed to unwind through a CEF
                    // callback or prevent the remaining observers running.
                }
            }
        }

        private static void PublishSafely(
            Action<int, int, ulong>? handlers,
            int width,
            int height,
            ulong generation)
        {
            if (handlers == null) return;
            foreach (Action<int, int, ulong> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(width, height, generation);
                }
                catch (Exception)
                {
                    // Frame observers are diagnostics/readiness participants;
                    // they cannot unwind through CEF's paint callback.
                }
            }
        }

        private static CefEventFlags ToCefModifiers(uint modifiers)
        {
            var result = CefEventFlags.None;
            if ((modifiers & 1) != 0) result |= CefEventFlags.ShiftDown;
            if ((modifiers & 2) != 0) result |= CefEventFlags.ControlDown;
            if ((modifiers & 4) != 0) result |= CefEventFlags.AltDown;
            if ((modifiers & 8) != 0) result |= CefEventFlags.LeftMouseButton;
            if ((modifiers & 16) != 0) result |= CefEventFlags.RightMouseButton;
            if ((modifiers & 32) != 0) result |= CefEventFlags.MiddleMouseButton;
            return result;
        }

        private static MouseButtonType ToMouseButton(int button) =>
            button == 1 ? MouseButtonType.Right : button == 2 ? MouseButtonType.Middle : MouseButtonType.Left;
    }
}
