using System;
using System.IO;
using System.Reflection;
using System.Threading;
using ReactorV.ExternalGpu;

namespace ReactorV.Preloader
{
    internal interface IExternalGpuBrowserProducerFactory
    {
        string DiscoverySource { get; }

        bool TryCreate(
            ExternalGpuBrowserProducerContext context,
            out IExternalGpuBrowserProducer? producer,
            out string detail);
    }

    /// <summary>
    /// Loads the optional producer from one fixed file beside the persistent
    /// preloader. The command line cannot redirect this boundary to arbitrary
    /// code, and the assembly is never touched while the shadow gate is off.
    /// </summary>
    internal sealed class ExternalGpuBrowserProducerAssemblyFactory :
        IExternalGpuBrowserProducerFactory
    {
        internal const string AssemblyFileName =
            "RageWebUI.DirectX.dll";
        internal const string ProducerTypeName =
            "RageWebUI.DirectX.ExternalGpuBrowserSession";

        private readonly string _assemblyPath;

        public ExternalGpuBrowserProducerAssemblyFactory(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
                throw new ArgumentException(
                    "The preloader directory is required.",
                    nameof(baseDirectory));

            _assemblyPath = Path.Combine(
                Path.GetFullPath(baseDirectory),
                AssemblyFileName);
        }

        public string DiscoverySource => "preloader-adjacent-assembly";

        public bool TryCreate(
            ExternalGpuBrowserProducerContext context,
            out IExternalGpuBrowserProducer? producer,
            out string detail)
        {
            producer = null;
            if (!File.Exists(_assemblyPath))
            {
                detail =
                    $"missing={AssemblyFileName}; install it beside " +
                    "ReactorV.Preloader.exe or disable externalGpuBrowserShadow";
                return false;
            }

            try
            {
                var assembly = Assembly.LoadFrom(_assemblyPath);
                var producerType = assembly.GetType(
                    ProducerTypeName,
                    throwOnError: false,
                    ignoreCase: false);
                if (producerType == null)
                {
                    detail = $"missing_type={ProducerTypeName}";
                    return false;
                }
                if (!typeof(IExternalGpuBrowserProducer).IsAssignableFrom(
                        producerType))
                {
                    detail =
                        $"incompatible_type={ProducerTypeName}; expected=" +
                        nameof(IExternalGpuBrowserProducer);
                    return false;
                }

                producer = Activator.CreateInstance(
                    producerType,
                    new object[] { context }) as IExternalGpuBrowserProducer;
                if (producer == null)
                {
                    detail =
                        $"construction_failed={ProducerTypeName}; expected_ctor=" +
                        nameof(ExternalGpuBrowserProducerContext);
                    return false;
                }

                detail =
                    $"assembly={AssemblyFileName}; type={ProducerTypeName}";
                return true;
            }
            catch (Exception error)
            {
                detail =
                    $"load_failed={error.GetType().Name}; message={error.Message}";
                return false;
            }
        }
    }

    /// <summary>
    /// Best-effort mirror of the persistent WebView2 host's already-authorized
    /// outbound state. This session never publishes bootstrap readiness and
    /// never owns a second BootstrapOverlayServer. Any producer fault disables
    /// only the shadow path and leaves WebView2/player behavior unchanged.
    /// </summary>
    internal sealed class ExternalGpuBrowserSession : IDisposable
    {
        private readonly IExternalGpuBrowserProducer _producer;
        private readonly Action<string, string?> _trace;
        private readonly string _rendererName;
        private int _active;
        private int _desiredVisible;
        private int _retainedFrameRefresh;
        private int _producerReleased;
        private int _disposed;

        private ExternalGpuBrowserSession(
            IExternalGpuBrowserProducer producer,
            Action<string, string?> trace)
        {
            _producer = producer ??
                throw new ArgumentNullException(nameof(producer));
            _trace = trace ?? throw new ArgumentNullException(nameof(trace));
            _rendererName = ReadRendererName(producer);
            _active = 1;
            _producer.ContentReady += OnContentReady;
            _producer.ContentUnavailable += OnContentUnavailable;
            _producer.StartupFailed += OnStartupFailed;
            if (_producer is IResizableExternalGpuBrowserProducer resizable)
            {
                resizable.PresentationReadinessChanged +=
                    OnPresentationReadinessChanged;
            }
        }

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public bool IsPresentationReady =>
            IsActive && ProducerPresentationReady(_producer);

        public bool SupportsRetainedPresentationRefresh =>
            _producer is IRetainedExternalGpuBrowserProducer;

        public int SurfaceWidth =>
            (_producer as IResizableExternalGpuBrowserProducer)?.SurfaceWidth ?? 0;

        public int SurfaceHeight =>
            (_producer as IResizableExternalGpuBrowserProducer)?.SurfaceHeight ?? 0;

        public event Action? Unavailable;
        public event Action<bool, int, int>? PresentationReadinessChanged;

