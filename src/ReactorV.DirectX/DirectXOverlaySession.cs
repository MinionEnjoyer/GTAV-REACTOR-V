using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using RageWebUI.DirectX.Browser;
using RageWebUI.DirectX.Native;

namespace RageWebUI.DirectX
{
    public sealed class DirectXOverlaySession : IDisposable
    {
        private readonly IntPtr _parentWindow;
        private readonly string _uiDirectory;
        private readonly string _runtimeDirectory;
        private readonly string _cacheDirectory;
        private readonly IBridgeMessageSink _bridgeSink;
        private readonly int _frameRate;
        private readonly bool _enableDevTools;
        private readonly bool _useGameCursor;
        private OffscreenBrowser? _browser;
        private int _visible;
        private int _disposed;
        private int _lastWidth;
        private int _lastHeight;
        private int _inputWidth;
        private int _inputHeight;
        private bool _nativeStarted;
        private bool _harnessStarted;

        public DirectXOverlaySession(
            IntPtr parentWindow,
            string uiDirectory,
            string runtimeDirectory,
            string cacheDirectory,
            IBridgeMessageSink bridgeSink,
            int initialWidth,
            int initialHeight,
            int frameRate = 60,
            bool enableDevTools = true,
            bool useGameCursor = false)
        {
            RuntimeDependencyLoader.Prepare(runtimeDirectory);
            _parentWindow = parentWindow;
            _uiDirectory = uiDirectory;
            _runtimeDirectory = runtimeDirectory;
            _cacheDirectory = cacheDirectory;
            _bridgeSink = bridgeSink ?? throw new ArgumentNullException(nameof(bridgeSink));
            _lastWidth = Math.Max(1, initialWidth);
            _lastHeight = Math.Max(1, initialHeight);
            _inputWidth = _lastWidth;
            _inputHeight = _lastHeight;
            _frameRate = Math.Max(1, Math.Min(60, frameRate));
            _enableDevTools = enableDevTools;
            _useGameCursor = useGameCursor;
        }

        public bool IsVisible => Volatile.Read(ref _visible) == 1;

        public RenderStats Stats => NativeCompositor.TryGetStats(out var stats) ? stats : default;

        public bool IsHarnessRunning => NativeCompositor.IsTestRunning;

        public bool StartInjected()
        {
            ThrowIfDisposed();
            if (!NativeCompositor.Initialize(_parentWindow)) return false;
            _nativeStarted = true;
            try
            {
                StartBrowser();
                return true;
            }
            catch
            {
                NativeCompositor.Shutdown();
                _nativeStarted = false;
                throw;
            }
        }

        public bool StartHarness(RenderApi api, string title)
        {
            ThrowIfDisposed();
            if (!NativeCompositor.StartTest(api, _lastWidth, _lastHeight, title)) return false;
            _harnessStarted = true;
            try
            {
                StartBrowser();
                SetVisible(true);
                return true;
            }
            catch
            {
                NativeCompositor.StopTest();
                _harnessStarted = false;
                throw;
            }
        }

        public void Toggle() => SetVisible(!IsVisible);

        public void SetVisible(bool visible)
        {
            Interlocked.Exchange(ref _visible, visible ? 1 : 0);
            NativeCompositor.SetVisible(visible);
            _browser?.SetVisible(visible);
        }

        public void PumpInput()
        {
            if (_browser == null) return;
            var processed = 0;
            while (processed++ < 256 && NativeCompositor.PollInput(out var input))
            {
                if (input.Type == NativeInputType.Resize)
                {
                    _inputWidth = Math.Max(1, input.X);
                    _inputHeight = Math.Max(1, input.Y);
                    continue;
                }
                if (_useGameCursor && input.Type >= NativeInputType.MouseMove && input.Type <= NativeInputType.MouseWheel)
                    continue;
                if (input.Type >= NativeInputType.MouseMove && input.Type <= NativeInputType.MouseWheel &&
                    (_inputWidth != _lastWidth || _inputHeight != _lastHeight))
                {
                    input.X = input.X * _lastWidth / _inputWidth;
                    input.Y = input.Y * _lastHeight / _inputHeight;
                }
                _browser.SendInput(input);
            }

            if (NativeCompositor.TryGetStats(out var stats) && stats.Width > 0 && stats.Height > 0 &&
                (stats.Width != _lastWidth || stats.Height != _lastHeight))
            {
                _lastWidth = stats.Width;
                _lastHeight = stats.Height;
                _browser.Resize(_lastWidth, _lastHeight);
            }
        }

        public void SendGameCursor(float normalizedX, float normalizedY, bool pressed, bool released, int wheelDelta)
        {
            if (_useGameCursor) _browser?.SendGameCursor(normalizedX, normalizedY, pressed, released, wheelDelta);
        }

        public void PostResponse(BridgeResponse response) =>
            _browser?.PostJson(BridgeProtocol.SerializeResponse(response));

        public void PostEvent(string eventName, JToken? payload) =>
            _browser?.PostJson(BridgeProtocol.SerializeEvent(eventName, payload));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            SetVisible(false);
            _browser?.Dispose();
            _browser = null;
            if (_harnessStarted) NativeCompositor.StopTest();
            if (_nativeStarted) NativeCompositor.Shutdown();
        }

        private void StartBrowser()
        {
            if (!Directory.Exists(_uiDirectory))
                throw new DirectoryNotFoundException($"RageWebUI assets were not found at '{_uiDirectory}'.");
            var timer = Stopwatch.StartNew();
            var logDirectory = Path.GetDirectoryName(_cacheDirectory) ?? _cacheDirectory;
            StartupTrace.Write(
                logDirectory,
                "reactorv-runtime.log",
                "directx",
                "browser_create_begin",
                $"width={_lastWidth} height={_lastHeight} frame_rate={_frameRate}");
            _browser = new OffscreenBrowser(
                _parentWindow,
                _uiDirectory,
                _runtimeDirectory,
                _cacheDirectory,
                _bridgeSink,
                _lastWidth,
                _lastHeight,
                _frameRate,
                _enableDevTools,
                IsVisible);
            StartupTrace.Write(
                logDirectory,
                "reactorv-runtime.log",
                "directx",
                "browser_create_complete",
                $"duration_ms={timer.Elapsed.TotalMilliseconds:F3}");
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(DirectXOverlaySession));
        }
    }
}
