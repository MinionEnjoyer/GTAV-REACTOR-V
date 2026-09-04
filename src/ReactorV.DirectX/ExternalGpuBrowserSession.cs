using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RageWebUI.Core;
using RageWebUI.DirectX.Browser;
using RageWebUI.DirectX.Native;
using ReactorV.ExternalGpu;
using ReactorV.FrameTransport;
using ReactorV.WebView2Host;

namespace RageWebUI.DirectX
{
    /// <summary>
    /// Owns the accelerated off-screen browser and its native cross-process
    /// frame producer in the Preloader's CLR default AppDomain. Browser-to-game
    /// messages are forwarded to the supplied authoritative bridge sink; this
    /// session never creates a second bridge registry.
    /// </summary>
    public sealed class ExternalGpuBrowserSession : IExternalGpuBrowserProducer,
        IResizableExternalGpuBrowserProducer,
        IRetainedExternalGpuBrowserProducer
    {
        internal const int FirstAcceleratedFrameTimeoutMilliseconds = 10000;
        internal const int AdapterLuidDiscoveryTimeoutMilliseconds = 10000;
        internal const int AdapterLuidDiscoveryPollMilliseconds = 50;
        internal const int MaximumPendingPostJsonMessages = 256;
        internal const int AcceleratedBootstrapRepaintIntervalMilliseconds = 250;
        internal const int SurfaceAcknowledgementPollMilliseconds = 25;
        internal const int MaximumAcceleratedBootstrapRepaintAttempts =
            FirstAcceleratedFrameTimeoutMilliseconds /
            AcceleratedBootstrapRepaintIntervalMilliseconds + 1;

        private readonly object _sync = new object();
        private readonly uint _targetGtaProcessId;
        private readonly IntPtr _parentWindow;
        private readonly string _uiDirectory;
        private readonly string _runtimeDirectory;
        private readonly string _cacheDirectory;
        private readonly string _logDirectory;
        private readonly IBridgeMessageSink _bridgeSink;
        private int _requestedWidth;
        private int _requestedHeight;
        private readonly int _frameRate;
        private readonly bool _enableDevTools;

        private OffscreenBrowser? _browser;
        private readonly Queue<string> _pendingPostJson = new Queue<string>();
        private Timer? _adapterLuidDiscoveryTimer;
        private long _adapterLuidDiscoveryDeadlineTimestamp;
        private int _adapterLuidDiscoveryCompletion;
        private Timer? _firstFrameTimer;
        private Timer? _surfaceAcknowledgementTimer;
        private Timer? _bootstrapRepaintTimer;
        private long _bootstrapRepaintDeadlineTimestamp;
        private int _bootstrapRepaintAttempts;
        private bool _producerStarted;
        private bool _desiredVisible;
        private int _browserContentReady;
        private int _transportReady;
        private int _sizedFrameReady;
        private int _contentReady;
        private int _surfaceRevision;
        private int _matchingFrameRevision;
        private long _matchingSubmittedGeneration;
        private long _latestSubmittedGeneration;
        private long _minimumRequiredGeneration = 1;
        private int _disableQueued;
        private int _unavailablePublished;
        private int _started;
        private int _disposed;

        public ExternalGpuBrowserSession(ExternalGpuBrowserProducerContext context)
            : this(
                (context ?? throw new ArgumentNullException(nameof(context))).TargetGtaProcessId,
                context!.UiDirectory,
                context.RuntimeDirectory,
                context.UserDataDirectory,
                context.BridgeSink,
                context.Width,
                context.Height,
                context.FrameRate,
                context.EnableDevTools,
                context.ParentWindow)
        {
        }

        public ExternalGpuBrowserSession(
            int targetGtaProcessId,
            string uiDirectory,
            string runtimeDirectory,
            string cacheDirectory,
            IBridgeMessageSink bridgeSink,
            int width,
            int height,
            int frameRate = 60,
            bool enableDevTools = false,
            IntPtr parentWindow = default)
        {
            if (targetGtaProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetGtaProcessId));
            if (string.IsNullOrWhiteSpace(uiDirectory))
                throw new ArgumentException("The UI directory is required.", nameof(uiDirectory));
            if (string.IsNullOrWhiteSpace(runtimeDirectory))
                throw new ArgumentException("The runtime directory is required.", nameof(runtimeDirectory));
            if (string.IsNullOrWhiteSpace(cacheDirectory))
                throw new ArgumentException("The cache directory is required.", nameof(cacheDirectory));
            if (bridgeSink == null)
                throw new ArgumentNullException(nameof(bridgeSink));
            if (width <= 0 || (uint)width > SharedGpuFrameProtocol.MaximumDimension)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0 || (uint)height > SharedGpuFrameProtocol.MaximumDimension)
                throw new ArgumentOutOfRangeException(nameof(height));
            if ((ulong)width * (ulong)height * 4ul >
                SharedGpuFrameProtocol.MaximumBytes)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (frameRate < 1 || frameRate > 60)
                throw new ArgumentOutOfRangeException(nameof(frameRate));

