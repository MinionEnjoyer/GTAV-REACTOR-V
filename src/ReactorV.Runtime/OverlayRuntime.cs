using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using RageWebUI.DirectX;
using RageWebUI.DirectX.Native;

namespace RageWebUI.Runtime
{
    /// <summary>
    /// Renderer entry point loaded explicitly by the SHVDN bootstrap. This
    /// assembly and its browser dependencies live outside the scripts tree.
    /// </summary>
    public sealed class OverlayRuntime : IOverlayRuntime
    {
        private readonly string _renderer;
        private readonly IntPtr _gtaWindow;
        private readonly string _uiDirectory;
        private readonly string _runtimeDirectory;
        private readonly string _localDataDirectory;
        private readonly BridgeBroker _broker;
        private readonly int _width;
        private readonly int _height;
        private readonly int _frameRate;
        private readonly bool _enableDevTools;
        private readonly bool _startVisible;
        private IOverlayRuntime? _active;

        public OverlayRuntime(
            string renderer,
            IntPtr gtaWindow,
            string uiDirectory,
            string runtimeDirectory,
            string localDataDirectory,
            BridgeBroker broker,
            int width,
            int height,
            int frameRate,
            bool enableDevTools,
            bool startVisible)
        {
            _renderer = (renderer ?? "auto").Trim().ToLowerInvariant();
            _gtaWindow = gtaWindow;
            _uiDirectory = uiDirectory;
            _runtimeDirectory = runtimeDirectory;
            _localDataDirectory = localDataDirectory;
            _broker = broker;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _frameRate = Math.Max(15, Math.Min(60, frameRate));
            _enableDevTools = enableDevTools;
            _startVisible = startVisible;
        }

        public bool IsVisible => _active?.IsVisible == true;

        public string RendererName => _active?.RendererName ?? "Renderer pending";

        public bool Start()
        {
            if (_active != null)
            {
                return true;
            }

            var startTimer = Stopwatch.StartNew();
            RuntimeTrace.Write(
                _localDataDirectory,
                "overlay_start_begin",
                $"renderer={_renderer} runtime={_runtimeDirectory} ui={_uiDirectory}");
            var directXHostSupported = AppDomain.CurrentDomain.IsDefaultAppDomain();
            if (_renderer != "windowed" && !directXHostSupported)
            {
                // CefSharp contains C++/CLI callbacks which are only supported in
                // the CLR default AppDomain. SHVDN scripts run in a secondary
                // domain, where Cef.Initialize can terminate the whole game from
                // a native callback before managed exception handling can run.
                RuntimeTrace.Write(
                    _localDataDirectory,
                    "directx_skipped",
                    $"reason=cefsharp_requires_default_appdomain " +
                    $"domain={AppDomain.CurrentDomain.FriendlyName} requestedRenderer={_renderer} " +
                    "fallback=webview2");
            }

            if (_renderer != "windowed" && directXHostSupported)
            {
                DirectXRuntime? directX = null;
                try
                {
                    directX = new DirectXRuntime(new DirectXOverlaySession(
                        _gtaWindow,
                        _uiDirectory,
                        _runtimeDirectory,
                        Path.Combine(_localDataDirectory, "CEF"),
                        _broker,
                        _width,
                        _height,
                        _frameRate,
                        _enableDevTools,
                        useGameCursor: true));
                    if (directX.Start())
                    {
                        directX.SetVisible(_startVisible);
                        _active = directX;
                        RuntimeTrace.Write(
                            _localDataDirectory,
                            "overlay_start_ready",
                            $"renderer={directX.RendererName} duration_ms={startTimer.Elapsed.TotalMilliseconds:F3}");
                        return true;
                    }

                    directX.Dispose();
                    directX = null;
                    if (_renderer == "directx")
                    {
                        throw new InvalidOperationException("The DirectX compositor could not install its swap-chain hooks.");
                    }
                }
                catch (Exception error)
                {
                    directX?.Dispose();
                    RuntimeTrace.Write(
                        _localDataDirectory,
                        "directx_failed",
                        $"type={error.GetType().FullName} message={error.Message} " +
                        $"duration_ms={startTimer.Elapsed.TotalMilliseconds:F3}");
                    if (_renderer == "directx")
                    {
                        throw;
                    }
                }
            }

            var windowed = new WindowedOverlaySession(
                _gtaWindow,
                _uiDirectory,
                Path.Combine(_localDataDirectory, "WebView2"),
                _broker,
                _enableDevTools,
                _startVisible);
            if (!windowed.Start())
            {
                windowed.Dispose();
                throw new InvalidOperationException("REACTOR V could not initialize its windowed renderer.");
            }

            _active = windowed;
            RuntimeTrace.Write(
                _localDataDirectory,
                "overlay_start_ready",
                $"renderer=WebView2_window duration_ms={startTimer.Elapsed.TotalMilliseconds:F3}");
            return true;
        }

        public void SetVisible(bool visible)
        {
            RuntimeTrace.Write(
                _localDataDirectory,
                "visibility_requested",
                $"visible={visible} renderer={RendererName}");
            RequireActive().SetVisible(visible);
        }

        public void PumpInput() => _active?.PumpInput();

        public void UpdateCursor(float normalizedX, float normalizedY, bool pressed, bool released, int wheelDelta) =>
            _active?.UpdateCursor(normalizedX, normalizedY, pressed, released, wheelDelta);

        public void PostResponse(BridgeResponse response) => _active?.PostResponse(response);

        public void PostEvent(string eventName, JToken? payload) => _active?.PostEvent(eventName, payload);

        public void Dispose()
        {
            RuntimeTrace.Write(_localDataDirectory, "overlay_dispose_begin", $"renderer={RendererName}");
            _active?.Dispose();
            _active = null;
            RuntimeTrace.Write(_localDataDirectory, "overlay_dispose_complete");
        }

        private IOverlayRuntime RequireActive() =>
            _active ?? throw new InvalidOperationException("REACTOR V renderer has not started.");

        private sealed class DirectXRuntime : IOverlayRuntime
        {
            private readonly DirectXOverlaySession _session;

            public DirectXRuntime(DirectXOverlaySession session) => _session = session;

            public bool IsVisible => _session.IsVisible;

            public string RendererName
            {
                get
                {
                    var api = _session.Stats.Api;
                    return api == RenderApi.None ? "DirectX (detecting)" :
                        api == RenderApi.Direct3D11 ? "DirectX 11" : "DirectX 12";
                }
            }

            public bool Start() => _session.StartInjected();

            public void SetVisible(bool visible) => _session.SetVisible(visible);

            public void PumpInput() => _session.PumpInput();

            public void UpdateCursor(float normalizedX, float normalizedY, bool pressed, bool released, int wheelDelta) =>
                _session.SendGameCursor(normalizedX, normalizedY, pressed, released, wheelDelta);

            public void PostResponse(BridgeResponse response) => _session.PostResponse(response);

            public void PostEvent(string eventName, JToken? payload) => _session.PostEvent(eventName, payload);

            public void Dispose() => _session.Dispose();
        }
    }

    internal static class RuntimeTrace
    {
        public static void Write(
            string localDataDirectory,
            string stage,
            string? detail = null) =>
            StartupTrace.Write(
                localDataDirectory,
                "reactorv-runtime.log",
                "runtime",
                stage,
                detail);
    }
}
