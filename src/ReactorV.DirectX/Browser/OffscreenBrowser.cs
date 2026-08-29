using System;
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
        private readonly BridgeBroker _broker;
        private readonly ChromiumWebBrowser _browser;
        private readonly IRequestContext _requestContext;
        private readonly string _logDirectory;
        private readonly Stopwatch _startupTimer = Stopwatch.StartNew();
        private ulong _frameGeneration;
        private bool _disposed;
        private bool _desiredVisible;
        private bool _contentReadyLogged;

        public OffscreenBrowser(
            IntPtr parentWindow,
            string uiDirectory,
            string runtimeDirectory,
            string cacheDirectory,
            BridgeBroker broker,
            int width,
            int height,
            int frameRate,
            bool enableDevTools,
            bool startVisible)
        {
            _broker = broker;
            _desiredVisible = startVisible;
            _logDirectory = Path.GetDirectoryName(cacheDirectory) ?? cacheDirectory;
            CefRuntime.EnsureInitialized(runtimeDirectory, cacheDirectory);

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
            _browser = new ChromiumWebBrowser(
                "https://ragewebui.local/index.html",
                browserSettings,
                _requestContext,
                automaticallyCreateBrowser: false,
                useLegacyRenderHandler: true)
            {
                Size = new Size(Math.Max(1, width), Math.Max(1, height)),
                RequestHandler = new LocalOnlyRequestHandler(),
            };
            _browser.Paint += OnPaint;
            _browser.JavascriptMessageReceived += OnJavascriptMessageReceived;
            _browser.LoadingStateChanged += OnLoadingStateChanged;

            var windowInfo = new WindowInfo();
            windowInfo.SetAsWindowless(parentWindow);
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
                host.WasHidden(!_desiredVisible);
                if (_desiredVisible) host.Invalidate(PaintElementType.View);
                // DevTools remain callable through CefSharp APIs when enabled;
                // no eager DevTools window is needed for preload.
            };
        }

        public void Resize(int width, int height)
        {
            if (_disposed || width <= 0 || height <= 0) return;
            var size = new Size(width, height);
            if (_browser.Size != size) _browser.Size = size;
        }

        public void SetVisible(bool visible)
        {
            _desiredVisible = visible;
            if (_disposed || !_browser.IsBrowserInitialized) return;
            _browser.GetBrowser().GetHost().WasHidden(!visible);
            if (visible) _browser.GetBrowser().GetHost().Invalidate(PaintElementType.View);
        }

        public void PostJson(string json)
        {
            if (_disposed || !_browser.IsBrowserInitialized) return;
            var script = "window.dispatchEvent(new CustomEvent('ragewebui:message',{detail:" + json + "}));";
            _browser.GetMainFrame().ExecuteJavaScriptAsync(script, "ragewebui://bridge", 1);
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
            if (_disposed) return;
            _disposed = true;
            _browser.Paint -= OnPaint;
            _browser.JavascriptMessageReceived -= OnJavascriptMessageReceived;
            _browser.LoadingStateChanged -= OnLoadingStateChanged;
            _browser.Dispose();
            _requestContext.Dispose();
        }

        private void OnPaint(object? sender, OnPaintEventArgs args)
        {
            if (args.IsPopup || _disposed) return;
            args.Handled = true;
            NativeCompositor.SubmitFrame(
                args.BufferHandle,
                args.Width,
                args.Height,
                checked(args.Width * 4),
                ++_frameGeneration);
        }

        private void OnLoadingStateChanged(object? sender, LoadingStateChangedEventArgs args)
        {
            if (_disposed || args.IsLoading || _contentReadyLogged)
            {
                return;
            }

            _contentReadyLogged = true;
            StartupTrace.Write(
                _logDirectory,
                "reactorv-runtime.log",
                "directx",
                "content_ready",
                $"duration_ms={_startupTimer.Elapsed.TotalMilliseconds:F3}");
        }

        private void OnJavascriptMessageReceived(object? sender, JavascriptMessageReceivedEventArgs args)
        {
            var json = JsonConvert.SerializeObject(args.Message);
            if (_broker.TryEnqueue(json, out var error)) return;

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