            _targetGtaProcessId = checked((uint)targetGtaProcessId);
            _parentWindow = parentWindow;
            _uiDirectory = Path.GetFullPath(uiDirectory);
            _runtimeDirectory = Path.GetFullPath(runtimeDirectory);
            _cacheDirectory = Path.GetFullPath(cacheDirectory);
            _logDirectory = Path.GetDirectoryName(_cacheDirectory) ?? _cacheDirectory;
            _bridgeSink = bridgeSink;
            _requestedWidth = width;
            _requestedHeight = height;
            _frameRate = frameRate;
            _enableDevTools = enableDevTools;
        }

        public string RendererName => "cef-gpu-external";

        public bool IsContentReady => Volatile.Read(ref _contentReady) == 1;

        public bool IsPresentationReady => IsContentReady;

        public int SurfaceWidth => Volatile.Read(ref _requestedWidth);

        public int SurfaceHeight => Volatile.Read(ref _requestedHeight);

        public bool IsStarted => Volatile.Read(ref _started) == 1;

        public event Action? ContentReady;
        public event Action? ContentUnavailable;
        public event Action<Exception>? StartupFailed;
        public event Action<bool, int, int>? PresentationReadinessChanged;

        public bool Start()
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                ThrowIfDisposed();
                if (Volatile.Read(ref _started) == 1) return true;
                Volatile.Write(ref _disableQueued, 0);
                Volatile.Write(ref _unavailablePublished, 0);

                try
                {
                    RuntimeDependencyLoader.Prepare(_runtimeDirectory);
                    if (!NativeCompositor.StartSharedTextureProducer(_targetGtaProcessId))
                        return false;
                    _producerStarted = true;
                    // Output stays hidden until a frame at the requested GTA
                    // client size has completed the consumer acknowledgement.
                    if (!NativeCompositor.SetSharedTextureProducerVisible(false))
                    {
                        StopProducer();
                        return false;
                    }

                    // Probe support is advertised separately from regular
                    // submission because the producer cannot select a matching
                    // GPU adapter until CEF supplies its first transient handle.
                    if (!NativeCompositor.TryGetSharedTextureCapabilities(out var capabilities) ||
                        (!capabilities.SupportsSynchronousBgra8 &&
                         !capabilities.SupportsBootstrapProbe) ||
                        !capabilities.SupportsDimensions(
                            SurfaceWidth,
                            SurfaceHeight))
                    {
                        StopProducer();
                        return false;
                    }

                    Directory.CreateDirectory(_cacheDirectory);
                    // The native compositor may not have captured GTA's device
                    // yet. Mark the lifecycle active and poll its authoritative
                    // adapter mapping on a worker. CEF is process-global, so it
                    // must not initialize until that exact LUID is available.
                    Volatile.Write(ref _started, 1);
                    StartAdapterLuidDiscovery();
                    return true;
                }
                catch (Exception error)
                {
                    StopBrowser();
                    StopProducer();
                    Volatile.Write(ref _started, 0);
                    NotifyStartupFailed(error);
                    return false;
                }
            }
        }

        public void SetVisible(bool visible)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                ThrowIfDisposed();
                _desiredVisible = visible;
                var presentationVisible = visible && IsPresentationReady;
                var visibilityApplied = !_producerStarted ||
                    (presentationVisible
                        ? NativeCompositor.SetSharedTextureProducerVisible(visible)
                        : NativeCompositor.SetSharedTextureProducerVisible(false));
                if (!visibilityApplied)
                {
                    QueueExternalGpuDisable(
                        "presentation-control-unavailable",
                        cancelIfTransportRecovered: false);
                    return;
                }
                // The accelerated browser is a required participant in the
                // dual-document presentation-ready barrier. CefSharp's
                // WasHidden(true) suspends requestAnimationFrame, so sleeping
                // the document here deadlocks a hidden-before-reveal menu: the
                // browser can never cross its two-frame paint boundary. Keep
                // CEF render-awake and use the native producer visibility bit
                // above to suppress compositor output while the menu is hidden.
                _browser?.SetVisible(true);
            }
        }

        public bool Resize(int width, int height)
        {
            ThrowIfDisposed();
            if (!SupportsSurfaceDimensions(width, height)) return false;

            lock (_sync)
            {
                ThrowIfDisposed();
                if (SurfaceWidth == width && SurfaceHeight == height)
                    return true;

                Volatile.Write(ref _requestedWidth, width);
                Volatile.Write(ref _requestedHeight, height);
                Interlocked.Increment(ref _surfaceRevision);
                InvalidateSizedFrameReadiness();

                if (_producerStarted &&
                    !NativeCompositor.SetSharedTextureProducerVisible(false))
                {
                    QueueExternalGpuDisable(
                        "resize-presentation-control-unavailable",
                        cancelIfTransportRecovered: false);
                    return false;
                }

                TraceAcceleratedBootstrap(
                    "accelerated_surface_resize_requested",
                    $"size={width}x{height} revision=" +
                    Volatile.Read(ref _surfaceRevision));

                var browser = _browser;
                if (browser == null) return true;
                if (!browser.Resize(width, height))
                {
                    QueueExternalGpuDisable(
                        "accelerated-surface-resize-rejected",
                        cancelIfTransportRecovered: false);
                    return false;
                }

                ArmSizedFrameDeadline();
                StartAcceleratedBootstrapRepaintPump();
                return true;
            }
        }

        public bool RefreshPresentation() =>
            RefreshPresentationCore(retainCurrentFrame: false);

        public bool RefreshPresentationRetainingCurrentFrame() =>
            RefreshPresentationCore(retainCurrentFrame: true);

        private bool RefreshPresentationCore(bool retainCurrentFrame)
        {
            ThrowIfDisposed();

            lock (_sync)
            {
                ThrowIfDisposed();
                var browser = _browser;
                if (!_producerStarted || browser == null || !IsStarted)
                {
                    TraceAcceleratedBootstrap(
                        "accelerated_presentation_refresh_rejected",
                        "reason=producer-not-ready");
                    return false;
                }

                // A cold refresh retires the preceding presentation. During a
                // same-native-plane handoff, keep the last qualified texture
                // visible while the strictly newer frame is staged. The GTA
                // consumer owns a local copy, so replacing it after a complete
                // copy is an atomic frame boundary rather than a blank pulse.
                var retainQualifiedFrame = retainCurrentFrame &&
                    _desiredVisible && IsPresentationReady;
                if (!retainQualifiedFrame &&
                    !NativeCompositor.SetSharedTextureProducerVisible(false))
                {
                    QueueExternalGpuDisable(
                        "refresh-presentation-control-unavailable",
                        cancelIfTransportRecovered: false);
                    return false;
                }

                var generationHighWatermark =
                    CaptureGenerationHighWatermark(browser);
                var minimumRequiredGeneration = NextGeneration(
                    generationHighWatermark);
                Interlocked.Exchange(
                    ref _minimumRequiredGeneration,
                    minimumRequiredGeneration);
                var revision = Interlocked.Increment(ref _surfaceRevision);
                InvalidateSizedFrameReadiness();

                TraceAcceleratedBootstrap(
                    "accelerated_presentation_refresh_requested",
                    $"size={SurfaceWidth}x{SurfaceHeight} revision={revision} " +
                    $"generation_high_watermark={generationHighWatermark} " +
                    $"minimum_generation={minimumRequiredGeneration} " +
                    $"retained_frame={retainQualifiedFrame}");

                ArmSizedFrameDeadline();
                // The one invalidation below can be dropped by transport
                // backpressure/rate limiting. A static menu may never paint
                // again. Retry only until this exact revision earns an ACK.
                StartAcceleratedBootstrapRepaintPump();
                if (browser.RequestAcceleratedPresentationPaint())
                    return true;

                QueueExternalGpuDisable(
                    "accelerated-presentation-refresh-rejected",
                    cancelIfTransportRecovered: false);
                return false;
            }
        }

        public void PostJson(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            ThrowIfDisposed();
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_browser != null)
                {
                    _browser.PostJson(json);
                }
                else
                {
                    if (_pendingPostJson.Count == MaximumPendingPostJsonMessages)
                        _pendingPostJson.Dequeue();
                    _pendingPostJson.Enqueue(json);
                }
            }
        }

        public void PostPointerInput(
            float normalizedX,
            float normalizedY,
            bool pressed,
            bool released,
            int wheelDelta)
        {
            ThrowIfDisposed();
            var pointerEventJson = WindowedInputPolicy.SerializeProviderPointerEvent(
                normalizedX,
                normalizedY,
                pressed,
                released,
                wheelDelta);
            lock (_sync)
            {
                ThrowIfDisposed();
                // This is the only provider pointer route. The page renders its
                // own cursor into the shared texture and dispatches the click
                // from this typed event; a native CEF mouse event here would
                // execute the action twice while leaving the cursor invisible.
                _browser?.PostJson(pointerEventJson);
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                StopBrowser();
                StopProducer();
                Volatile.Write(ref _browserContentReady, 0);
                Volatile.Write(ref _transportReady, 0);
                Volatile.Write(ref _sizedFrameReady, 0);
                Volatile.Write(ref _contentReady, 0);
                Volatile.Write(ref _disableQueued, 0);
                Interlocked.Exchange(ref _latestSubmittedGeneration, 0);
                Interlocked.Exchange(ref _minimumRequiredGeneration, 1);
                Volatile.Write(ref _started, 0);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
        }

        private OffscreenBrowser CreateBrowser(GpuAdapterLuid adapterLuid) => new OffscreenBrowser(
                _parentWindow,
                _uiDirectory,
                _runtimeDirectory,
                _cacheDirectory,
                _bridgeSink,
                SurfaceWidth,
                SurfaceHeight,
                _frameRate,
                _enableDevTools,
                // The native shared-texture producer owns presentation
                // visibility. The document itself must stay render-awake so a
                // hidden replacement can become ready before its atomic reveal.
                startVisible: true,
                allowAcceleratedBootstrapProbe: true,
                forceCpuRendering: false,
                browserRole: "gpu-renderer",
                adapterLuid: adapterLuid);

        private void AttachBrowser(OffscreenBrowser browser)
        {
            _browser = browser ?? throw new ArgumentNullException(nameof(browser));
            browser.ContentReady += OnBrowserContentReady;
            browser.ContentUnavailable += OnBrowserContentUnavailable;
            browser.AcceleratedTransportReady += OnAcceleratedTransportReady;
            browser.AcceleratedTransportUnavailable += OnAcceleratedTransportUnavailable;
            browser.AcceleratedFrameSubmitted += OnAcceleratedFrameSubmitted;

            Volatile.Write(ref _browserContentReady, browser.IsContentReady ? 1 : 0);
            Volatile.Write(ref _transportReady, browser.IsAcceleratedTransportReady ? 1 : 0);
            Volatile.Write(ref _sizedFrameReady, 0);
            Volatile.Write(ref _contentReady, 0);
            Volatile.Write(ref _matchingFrameRevision, 0);
            Interlocked.Exchange(ref _matchingSubmittedGeneration, 0);
            Interlocked.Exchange(ref _latestSubmittedGeneration, 0);
            Interlocked.Exchange(ref _minimumRequiredGeneration, 1);

            if (browser.SurfaceWidth != SurfaceWidth ||
                browser.SurfaceHeight != SurfaceHeight)
            {
                if (!browser.Resize(SurfaceWidth, SurfaceHeight))
                    throw new InvalidOperationException(
                        "CEF rejected the requested GTA client surface size.");
            }

            while (_pendingPostJson.Count > 0)
                browser.PostJson(_pendingPostJson.Dequeue());

            ArmSizedFrameDeadline();
            StartAcceleratedBootstrapRepaintPump();

            PublishContentReadyIfEligible();
        }

        private void StopBrowser()
        {
            CancelAdapterLuidDiscovery();
            CancelFirstFrameTimer();
            CancelSurfaceAcknowledgementPoll();
            CancelAcceleratedBootstrapRepaintPump();
            _pendingPostJson.Clear();
            var browser = _browser;
            _browser = null;
            if (browser == null) return;
            browser.ContentReady -= OnBrowserContentReady;
            browser.ContentUnavailable -= OnBrowserContentUnavailable;
            browser.AcceleratedTransportReady -= OnAcceleratedTransportReady;
            browser.AcceleratedTransportUnavailable -= OnAcceleratedTransportUnavailable;
            browser.AcceleratedFrameSubmitted -= OnAcceleratedFrameSubmitted;
            browser.Dispose();
        }

        private void StartAdapterLuidDiscovery()
        {
            CancelAdapterLuidDiscovery();
            Volatile.Write(ref _adapterLuidDiscoveryCompletion, 0);
            Volatile.Write(
                ref _adapterLuidDiscoveryDeadlineTimestamp,
                Stopwatch.GetTimestamp() + Math.Max(
                    1L,
                    Stopwatch.Frequency *
                    AdapterLuidDiscoveryTimeoutMilliseconds / 1000L));
            var timer = new Timer(
                _ => PollAdapterLuidDiscovery(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);
            Interlocked.Exchange(ref _adapterLuidDiscoveryTimer, timer)?.Dispose();
            TraceAcceleratedBootstrap(
                "adapter_luid_discovery_started",
                $"target_pid={_targetGtaProcessId} " +
                $"poll_ms={AdapterLuidDiscoveryPollMilliseconds} " +
                $"deadline_ms={AdapterLuidDiscoveryTimeoutMilliseconds}");
            try
            {
                timer.Change(0, AdapterLuidDiscoveryPollMilliseconds);
            }
            catch (ObjectDisposedException)
            {
                // Stop may win immediately after the lifecycle is armed.
            }
        }

        private void PollAdapterLuidDiscovery()
        {
            var found = NativeAdapterLuidDiscovery.TryQuery(
                _targetGtaProcessId,
                out var adapterLuid);
            var decision = AdapterLuidDiscoveryWaitPolicy.Evaluate(
                found,
                Stopwatch.GetTimestamp() >= Volatile.Read(
                    ref _adapterLuidDiscoveryDeadlineTimestamp),
                Volatile.Read(ref _disposed) != 0 ||
                    Volatile.Read(ref _started) == 0);
            if (decision == AdapterLuidDiscoveryDecision.Continue) return;
            if (Interlocked.CompareExchange(
                    ref _adapterLuidDiscoveryCompletion, 1, 0) != 0) return;
            CancelAdapterLuidDiscoveryTimer();

            if (decision == AdapterLuidDiscoveryDecision.Stop) return;
            if (decision == AdapterLuidDiscoveryDecision.DisableExternalGpuPath)
            {
                TraceAcceleratedBootstrap(
                    "adapter_luid_discovery_deadline",
                    $"target_pid={_targetGtaProcessId} outcome=not-published");
                QueueExternalGpuDisable(
                    "adapter-luid-discovery-timeout",
                    cancelIfTransportRecovered: false);
                return;
            }

            TraceAcceleratedBootstrap(
                "adapter_luid_discovered",
                $"target_pid={_targetGtaProcessId} adapter_luid={adapterLuid}");
            try
            {
                // This callback runs on a Timer/ThreadPool worker. CEF's
                // context-readiness wait can never block the Preloader UI.
                var browser = CreateBrowser(adapterLuid);
                lock (_sync)
                {
                    if (Volatile.Read(ref _disposed) != 0 ||
                        Volatile.Read(ref _started) == 0)
                    {
                        browser.Dispose();
                        return;
                    }
                    AttachBrowser(browser);
                }
            }
            catch (Exception error)
            {
                TraceAcceleratedBootstrap(
                    "adapter_pinned_cef_startup_failed",
                    $"type={error.GetType().Name} message={error.Message}");
                QueueExternalGpuDisable(
                    "adapter-pinned-cef-startup-failed",
                    cancelIfTransportRecovered: false);
            }
        }

        private void CancelAdapterLuidDiscovery()
        {
            Interlocked.Exchange(ref _adapterLuidDiscoveryCompletion, 1);
            CancelAdapterLuidDiscoveryTimer();
        }

        private void CancelAdapterLuidDiscoveryTimer()
        {
            var timer = Interlocked.Exchange(ref _adapterLuidDiscoveryTimer, null);
            timer?.Dispose();
        }

        private void StopProducer()
        {
            if (!_producerStarted) return;
            _producerStarted = false;
            NativeCompositor.StopSharedTextureProducer();
        }

        private void CancelFirstFrameTimer()
        {
            var timer = Interlocked.Exchange(ref _firstFrameTimer, null);
            timer?.Dispose();
        }

        private void ArmSizedFrameDeadline()
        {
            CancelFirstFrameTimer();
            _firstFrameTimer = new Timer(
                _ => OnFirstAcceleratedFrameTimeout(),
                null,
                FirstAcceleratedFrameTimeoutMilliseconds,
                Timeout.Infinite);
        }

        private void StartSurfaceAcknowledgementPoll()
        {
            if (Volatile.Read(ref _surfaceAcknowledgementTimer) != null)
                return;

            var timer = new Timer(
                _ => PollSurfaceAcknowledgement(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);
            if (Interlocked.CompareExchange(
                    ref _surfaceAcknowledgementTimer,
                    timer,
                    null) != null)
            {
                timer.Dispose();
                return;
            }

            try
            {
                timer.Change(
                    dueTime: 0,
                    period: SurfaceAcknowledgementPollMilliseconds);
            }
            catch (ObjectDisposedException)
            {
                // Stop or a synchronous acknowledgement won the race.
            }
        }

        private void CancelSurfaceAcknowledgementPoll()
        {
            var timer = Interlocked.Exchange(
                ref _surfaceAcknowledgementTimer,
                null);
            timer?.Dispose();
        }

        private void PollSurfaceAcknowledgement()
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _started) == 0)
            {
                CancelSurfaceAcknowledgementPoll();
                return;
            }

            var generation = Interlocked.Read(
                ref _matchingSubmittedGeneration);
            var revision = Volatile.Read(ref _matchingFrameRevision);
            var minimumRequiredGeneration = Interlocked.Read(
                ref _minimumRequiredGeneration);
            if (generation <= 0 ||
                generation < minimumRequiredGeneration ||
                revision != Volatile.Read(ref _surfaceRevision) ||
                !NativeCompositor.TryGetSharedTextureProducerDiagnostics(
                    out var diagnostics) ||
                diagnostics.LastAcknowledgedGeneration <
                    unchecked((ulong)generation))
            {
                return;
            }

            lock (_sync)
            {
                if (revision != Volatile.Read(ref _surfaceRevision) ||
                    generation != Interlocked.Read(
                        ref _matchingSubmittedGeneration) ||
                    generation < Interlocked.Read(
                        ref _minimumRequiredGeneration) ||
                    Interlocked.CompareExchange(
                        ref _sizedFrameReady,
                        1,
                        0) != 0)
                {
                    CancelSurfaceAcknowledgementPoll();
                    return;
                }
            }

            CancelSurfaceAcknowledgementPoll();
            CancelFirstFrameTimer();
            CancelAcceleratedBootstrapRepaintPump();
            TraceAcceleratedBootstrap(
                "accelerated_surface_frame_acknowledged",
                $"size={SurfaceWidth}x{SurfaceHeight} revision={revision} " +
                $"generation={unchecked((ulong)generation)} " +
                $"acknowledged_generation={diagnostics.LastAcknowledgedGeneration}");
            PublishContentReadyIfEligible();
        }

        private void InvalidateSizedFrameReadiness()
        {
            CancelSurfaceAcknowledgementPoll();
            CancelFirstFrameTimer();
            var wasReady = Interlocked.Exchange(ref _sizedFrameReady, 0) == 1;
            Volatile.Write(ref _contentReady, 0);
            Volatile.Write(ref _matchingFrameRevision, 0);
            Interlocked.Exchange(ref _matchingSubmittedGeneration, 0);
            if (wasReady) PublishPresentationReadiness(false);
        }

        private void StartAcceleratedBootstrapRepaintPump()
        {
            CancelAcceleratedBootstrapRepaintPump();
            Volatile.Write(ref _bootstrapRepaintAttempts, 0);
            Volatile.Write(
                ref _bootstrapRepaintDeadlineTimestamp,
                Stopwatch.GetTimestamp() + Math.Max(
                    1L,
                    Stopwatch.Frequency *
                    FirstAcceleratedFrameTimeoutMilliseconds / 1000L));

            var timer = new Timer(
                _ => PumpAcceleratedBootstrapPaint(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);
            Interlocked.Exchange(ref _bootstrapRepaintTimer, timer)?.Dispose();
            TraceAcceleratedBootstrap(
                "accelerated_bootstrap_repaint_started",
                $"cadence_ms={AcceleratedBootstrapRepaintIntervalMilliseconds} " +
                $"deadline_ms={FirstAcceleratedFrameTimeoutMilliseconds} " +
                $"maximum_attempts={MaximumAcceleratedBootstrapRepaintAttempts}");
            try
            {
                timer.Change(
                    dueTime: 0,
                    period: AcceleratedBootstrapRepaintIntervalMilliseconds);
            }
            catch (ObjectDisposedException)
            {
                // A synchronous first paint can promote the transport between
                // publishing and arming this timer. READY already cancelled it.
            }
        }

        private void PumpAcceleratedBootstrapPaint()
        {
            OffscreenBrowser? browser;
            AcceleratedBootstrapRepaintDecision decision;
            var attempts = Volatile.Read(ref _bootstrapRepaintAttempts);

            lock (_sync)
            {
                browser = _browser;
                if (browser == null)
                {
                    CancelAcceleratedBootstrapRepaintPump();
                    return;
                }

                decision = AcceleratedBootstrapRepaintPolicy.EvaluateSurface(
                    browser.IsAcceleratedBootstrapPending,
                    Volatile.Read(ref _transportReady) == 1,
                    Volatile.Read(ref _sizedFrameReady) == 1,
                    Volatile.Read(ref _disableQueued) == 1 ||
                        Volatile.Read(ref _disposed) != 0 ||
                        Volatile.Read(ref _started) == 0,
                    Stopwatch.GetTimestamp() >= Volatile.Read(
                        ref _bootstrapRepaintDeadlineTimestamp),
                    attempts,
                    MaximumAcceleratedBootstrapRepaintAttempts);

                if (decision != AcceleratedBootstrapRepaintDecision.RequestInvalidate)
                {
                    CancelAcceleratedBootstrapRepaintPump();
                    return;
                }

                attempts = Interlocked.Increment(ref _bootstrapRepaintAttempts);
            }

            var scheduled = browser.IsAcceleratedBootstrapPending
                ? browser.RequestAcceleratedBootstrapPaint()
                : browser.RequestAcceleratedPresentationPaint();
            if (attempts == 1 || attempts % 8 == 0)
            {
                TraceAcceleratedBootstrap(
                    "accelerated_bootstrap_repaint_requested",
                    $"attempt={attempts} cef_ui_scheduled={scheduled} " +
                    $"paint_callbacks={browser.AcceleratedPaintCallbackCount} " +
                    $"probe_rejected={browser.AcceleratedProbeRejectedCount} " +
                    $"probe_deferred={browser.AcceleratedProbeDeferredCount}");
            }
        }

        private void CancelAcceleratedBootstrapRepaintPump()
        {
            var timer = Interlocked.Exchange(ref _bootstrapRepaintTimer, null);
            timer?.Dispose();
        }

        private void OnFirstAcceleratedFrameTimeout()
        {
            CancelAcceleratedBootstrapRepaintPump();
            CancelSurfaceAcknowledgementPoll();
            if (Volatile.Read(ref _sizedFrameReady) == 1) return;

            var browser = _browser;
            var callbacks = browser?.AcceleratedPaintCallbackCount ?? 0;
            var rejected = browser?.AcceleratedProbeRejectedCount ?? 0;
            var deferred = browser?.AcceleratedProbeDeferredCount ?? 0;
            var matchingGeneration = Interlocked.Read(
                ref _matchingSubmittedGeneration);
            var outcome = callbacks == 0
                ? "no-accelerated-paint-callback"
                : matchingGeneration > 0
                    ? "matching-frame-not-acknowledged"
                : rejected > 0
                    ? "bootstrap-probe-rejected"
                    : "paint-callback-without-requested-size-submit";
            TraceAcceleratedBootstrap(
                "accelerated_bootstrap_deadline",
                $"outcome={outcome} attempts={Volatile.Read(ref _bootstrapRepaintAttempts)} " +
                $"paint_callbacks={callbacks} probe_rejected={rejected} " +
                $"probe_deferred={deferred} requested_size={SurfaceWidth}x{SurfaceHeight} " +
                $"matching_generation={unchecked((ulong)Math.Max(0L, matchingGeneration))}");
            QueueExternalGpuDisable(
                "first-accelerated-frame-timeout",
                cancelIfTransportRecovered: true);
        }

        private void TraceAcceleratedBootstrap(string stage, string detail) =>
            StartupTrace.Write(
                _logDirectory,
                "reactorv-runtime.log",
                "directx",
                stage,
                detail);

        private void OnBrowserContentReady()
        {
            Volatile.Write(ref _browserContentReady, 1);
            PublishContentReadyIfEligible();
        }

        private void OnBrowserContentUnavailable()
        {
            CancelSurfaceAcknowledgementPoll();
            CancelAcceleratedBootstrapRepaintPump();
            Volatile.Write(ref _browserContentReady, 0);
            QueueExternalGpuDisable(
                "browser-content-unavailable",
                cancelIfTransportRecovered: false);
        }

        private void OnAcceleratedTransportReady()
        {
            // Transport readiness alone is not proof of a matching, ACKed
            // surface. Keep the bounded pump alive until both are true.
            if (Volatile.Read(ref _sizedFrameReady) == 1)
                CancelAcceleratedBootstrapRepaintPump();
            Volatile.Write(ref _transportReady, 1);
            var browser = _browser;
            TraceAcceleratedBootstrap(
                "accelerated_bootstrap_ready",
                $"attempts={Volatile.Read(ref _bootstrapRepaintAttempts)} " +
                $"paint_callbacks={browser?.AcceleratedPaintCallbackCount ?? 0} " +
                $"probe_rejected={browser?.AcceleratedProbeRejectedCount ?? 0} " +
                $"probe_deferred={browser?.AcceleratedProbeDeferredCount ?? 0}");
            PublishContentReadyIfEligible();
        }

        private void OnAcceleratedFrameSubmitted(
            int width,
            int height,
            ulong generation)
        {
            var submittedGeneration = NormalizeGeneration(generation);
            ObserveLatestSubmittedGeneration(submittedGeneration);

            if (width != SurfaceWidth || height != SurfaceHeight)
            {
                TraceAcceleratedBootstrap(
                    "accelerated_surface_frame_ignored",
                    $"submitted_size={width}x{height} " +
                    $"requested_size={SurfaceWidth}x{SurfaceHeight} " +
                    $"generation={generation}");
                return;
            }

            int revision;
            long minimumRequiredGeneration;
            var staleGeneration = false;
            lock (_sync)
            {
                if (width != SurfaceWidth || height != SurfaceHeight)
                    return;
                revision = Volatile.Read(ref _surfaceRevision);
                minimumRequiredGeneration = Interlocked.Read(
                    ref _minimumRequiredGeneration);
                if (submittedGeneration < minimumRequiredGeneration)
                {
                    staleGeneration = true;
                }
                else if (Volatile.Read(ref _matchingFrameRevision) == revision &&
                    Interlocked.Read(ref _matchingSubmittedGeneration) > 0)
                {
                    return;
                }
                else
                {
                    Volatile.Write(ref _matchingFrameRevision, revision);
                    Interlocked.Exchange(
                        ref _matchingSubmittedGeneration,
                        submittedGeneration);
                }
            }
            if (staleGeneration)
            {
                TraceAcceleratedBootstrap(
                    "accelerated_surface_frame_ignored",
                    $"reason=stale-generation size={width}x{height} " +
                    $"revision={revision} generation={generation} " +
                    $"minimum_generation={minimumRequiredGeneration}");
                return;
            }
            TraceAcceleratedBootstrap(
                "accelerated_surface_frame_submitted",
                $"size={width}x{height} revision={revision} " +
                $"generation={generation}");
            StartSurfaceAcknowledgementPoll();
        }

        private long CaptureGenerationHighWatermark(OffscreenBrowser browser)
        {
            var highWatermark = Math.Max(
                Math.Max(
                    browser.AcceleratedPaintCallbackCount,
                    Interlocked.Read(ref _latestSubmittedGeneration)),
                Interlocked.Read(ref _matchingSubmittedGeneration));

            if (!NativeCompositor.TryGetSharedTextureProducerDiagnostics(
                    out var diagnostics))
            {
                return highWatermark;
            }

            highWatermark = Math.Max(
                highWatermark,
                NormalizeGeneration(diagnostics.LastAttemptedGeneration));
            highWatermark = Math.Max(
                highWatermark,
                NormalizeGeneration(diagnostics.LastSubmittedGeneration));
            return Math.Max(
                highWatermark,
                NormalizeGeneration(diagnostics.LastAcknowledgedGeneration));
        }

        private void ObserveLatestSubmittedGeneration(long generation)
        {
            while (true)
            {
                var observed = Interlocked.Read(
                    ref _latestSubmittedGeneration);
                if (generation <= observed) return;
                if (Interlocked.CompareExchange(
                        ref _latestSubmittedGeneration,
                        generation,
                        observed) == observed)
                {
                    return;
                }
            }
        }

        private static long NormalizeGeneration(ulong generation) =>
            generation > long.MaxValue
                ? long.MaxValue
                : unchecked((long)generation);

        private static long NextGeneration(long generation) =>
            generation >= long.MaxValue ? long.MaxValue : generation + 1;

        private void OnAcceleratedTransportUnavailable()
        {
            CancelFirstFrameTimer();
            CancelSurfaceAcknowledgementPoll();
            CancelAcceleratedBootstrapRepaintPump();
            // This callback is a terminal demotion from an established READY
            // transport, not a bootstrap timeout. Clear readiness before the
            // queued worker can observe it, and never let stale READY state
            // cancel teardown of the dead GPU shadow.
            Volatile.Write(ref _transportReady, 0);
            if (Interlocked.Exchange(ref _sizedFrameReady, 0) == 1)
                PublishPresentationReadiness(false);
            Volatile.Write(ref _contentReady, 0);
            QueueExternalGpuDisable(
                "accelerated-transport-unavailable",
                cancelIfTransportRecovered: false);
        }

        private void QueueExternalGpuDisable(
            string reason,
            bool cancelIfTransportRecovered)
        {
            if (Interlocked.CompareExchange(ref _disableQueued, 1, 0) != 0)
                return;

            // Never tear down CefSharp from OnAcceleratedPaint. Both a failed
            // probe and the timer only enqueue work; shutdown occurs on an
            // independent CLR worker after the callback has returned. There is
            // deliberately no process-local CPU browser here: its native
            // mailbox would belong to the Preloader process, not GTA.
            ThreadPool.QueueUserWorkItem(_ => DisableExternalGpuPath(
                reason,
                cancelIfTransportRecovered));
        }

        private void DisableExternalGpuPath(
            string reason,
            bool cancelIfTransportRecovered)
        {
            lock (_sync)
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    Volatile.Read(ref _started) == 0)
                {
                    return;
                }
                if (cancelIfTransportRecovered &&
                    Volatile.Read(ref _sizedFrameReady) == 1)
                {
                    Volatile.Write(ref _disableQueued, 0);
                    return;
                }

                StopBrowser();
                StopProducer();
                Volatile.Write(ref _browserContentReady, 0);
                Volatile.Write(ref _transportReady, 0);
                Volatile.Write(ref _sizedFrameReady, 0);
                Volatile.Write(ref _contentReady, 0);
                Volatile.Write(ref _started, 0);
            }
            PublishContentUnavailable();
            NotifyStartupFailed(new InvalidOperationException(
                $"External GPU browser path disabled: {reason}. " +
                "The authoritative WebView2 host remains active."));
        }

        private void PublishContentReadyIfEligible()
        {
            if (Volatile.Read(ref _browserContentReady) != 1 ||
                Volatile.Read(ref _transportReady) != 1 ||
                Volatile.Read(ref _sizedFrameReady) != 1 ||
                Interlocked.Exchange(ref _contentReady, 1) == 1)
            {
                return;
            }


            // Announce the exact-size commit boundary before enabling native
            // output. The Preloader can retire WebView2 for the same
            // presentation ID, then apply its deferred visibility request.
            PublishPresentationReadiness(true);
            if (_producerStarted &&
                !NativeCompositor.SetSharedTextureProducerVisible(_desiredVisible))
            {
                Volatile.Write(ref _contentReady, 0);
                PublishPresentationReadiness(false);
                QueueExternalGpuDisable(
                    "presentation-ready-visibility-unavailable",
                    cancelIfTransportRecovered: false);
                return;
            }

            try
            {
                ContentReady?.Invoke();
            }
            catch
            {
                // Host observers cannot unwind through a CefSharp callback.
            }
        }

        private void PublishContentUnavailable()
        {
            if (Interlocked.Exchange(ref _unavailablePublished, 1) == 1) return;
            try
            {
                ContentUnavailable?.Invoke();
            }
            catch
            {
                // Host observers cannot destabilize producer state changes.
            }
        }

        private void PublishPresentationReadiness(bool ready)
        {
            var handlers = PresentationReadinessChanged;
            if (handlers == null) return;
            foreach (Action<bool, int, int> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(ready, SurfaceWidth, SurfaceHeight);
                }
                catch
                {
                    // Readiness observers cannot destabilize CEF or teardown.
                }
            }
        }

        private void NotifyStartupFailed(Exception error)
        {
            try
            {
                StartupFailed?.Invoke(error);
            }
            catch
            {
                // Consumer diagnostics must not destabilize producer teardown.
            }
        }

        private static bool SupportsSurfaceDimensions(int width, int height) =>
            width > 0 && height > 0 &&
            (uint)width <= SharedGpuFrameProtocol.MaximumDimension &&
            (uint)height <= SharedGpuFrameProtocol.MaximumDimension &&
            (ulong)width * (ulong)height * 4ul <=
                SharedGpuFrameProtocol.MaximumBytes;

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ExternalGpuBrowserSession));
        }
    }
}