        public static ExternalGpuBrowserSession? TryStart(
            bool enabled,
            ExternalGpuBrowserProducerContext context,
            IExternalGpuBrowserProducerFactory factory,
            Action<string, string?> trace)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            if (trace == null)
                throw new ArgumentNullException(nameof(trace));

            if (!enabled)
            {
                trace(
                    "external_gpu_browser_shadow_disabled",
                    "gate=off fallback=webview2");
                return null;
            }

            trace(
                "external_gpu_browser_shadow_discovery_begin",
                $"target_pid={context.TargetGtaProcessId} " +
                $"discovery={context.TransportDiscoveryName} " +
                $"source={factory.DiscoverySource}");

            IExternalGpuBrowserProducer? producer;
            string detail;
            try
            {
                if (!factory.TryCreate(context, out producer, out detail) ||
                    producer == null)
                {
                    trace(
                        "external_gpu_browser_shadow_unavailable",
                        $"source={factory.DiscoverySource} {detail} " +
                        "fallback=webview2");
                    producer?.Dispose();
                    return null;
                }
            }
            catch (Exception error)
            {
                trace(
                    "external_gpu_browser_shadow_unavailable",
                    $"source={factory.DiscoverySource} " +
                    $"factory_failed={error.GetType().Name} " +
                    $"message={error.Message} fallback=webview2");
                return null;
            }

            var session = new ExternalGpuBrowserSession(producer, trace);
            try
            {
                if (!producer.Start() || !session.IsActive)
                {
                    trace(
                        "external_gpu_browser_shadow_start_rejected",
                        $"renderer={session._rendererName} fallback=webview2");
                    session.Dispose();
                    return null;
                }

                trace(
                    "external_gpu_browser_shadow_started",
                    $"renderer={session._rendererName} target_pid=" +
                    $"{context.TargetGtaProcessId} authoritative_host=webview2 " +
                    "bridge_authority=bootstrap-server");
                return session;
            }
            catch (Exception error)
            {
                session.DisableAfterFault("start", error);
                return null;
            }
        }

        public void SetVisible(bool visible)
        {
            Volatile.Write(ref _desiredVisible, visible ? 1 : 0);
            if (!visible)
                Interlocked.Exchange(ref _retainedFrameRefresh, 0);
            Forward(
                "visibility",
                producer =>
                {
                    if (visible &&
                        Volatile.Read(ref _retainedFrameRefresh) != 0 &&
                        !ProducerPresentationReady(producer))
                    {
                        // The producer intentionally keeps the preceding
                        // qualified texture visible until the replacement is
                        // acknowledged. Do not translate temporary readiness
                        // loss into a native hidden edge.
                        return;
                    }
                    producer.SetVisible(
                        visible && ProducerPresentationReady(producer));
                });
        }

        public bool Resize(int width, int height)
        {
            if (!IsActive || Volatile.Read(ref _disposed) != 0)
                return false;
            if (!(_producer is IResizableExternalGpuBrowserProducer resizable))
            {
                _trace(
                    "external_gpu_browser_shadow_resize_rejected",
                    $"renderer={_rendererName} reason=unsupported " +
                    $"requested={width}x{height}");
                return false;
            }

            try
            {
                // Resize is an atomic presentation boundary. Suppress the old
                // surface immediately and keep the caller's desired state for
                // automatic promotion after the exact-size frame is acked.
                _producer.SetVisible(false);
                var accepted = resizable.Resize(width, height);
                _trace(
                    accepted
                        ? "external_gpu_browser_shadow_resize_requested"
                        : "external_gpu_browser_shadow_resize_rejected",
                    $"renderer={_rendererName} requested={width}x{height} " +
                    $"accepted={accepted}");
                return accepted;
            }
            catch (Exception error)
            {
                DisableAfterFault("resize", error);
                return false;
            }
        }

        public bool RefreshPresentation(bool retainCurrentFrame = false)
        {
            if (!IsActive || Volatile.Read(ref _disposed) != 0)
                return false;
            if (!(_producer is IResizableExternalGpuBrowserProducer resizable))
            {
                _trace(
                    "external_gpu_browser_shadow_refresh_rejected",
                    $"renderer={_rendererName} reason=unsupported");
                return false;
            }

            try
            {
                var retainedProducer =
                    _producer as IRetainedExternalGpuBrowserProducer;
                var canRetain = retainCurrentFrame &&
                    Volatile.Read(ref _desiredVisible) != 0 &&
                    ProducerPresentationReady(_producer) &&
                    retainedProducer != null;
                Interlocked.Exchange(
                    ref _retainedFrameRefresh,
                    canRetain ? 1 : 0);
                if (!canRetain)
                    _producer.SetVisible(false);
                var accepted = canRetain
                    ? retainedProducer!.RefreshPresentationRetainingCurrentFrame()
                    : resizable.RefreshPresentation();
                if (!accepted)
                    Interlocked.Exchange(ref _retainedFrameRefresh, 0);
                _trace(
                    accepted
                        ? "external_gpu_browser_shadow_refresh_requested"
                        : "external_gpu_browser_shadow_refresh_rejected",
                    $"renderer={_rendererName} " +
                    $"surface={resizable.SurfaceWidth}x{resizable.SurfaceHeight} " +
                    $"accepted={accepted} retained_frame={canRetain}");
                return accepted;
            }
            catch (Exception error)
            {
                DisableAfterFault("refresh", error);
                return false;
            }
        }

        public void PostJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;
            Forward("json", producer => producer.PostJson(json));
        }

        public void PostPointerInput(
            float normalizedX,
            float normalizedY,
            bool pressed,
            bool released,
            int wheelDelta) => Forward(
                "pointer",
                producer => producer.PostPointerInput(
                    normalizedX,
                    normalizedY,
                    pressed,
                    released,
                    wheelDelta));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Interlocked.Exchange(ref _active, 0);
            ReleaseProducer();
            _trace(
                "external_gpu_browser_shadow_stopped",
                $"renderer={_rendererName} reason=disposed");
        }

        private void Forward(
            string operation,
            Action<IExternalGpuBrowserProducer> action)
        {
            if (!IsActive || Volatile.Read(ref _disposed) != 0)
                return;
            try
            {
                action(_producer);
            }
            catch (Exception error)
            {
                DisableAfterFault(operation, error);
            }
        }

        private void OnContentReady()
        {
            _trace(
                "external_gpu_browser_shadow_content_ready",
                $"renderer={_rendererName} authority=shadow-only " +
                $"presentation_ready={IsPresentationReady} " +
                $"surface={SurfaceWidth}x{SurfaceHeight}");
        }

        private void OnContentUnavailable() => _trace(
            "external_gpu_browser_shadow_content_unavailable",
            $"renderer={_rendererName} fallback=webview2");

        private void OnPresentationReadinessChanged(
            bool ready,
            int width,
            int height)
        {
            if (ready)
                Interlocked.Exchange(ref _retainedFrameRefresh, 0);
            _trace(
                "external_gpu_browser_shadow_readiness_changed",
                $"renderer={_rendererName} ready={ready} " +
                $"surface={width}x{height}");
            var handlers = PresentationReadinessChanged;
            if (handlers != null)
            {
                foreach (Action<bool, int, int> handler in handlers.GetInvocationList())
                {
                    try { handler(ready, width, height); }
                    catch (Exception)
                    {
                        // A presentation coordinator cannot destabilize the
                        // optional browser producer.
                    }
                }
            }
            // Readiness is evidence, not presentation authority. The host STA
            // consumes this callback, verifies the exact presentation ID and
            // GTA client size, then applies the sole visibility decision. An
            // eager SetVisible(true) here used to race that arbitration and
            // forced an extra accelerated CEF invalidate on every handoff.
            if (ready)
                _trace(
                    "external_gpu_browser_shadow_presentation_ready",
                    $"renderer={_rendererName} surface={width}x{height} " +
                    "visibility_authority=host-arbiter");
        }

        private void OnStartupFailed(Exception error) =>
            DisableAfterFault("startup-event", error);

        private void DisableAfterFault(string operation, Exception error)
        {
            if (Interlocked.Exchange(ref _active, 0) == 0)
                return;
            _trace(
                "external_gpu_browser_shadow_faulted",
                $"renderer={_rendererName} operation={operation} " +
                $"type={error.GetType().Name} message={error.Message} " +
                "fallback=webview2");
            ReleaseProducer();
            try { Unavailable?.Invoke(); }
            catch (Exception callbackError)
            {
                _trace(
                    "external_gpu_browser_shadow_fallback_callback_failed",
                    $"type={callbackError.GetType().Name} " +
                    $"message={callbackError.Message}");
            }
        }

        private void ReleaseProducer()
        {
            if (Interlocked.Exchange(ref _producerReleased, 1) != 0)
                return;
            _producer.ContentReady -= OnContentReady;
            _producer.ContentUnavailable -= OnContentUnavailable;
            _producer.StartupFailed -= OnStartupFailed;
            if (_producer is IResizableExternalGpuBrowserProducer resizable)
            {
                resizable.PresentationReadinessChanged -=
                    OnPresentationReadinessChanged;
            }
            try
            {
                _producer.Dispose();
            }
            catch (Exception error)
            {
                _trace(
                    "external_gpu_browser_shadow_dispose_failed",
                    $"renderer={_rendererName} type={error.GetType().Name} " +
                    $"message={error.Message}");
            }
        }

        private static string ReadRendererName(
            IExternalGpuBrowserProducer producer)
        {
            try
            {
                return string.IsNullOrWhiteSpace(producer.RendererName)
                    ? "external-gpu-browser"
                    : producer.RendererName;
            }
            catch
            {
                return "external-gpu-browser";
            }
        }

        private static bool ProducerPresentationReady(
            IExternalGpuBrowserProducer producer) =>
            producer is IResizableExternalGpuBrowserProducer resizable
                ? resizable.IsPresentationReady
                // Compatibility for optional producers compiled before the
                // resize contract: their historical visibility edge remains
                // authoritative and cannot claim exact-size readiness.
                : true;
    }
}
