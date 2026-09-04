using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using RageWebUI.Runtime;
using RageWebUI.Windowing;
using ReactorV.BootstrapHost;
using ReactorV.BootstrapInput;
using ReactorV.ExternalGpu;
using ReactorV.WebView2Host;

namespace ReactorV.Preloader
{
    internal static class PreloaderSelfTestNames
    {
        private const string StopEventPrefix =
            @"Local\ReactorV.Preloader.SelfTestStop.";

        public static string StopEvent(int preloaderProcessId)
        {
            if (preloaderProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(preloaderProcessId));
            return StopEventPrefix + preloaderProcessId;
        }
    }

    internal static class Program
    {
        private const string MutexPrefix = @"Local\ReactorV.Preloader.Singleton.";

        [STAThread]
        private static int Main(string[] args)
        {
            // This child mode must remain ahead of settings, normal argument
            // parsing, and the singleton. The renderer uses it as a disposable
            // out-of-process isolation boundary for composited-desktop proof.
            if (DesktopPresentationProbeChild.TryRun(args, out var probeExitCode))
                return probeExitCode;

            var executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var localDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ReactorV");
            var settings = PreloaderSettings.Load(Path.Combine(
                executableDirectory,
                "ReactorV.Preloader.json"));

            if (!PreloaderOptions.TryParse(args, settings, executableDirectory, out var options, out var error))
            {
                Trace(localDataDirectory, "arguments_rejected", error);
                return 2;
            }
            var traceDirectory = options.LogDirectory;

            bool createdNew;
            using (var singleton = new Mutex(
                true,
                MutexPrefix + options.InstanceId,
                out createdNew))
            {
                if (!createdNew)
                {
                    Trace(
                        traceDirectory,
                        "duplicate_instance_skipped",
                        $"instance_id={options.InstanceId}");
                    return 0;
                }

                Trace(
                    traceDirectory,
                    "preloader_start",
                    $"ui={options.UiDirectory} udf={options.UserDataDirectory} " +
                    $"process={options.WaitForProcess ?? "none"} parent_pid={options.ParentProcessId?.ToString() ?? "none"} " +
                    $"self_test={options.SelfTest} cache_only={options.CacheOnly} " +
                    $"persistent_host={options.PersistentHost} " +
                    $"external_gpu_browser_shadow={options.ExternalGpuBrowserShadow} " +
                    $"external_gpu_frame_rate={options.ExternalGpuFrameRate} " +
                    $"bootstrap_harness_webview_presenter=" +
                    $"{options.BootstrapHarnessWebViewPresenter} " +
                    $"timeout_seconds={options.MaximumLifetime.TotalSeconds:F0} " +
                    $"instance_id={options.InstanceId}");

                if (options.CacheOnly)
                {
                    return BuildCacheOnly(options, traceDirectory);
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var context = new PreloaderApplicationContext(options, traceDirectory))
                {
                    Application.Run(context);
                    Trace(traceDirectory, "preloader_stop", $"exit_code={context.ExitCode}");
                    return context.ExitCode;
                }
            }
        }

        internal static void Trace(string directory, string stage, string? detail = null) =>
            StartupTrace.Write(directory, "reactorv-preloader.log", "preloader", stage, detail);

        private static int BuildCacheOnly(PreloaderOptions options, string traceDirectory)
        {
            var processId = options.ParentProcessId!.Value;
            using (var ready = PreloadHandoff.CreatePreloadDataReadyWaitHandle(processId))
            {
                try
                {
                    var result = PreloadDataCache.Build(
                        options.GtaRoot!,
                        processId,
                        options.CacheRootOverride,
                        (stage, detail) => Trace(traceDirectory, stage, detail));
                    if (PreloadDataCache.IsReadyForHandoff(processId, result))
                    {
                        var signaled = PreloadHandoff.TrySignalPreloadDataReady(
                            processId,
                            result);
                        Trace(
                            traceDirectory,
                            signaled
                                ? "preload_data_ready_signaled"
                                : "preload_data_ready_signal_failed",
                            $"pid={processId} manifests={result.SnapshotPaths.Count} " +
                            $"entries={result.EntryCount} complete=True");
                    }
                    else
                    {
                        Trace(
                            traceDirectory,
                            "preload_data_not_ready",
                            $"pid={processId} manifests={result.SnapshotPaths.Count} " +
                            $"complete={result.Complete}");
                    }
                    return result.Complete ? 0 : 1;
                }
                catch (Exception error)
                {
                    Trace(
                        traceDirectory,
                        "preload_data_failed",
                        $"pid={processId} type={error.GetType().Name} message={error.Message}");
                    return 1;
                }
            }
        }
    }

    internal sealed class PreloaderApplicationContext : ApplicationContext
    {
        private static readonly TimeSpan DefaultMenuIntentLifetime =
            TimeSpan.FromMinutes(2);
        private static readonly TimeSpan HostSurfaceReadyDeadline =
            TimeSpan.FromSeconds(5);
        private const int HostSignalPollMilliseconds = 8;

        private readonly PreloaderOptions _options;
        private readonly string _logDirectory;
        private readonly Stopwatch _lifetime = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer _lifecycleTimer;
        private readonly System.Windows.Forms.Timer? _aboutInputTimer;
        private readonly System.Windows.Forms.Timer? _pointerInputTimer;
        private readonly System.Threading.Timer? _hostSignalTimer;
        private readonly FileSystemWatcher? _liveAcceptanceCaptureWatcher;
        private readonly LiveAcceptanceCaptureWakeReceiver?
            _liveAcceptanceCaptureWakeReceiver;
        private readonly Form _window;
        private readonly PreloadWindow? _preloadWindow;
        private readonly OverlayWindow? _hostWindow;
        private readonly BootstrapOverlayServer? _hostServer;
        private readonly ExternalGpuBrowserSession? _externalGpuBrowserSession;
        private readonly bool _requireNativePresenter;
        private readonly EventWaitHandle? _hostToggle;
        private readonly EventWaitHandle? _hostAboutToggle;
        private readonly EventWaitHandle? _hostVerifyToggle;
        private readonly EventWaitHandle? _hostVerifyActive;
        private readonly EventWaitHandle? _hostAboutActive;
        private readonly EventWaitHandle? _hostInitializerPromotion;
        private readonly EventWaitHandle? _hostClose;
        private readonly EventWaitHandle? _defaultMenuIntent;
        private readonly EventWaitHandle? _defaultMenuIntentClaimed;
        private readonly EventWaitHandle? _defaultMenuIntentActive;
        private readonly EventWaitHandle? _defaultMenuIntentCancelled;
        private readonly EventWaitHandle? _selfTestStop;
        private readonly PreProviderAboutPointerSampler? _aboutInputSampler;
        private readonly object _preloadDataSync = new object();
        private readonly HostPointerIngressBuffer _hostPointerIngress =
            new HostPointerIngressBuffer();
        private readonly HostPointerCoalescingTraceGate _hostPointerTraceGate =
            new HostPointerCoalescingTraceGate(TimeSpan.FromSeconds(5));
        private Process? _targetProcess;
        private TargetProcessExitCodeReader? _targetExitCodeReader;
        private TargetWindowLifecycleProbe? _targetWindowLifecycleProbe;
        private TargetWindowLifecycleJournal? _targetWindowLifecycleJournal;
        private EventWaitHandle? _handoff;
        private EventWaitHandle? _preloadDataReady;
        private CancellationTokenSource? _preloadDataCancellation;
        private Task? _preloadDataTask;
        private int _attachedProcessId;
        private int _targetExitObserverProcessId;
        private int _targetExitSignalObserved;
        private int _targetExitHandlingStarted;
        private int _targetWindowObserverFailed;
        private int _disposeStarted;
        private bool _disposed;
        private bool _contentReady;
        private bool _profileReleaseStarted;
        private bool _profileReleased;
        private bool _stopping;
        private string _hostSurfaceMode = "none";
        private int _hostSurfaceGeneration;
        private int _pendingHostSurfaceGeneration;
        private int _hostSurfacePixelVerificationGeneration;
        private int _webViewInitializerReadyGeneration;
        private int _externalInitializerAckGeneration;
        private int _externalInitializerRefreshGeneration;
        private int _externalInitializerFreshGeneration;
        private string _pendingHostSurfaceMode = "none";
        private TimeSpan _pendingHostSurfaceExpiresAt = TimeSpan.Zero;
        private string _hostSurfaceReadyRecoveryMode = "none";
        private int _hostSurfaceReadyRecoveryAttempts;
        private TimeSpan _nextStartupStatusPublish = TimeSpan.Zero;
        private TimeSpan _defaultMenuIntentExpiresAt = TimeSpan.Zero;
        private bool _initializerOpeningEdgePending;
        private bool _deferredInitializerPromotion;
        private int _providerSessionGeneration;
        private int _hostSignalPollActive;
        private string? _dualBrowserReadyPresentationId;
        private int _dualBrowserReadyProviderSessionGeneration;
        private string? _awaitingExternalPostAcceptPaintPresentationId;
        private int _awaitingExternalPostAcceptPaintProviderSessionGeneration;
        private string? _externalFreshPresentationId;
        private string? _externalCommittedPresentationId;
        private string? _externalReplacementPresentationId;
        private string? _hiddenExternalPreparationPresentationId;
        private string? _queuedExternalReplacementPresentationId;
        private int _queuedExternalReplacementProviderSessionGeneration;
        private bool _externalPresentationFallbackToWebView;
        private bool _browserPresentationRequestedVisible;
        private BrowserPresentationDecision _browserPresentation =
            ExclusiveBrowserPresentationPolicy.Resolve(
                requestedVisible: false,
                providerConnected: false,
                hostSurfaceMode: HostSurfaceMode.None,
                externalGpuActive: false,
                externalGpuPresentationReady: false);
        private readonly LiveAcceptanceCaptureGate _liveAcceptanceCaptureGate =
            new LiveAcceptanceCaptureGate();
        private readonly LiveAcceptanceCaptureDispatchGate
            _liveAcceptanceCaptureDispatchGate =
                new LiveAcceptanceCaptureDispatchGate();

        public PreloaderApplicationContext(PreloaderOptions options, string logDirectory)
        {
            _options = options;
            _logDirectory = logDirectory;
            _requireNativePresenter = options.ExternalGpuBrowserShadow && !options.BootstrapHarnessWebViewPresenter &&
                File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReactorV.LegacyCpuFrames.enabled")) &&
                File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReactorV.LegacyLiveTest.json"));
            _selfTestStop = options.SelfTest && options.PersistentHost
                ? new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    PreloaderSelfTestNames.StopEvent(
                        Process.GetCurrentProcess().Id))
                : null;
            if (options.PersistentHost)
            {
                var processId = options.ParentProcessId!.Value;
                _hostServer = new BootstrapOverlayServer(
                    processId,
                    (stage, detail) => Program.Trace(_logDirectory, stage, detail),
                    externalGpuBrowserShadowRequired:
                        options.ExternalGpuBrowserShadow);
                _hostWindow = new OverlayWindow(
                    IntPtr.Zero,
                    (uint)processId,
                    options.UiDirectory,
                    options.UserDataDirectory,
                    _hostServer,
                    enableDevTools: false,
                    startVisible: false,
                    (stage, detail) => Program.Trace(_logDirectory, stage, detail),
                    OnWebViewVisibilityApplied,
                    OnContentReady,
                    _hostServer.MarkContentUnavailable,
                    error => Stop(
                        1,
                        "browser_failed",
                        $"type={error.GetType().FullName} message={error.Message}"),
                    finalRevealIngressBoundary:
                        TryAnnounceHostSignalsAtRevealBoundary,
                    presentationUnavailable:
                        _hostServer.MarkPresentationUnavailable);
                _hostWindow.ProviderPresentationCommitted += presentationId =>
                {
                    // A parked/hidden WebView is not proof of fullscreen pixels.
                    // Required-native sessions commit only through TryCommitExternalProviderPresentation.
                    if (!_requireNativePresenter)
                        _hostServer.PublishProviderPresentationCommitted(presentationId,
                            _hostWindow.IsProviderPresentationAuthorizedByUserIntent(presentationId));
                };
                _hostServer.DualBrowserPresentationReady +=
                    (presentationId, providerSessionGeneration) => InvokeHost(
                        window =>
                        {
                            if (!TryRecordDualBrowserPresentationReady(
                                    presentationId,
                                    providerSessionGeneration))
                                return;

                            // A disabled or unavailable accelerated shadow
                            // retains the established single-WebView path.
                            if (_hostServer?.
                                    IsExternalGpuBrowserShadowRequired != true)
                            {
                                _awaitingExternalPostAcceptPaintPresentationId = null;
                                _awaitingExternalPostAcceptPaintProviderSessionGeneration = 0;
                                ContinueExternalProviderPresentationAfterPaint(
                                    window,
                                    presentationId,
                                    providerSessionGeneration,
                                    "single-browser-ready");
                            }
                        });
                _hostServer.ExternalGpuPostAcceptPaintReady +=
                    (presentationId, providerSessionGeneration) => InvokeHost(
                        window =>
                        {
                            if (!string.Equals(
                                    _awaitingExternalPostAcceptPaintPresentationId,
                                    presentationId,
                                    StringComparison.Ordinal) ||
                                _awaitingExternalPostAcceptPaintProviderSessionGeneration !=
                                    providerSessionGeneration)
                            {
                                Program.Trace(
                                    _logDirectory,
                                    "external_gpu_post_accept_paint_stale",
                                    $"presentation={presentationId} " +
                                    $"provider_session_generation=" +
                                    $"{providerSessionGeneration} " +
                                    $"expected_presentation=" +
                                    $"{_awaitingExternalPostAcceptPaintPresentationId ?? "none"} " +
                                    $"expected_provider_session_generation=" +
                                    $"{_awaitingExternalPostAcceptPaintProviderSessionGeneration}");
                                return;
                            }

                            _awaitingExternalPostAcceptPaintPresentationId = null;
                            _awaitingExternalPostAcceptPaintProviderSessionGeneration = 0;
                            ContinueExternalProviderPresentationAfterPaint(
                                window,
                                presentationId,
                                providerSessionGeneration,
                                "post-accept-browser-paint");
                        });
                _hostServer.ProviderInputIntentArmRequested += token =>
                    InvokeHost(window => window.ArmProviderInputIntent(token));
                _hostServer.ProviderInputIntentBindRequested +=
                    (intentProcessId, epoch, presentationId) => InvokeHost(
                        window => window.BindProviderInputIntent(
                            intentProcessId,
                            epoch,
                            presentationId));
                _hostServer.ProviderInputIntentCancelRequested +=
                    (intentProcessId, epoch) => InvokeHost(
                        window => window.CancelProviderInputIntent(
                            intentProcessId,
                            epoch));
                _hostServer.VisibilityRequested += (visible, reason) => InvokeHost(window =>
                {
                    if (!visible)
                    {
                        RetireExternalProviderProof(
                            "host-" + reason.ToString().ToLowerInvariant());
                        CancelPendingHostSurfaceReveal(
                            reason == HostVisibilityReason.PresentationPreparation
                                ? "presentation-preparation"
                                : "host-close");
                        _aboutInputSampler?.ResetBoundary("visibility-hidden");
                        if (HostSurfaceIntentPolicy.ShouldCancelDefaultMenuIntent(
                                visible,
                                reason))
                        {
                            CancelDefaultMenuIntent("host-close");
                        }
                        PostHostSurface(window, "none");
                    }
                    else
                    {
                        // A typed provider presentation supersedes a pending
                        // bootstrap surface and retains its existing menu paint
                        // acknowledgement contract.
                        CancelPendingHostSurfaceReveal("provider-visibility");
                    }
                    window.SetBootstrapPointerCapture(
                        PreProviderAboutInputPolicy.ShouldCaptureWindowHitTests(
                            _contentReady,
                            visible,
                            _hostSurfaceMode,
                            _hostServer.IsConnected));
                    SetBrowserVisible(
                        window,
                        visible,
                        "host-" + reason.ToString().ToLowerInvariant());
                }, signalRevealIngress: !visible);
                _hostServer.BootstrapSurfaceRetirementRequested += hide =>
                    InvokeHost(
                        window => RetireBootstrapSurface(window, hide),
                        signalRevealIngress: true);
                _hostServer.RuntimeReadyLeaseSignaledChanged += () =>
                    InvokeHost(window =>
                    {
                        var preserveInitializer =
                            PreloadHandoff.IsDefaultMenuIntentActive(processId) &&
                            IsHostSurfaceLogicallyOpen("initializing");
                        if (!preserveInitializer)
                            RetireBootstrapSurface(window, hide: true);
                        Program.Trace(
                            _logDirectory,
                            "bootstrap_runtime_ready_surface_gate_armed",
                            $"pid={processId} " +
                            $"initializer_preserved={preserveInitializer} " +
                            "handoff=matching-presentation-paint");
                    }, signalRevealIngress: true);
                _hostServer.SurfaceReady += (mode, generation) => InvokeHost(window =>
                    CompleteHostSurfaceReveal(window, mode, generation));
                _hostServer.ExternalSurfaceReady += (mode, generation) =>
                    InvokeHost(window => BeginExternalInitializerRefresh(
                        window,
                        mode,
                        generation,
                        "external-surface-ready"));
                _hostServer.JsonRequested += json => InvokeHost(window =>
                {
                    PostBrowserJson(window, json);
                }, hostMessageIngress: json);
                _hostServer.PointerRequested += (x, y, pressed, released, wheel) =>
                    QueueHostPointerInput(x, y, pressed, released, wheel);
                _hostServer.ProviderConnected += () => InvokeHost(window =>
                {
                    // Keep an already visible transition surface in place
                    // until the provider presents its real menu. Hiding here
                    // creates a blank/flickering handoff during slow startup.
                    _aboutInputSampler?.ResetBoundary("provider-connected");
                    window.SetBootstrapPointerCapture(false);
                    var sessionGeneration =
                        Interlocked.Increment(ref _providerSessionGeneration);
                    _dualBrowserReadyPresentationId = null;
                    _dualBrowserReadyProviderSessionGeneration = 0;
                    _awaitingExternalPostAcceptPaintPresentationId = null;
                    _awaitingExternalPostAcceptPaintProviderSessionGeneration = 0;
                    _externalFreshPresentationId = null;
                    _externalCommittedPresentationId = null;
                    _externalReplacementPresentationId = null;
                    _hiddenExternalPreparationPresentationId = null;
                    _queuedExternalReplacementPresentationId = null;
                    _queuedExternalReplacementProviderSessionGeneration = 0;
                    _externalPresentationFallbackToWebView = false;
                    PostHostProvider(window, true, sessionGeneration);
                }, signalRevealIngress: true);
                _hostServer.ProviderDisconnected += () => InvokeHost(window =>
                {
                    var disconnectedOwner = _browserPresentation.Owner;
                    var externalSession = _externalGpuBrowserSession;
                    var retainedProviderPromotionInFlight =
                        ProviderPresentationCommitContract.IsValidPresentationId(
                            _externalReplacementPresentationId) ||
                        (_browserPresentation.ExternalGpuVisible &&
                         externalSession?.IsActive == true &&
                         externalSession.IsPresentationReady != true);
                    var hasPendingDefaultMenuIntent =
                        _defaultMenuIntentExpiresAt != TimeSpan.Zero &&
                        PreloadHandoff.IsDefaultMenuIntentActive(processId);
                    var preserveVisibleInitializer =
                        hasPendingDefaultMenuIntent &&
                        IsHostSurfaceLogicallyOpen("initializing");

                    _dualBrowserReadyPresentationId = null;
                    _dualBrowserReadyProviderSessionGeneration = 0;
                    _awaitingExternalPostAcceptPaintPresentationId = null;
                    _awaitingExternalPostAcceptPaintProviderSessionGeneration = 0;
                    _externalFreshPresentationId = null;
                    _externalCommittedPresentationId = null;
                    _externalReplacementPresentationId = null;
                    _hiddenExternalPreparationPresentationId = null;
                    _queuedExternalReplacementPresentationId = null;
                    _queuedExternalReplacementProviderSessionGeneration = 0;
                    _externalPresentationFallbackToWebView = false;
                    var preserveBootstrapPresenter =
                        (disconnectedOwner ==
                             BrowserPresentationOwner.WebViewBootstrap ||
                         (disconnectedOwner ==
                              BrowserPresentationOwner.ExternalGpuBootstrap &&
                          externalSession?.IsPresentationReady == true &&
                          !retainedProviderPromotionInFlight)) &&
                        !string.Equals(
                            HostSurfaceMode.Normalize(_hostSurfaceMode),
                            HostSurfaceMode.None,
                            StringComparison.Ordinal);
                    if (!preserveBootstrapPresenter)
                    {
                        SetBrowserVisible(
                            window,
                            visible: false,
                            trigger: "provider-disconnected");
                    }
                    var preserveForwardedHiddenIntent =
                        hasPendingDefaultMenuIntent &&
                        _hostServer.RuntimeReadyLeaseSignaled &&
                        !_hostServer.IsVisible &&
                        string.Equals(
                            _hostSurfaceMode,
                            "none",
                            StringComparison.Ordinal);
                    if (preserveVisibleInitializer || preserveForwardedHiddenIntent)
                        RefreshDefaultMenuIntent();
                    else
                        CancelDefaultMenuIntent("provider-disconnected");
                    PostHostProvider(
                        window,
                        false,
                        Volatile.Read(ref _providerSessionGeneration));
                    window.SetBootstrapPointerCapture(
                        PreProviderAboutInputPolicy.ShouldCaptureWindowHitTests(
                            _contentReady,
                            _hostServer.IsVisible,
                            _hostSurfaceMode,
                            providerConnected: false));
                    // A provider loss may occur while Reactor About is open at
                    // the frontend. Preserve that explicitly selected surface.
                    // Before RuntimeReady an already-visible initializer stays
                    // visible; after RuntimeReady the durable default-menu
                    // request remains hidden until its replacement provider
                    // supplies a typed menu.
                    if (preserveVisibleInitializer &&
                        string.Equals(
                            _hostSurfaceMode,
                            "initializing",
                            StringComparison.Ordinal))
                    {
                        if (preserveBootstrapPresenter)
                        {
                            // No provider refresh can still replace this
                            // verified bootstrap frame, so its generation may
                            // remain the continuity owner.
                            Program.Trace(
                                _logDirectory,
                                "bootstrap_host_initializer_preserved_on_provider_disconnect",
                                $"pid={processId} generation={_hostSurfaceGeneration} " +
                                $"owner={_browserPresentation.OwnerTraceValue} " +
                                "retained_provider_promotion=false");
                        }
                        else
                        {
                            // A retained provider refresh may still complete
                            // after disconnect, or provider pixels may already
                            // own the native plane while the logical
                            // initializer has not retired. Keep desired native
                            // visibility false and issue a new generation only
                            // after host.provider=false reached both browsers.
                            // The normal exact-generation WebView/external
                            // gates decide which verified initializer can
                            // become visible again.
                            externalSession?.SetVisible(false);
                            ResetExternalInitializerReadiness();
                            var previousGeneration = _hostSurfaceGeneration;
                            RequestHostSurface(
                                window,
                                "initializing",
                                visible: true);
                            Program.Trace(
                                _logDirectory,
                                "bootstrap_host_initializer_reproof_requested_on_provider_disconnect",
                                $"pid={processId} previous_generation=" +
                                $"{previousGeneration} " +
                                $"generation={_hostSurfaceGeneration} " +
                                $"previous_owner={disconnectedOwner} " +
                                $"retained_provider_promotion=" +
                                $"{retainedProviderPromotionInFlight}");
                        }
                    }
                    else if (_hostServer.IsVisible &&
                        string.Equals(
                            _hostSurfaceMode,
                            "initializing",
                            StringComparison.Ordinal))
                    {
                        // Never leave initializer pixels visible without the
                        // process-scoped request that can eventually replace
                        // them with a typed menu presentation.
                        RequestHostSurface(window, "none", false);
                        Program.Trace(
                            _logDirectory,
                            "bootstrap_host_orphan_initializer_hidden",
                            $"pid={processId} reason=provider-disconnected");
                    }
                }, signalRevealIngress: true);
                _hostToggle = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    BootstrapHostNames.ToggleEvent(processId));
                _hostAboutToggle = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    BootstrapHostNames.AboutToggleEvent(processId));
                _hostVerifyToggle = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    BootstrapHostNames.VerifyToggleEvent(processId));
                _hostVerifyActive = new EventWaitHandle(
                    false,
                    EventResetMode.ManualReset,
                    BootstrapHostNames.VerifyActiveEvent(processId));
                _hostAboutActive = new EventWaitHandle(
                    false,
                    EventResetMode.ManualReset,
                    BootstrapHostNames.AboutActiveEvent(processId));
                _hostInitializerPromotion = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    BootstrapHostNames.InitializerPromotionEvent(processId));
                _hostClose = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    BootstrapHostNames.CloseEvent(processId));
                _defaultMenuIntent =
                    PreloadHandoff.CreateDefaultMenuIntentWaitHandle(processId);
                _defaultMenuIntentClaimed =
                    PreloadHandoff.CreateDefaultMenuIntentClaimedWaitHandle(processId);
                _defaultMenuIntentActive =
                    PreloadHandoff.CreateDefaultMenuIntentActiveWaitHandle(processId);
                _defaultMenuIntentCancelled =
                    PreloadHandoff.CreateDefaultMenuIntentCancelledWaitHandle(processId);
                _liveAcceptanceCaptureWakeReceiver =
                    new LiveAcceptanceCaptureWakeReceiver(
                        processId,
                        () => QueueLiveAcceptanceCapturePoll("named-event"));
                _window = _hostWindow;
                _hostServer.Start();
                if (options.ExternalGpuBrowserShadow)
                {
                    var externalSurfaceWidth = _hostWindow.ClientSize.Width;
                    var externalSurfaceHeight = _hostWindow.ClientSize.Height;
                    if (TryResolveExternalGpuSurfaceSize(
                            _hostWindow,
                            out var resolvedSurfaceWidth,
                            out var resolvedSurfaceHeight))
                    {
                        externalSurfaceWidth = resolvedSurfaceWidth;
                        externalSurfaceHeight = resolvedSurfaceHeight;
                    }
                    Program.Trace(
                        _logDirectory,
                        "external_gpu_initial_surface_resolved",
                        $"surface={externalSurfaceWidth}x{externalSurfaceHeight} " +
                        $"source={(resolvedSurfaceWidth > 0 ? "gta-client" : "host-client-fallback")}");
                    _externalGpuBrowserSession = ExternalGpuBrowserSession.TryStart(
                        enabled: true,
                        new ExternalGpuBrowserProducerContext(
                            processId,
                            options.UiDirectory,
                            AppDomain.CurrentDomain.BaseDirectory,
                             Path.Combine(
                                 options.UserDataDirectory,
                                 "ExternalGpuBrowserShadow"),
                             _hostServer.CreateBrowserSink(
                                 PresentationReadyBrowserRole.ExternalGpuShadow),
                            externalSurfaceWidth,
                            externalSurfaceHeight,
                            frameRate: options.ExternalGpuFrameRate,
                            enableDevTools: false,
                            parentWindow: _hostWindow.Handle),
                        new ExternalGpuBrowserProducerAssemblyFactory(
                            AppDomain.CurrentDomain.BaseDirectory),
                         (stage, detail) =>
                             Program.Trace(_logDirectory, stage, detail));
                    if (_externalGpuBrowserSession != null)
                    {
                        _externalGpuBrowserSession.Unavailable += () =>
                            InvokeHost(window =>
                            {
                                ResetExternalInitializerReadiness();
                                _hostServer.DisableExternalGpuBrowserShadow(
                                    "external-session-fault");
                                PostBrowserRoles(window, externalGpuActive: false);
                                TryCompleteHostSurfaceReveal(
                                    window,
                                    _pendingHostSurfaceMode,
                                    _pendingHostSurfaceGeneration,
                                    "external-gpu-unavailable");
                                SetBrowserVisible(
                                    window,
                                    _browserPresentationRequestedVisible,
                                    "external-gpu-unavailable");
                            });
                        _externalGpuBrowserSession.PresentationReadinessChanged +=
                            (ready, width, height) => InvokeHost(window =>
                            {
                                Program.Trace(
                                    _logDirectory,
                                    "external_gpu_presenter_readiness_ingress",
                                    $"ready={ready} surface={width}x{height} " +
                                    $"requested_visible={_browserPresentationRequestedVisible}");
                                ObserveExternalInitializerReadiness(
                                    window,
                                    ready,
                                    width,
                                    height);
                                if (ready &&
                                    TryStartQueuedExternalProviderReplacement(
                                        window))
                                {
                                    return;
                                }
                                SetBrowserVisible(
                                    window,
                                    _browserPresentationRequestedVisible,
                                    "external-gpu-readiness");
                                TryCommitExternalProviderPresentation(
                                    window,
                                    "external-gpu-readiness");
                            });
                        _hostWindow.ClientSizeChanged += (_, __) =>
                            InvokeHost(window =>
                            {
                                if (_externalInitializerAckGeneration > 0 &&
                                    _externalInitializerAckGeneration ==
                                        _pendingHostSurfaceGeneration &&
                                    IsNativeBootstrapSurface(
                                        _pendingHostSurfaceMode))
                                {
                                    BeginExternalInitializerRefresh(
                                        window,
                                        _pendingHostSurfaceMode,
                                        _pendingHostSurfaceGeneration,
                                        "host-client-size-changed");
                                }
                                // Arbitration owns synchronization and the
                                // fail-closed waiting state. Always run it on a
                                // client-size edge—even a rejected/deferred
                                // resize must immediately retire old pixels.
                                SetBrowserVisible(
                                    window,
                                    _browserPresentationRequestedVisible,
                                    "host-client-size-changed");
                            });
                    }
                    else
                    {
                        _hostServer.DisableExternalGpuBrowserShadow(
                            "external-session-start-unavailable");
                    }
                    _externalGpuBrowserSession?.SetVisible(false);
                }
                else
                {
                    Program.Trace(
                        _logDirectory,
                        "external_gpu_browser_shadow_disabled",
                        "gate=off fallback=webview2");
                }
                PostBrowserRoles(_hostWindow, _externalGpuBrowserSession?.IsActive == true);
            }
            else
            {
                _liveAcceptanceCaptureWakeReceiver = null;
                _preloadWindow = new PreloadWindow(
                    options.UiDirectory,
                    options.UserDataDirectory,
                    (stage, detail) => Program.Trace(_logDirectory, stage, detail),
                    OnContentReady,
                    error => Stop(
                        1,
                        "browser_failed",
                        $"type={error.GetType().FullName} message={error.Message}"));
                _window = _preloadWindow;
            }
            _window.FormClosed += (_, __) => ExitThread();
            var unusedHandle = _window.Handle;
            if (_hostWindow != null)
                _hostWindow.BeginInvoke(new Action(_hostWindow.BeginPreload));
            else
                _preloadWindow!.BeginInvoke(new Action(_preloadWindow.BeginPreload));

            _liveAcceptanceCaptureWatcher = options.PersistentHost
                ? CreateLiveAcceptanceCaptureWatcher()
                : null;

            _lifecycleTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _lifecycleTimer.Tick += (_, __) => PollLifecycle();
            _lifecycleTimer.Start();

            if (_hostWindow != null && _hostServer != null &&
                options.ParentProcessId.HasValue)
            {
                _aboutInputSampler = new PreProviderAboutPointerSampler(
                    options.ParentProcessId.Value,
                    _hostWindow,
                    (stage, detail) => Program.Trace(_logDirectory, stage, detail),
                    PostAboutPointerInput,
                    () => PostBrowserJson(_hostWindow, BridgeProtocol.SerializeEvent(
                        WindowedInputPolicy.BootstrapPointerResetEventName, JValue.CreateNull())));
                _aboutInputTimer = new System.Windows.Forms.Timer
                {
                    Interval = PreProviderAboutInputPolicy.IdlePollIntervalMilliseconds,
                };
                _aboutInputTimer.Tick += (_, __) =>
                {
                    var structurallyEligible =
                        PreProviderAboutInputPolicy.ShouldSample(
                            _contentReady,
                            _hostServer.IsVisible,
                            _hostSurfaceMode,
                            _hostServer.IsConnected,
                            gameForeground: true);
                    _aboutInputTimer.Interval = structurallyEligible
                        ? PreProviderAboutInputPolicy.PollIntervalMilliseconds
                        : PreProviderAboutInputPolicy.IdlePollIntervalMilliseconds;
                    _aboutInputSampler.Poll(
                        _contentReady,
                        _hostServer.IsVisible,
                        _hostSurfaceMode,
                        _hostServer.IsConnected);
                };
                _aboutInputTimer.Start();

                // Provider pointer samples are a latest-state mailbox read by
                // the host STA. They never enqueue work onto the lifecycle/
                // presentation callback queue, so a cursor flood cannot sit
                // in front of a visibility or menu transition.
                _pointerInputTimer = new System.Windows.Forms.Timer
                {
                    Interval = 16,
                };
                _pointerInputTimer.Tick += (_, __) =>
                {
                    if (_hostPointerIngress.HasPending && _hostWindow != null)
                        DrainHostPointerInput(_hostWindow);
                };
                _pointerInputTimer.Start();
            }

            // Native lifecycle/F9 events must remain observable while the host
            // STA is synchronously waiting on a DirectComposition fence. A
            // WinForms timer cannot run during that wait, so a bounded worker
            // poll announces the ownership edge before queueing its STA work.
            _hostSignalTimer = options.PersistentHost
                ? new System.Threading.Timer(
                    _ => PollHostSignalsFromWorker(),
                    null,
                    dueTime: 0,
                    period: HostSignalPollMilliseconds)
                : null;

            if (options.ParentProcessId.HasValue)
            {
                AttachToProcess(options.ParentProcessId.Value);
            }
            else if (!options.SelfTest)
            {
                Program.Trace(
                    _logDirectory,
                    "process_wait_begin",
                    $"name={options.WaitForProcess} timeout_seconds={options.ProcessWaitTimeout.TotalSeconds:F0}");
            }
        }

        public int ExitCode { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                return;
            if (disposing)
            {
                CancellationTokenSource? cancellation;
                Task? pendingTask;
                lock (_preloadDataSync)
                {
                    _disposed = true;
                    _attachedProcessId = 0;
                    cancellation = _preloadDataCancellation;
                    pendingTask = _preloadDataTask;
                }
                TryCancel(cancellation);
                if (pendingTask != null && !pendingTask.IsCompleted)
                {
                    Program.Trace(
                        _logDirectory,
                        "preload_data_cancel_requested",
                        "reason=context_disposed");
                }
                _lifecycleTimer.Dispose();
                _aboutInputTimer?.Stop();
                _aboutInputTimer?.Dispose();
                _pointerInputTimer?.Stop();
                _pointerInputTimer?.Dispose();
                try
                {
                    _hostSignalTimer?.Change(
                        System.Threading.Timeout.Infinite,
                        System.Threading.Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                }
                _hostSignalTimer?.Dispose();
                _liveAcceptanceCaptureDispatchGate.Stop();
                _liveAcceptanceCaptureWakeReceiver?.Dispose();
                if (_liveAcceptanceCaptureWatcher != null)
                {
                    _liveAcceptanceCaptureWatcher.EnableRaisingEvents = false;
                    _liveAcceptanceCaptureWatcher.Dispose();
                }
                _liveAcceptanceCaptureGate.Dispose();
                _aboutInputSampler?.Dispose();
                _handoff?.Dispose();
                _preloadDataReady?.Dispose();
                DetachTargetProcessExitObserver();
                _targetProcess?.Dispose();
                _targetExitCodeReader?.Dispose();
                _hostToggle?.Dispose();
                _hostAboutToggle?.Dispose();
                _hostVerifyToggle?.Dispose();
                try { _hostVerifyActive?.Reset(); }
                catch (ObjectDisposedException) { }
                _hostVerifyActive?.Dispose();
                try { _hostAboutActive?.Reset(); }
                catch (ObjectDisposedException) { }
                _hostAboutActive?.Dispose();
                _hostInitializerPromotion?.Dispose();
                _hostClose?.Dispose();
                _defaultMenuIntent?.Dispose();
                _defaultMenuIntentClaimed?.Dispose();
                _defaultMenuIntentActive?.Dispose();
                _defaultMenuIntentCancelled?.Dispose();
                _selfTestStop?.Dispose();
                _externalGpuBrowserSession?.Dispose();
                _hostServer?.Dispose();
                _window.Dispose();
            }
            base.Dispose(disposing);
        }

        private void OnContentReady()
        {
            _contentReady = true;
            if (_hostServer != null && _hostWindow != null)
            {
                if (_pendingHostSurfaceGeneration > 0)
                    RequestHostSurface(_hostWindow, _pendingHostSurfaceMode, true);
                else
                    PostHostSurface(_hostWindow, _hostSurfaceMode);
                PostHostProvider(
                    _hostWindow,
                    _hostServer.IsConnected,
                    Volatile.Read(ref _providerSessionGeneration));
                _hostServer.MarkContentReady();
                Program.Trace(
                    _logDirectory,
                    "bootstrap_host_state_replayed",
                    $"provider_connected={_hostServer.IsConnected} " +
                    $"surface={_hostSurfaceMode} visible={_hostServer.IsVisible}");
                return;
            }
            BeginProfileRelease();
        }

        private async void BeginProfileRelease()
        {
            if (_profileReleaseStarted || _stopping)
            {
                return;
            }
            _profileReleaseStarted = true;
            try
            {
                var browserExited = await _preloadWindow!.ReleaseBrowserAsync(
                    TimeSpan.FromSeconds(8));
                if (_stopping)
                {
                    return;
                }
                if (!browserExited)
                {
                    Stop(
                        1,
                        "webview_profile_release_timeout",
                        "The shared WebView2 browser process did not release its resources.");
                    return;
                }

                _profileReleased = true;
                Program.Trace(
                    _logDirectory,
                    "webview_warm_cache_released",
                    $"elapsed_ms={_lifetime.Elapsed.TotalMilliseconds:F3} udf={_options.UserDataDirectory}");
                if (_options.SelfTest)
                {
                    Stop(0, "self_test_complete", "profile_released=True");
                }
            }
            catch (Exception error)
            {
                Stop(
                    1,
                    "webview_profile_release_failed",
                    $"type={error.GetType().FullName} message={error.Message}");
            }
        }

        private void PollLifecycle()
        {
            if (_stopping)
            {
                return;
            }

            if (!_options.PersistentHost && _lifetime.Elapsed >= _options.MaximumLifetime)
            {
                Stop(0, "maximum_lifetime_reached");
                return;
            }

            if (_options.SelfTest)
            {
                if (_selfTestStop?.WaitOne(0) == true)
                {
                    Stop(
                        0,
                        "self_test_stop",
                        "source=qualified_named_event");
                }
                return;
            }

            if (_options.PersistentHost)
                PollLiveAcceptanceCaptureRequests("lifecycle-fallback");

            if (_profileReleaseStarted && !_profileReleased)
            {
                return;
            }

            if (_targetProcess == null)
            {
                TryAttachToNamedProcess();
                if (_targetProcess == null && _lifetime.Elapsed >= _options.ProcessWaitTimeout)
                {
                    Stop(0, "process_wait_timeout", $"name={_options.WaitForProcess}");
                }
                return;
            }

            ObserveTargetWindowLifecycle("poll");

            try
            {
                if (_targetProcess.HasExited)
                {
                    TryHandleTargetProcessExit(_targetProcess, "poll");
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                Stop(0, "target_process_unavailable");
                return;
            }

            if (_options.PersistentHost)
            {
                var deadlineArmed = _defaultMenuIntentExpiresAt != TimeSpan.Zero;
                var claimObserved = deadlineArmed &&
                    PreloadHandoff.TryTakeDefaultMenuIntentClaim(
                        _options.ParentProcessId!.Value);
                var deadlineAction = DefaultMenuIntentDeadlinePolicy.Evaluate(
                    deadlineArmed,
                    claimObserved,
                    _defaultMenuIntentExpiresAt != TimeSpan.Zero &&
                        _lifetime.Elapsed >= _defaultMenuIntentExpiresAt,
                    IsHostSurfaceLogicallyOpen("initializing"));
                if (deadlineAction == DefaultMenuIntentDeadlineAction.CompleteClaim)
                {
                    CompleteDefaultMenuIntentClaim();
                }
                else if (deadlineAction ==
                    DefaultMenuIntentDeadlineAction.RefreshVisibleInitializer)
                {
                    RefreshDefaultMenuIntent();
                }
                else if (deadlineAction ==
                    DefaultMenuIntentDeadlineAction.ExpireWithoutHide)
                {
                    ExpireDefaultMenuIntent(hideInitializer: false);
                }
                if (string.Equals(_hostSurfaceMode, "initializing", StringComparison.Ordinal) &&
                    _hostServer?.IsVisible == true &&
                    !_hostServer.IsConnected &&
                    _lifetime.Elapsed >= _nextStartupStatusPublish)
                {
                    _nextStartupStatusPublish = _lifetime.Elapsed + TimeSpan.FromMilliseconds(500);
                    _hostServer.PublishStartupStatus();
                }
                var surfaceDeadlineAction =
                    HostSurfaceIntentPolicy.EvaluateReadyDeadline(
                        _pendingHostSurfaceGeneration,
                        _pendingHostSurfaceExpiresAt != TimeSpan.Zero,
                        _pendingHostSurfaceExpiresAt != TimeSpan.Zero &&
                            _lifetime.Elapsed >= _pendingHostSurfaceExpiresAt);
                if (surfaceDeadlineAction ==
                    HostSurfaceReadyDeadlineAction.FailClosedAndRetry)
                {
                    HandleHostSurfaceReadyDeadline();
                }
                return;
            }

            if (_profileReleased && _handoff?.WaitOne(0) == true)
            {
                Stop(
                    0,
                    "content_ready_handoff_received",
                    $"pid={_targetProcess.Id} preload_ready={_contentReady} profile_released=True");
            }
        }

        private void PollLiveAcceptanceCaptureRequests(string dispatchSource)
        {
            if (_hostWindow == null || _hostWindow.IsDisposed || !_contentReady)
                return;

            var armPath = Path.Combine(_logDirectory, "Acceptance", "armed.json");
            try
            {
                if (!File.Exists(armPath) || new FileInfo(armPath).Length > 65536)
                    return;
                var arm = JObject.Parse(File.ReadAllText(armPath));
                if (arm.Value<int?>("schemaVersion") != LiveAcceptanceContract.SchemaVersion ||
                    !string.Equals(
                        arm.Value<string>("scenario"),
                        LiveAcceptanceContract.Scenario,
                        StringComparison.Ordinal))
                    return;
                var runId = arm.Value<string>("runId");
                var harnessPid = arm.Value<int?>("harnessPid") ?? 0;
                if (!IsSafeAcceptanceIdentity(runId) || !IsLiveProcess(harnessPid))
                    return;

                var exchange = Path.Combine(
                    _logDirectory,
                    "Acceptance",
                    "Runs",
                    runId!,
                    "capture-exchange");
                if (!Directory.Exists(exchange)) return;
                var requestPath = Directory.GetFiles(exchange, "request-*.json")
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .FirstOrDefault(path => !File.Exists(ResponsePath(path)));
                if (requestPath == null || new FileInfo(requestPath).Length > 65536)
                    return;

                var request = JObject.Parse(File.ReadAllText(requestPath));
                var requestId = request.Value<string>("requestId");
                if (request.Value<int?>("schemaVersion") !=
                        LiveAcceptancePreviewCaptureContract.SchemaVersion ||
                    !string.Equals(request.Value<string>("runId"), runId, StringComparison.Ordinal) ||
                    request.Value<int?>("harnessPid") != harnessPid ||
                    !IsSafeAcceptanceIdentity(requestId) ||
                    !string.Equals(
                        Path.GetFileName(requestPath),
                        $"request-{requestId}.json",
                        StringComparison.Ordinal))
                    return;
                if (!Enum.TryParse(
                        request.Value<string>("expectation"),
                        ignoreCase: false,
                        out LiveAcceptanceVisualExpectation expectation) ||
                    !LiveAcceptancePreviewCaptureContract.RequiresHostPreview(expectation))
                    return;
                var expectedSurfaceMode = request.Value<string>("expectedSurfaceMode");
                var expectedSurfaceGeneration =
                    request.Value<int?>("expectedSurfaceGeneration");

                if (_liveAcceptanceCaptureGate.IsActiveRequest(requestId!))
                    return;

                Program.Trace(
                    _logDirectory,
                    "live_acceptance_capture_request_dequeued",
                    $"request={requestId} expectation={expectation} " +
                    $"surface={expectedSurfaceMode ?? "any"} " +
                    $"generation={expectedSurfaceGeneration?.ToString() ?? "any"} " +
                    $"dispatch={dispatchSource}");

                var responsePath = ResponsePath(requestPath);
                if (!_hostWindow.TryGetAcceptanceCaptureHostStatus(
                        out var hostFailure,
                        out var hostDetail))
                {
                    WriteCaptureFailureResponse(
                        responsePath,
                        runId!,
                        requestId!,
                        hostFailure,
                        0d);
                    Program.Trace(
                        _logDirectory,
                        "live_acceptance_capture_request_rejected",
                        $"request={requestId} reason={hostFailure} {hostDetail}");
                    return;
                }

                var captureTimer = Stopwatch.StartNew();
                var controllerGeneration =
                    _hostWindow.AcceptanceCaptureControllerGeneration;
                if (!_liveAcceptanceCaptureGate.TryBegin(
                        controllerGeneration,
                        requestId!,
                        LiveAcceptancePreviewCaptureContract.CaptureDeadline,
                        timeout =>
                        {
                            WriteCaptureFailureResponse(
                                responsePath,
                                runId!,
                                requestId!,
                                "WebView2 acceptance capture exceeded its bounded " +
                                    $"{LiveAcceptancePreviewCaptureContract.CaptureDeadline.TotalMilliseconds:F0} ms " +
                                    "deadline; this controller generation is poisoned.",
                                captureTimer.Elapsed.TotalMilliseconds);
                            Program.Trace(
                                _logDirectory,
                                "live_acceptance_webview_capture_timeout",
                                $"request={requestId} " +
                                $"controller_generation={timeout.ControllerGeneration} " +
                                $"duration_ms={captureTimer.Elapsed.TotalMilliseconds:F3} " +
                                "controller_poisoned=True");
                        },
                        out var lease,
                        out var gateRejection))
                {
                    WriteCaptureFailureResponse(
                        responsePath,
                        runId!,
                        requestId!,
                        gateRejection,
                        captureTimer.Elapsed.TotalMilliseconds);
                    Program.Trace(
                        _logDirectory,
                        "live_acceptance_capture_request_rejected",
                        $"request={requestId} reason={gateRejection} " +
                        $"controller_generation={controllerGeneration} " +
                        $"gate={_liveAcceptanceCaptureGate.State}");
                    return;
                }

                Program.Trace(
                    _logDirectory,
                    "live_acceptance_webview_capture_started",
                    $"request={requestId} expectation={expectation} " +
                    $"surface={expectedSurfaceMode ?? "any"} " +
                    $"generation={expectedSurfaceGeneration?.ToString() ?? "any"} " +
                    $"controller_generation={controllerGeneration} " +
                    $"deadline_ms={LiveAcceptancePreviewCaptureContract.CaptureDeadline.TotalMilliseconds:F0} " +
                    hostDetail);
                CaptureLiveAcceptancePreviewAsync(
                    requestPath,
                    runId!,
                    requestId!,
                    expectation,
                    expectedSurfaceMode,
                    expectedSurfaceGeneration,
                    captureTimer,
                    lease!);
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is JsonException)
            {
                Program.Trace(
                    _logDirectory,
                    "live_acceptance_capture_request_ignored",
                    $"type={error.GetType().Name} message={error.Message}");
            }
        }

        private FileSystemWatcher? CreateLiveAcceptanceCaptureWatcher()
        {
            try
            {
                var acceptanceDirectory = Path.Combine(_logDirectory, "Acceptance");
                Directory.CreateDirectory(acceptanceDirectory);
                var watcher = new FileSystemWatcher(
                    acceptanceDirectory,
                    "request-*.json")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName |
                        NotifyFilters.CreationTime |
                        NotifyFilters.Size,
                    InternalBufferSize = 4096,
                };
                watcher.Created += (_, __) =>
                    QueueLiveAcceptanceCapturePoll("filesystem-created");
                watcher.Renamed += (_, __) =>
                    QueueLiveAcceptanceCapturePoll("filesystem-renamed");
                watcher.Changed += (_, __) =>
                    QueueLiveAcceptanceCapturePoll("filesystem-changed");
                watcher.Error += (_, eventArgs) =>
                {
                    Program.Trace(
                        _logDirectory,
                        "live_acceptance_capture_watcher_failed",
                        $"type={eventArgs.GetException().GetType().Name} " +
                        "fallback=250ms-lifecycle-poll");
                    QueueLiveAcceptanceCapturePoll("filesystem-error");
                };
                watcher.EnableRaisingEvents = true;
                Program.Trace(
                    _logDirectory,
                    "live_acceptance_capture_watcher_ready",
                    "dispatch=filesystem-notification fallback=250ms-lifecycle-poll");
                return watcher;
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is ArgumentException)
            {
                Program.Trace(
                    _logDirectory,
                    "live_acceptance_capture_watcher_unavailable",
                    $"type={error.GetType().Name} fallback=250ms-lifecycle-poll");
                return null;
            }
        }

        private void QueueLiveAcceptanceCapturePoll(string source)
        {
            if (!_liveAcceptanceCaptureDispatchGate.TryReserve())
                return;

            Program.Trace(
                _logDirectory,
                "live_acceptance_capture_wake_received",
                $"source={source} dispatch=host-sta");

            var window = _hostWindow;
            if (window == null || window.IsDisposed || !window.IsHandleCreated)
            {
                _liveAcceptanceCaptureDispatchGate.Complete();
                return;
            }

            try
            {
                window.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!_liveAcceptanceCaptureDispatchGate.IsStopped)
                        {
                            Program.Trace(
                                _logDirectory,
                                "live_acceptance_capture_dispatch_entered",
                                $"source={source}");
                            PollLiveAcceptanceCaptureRequests(source);
                        }
                    }
                    finally
                    {
                        _liveAcceptanceCaptureDispatchGate.Complete();
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                _liveAcceptanceCaptureDispatchGate.Complete();
            }
        }

        private async void CaptureLiveAcceptancePreviewAsync(
            string requestPath,
            string runId,
            string requestId,
            LiveAcceptanceVisualExpectation expectation,
            string? expectedSurfaceMode,
            int? expectedSurfaceGeneration,
            Stopwatch timer,
            LiveAcceptanceCaptureLease lease)
        {
            var responsePath = ResponsePath(requestPath);
            var ownsCompletion = false;
            try
            {
                var window = _hostWindow;
                if (window == null || window.IsDisposed)
                    throw new InvalidOperationException("The persistent browser host is unavailable.");
                var identities = new List<LiveAcceptancePreviewIdentity>(
                    LiveAcceptancePreviewCaptureContract.RequiredFrameCount);
                var frames = new JArray();
                for (var index = 0;
                    index < LiveAcceptancePreviewCaptureContract.RequiredFrameCount;
                    index++)
                {
                    if (!lease.IsActive) return;
                    var frameStartedUtc = DateTime.UtcNow;
                    var frameTimer = Stopwatch.StartNew();
                    var frame = await window.CaptureAcceptancePreviewAsync();
                    if (!lease.IsActive) return;
                    frameTimer.Stop();
                    if (frame.Png.Length > LiveAcceptancePreviewCaptureContract.MaximumPngBytes)
                        throw new InvalidOperationException("The browser preview exceeded its size bound.");
                    var identity = new LiveAcceptancePreviewIdentity(
                        frame.SurfaceMode,
                        frame.SurfaceGeneration,
                        frame.ControllerGeneration,
                        frame.MenuPresentationId);
                    identities.Add(identity);
                    var pngName = $"{requestId}-frame-{index + 1}.png";
                    WriteBytesAtomically(
                        Path.Combine(Path.GetDirectoryName(requestPath)!, pngName),
                        frame.Png);
                    frames.Add(new JObject
                    {
                        ["file"] = pngName,
                        ["surfaceMode"] = identity.SurfaceMode,
                        ["surfaceGeneration"] = identity.SurfaceGeneration,
                        ["controllerGeneration"] = identity.ControllerGeneration,
                        ["menuPresentationId"] = identity.MenuPresentationId,
                        ["pngBytes"] = frame.Png.Length,
                        ["startedUtc"] = frameStartedUtc.ToString("O"),
                        ["completedUtc"] = DateTime.UtcNow.ToString("O"),
                        ["durationMs"] = frameTimer.Elapsed.TotalMilliseconds,
                    });
                    if (index + 1 < LiveAcceptancePreviewCaptureContract.RequiredFrameCount)
                        await Task.Delay(50);
                }

                if (!LiveAcceptancePreviewCaptureContract.TryValidateCorrelatedFrames(
                        expectation,
                        expectedSurfaceMode,
                        expectedSurfaceGeneration,
                        identities,
                        out var correlationFailure))
                    throw new InvalidOperationException(correlationFailure);

                ownsCompletion = lease.TryComplete();
                if (!ownsCompletion) return;
                WriteJsonAtomically(responsePath, new JObject
                {
                    ["schemaVersion"] = LiveAcceptancePreviewCaptureContract.SchemaVersion,
                    ["runId"] = runId,
                    ["requestId"] = requestId,
                    ["status"] = "passed",
                    ["completedUtc"] = DateTime.UtcNow.ToString("O"),
                    ["durationMs"] = timer.Elapsed.TotalMilliseconds,
                    ["frames"] = frames,
                });
                Program.Trace(
                    _logDirectory,
                    "live_acceptance_webview_capture_complete",
                    $"request={requestId} expectation={expectation} " +
                    $"surface={identities[0].SurfaceMode} " +
                    $"generation={identities[0].SurfaceGeneration} " +
                    $"controller_generation={identities[0].ControllerGeneration} " +
                    $"presentation={identities[0].MenuPresentationId} " +
                    $"duration_ms={timer.Elapsed.TotalMilliseconds:F3}");
            }
            catch (Exception error)
            {
                if (!ownsCompletion)
                    ownsCompletion = lease.TryComplete();
                if (!ownsCompletion) return;
                try
                {
                    WriteCaptureFailureResponse(
                        responsePath,
                        runId,
                        requestId,
                        error.Message,
                        timer.Elapsed.TotalMilliseconds);
                }
                catch (Exception writeError) when (
                    writeError is IOException ||
                    writeError is UnauthorizedAccessException)
                {
                    Program.Trace(
                        _logDirectory,
                        "live_acceptance_capture_response_failed",
                        $"request={requestId} type={writeError.GetType().Name} " +
                        $"message={writeError.Message}");
                }
            }
        }

        private static void WriteCaptureFailureResponse(
            string responsePath,
            string runId,
            string requestId,
            string error,
            double durationMilliseconds)
        {
            WriteJsonAtomically(responsePath, new JObject
            {
                ["schemaVersion"] = LiveAcceptancePreviewCaptureContract.SchemaVersion,
                ["runId"] = runId,
                ["requestId"] = requestId,
                ["status"] = "failed",
                ["completedUtc"] = DateTime.UtcNow.ToString("O"),
                ["durationMs"] = durationMilliseconds,
                ["error"] = error,
            });
        }

        private static string ResponsePath(string requestPath) =>
            Path.Combine(
                Path.GetDirectoryName(requestPath)!,
                Path.GetFileName(requestPath).Replace("request-", "response-"));

        private static bool IsSafeAcceptanceIdentity(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value!.Length <= 96 &&
            value.All(character =>
                char.IsLetterOrDigit(character) || character == '-' || character == '_');

        private static bool IsLiveProcess(int processId)
        {
            if (processId <= 0) return false;
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static void WriteBytesAtomically(string path, byte[] bytes)
        {
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        private static void WriteJsonAtomically(string path, JObject value)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, value.ToString(Formatting.None));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        private void PollHostSignalsFromWorker()
        {
            if (_stopping || !_options.PersistentHost ||
                Interlocked.Exchange(ref _hostSignalPollActive, 1) != 0)
            {
                return;
            }
            OverlayWindow? scanWindow = null;
            var scanIngressAnnounced = false;
            var signalObserved = false;
            try
            {
                scanWindow = _hostWindow;
                if (scanWindow != null && !scanWindow.IsDisposed &&
                    scanWindow.IsHandleCreated)
                {
                    // Announce the scan before consuming an AutoReset event.
                    // Without this lease, the worker could reset the handle,
                    // lose its timeslice, and let the STA reveal stale pixels
                    // before InvokeHost publishes the typed event ingress.
                    scanWindow.ReserveRevealIngressScan();
                    scanIngressAnnounced = true;
                }
                signalObserved = PollHostSignals();
            }
            catch (ObjectDisposedException)
            {
                // Shutdown can dispose the named events after this callback was
                // already scheduled. The worker owns no durable state.
            }
            finally
            {
                if (scanIngressAnnounced && scanWindow != null)
                {
                    var revealResumeRequired =
                        scanWindow.ReleaseRevealIngressScan();
                    if (signalObserved || revealResumeRequired)
                    {
                        try
                        {
                            scanWindow.BeginInvoke(
                                (Action)(() => scanWindow.ResumeRevealAfterIngress()));
                        }
                        catch (Exception error) when (
                            error is InvalidOperationException ||
                            error is ObjectDisposedException)
                        {
                        }
                    }
                }
                Volatile.Write(ref _hostSignalPollActive, 0);
            }
        }

        private bool TryAnnounceHostSignalsAtRevealBoundary()
        {
            // A worker that is between WaitOne and InvokeHost owns a durable
            // reveal-ingress lease. Conservatively withhold this reveal; the
            // worker's completion dispatch will resume it after every typed
            // mutation it discovered has been announced.
            if (Interlocked.CompareExchange(
                    ref _hostSignalPollActive,
                    1,
                    0) != 0)
                return true;
            try
            {
                return PollHostSignals();
            }
            catch (ObjectDisposedException)
            {
                // Shutdown may dispose a named event after the stopping check.
                // A closing host must never be promoted as a side effect.
                return true;
            }
            finally
            {
                Volatile.Write(ref _hostSignalPollActive, 0);
            }
        }

        private bool PollHostSignals()
        {
            if (_stopping || !_options.PersistentHost)
                return false;

            var observed = false;
            // Snapshot every auto-reset signal once, then apply one explicit
            // priority order. A close is terminal for this polling epoch: a
            // same-poll Story promotion or F9 edge must never reopen the host
            // after close has revoked its input and visibility leases.
            var closeRequested = _hostClose?.WaitOne(0) == true;
            var aboutToggleRequested = _hostAboutToggle?.WaitOne(0) == true;
            var verifyToggleRequested = _hostVerifyToggle?.WaitOne(0) == true;
            var initializerEventRequested =
                _hostInitializerPromotion?.WaitOne(0) == true;
            var runtimeReadyLeaseSignaled =
                _hostServer?.RuntimeReadyLeaseSignaled == true;
            var initializerPromotionRequested =
                !runtimeReadyLeaseSignaled &&
                (_deferredInitializerPromotion || initializerEventRequested);
            _deferredInitializerPromotion = false;
            var hostToggleRequested = _hostToggle?.WaitOne(0) == true;
            var signalAction = HostSurfaceIntentPolicy.EvaluateSignalBatch(
                closeRequested,
                aboutToggleRequested,
                verifyToggleRequested,
                initializerPromotionRequested,
                hostToggleRequested);

            if (signalAction == BootstrapHostSignalAction.Close)
            {
                var deferredInitializer =
                    HostSurfaceIntentPolicy.ShouldDeferInitializerAfterClose(
                        closeRequested,
                        initializerPromotionRequested,
                        runtimeReadyLeaseSignaled);
                _deferredInitializerPromotion = deferredInitializer;
                InvokeHost(window =>
                {
                    CancelDefaultMenuIntent("native-close-boundary");
                    RequestHostSurface(window, "none", false);
                    Program.Trace(
                        _logDirectory,
                        "bootstrap_host_native_close",
                        "source=native-close-boundary terminal_epoch=True " +
                        $"suppressed_about={aboutToggleRequested} " +
                        $"suppressed_verify={verifyToggleRequested} " +
                        $"suppressed_initializer={initializerPromotionRequested} " +
                        $"deferred_initializer={deferredInitializer} " +
                        $"suppressed_toggle={hostToggleRequested} " +
                        $"provider_connected={_hostServer?.IsConnected == true}");
                }, signalRevealIngress: true);
                return true;
            }

            if (signalAction == BootstrapHostSignalAction.ToggleAbout)
            {
                observed = true;
                InvokeHost(window =>
                {
                    var show = !IsHostSurfaceLogicallyOpen("about");
                    CancelDefaultMenuIntent(
                        show ? "about-surface" : "native-about-toggle-close");
                    RequestHostSurface(window, show ? "about" : "none", show);
                    Program.Trace(
                        _logDirectory,
                        "bootstrap_host_native_about_toggle",
                        $"visible={show} provider_connected={_hostServer?.IsConnected == true}");
                }, signalRevealIngress: true);
            }
            if (signalAction == BootstrapHostSignalAction.ToggleVerification)
            {
                observed = true;
                InvokeHost(window =>
                {
                    var show = !IsHostSurfaceLogicallyOpen(HostSurfaceMode.Verifying);
                    CancelDefaultMenuIntent(
                        show ? "game-state-verification" : "native-verification-toggle-close");
                    RequestHostSurface(
                        window,
                        show ? HostSurfaceMode.Verifying : HostSurfaceMode.None,
                        show);
                    Program.Trace(
                        _logDirectory,
                        "bootstrap_host_native_verification_toggle",
                        $"visible={show} provider_connected={_hostServer?.IsConnected == true}");
                }, signalRevealIngress: true);
            }
            if (signalAction == BootstrapHostSignalAction.PromoteInitializer)
            {
                observed = true;
                InvokeHost(window =>
                {
                    if (!HostSurfaceIntentPolicy.ShouldPromoteToInitializer(
                            objectiveStoryEvidence: true))
                    {
                        Program.Trace(
                            _logDirectory,
                            "bootstrap_host_initializer_promotion_ignored",
                            $"reason=invalid-story-evidence surface={_hostSurfaceMode} " +
                            $"provider_connected={_hostServer?.IsConnected == true}");
                        return;
                    }

                    var previousSurface = _hostSurfaceMode;
                    RequestHostSurface(window, "initializing", true);
                    // Objective Story detection, rather than an F9 edge, owns
                    // this automatic initializer promotion. It must not arm a
                    // default-menu intent: RuntimeReady retires the preloader
                    // to gameplay, and only a fresh player F9 may open a mod
                    // menu. The actual F9 paths below retain their typed intent.
                    _initializerOpeningEdgePending = false;
                    Program.Trace(
                        _logDirectory,
                        "bootstrap_host_initializer_promoted",
                        $"source=objective-story-evidence previous={previousSurface} " +
                        $"provider_connected={_hostServer?.IsConnected == true}");
                }, signalRevealIngress: true);
            }
            if (signalAction != BootstrapHostSignalAction.ToggleInitializer)
                return observed;

            observed = true;
            InvokeHost(window =>
            {
                if (HostSurfaceIntentPolicy.EvaluateNativeToggle(
                        _hostServer?.RuntimeReadyLeaseSignaled == true) ==
                    NativeHostToggleAction.ForwardDefaultMenuIntentHidden)
                {
                    ArmDefaultMenuIntent();
                    RequestHostSurface(window, "none", false);
                    Program.Trace(
                        _logDirectory,
                        "bootstrap_host_native_toggle_forwarded",
                        "visible=false destination=default-menu " +
                        $"provider_connected={_hostServer?.IsConnected == true}");
                    return;
                }
                // Bootstrap F9 follows the same deterministic toggle contract
                // as About and the provider menu. Pending paint is a logical
                // open state, so a second edge also cancels a renderer-starved
                // request instead of hiding and immediately revealing it.
                var initializerLogicallyOpen =
                    IsHostSurfaceLogicallyOpen("initializing");
                if (HostSurfaceIntentPolicy.ShouldConsumeOpeningInitializerToggle(
                        _initializerOpeningEdgePending,
                        initializerLogicallyOpen))
                {
                    _initializerOpeningEdgePending = false;
                    RefreshDefaultMenuIntent();
                    Program.Trace(
                        _logDirectory,
                        "bootstrap_host_native_toggle_consumed",
                        "action=preserve-opening-initializer " +
                        $"generation={_hostSurfaceGeneration} " +
                        $"provider_connected={_hostServer?.IsConnected == true}");
                    return;
                }

                var show = HostSurfaceIntentPolicy.EvaluateBootstrapToggle(
                        initializerLogicallyOpen) ==
                    BootstrapSurfaceToggleAction.Show;
                if (show)
                    ArmDefaultMenuIntent();
                else
                    CancelDefaultMenuIntent("native-initializer-toggle-close");
                RequestHostSurface(
                    window,
                    show ? "initializing" : "none",
                    show);
                Program.Trace(
                    _logDirectory,
                    "bootstrap_host_native_toggle",
                    $"visible={show} action={(show ? "show" : "close")} " +
                    $"provider_connected={_hostServer?.IsConnected == true}");
            }, signalRevealIngress: true);
            return observed;
        }

        private bool IsHostSurfaceLogicallyOpen(string mode)
        {
            return HostSurfaceIntentPolicy.IsLogicallyOpen(
                _hostServer?.IsVisible == true,
                _hostSurfaceMode,
                _hostSurfaceGeneration,
                _pendingHostSurfaceGeneration,
                _pendingHostSurfaceMode,
                mode);
        }

        private void HandleTargetProcessExit(Process targetProcess)
        {
            ObserveTargetWindowLifecycle("process-exit-poll");
            int? targetExitCode = null;
            var exitCodeSource = "process";
            try
            {
                targetExitCode = targetProcess.ExitCode;
            }
            catch (Exception error) when (
                error is InvalidOperationException ||
                error is System.ComponentModel.Win32Exception ||
                error is NotSupportedException)
            {
            }

            if (!targetExitCode.HasValue &&
                _targetExitCodeReader != null &&
                _targetExitCodeReader.TryRead(
                    out var retainedExitCode,
                    out var retainedOutcome))
            {
                targetExitCode = retainedExitCode;
                exitCodeSource = "retained-handle";
            }

            var markerOutcome = "preserved-exit-code-unavailable";
            var markerCleared = false;
            if (targetExitCode.HasValue)
            {
                if (targetExitCode.Value != 0)
                {
                    markerCleared = NormalExitMarkerCleanup.TryClearAllin1Marker(
                        string.Empty,
                        targetExitCode.Value,
                        out markerOutcome);
                }
                else
                {
                    var gtaRoot = ResolveGtaRootFromPreloaderBase();
                    if (gtaRoot == null)
                    {
                        markerOutcome = "preserved-game-root-unavailable";
                    }
                    else
                    {
                        markerCleared = NormalExitMarkerCleanup.TryClearAllin1Marker(
                            gtaRoot,
                            targetExitCode.Value,
                            out markerOutcome);
                    }
                }
            }

            var exitCodeText = targetExitCode.HasValue
                ? targetExitCode.Value.ToString()
                : "unavailable";
            var windowState = _targetWindowLifecycleJournal?.DescribeLastState() ??
                "window_state=unobserved";
            Program.Trace(
                _logDirectory,
                "normal_exit_marker_cleanup",
                $"exit_code={exitCodeText} outcome={markerOutcome} " +
                $"cleared={markerCleared} source={exitCodeSource}");
            Stop(
                0,
                "target_process_exited",
                $"pid={targetProcess.Id} exit_code={exitCodeText} {windowState}");
        }

        private void TryHandleTargetProcessExit(Process targetProcess, string source)
        {
            if (Interlocked.Exchange(ref _targetExitHandlingStarted, 1) != 0)
                return;

            Program.Trace(
                _logDirectory,
                "target_process_exit_handling_begin",
                $"pid={targetProcess.Id} source={source} " +
                $"elapsed_ms={_lifetime.Elapsed.TotalMilliseconds:F3}");
            HandleTargetProcessExit(targetProcess);
        }

        private static string? ResolveGtaRootFromPreloaderBase()
        {
            try
            {
                var reactorDirectory = new DirectoryInfo(
                    AppDomain.CurrentDomain.BaseDirectory);
                var pluginsDirectory = reactorDirectory.Parent;
                if (!string.Equals(
                        reactorDirectory.Name,
                        "ReactorV",
                        StringComparison.OrdinalIgnoreCase) ||
                    pluginsDirectory == null ||
                    !string.Equals(
                        pluginsDirectory.Name,
                        "plugins",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return pluginsDirectory.Parent?.FullName;
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is ArgumentException ||
                error is NotSupportedException)
            {
                return null;
            }
        }

        private void TryAttachToNamedProcess()
        {
            if (string.IsNullOrWhiteSpace(_options.WaitForProcess))
            {
                return;
            }

            Process? candidate = null;
            Process[] candidates;
            try
            {
                candidates = Process.GetProcessesByName(_options.WaitForProcess);
                candidate = candidates
                    .OrderByDescending(SafeStartTimeUtc)
                    .FirstOrDefault(process => !SafeHasExited(process));
            }
            catch
            {
                return;
            }

            foreach (var process in candidates)
            {
                if (!ReferenceEquals(process, candidate))
                {
                    process.Dispose();
                }
            }

            if (candidate != null)
            {
                AttachToProcess(candidate);
            }
        }

        private void AttachToProcess(int processId)
        {
            try
            {
                AttachToProcess(Process.GetProcessById(processId));
            }
            catch (ArgumentException)
            {
                Stop(2, "parent_process_missing", $"pid={processId}");
            }
        }

        private void AttachToProcess(Process process)
        {
            CancellationTokenSource? previousCancellation;
            lock (_preloadDataSync)
            {
                previousCancellation = _preloadDataCancellation;
            }
            TryCancel(previousCancellation);
            DetachTargetProcessExitObserver();
            _targetProcess?.Dispose();
            _targetExitCodeReader?.Dispose();
            _targetExitCodeReader = null;
            _handoff?.Dispose();
            _preloadDataReady?.Dispose();
            _targetProcess = process;
            _targetWindowLifecycleProbe = new TargetWindowLifecycleProbe(
                (uint)process.Id,
                _hostWindow?.Handle ?? IntPtr.Zero);
            _targetWindowLifecycleJournal = new TargetWindowLifecycleJournal(
                (stage, detail) => Program.Trace(_logDirectory, stage, detail));
            Interlocked.Exchange(ref _targetWindowObserverFailed, 0);
            Interlocked.Exchange(ref _targetExitHandlingStarted, 0);
            ArmTargetProcessExitObserver(process);
            ObserveTargetWindowLifecycle("attached");
            if (!TargetProcessExitCodeReader.TryOpen(
                    process.Id,
                    out _targetExitCodeReader,
                    out var exitCodeHandleOutcome))
            {
                Program.Trace(
                    _logDirectory,
                    "target_exit_code_handle_unavailable",
                    $"pid={process.Id} outcome={exitCodeHandleOutcome}");
            }
            _handoff = PreloadHandoff.CreateWaitHandle(process.Id);
            _preloadDataReady = PreloadHandoff.CreatePreloadDataReadyWaitHandle(process.Id);
            Program.Trace(
                _logDirectory,
                "target_process_attached",
                $"pid={process.Id} name={SafeProcessName(process)} event={PreloadHandoff.EventName(process.Id)} " +
                $"elapsed_ms={_lifetime.Elapsed.TotalMilliseconds:F3}");
            QueuePreloadData(process.Id);
        }

        private void ArmTargetProcessExitObserver(Process process)
        {
            var processId = process.Id;
            Volatile.Write(ref _targetExitObserverProcessId, processId);
            Interlocked.Exchange(ref _targetExitSignalObserved, 0);
            try
            {
                process.Exited += OnTargetProcessExited;
                process.EnableRaisingEvents = true;
                Program.Trace(
                    _logDirectory,
                    "target_process_exit_observer_armed",
                    $"pid={processId} elapsed_ms={_lifetime.Elapsed.TotalMilliseconds:F3}");
            }
            catch (Exception error) when (
                error is InvalidOperationException ||
                error is System.ComponentModel.Win32Exception ||
                error is NotSupportedException)
            {
                try { process.Exited -= OnTargetProcessExited; }
                catch { }
                Volatile.Write(ref _targetExitObserverProcessId, 0);
                Program.Trace(
                    _logDirectory,
                    "target_process_exit_observer_unavailable",
                    $"pid={processId} type={error.GetType().Name} message={error.Message}");
            }
        }

        private void DetachTargetProcessExitObserver()
        {
            var process = _targetProcess;
            if (process != null)
            {
                try { process.Exited -= OnTargetProcessExited; }
                catch (InvalidOperationException) { }
            }
            Volatile.Write(ref _targetExitObserverProcessId, 0);
            Interlocked.Exchange(ref _targetExitSignalObserved, 0);
            Interlocked.Exchange(ref _targetExitHandlingStarted, 0);
            _targetWindowLifecycleProbe = null;
            _targetWindowLifecycleJournal = null;
        }

        private void OnTargetProcessExited(object? sender, EventArgs eventArgs)
        {
            var processId = Volatile.Read(ref _targetExitObserverProcessId);
            if (processId <= 0 || !ReferenceEquals(sender, _targetProcess) ||
                Interlocked.Exchange(ref _targetExitSignalObserved, 1) != 0)
            {
                return;
            }

            var windowState = _targetWindowLifecycleJournal?.DescribeLastState() ??
                "window_state=unobserved";
            Program.Trace(
                _logDirectory,
                "target_process_exit_signal_received",
                $"pid={processId} elapsed_ms={_lifetime.Elapsed.TotalMilliseconds:F3} " +
                $"signal_thread={Thread.CurrentThread.ManagedThreadId} {windowState}");

            var target = _targetProcess;
            if (target == null)
                return;
            try
            {
                _window.BeginInvoke(new Action(() =>
                    TryHandleTargetProcessExit(target, "process-event")));
            }
            catch (Exception error) when (
                error is InvalidOperationException ||
                error is ObjectDisposedException)
            {
                // The 250 ms lifecycle poll remains the fallback if shutdown
                // has already started or the HWND is being destroyed.
            }
        }

        private void ObserveTargetWindowLifecycle(string reason)
        {
            var probe = _targetWindowLifecycleProbe;
            var journal = _targetWindowLifecycleJournal;
            if (probe == null || journal == null)
            {
                return;
            }

            try
            {
                var state = probe.Capture(out var discoveryDetail);
                journal.Observe(
                    state,
                    reason,
                    _lifetime.Elapsed.TotalMilliseconds,
                    discoveryDetail);
                TrySynchronizeExternalGpuSurfaceFromTargetWindow(state, reason);
            }
            catch (Exception error)
            {
                // Observability must never influence the preloader lifecycle.
                // Record only the first failure to avoid a 250 ms error flood.
                if (Interlocked.Exchange(ref _targetWindowObserverFailed, 1) == 0)
                {
                    Program.Trace(
                        _logDirectory,
                        "target_window_lifecycle_observer_unavailable",
                        $"type={error.GetType().Name} message={error.Message}");
                }
            }
        }

        private void QueuePreloadData(int processId)
        {
            var cancellation = new CancellationTokenSource();
            lock (_preloadDataSync)
            {
                if (_disposed || _stopping)
                {
                    cancellation.Dispose();
                    return;
                }
                _attachedProcessId = processId;
                _preloadDataCancellation = cancellation;
            }
            Program.Trace(
                _logDirectory,
                "preload_data_queued",
                $"pid={processId} ui_thread={Thread.CurrentThread.ManagedThreadId}");
            var task = BuildPreloadDataAsync(processId, cancellation);
            lock (_preloadDataSync)
            {
                if (ReferenceEquals(_preloadDataCancellation, cancellation))
                {
                    _preloadDataTask = task;
                }
            }
        }

        private async Task BuildPreloadDataAsync(
            int processId,
            CancellationTokenSource owner)
        {
            try
            {
                var gtaRoot = _options.GtaRoot ??
                    PreloadDataCache.ResolveGtaRootFromPreloaderDirectory(
                        AppDomain.CurrentDomain.BaseDirectory);
                var result = await PreloadDataCache.BuildAsync(
                    gtaRoot,
                    processId,
                    _options.CacheRootOverride,
                    (stage, detail) => Program.Trace(_logDirectory, stage, detail),
                    owner.Token).ConfigureAwait(false);

                bool current;
                bool ready;
                bool signaled = false;
                lock (_preloadDataSync)
                {
                    current =
                        !_disposed &&
                        !_stopping &&
                        !owner.IsCancellationRequested &&
                        ReferenceEquals(_preloadDataCancellation, owner) &&
                        _attachedProcessId == processId;
                    ready = current &&
                        PreloadDataCache.IsReadyForHandoff(processId, result);
                    if (ready)
                    {
                        signaled = PreloadHandoff.TrySignalPreloadDataReady(
                            processId,
                            result);
                    }
                }

                // The persistent main-menu host may expose a compact,
                // read-only package catalog as soon as this exact process's
                // validated preload snapshot exists. This is independent of
                // the broader handoff signal: one unrelated optional snapshot
                // cannot force the About surface back to "registry not ready."
                if (current)
                    _hostServer?.PublishBootstrapExtensionCatalog(result);

                if (!current)
                {
                    Program.Trace(
                        _logDirectory,
                        "preload_data_abandoned",
                        $"pid={processId} reason=context_changed");
                }
                else if (ready)
                {
                    Program.Trace(
                        _logDirectory,
                        signaled
                            ? "preload_data_ready_signaled"
                            : "preload_data_ready_signal_failed",
                        $"pid={processId} manifests={result.SnapshotPaths.Count} " +
                        $"entries={result.EntryCount} complete=True");
                }
                else
                {
                    Program.Trace(
                        _logDirectory,
                        "preload_data_not_ready",
                        $"pid={processId} manifests={result.SnapshotPaths.Count} " +
                        $"complete={result.Complete}");
                }
            }
            catch (OperationCanceledException)
            {
                Program.Trace(
                    _logDirectory,
                    "preload_data_abandoned",
                    $"pid={processId} reason=cancelled");
            }
            catch (Exception error)
            {
                Program.Trace(
                    _logDirectory,
                    "preload_data_failed",
                    $"pid={processId} type={error.GetType().Name} message={error.Message}");
            }
            finally
            {
                lock (_preloadDataSync)
                {
                    if (ReferenceEquals(_preloadDataCancellation, owner))
                    {
                        _preloadDataCancellation = null;
                        _preloadDataTask = null;
                    }
                }
                owner.Dispose();
            }
        }

        private void Stop(int exitCode, string stage, string? detail = null)
        {
            CancellationTokenSource? cancellation;
            lock (_preloadDataSync)
            {
                if (_stopping)
                {
                    return;
                }
                _stopping = true;
                cancellation = _preloadDataCancellation;
            }
            TryCancel(cancellation);
            try { _hostVerifyActive?.Reset(); }
            catch (ObjectDisposedException) { }
            try { _hostAboutActive?.Reset(); }
            catch (ObjectDisposedException) { }
            ExitCode = exitCode;
            Program.Trace(
                _logDirectory,
                stage,
                $"elapsed_ms={_lifetime.Elapsed.TotalMilliseconds:F3}" +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail));
            _lifecycleTimer.Stop();
            try
            {
                _hostSignalTimer?.Change(
                    System.Threading.Timeout.Infinite,
                    System.Threading.Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
            _aboutInputTimer?.Stop();
            _pointerInputTimer?.Stop();
            _liveAcceptanceCaptureDispatchGate.Stop();
            if (_liveAcceptanceCaptureWatcher != null)
                _liveAcceptanceCaptureWatcher.EnableRaisingEvents = false;
            _aboutInputSampler?.ResetBoundary("preloader-stop");
            if (!_window.IsDisposed)
            {
                _window.Close();
            }
            else
            {
                ExitThread();
            }
        }

        private bool InvokeHost(
            Action<OverlayWindow> action,
            bool signalRevealIngress = false,
            string? hostMessageIngress = null)
        {
            var window = _hostWindow;
            if (window == null || window.IsDisposed || !window.IsHandleCreated) return false;
            var ingressAnnouncements = 0;
            try
            {
                // Publish ownership/visibility ingress before queueing the STA
                // mutation. A synchronous DirectComposition fence can block
                // this message loop; the atomic epoch lets the in-flight
                // reveal observe the queued replacement before Show().
                if (signalRevealIngress)
                {
                    window.SignalRevealIngress();
                    ingressAnnouncements++;
                }
                if (!string.IsNullOrWhiteSpace(hostMessageIngress) &&
                    window.SignalHostMessageIngress(hostMessageIngress!))
                {
                    ingressAnnouncements++;
                }
                // Even when the caller is a WinForms timer, enqueue host
                // mutations behind the current message. Surface changes,
                // WebView acknowledgements, and visibility commits therefore
                // keep the same FIFO boundary used by background pipe events.
                window.BeginInvoke(
                    (Action<OverlayWindow>)(queuedWindow =>
                    {
                        if (ingressAnnouncements > 0)
                            queuedWindow.ApplyRevealIngress(ingressAnnouncements);
                        try
                        {
                            action(queuedWindow);
                        }
                        finally
                        {
                            if (ingressAnnouncements > 0)
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
                if (ingressAnnouncements > 0)
                    window.ApplyRevealIngress(ingressAnnouncements);
                return false;
            }
        }

        private void QueueHostPointerInput(
            float x,
            float y,
            bool pressed,
            bool released,
            int wheel)
        {
            _hostPointerIngress.Enqueue(
                new HostPointerInputFrame(x, y, pressed, released, wheel));
        }

        private void DrainHostPointerInput(OverlayWindow window)
        {
            var batch = _hostPointerIngress.Drain();
            if (_requireNativePresenter && (!_browserPresentation.ExternalGpuVisible ||
                _externalGpuBrowserSession?.IsPresentationReady != true)) return;
            var externalOwnsPointer = DualBrowserInputAuthority.UseExternalGpuRenderer(
                _externalGpuBrowserSession?.IsActive == true,
                _hostSurfaceMode);
            foreach (var frame in batch.Frames)
            {
                if (externalOwnsPointer)
                {
                    _externalGpuBrowserSession?.PostPointerInput(
                        frame.X,
                        frame.Y,
                        frame.Pressed,
                        frame.Released,
                        frame.Wheel);
                }
                else
                {
                    window.PostPointerInput(
                        frame.X,
                        frame.Y,
                        frame.Pressed,
                        frame.Released,
                        frame.Wheel);
                }
            }

            if (batch.CoalescedNeutralFrames > 0)
            {
                var trace = _hostPointerTraceGate.Observe(
                    batch.CoalescedNeutralFrames,
                    _lifetime.Elapsed);
                if (trace.ShouldTrace)
                {
                    Program.Trace(
                        _logDirectory,
                        "bootstrap_host_pointer_coalesced",
                        $"batch_coalesced={trace.BatchCoalescedFrames} " +
                        $"interval_coalesced={trace.IntervalCoalescedFrames} " +
                        $"total_coalesced={trace.TotalCoalescedFrames} " +
                        $"total_dropped={trace.TotalCoalescedFrames} " +
                        $"delivered={batch.Frames.Count} " +
                        "report_interval_ms=5000");
                }
            }
        }

        private void PostAboutPointerInput(float x, float y, bool pressed, bool released)
        {
            if (_hostWindow == null || _hostServer?.IsVisible != true || _hostServer.IsConnected ||
                !string.Equals(_hostSurfaceMode, HostSurfaceMode.About, StringComparison.Ordinal)) return;
            if (_requireNativePresenter)
            {
                if (_browserPresentation.Owner != BrowserPresentationOwner.ExternalGpuBootstrap ||
                    _externalGpuBrowserSession?.IsPresentationReady != true) return;
                // Private About events can target only its marked tabs/catalog
                // controls. They grant no provider or game-action authority.
                _externalGpuBrowserSession.PostJson(BridgeProtocol.SerializeEvent(
                    WindowedInputPolicy.BootstrapPointerEventName, new JObject
                    { ["x"] = x, ["y"] = y, ["pressed"] = pressed, ["released"] = released, ["wheelDelta"] = 0 }));
            }
            else _hostWindow.PostBootstrapPointerInput(x, y, pressed, released);
        }

        private int PostHostSurface(
            OverlayWindow window,
            string mode,
            string? handoff = null)
        {
            _hostSurfaceMode = string.Equals(mode, "about", StringComparison.Ordinal)
                ? "about"
                : string.Equals(mode, HostSurfaceMode.Verifying, StringComparison.Ordinal)
                    ? HostSurfaceMode.Verifying
                    : string.Equals(mode, HostSurfaceMode.SetupStatus, StringComparison.Ordinal)
                        ? HostSurfaceMode.SetupStatus
                        : string.Equals(mode, "initializing", StringComparison.Ordinal)
                            ? "initializing"
                            : "none";
            if (HostSurfaceIntentPolicy.IsVerificationActiveSurface(_hostSurfaceMode))
                _hostVerifyActive?.Set();
            else
                _hostVerifyActive?.Reset();
            if (string.Equals(_hostSurfaceMode, "about", StringComparison.Ordinal))
                _hostAboutActive?.Set();
            else
                _hostAboutActive?.Reset();
            if (!string.Equals(_hostSurfaceMode, "about", StringComparison.Ordinal))
                _aboutInputSampler?.ResetBoundary("surface-change");
            window.SetBootstrapPointerCapture(
                PreProviderAboutInputPolicy.ShouldCaptureWindowHitTests(
                    _contentReady,
                    _hostServer?.IsVisible == true,
                    _hostSurfaceMode,
                    _hostServer?.IsConnected == true));
            var generation = ++_hostSurfaceGeneration;
            _webViewInitializerReadyGeneration = 0;
            ResetExternalInitializerReadiness();
            _hostServer?.PublishSurfaceMode(_hostSurfaceMode);
            var payload = new JObject
            {
                ["mode"] = _hostSurfaceMode,
                ["generation"] = generation,
                ["edition"] = ResolveGameEdition(),
                ["gameVersion"] = ResolveGameVersion(),
            };
            var presentationHandoff =
                string.Equals(_hostSurfaceMode, HostSurfaceMode.None, StringComparison.Ordinal) &&
                string.Equals(
                    handoff,
                    HostSurfaceIntentPolicy.PresentationHandoff,
                    StringComparison.Ordinal);
            if (presentationHandoff)
                payload["handoff"] = HostSurfaceIntentPolicy.PresentationHandoff;
            PostBrowserJson(
                window,
                BridgeProtocol.SerializeEvent("host.surface", payload));
            Program.Trace(
                _logDirectory,
                "bootstrap_host_surface_published",
                $"mode={_hostSurfaceMode} generation={generation} " +
                $"handoff={(presentationHandoff ? HostSurfaceIntentPolicy.PresentationHandoff : "none")}");
            return generation;
        }

        private void RetireBootstrapSurface(OverlayWindow window, bool hide)
        {
            CancelPendingHostSurfaceReveal("provider-retire");
            _aboutInputSampler?.ResetBoundary("provider-retire");
            window.SetBootstrapPointerCapture(false);

            if (hide)
            {
                // This is a managed ownership transition, not a user close.
                // Keep a pending default-menu intent intact while removing the
                // loading surface as soon as Story mode becomes usable.
                RequestHostSurface(window, "none", false);
            }
            else
            {
                // A typed presentation follows this command on the same FIFO
                // provider pipe. Clear only the external logical identity so
                // the already-painted initializer remains as a transition
                // frame until React receives the replacement presentation.
                // The native overlay must cross the same identity boundary:
                // otherwise an initializer pixel probe superseded by the menu
                // can leave OverlayWindow believing it still owns an
                // initializing surface and incorrectly withhold the accepted
                // provider reveal for lack of initializer proof.
                PostHostSurface(
                    window,
                    "none",
                    HostSurfaceIntentPolicy.RetirementHandoff(hide));
            }

            Program.Trace(
                _logDirectory,
                "bootstrap_surface_retired",
                $"hide={hide} pid={_targetProcess?.Id ?? 0}");
        }

        private void RequestHostSurface(
            OverlayWindow window,
            string mode,
            bool visible)
        {
            var preserveCurrentPaint =
                HostSurfaceIntentPolicy.ShouldPreserveVisibleSurfaceDuringPromotion(
                    _hostServer?.IsVisible == true,
                    _hostSurfaceMode,
                    mode);
            var generation = PostHostSurface(window, mode);
            if (!visible)
            {
                CancelPendingHostSurfaceReveal("surface-hidden");
                window.SetBootstrapPointerCapture(false);
                SetBrowserVisible(window, false);
                return;
            }

            _pendingHostSurfaceGeneration = generation;
            _pendingHostSurfaceMode = _hostSurfaceMode;
            _pendingHostSurfaceExpiresAt =
                _lifetime.Elapsed + HostSurfaceReadyDeadline;
            // Verification is deliberately neutral, so it can remain visible
            // while React commits the authoritative About/preloader mode. An
            // explicitly opened About surface is also retained while objective
            // Story evidence promotes that same F9 intent to Initializing.
            // Other transitions still hide stale pixels until acknowledged.
            // Initializer capture requires a hidden, exclusively owned HWND
            // lease. Even when About/verifying pixels were intentionally
            // preserved through the React state transition, park them before
            // the generation-bound pixel probe begins.
            if (_requireNativePresenter || !preserveCurrentPaint ||
                HostSurfaceIntentPolicy.ShouldParkForBootstrapPixelProof(
                    _hostSurfaceMode))
                SetBrowserVisible(window, false);
            Program.Trace(
                _logDirectory,
                "bootstrap_host_surface_ready_wait",
                $"mode={_pendingHostSurfaceMode} generation={generation} " +
                $"deadline_ms={HostSurfaceReadyDeadline.TotalMilliseconds:F0} " +
                $"preserved_previous_paint={preserveCurrentPaint}");
        }

        private async void CompleteHostSurfaceReveal(
            OverlayWindow window,
            string mode,
            int generation)
        {
            if (generation != _pendingHostSurfaceGeneration ||
                !string.Equals(mode, _pendingHostSurfaceMode, StringComparison.Ordinal) ||
                !string.Equals(mode, _hostSurfaceMode, StringComparison.Ordinal))
            {
                Program.Trace(
                    _logDirectory,
                    "bootstrap_host_surface_ready_stale",
                    $"mode={mode} generation={generation} " +
                    $"expected_mode={_pendingHostSurfaceMode} " +
                    $"expected_generation={_pendingHostSurfaceGeneration}");
                return;
            }

            if (string.Equals(mode, "initializing", StringComparison.Ordinal))
            {
                if (_hostSurfacePixelVerificationGeneration == generation)
                {
                    Program.Trace(
                        _logDirectory,
                        "bootstrap_host_surface_pixel_verification_coalesced",
                        $"mode={mode} generation={generation}");
                    return;
                }
                _hostSurfacePixelVerificationGeneration = generation;
                Program.Trace(
                    _logDirectory,
                    "bootstrap_host_surface_pixel_verification_begin",
                    $"mode={mode} generation={generation}");
                bool verified;
                try
                {
                    verified = await window.VerifyBootstrapSurfacePixelsAsync(
                        mode,
                        generation);
                }
                finally
                {
                    if (_hostSurfacePixelVerificationGeneration == generation)
                        _hostSurfacePixelVerificationGeneration = 0;
                }
                if (generation != _pendingHostSurfaceGeneration ||
                    !string.Equals(mode, _pendingHostSurfaceMode, StringComparison.Ordinal) ||
                    !string.Equals(mode, _hostSurfaceMode, StringComparison.Ordinal))
                {
                    Program.Trace(
                        _logDirectory,
                        "bootstrap_host_surface_pixel_verification_stale",
                        $"mode={mode} generation={generation} " +
                        $"expected_mode={_pendingHostSurfaceMode} " +
                        $"expected_generation={_pendingHostSurfaceGeneration}");
                    return;
                }
                if (!verified)
                {
                    CancelPendingHostSurfaceReveal("surface-pixels-unverified");
                    window.SetBootstrapPointerCapture(false);
                    SetBrowserVisible(window, false);
                    _initializerOpeningEdgePending = false;
                    // The verifier already performed its single bounded root-
                    // rebind retry. Return to an idle generation so the next
                    // user edge can request a fresh initializer rather than
                    // being consumed by an invisible logically-open surface.
                    PostHostSurface(window, "none");
                    Program.Trace(
                        _logDirectory,
                        "bootstrap_host_surface_pixel_verification_failed",
                        $"mode={mode} generation={generation} visible=False " +
                        "retry=exhausted next_surface=none");
                    return;
                }
                Program.Trace(
                    _logDirectory,
                    "bootstrap_host_surface_pixel_verification_passed",
                    $"mode={mode} generation={generation}");
                _webViewInitializerReadyGeneration = generation;
            }

            if (IsNativeBootstrapSurface(mode)) _webViewInitializerReadyGeneration = generation;
            TryCompleteHostSurfaceReveal(
                window,
                mode,
                generation,
                "webview-surface-ready");
        }

        private void BeginExternalInitializerRefresh(
            OverlayWindow window,
            string mode,
            int generation,
            string trigger)
        {
            if (!IsNativeBootstrapSurface(mode) ||
                generation <= 0 ||
                generation != _pendingHostSurfaceGeneration ||
                generation != _hostSurfaceGeneration ||
                !string.Equals(mode, _pendingHostSurfaceMode, StringComparison.Ordinal) ||
                !string.Equals(mode, _hostSurfaceMode, StringComparison.Ordinal))
            {
                Program.Trace(
                    _logDirectory,
                    "external_gpu_initializer_surface_ready_stale",
                    $"trigger={trigger} mode={mode} generation={generation} " +
                    $"expected_mode={_pendingHostSurfaceMode} " +
                    $"expected_generation={_pendingHostSurfaceGeneration}");
                return;
            }

            _externalInitializerAckGeneration = generation;
            if (!ShouldUseExternalInitializerPresenter())
            {
                Program.Trace(
                    _logDirectory,
                    "external_gpu_initializer_surface_ready_fallback",
                    $"trigger={trigger} mode={mode} generation={generation} " +
                    "presenter=webview");
                TryCompleteHostSurfaceReveal(
                    window,
                    mode,
                    generation,
                    "external-initializer-fallback");
                return;
            }

            var session = _externalGpuBrowserSession;
            session?.SetVisible(false);
            if (!TrySynchronizeExternalGpuSurfaceSize(window, trigger))
            {
                Program.Trace(
                    _logDirectory,
                    "external_gpu_initializer_surface_sync_deferred",
                    $"trigger={trigger} mode={mode} generation={generation} " +
                    $"fallback_to_webview={_externalPresentationFallbackToWebView}");
                if (_externalPresentationFallbackToWebView)
                {
                    TryCompleteHostSurfaceReveal(
                        window,
                        mode,
                        generation,
                        "external-initializer-resize-fallback");
                }
                return;
            }

            _externalInitializerRefreshGeneration = generation;
            _externalInitializerFreshGeneration = 0;
            var accepted = session?.IsActive == true &&
                session.RefreshPresentation();
            if (!accepted)
                _externalPresentationFallbackToWebView = true;
            Program.Trace(
                _logDirectory,
                accepted
                    ? "external_gpu_initializer_fresh_frame_requested"
                    : "external_gpu_initializer_fresh_frame_rejected",
                $"trigger={trigger} mode={mode} generation={generation} " +
                $"accepted={accepted} fallback_to_webview=" +
                $"{_externalPresentationFallbackToWebView}");
            if (!accepted)
            {
                TryCompleteHostSurfaceReveal(
                    window,
                    mode,
                    generation,
                    "external-initializer-refresh-fallback");
            }
        }

        private void ObserveExternalInitializerReadiness(
            OverlayWindow window,
            bool ready,
            int width,
            int height)
        {
            var generation = _externalInitializerRefreshGeneration;
            if (!ready || generation <= 0 ||
                generation != _pendingHostSurfaceGeneration ||
                generation != _hostSurfaceGeneration ||
                !IsNativeBootstrapSurface(_pendingHostSurfaceMode) ||
                _externalInitializerAckGeneration != generation)
            {
                return;
            }

            var session = _externalGpuBrowserSession;
            var targetSizeResolved = TryResolveExternalGpuSurfaceSize(
                window,
                out var targetWidth,
                out var targetHeight);
            if (session?.IsPresentationReady != true ||
                !targetSizeResolved ||
                width != targetWidth ||
                height != targetHeight ||
                session.SurfaceWidth != targetWidth ||
                session.SurfaceHeight != targetHeight)
            {
                Program.Trace(
                    _logDirectory,
                    "external_gpu_initializer_fresh_frame_mismatch",
                    $"generation={generation} frame={width}x{height} " +
                    $"target={targetWidth}x{targetHeight} " +
                    $"session={session?.SurfaceWidth ?? 0}x" +
                    $"{session?.SurfaceHeight ?? 0}");
                return;
            }

            _externalInitializerFreshGeneration = generation;
            Program.Trace(
                _logDirectory,
                "external_gpu_initializer_fresh_frame_ready",
                $"mode={_pendingHostSurfaceMode} generation={generation} " +
                $"surface={width}x{height}");
            TryCompleteHostSurfaceReveal(
                window,
                _pendingHostSurfaceMode,
                generation,
                "external-initializer-fresh-frame");
        }

        private bool TryCompleteHostSurfaceReveal(
            OverlayWindow window,
            string mode,
            int generation,
            string trigger)
        {
            if (generation <= 0 ||
                generation != _pendingHostSurfaceGeneration ||
                generation != _hostSurfaceGeneration ||
                !string.Equals(mode, _pendingHostSurfaceMode, StringComparison.Ordinal) ||
                !string.Equals(mode, _hostSurfaceMode, StringComparison.Ordinal))
            {
                return false;
            }

            if (IsNativeBootstrapSurface(mode))
            {
                if (_webViewInitializerReadyGeneration != generation)
                    return false;
                var failClosedInitializerFallback =
                    _options.ExternalGpuBrowserShadow &&
                    !_options.BootstrapHarnessWebViewPresenter;
                if (failClosedInitializerFallback &&
                    !ShouldUseExternalInitializerPresenter())
                {
                    Program.Trace(
                        _logDirectory,
                        "external_gpu_initializer_reveal_withheld",
                        $"trigger={trigger} generation={generation} " +
                        $"external_gpu_active=" +
                        $"{_externalGpuBrowserSession?.IsActive == true} " +
                        $"fallback_to_webview=" +
                        $"{_externalPresentationFallbackToWebView} " +
                        "action=fail-closed-hide");
                    window.SetBootstrapPointerCapture(false);
                    SetBrowserVisible(
                        window,
                        visible: false,
                        trigger: "initializer-native-presenter-unavailable");
                    return false;
                }
                if (ShouldUseExternalInitializerPresenter())
                {
                    var session = _externalGpuBrowserSession;
                    if (_externalInitializerAckGeneration != generation ||
                        _externalInitializerRefreshGeneration != generation ||
                        _externalInitializerFreshGeneration != generation ||
                        session?.IsPresentationReady != true ||
                        !TryResolveExternalGpuSurfaceSize(
                            window,
                            out var targetWidth,
                            out var targetHeight) ||
                        session.SurfaceWidth != targetWidth ||
                        session.SurfaceHeight != targetHeight)
                    {
                        Program.Trace(
                            _logDirectory,
                            "external_gpu_initializer_reveal_wait",
                            $"trigger={trigger} generation={generation} " +
                            $"webview_ready={_webViewInitializerReadyGeneration == generation} " +
                            $"external_ack={_externalInitializerAckGeneration == generation} " +
                            $"external_refresh=" +
                            $"{_externalInitializerRefreshGeneration == generation} " +
                            $"external_fresh={_externalInitializerFreshGeneration == generation} " +
                            $"presentation_ready={session?.IsPresentationReady == true}");
                        return false;
                    }
                }
            }

            CancelPendingHostSurfaceReveal("surface-ready");
            _hostSurfaceReadyRecoveryMode = "none";
            _hostSurfaceReadyRecoveryAttempts = 0;
            Program.Trace(
                _logDirectory,
                "bootstrap_host_surface_ready",
                $"mode={mode} generation={generation} trigger={trigger} " +
                $"presenter={(ShouldUseExternalInitializerPresenter() ? "external-gpu" : "webview")}");
            window.SetBootstrapPointerCapture(
                PreProviderAboutInputPolicy.ShouldCaptureWindowHitTests(
                    _contentReady,
                    visible: true,
                    _hostSurfaceMode,
                    _hostServer?.IsConnected == true));
            SetBrowserVisible(window, true, trigger);
            return true;
        }

        private bool ShouldUseExternalInitializerPresenter() =>
            !_options.BootstrapHarnessWebViewPresenter &&
            !_externalPresentationFallbackToWebView &&
            _externalGpuBrowserSession?.IsActive == true &&
            IsNativeBootstrapSurface(_hostSurfaceMode);

        private bool IsNativeBootstrapSurface(string? mode) =>
            ExclusiveBrowserPresentationPolicy.IsNativeBootstrapSurface(mode, _requireNativePresenter);

        private void ResetExternalInitializerReadiness()
        {
            _externalInitializerAckGeneration = 0;
            _externalInitializerRefreshGeneration = 0;
            _externalInitializerFreshGeneration = 0;
        }

        private void HandleHostSurfaceReadyDeadline()
        {
            var window = _hostWindow;
            if (window == null || window.IsDisposed ||
                _pendingHostSurfaceGeneration <= 0)
            {
                CancelPendingHostSurfaceReveal("surface-ready-deadline-no-window");
                return;
            }

            var mode = _pendingHostSurfaceMode;
            var generation = _pendingHostSurfaceGeneration;
            if (string.Equals(
                    mode,
                    _hostSurfaceReadyRecoveryMode,
                    StringComparison.Ordinal))
            {
                _hostSurfaceReadyRecoveryAttempts++;
            }
            else
            {
                _hostSurfaceReadyRecoveryMode = mode;
                _hostSurfaceReadyRecoveryAttempts = 1;
            }

            var attempt = _hostSurfaceReadyRecoveryAttempts;
            Program.Trace(
                _logDirectory,
                "bootstrap_host_surface_ready_timeout",
                $"mode={mode} generation={generation} " +
                $"deadline_ms={HostSurfaceReadyDeadline.TotalMilliseconds:F0} " +
                $"recovery_attempt={attempt} maximum=1 " +
                "action=hide-and-new-generation input_enabled=False");
            CancelPendingHostSurfaceReveal("surface-ready-timeout");
            window.SetBootstrapPointerCapture(false);
            SetBrowserVisible(window, false);
            PostHostSurface(window, HostSurfaceMode.None);

            if (attempt <= 1 &&
                !string.Equals(mode, HostSurfaceMode.None, StringComparison.Ordinal))
            {
                RequestHostSurface(window, mode, visible: true);
                return;
            }

            _initializerOpeningEdgePending = false;
            _hostSurfaceReadyRecoveryMode = "none";
            _hostSurfaceReadyRecoveryAttempts = 0;
            Program.Trace(
                _logDirectory,
                "bootstrap_host_surface_ready_abandoned",
                $"mode={mode} generation={generation} visible=False " +
                "requires_fresh_toggle=True");
        }

        private void CancelPendingHostSurfaceReveal(string reason)
        {
            if (_pendingHostSurfaceGeneration > 0)
            {
                Program.Trace(
                    _logDirectory,
                    "bootstrap_host_surface_ready_cancelled",
                    $"mode={_pendingHostSurfaceMode} " +
                    $"generation={_pendingHostSurfaceGeneration} reason={reason}");
            }
            _pendingHostSurfaceGeneration = 0;
            _pendingHostSurfaceMode = "none";
            _pendingHostSurfaceExpiresAt = TimeSpan.Zero;
        }

        private void ArmDefaultMenuIntent()
        {
            if (_defaultMenuIntent == null) return;
            if (!PreloadHandoff.TryArmDefaultMenuIntent(
                    _options.ParentProcessId!.Value))
            {
                Program.Trace(
                    _logDirectory,
                    "default_menu_intent_arm_failed",
                    $"pid={_targetProcess?.Id ?? 0}");
                return;
            }
            _defaultMenuIntentExpiresAt = _lifetime.Elapsed + DefaultMenuIntentLifetime;
            _hostServer?.PublishDefaultMenuIntentState(
                requested: true,
                deadlineUtc: DateTime.UtcNow + DefaultMenuIntentLifetime);
            Program.Trace(
                _logDirectory,
                "default_menu_intent_armed",
                $"pid={_targetProcess?.Id ?? 0} " +
                $"deadline_ms={DefaultMenuIntentLifetime.TotalMilliseconds:F0}");
        }

        private void CancelDefaultMenuIntent(string reason)
        {
            if (_defaultMenuIntent == null) return;
            _initializerOpeningEdgePending = false;
            PreloadHandoff.TryCancelDefaultMenuIntent(
                _options.ParentProcessId!.Value);
            _defaultMenuIntentExpiresAt = TimeSpan.Zero;
            _hostServer?.PublishDefaultMenuIntentState(
                requested: false,
                deadlineUtc: null);
            Program.Trace(
                _logDirectory,
                "default_menu_intent_cancelled",
                $"pid={_targetProcess?.Id ?? 0} reason={reason}");
        }

        private void CompleteDefaultMenuIntentClaim()
        {
            if (_defaultMenuIntentExpiresAt == TimeSpan.Zero)
                return;

            _defaultMenuIntentExpiresAt = TimeSpan.Zero;
            _initializerOpeningEdgePending = false;
            _hostServer?.PublishDefaultMenuIntentState(
                requested: false,
                deadlineUtc: null);
            Program.Trace(
                _logDirectory,
                "default_menu_intent_claimed",
                $"pid={_targetProcess?.Id ?? 0} action=expiry-disarmed");
        }

        private void RefreshDefaultMenuIntent()
        {
            if (_defaultMenuIntentExpiresAt == TimeSpan.Zero)
            {
                return;
            }

            _defaultMenuIntentExpiresAt =
                _lifetime.Elapsed + DefaultMenuIntentLifetime;
            _hostServer?.PublishDefaultMenuIntentState(
                requested: true,
                deadlineUtc: DateTime.UtcNow + DefaultMenuIntentLifetime);
            Program.Trace(
                _logDirectory,
                "default_menu_intent_deadline_refreshed",
                $"pid={_targetProcess?.Id ?? 0} " +
                $"lifetime_ms={DefaultMenuIntentLifetime.TotalMilliseconds:F0}");
        }

        private void ExpireDefaultMenuIntent(bool hideInitializer)
        {
            CancelDefaultMenuIntent("deadline-expired");
            var window = _hostWindow;
            if (!hideInitializer || window == null)
            {
                return;
            }

            // A no-activate initializer cannot rely on DOM keyboard focus.
            // Once its typed intent expires, remove both the logical surface
            // and its pixels so a never-ready provider cannot strand it.
            RequestHostSurface(window, "none", false);
            Program.Trace(
                _logDirectory,
                "default_menu_intent_expired_hidden",
                $"pid={_targetProcess?.Id ?? 0}");
        }

        private string? ResolveGameEdition()
        {
            var processName = _targetProcess?.ProcessName ?? _options.WaitForProcess ?? string.Empty;
            if (processName.IndexOf("Enhanced", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Enhanced";
            }
            if (processName.IndexOf("GTA5", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Legacy";
            }
            return null;
        }

        private string? ResolveGameVersion()
        {
            try
            {
                var value = _targetProcess?.MainModule?.FileVersionInfo.FileVersion;
                return Version.TryParse(value, out var parsed) ? parsed.ToString() : null;
            }
            catch (Exception error) when (
                error is InvalidOperationException ||
                error is System.ComponentModel.Win32Exception ||
                error is NotSupportedException)
            {
                return null;
            }
        }

        private void PostHostProvider(
            OverlayWindow window,
            bool connected,
            int sessionGeneration)
        {
            PostBrowserJson(window, BridgeProtocol.SerializeEvent(
                "host.provider",
                new JObject
                {
                    ["connected"] = connected,
                    ["sessionGeneration"] = Math.Max(0, sessionGeneration),
                }));
        }

        private void PostBrowserJson(OverlayWindow window, string json)
        {
            // WebView2 remains the bridge/readiness authority. The optional
            // producer receives the exact same already-authorized stream and
            // cannot create a parallel BootstrapOverlayServer or registry;
            // presentation ownership is resolved independently below.
            window.PostJson(json);
            _externalGpuBrowserSession?.PostJson(json);
        }

        private void PostBrowserRoles(OverlayWindow window, bool externalGpuActive)
        {
            window.PostJson(BridgeProtocol.SerializeEvent(
                "host.browserRole",
                new JObject
                {
                    ["role"] = externalGpuActive ? "webview-host" : "primary",
                }));
            if (externalGpuActive)
            {
                _externalGpuBrowserSession?.PostJson(BridgeProtocol.SerializeEvent(
                    "host.browserRole",
                    new JObject { ["role"] = "gpu-renderer" }));
            }
        }

        private void SetBrowserVisible(
            OverlayWindow window,
            bool visible,
            string trigger = "visibility-request")
        {
            var requestedVisible = visible;
            var providerConnected = _hostServer?.IsConnected == true;
            var normalizedHostSurface =
                HostSurfaceMode.Normalize(_hostSurfaceMode);
            var disconnectedSurfaceLessRequestClamped =
                visible && !providerConnected &&
                string.Equals(
                    normalizedHostSurface,
                    HostSurfaceMode.None,
                    StringComparison.Ordinal);
            if (disconnectedSurfaceLessRequestClamped)
                visible = false;
            _browserPresentationRequestedVisible = visible;
            var externalGpuActive =
                _externalGpuBrowserSession?.IsActive == true;
            var externalGpuPresentationEligible =
                externalGpuActive && !_externalPresentationFallbackToWebView;
            var providerMenuRequested = visible &&
                externalGpuPresentationEligible &&
                providerConnected &&
                string.Equals(
                    normalizedHostSurface,
                    HostSurfaceMode.None,
                    StringComparison.Ordinal);
            var externalInitializerRequested = visible &&
                externalGpuPresentationEligible &&
                !_options.BootstrapHarnessWebViewPresenter &&
                IsNativeBootstrapSurface(normalizedHostSurface);
            var externalPresentationRequested =
                providerMenuRequested || externalInitializerRequested;
            var externalSurfaceSynchronized = !externalPresentationRequested ||
                TrySynchronizeExternalGpuSurfaceSize(window, trigger);
            if (providerMenuRequested && !externalSurfaceSynchronized)
            {
                // A transient target-size lookup failure keeps the WebView as
                // the one visible presenter for this arbitration edge. An
                // explicit Resize rejection also sets the persistent fallback
                // flag inside TrySynchronizeExternalGpuSurfaceSize.
                externalGpuPresentationEligible = false;
                providerMenuRequested = false;
            }
            if (externalInitializerRequested &&
                !externalSurfaceSynchronized &&
                _externalPresentationFallbackToWebView)
            {
                externalGpuPresentationEligible = false;
                externalInitializerRequested = false;
            }
            externalPresentationRequested =
                providerMenuRequested || externalInitializerRequested;

            var externalFrameFreshForPresentation =
                !providerMenuRequested ||
                (ProviderPresentationCommitContract.IsValidPresentationId(
                        _dualBrowserReadyPresentationId) &&
                 _dualBrowserReadyProviderSessionGeneration ==
                     Volatile.Read(ref _providerSessionGeneration) &&
                 string.Equals(
                     _externalFreshPresentationId,
                     _dualBrowserReadyPresentationId,
                     StringComparison.Ordinal));
            var externalGpuPresentationReady =
                _externalGpuBrowserSession?.IsPresentationReady == true &&
                externalFrameFreshForPresentation &&
                externalSurfaceSynchronized;
            var externalGpuBootstrapReady =
                externalInitializerRequested &&
                ExternalBootstrapPresentationGate.IsReady(
                    normalizedHostSurface,
                    _hostSurfaceGeneration,
                    _webViewInitializerReadyGeneration,
                    _externalInitializerAckGeneration,
                    _externalInitializerRefreshGeneration,
                    _externalInitializerFreshGeneration,
                    _externalGpuBrowserSession?.IsPresentationReady == true,
                    externalSurfaceSynchronized,
                    includeInteractiveBootstrap: _requireNativePresenter);
            var externalProviderReplacementPending =
                ProviderPresentationCommitContract.IsValidPresentationId(
                    _externalReplacementPresentationId) &&
                string.Equals(
                    _externalReplacementPresentationId,
                    _dualBrowserReadyPresentationId,
                    StringComparison.Ordinal) &&
                _dualBrowserReadyProviderSessionGeneration ==
                    Volatile.Read(ref _providerSessionGeneration);
            var externalProviderReplacementReady =
                externalProviderReplacementPending &&
                externalGpuPresentationReady;
            var decision = ExclusiveBrowserPresentationPolicy.Resolve(
                visible,
                providerConnected,
                _hostSurfaceMode,
                externalGpuPresentationEligible,
                externalGpuPresentationReady,
                externalInitializerRequested,
                externalGpuBootstrapReady,
                externalProviderReplacementPending,
                externalProviderReplacementReady,
                _browserPresentation.Owner,
                failClosedInitializerFallback:
                    _options.ExternalGpuBrowserShadow &&
                    !_options.BootstrapHarnessWebViewPresenter,
                requireNativePresenter: _requireNativePresenter);
            if (_options.BootstrapHarnessWebViewPresenter &&
                visible && externalGpuActive && externalGpuPresentationReady &&
                _hostServer?.IsConnected == true &&
                string.Equals(
                    HostSurfaceMode.Normalize(_hostSurfaceMode),
                    HostSurfaceMode.None,
                    StringComparison.Ordinal))
            {
                // The packaged bootstrap-host visual harness observes the
                // integrated WebView HWND. Keep external CEF alive as the
                // required readiness, resize, and message-mirroring shadow,
                // but let WebView remain the sole pixel presenter for this
                // synthetic-host-only run. Production never passes this
                // command-line switch and therefore retains exclusive native
                // presentation for connected provider menus.
                decision = new BrowserPresentationDecision(
                    BrowserPresentationOwner.WebViewBootstrap,
                    webViewVisible: true,
                    externalGpuVisible: false,
                    reason: "bootstrap-harness-shadow-presenter");
            }

            // Publish the decision before mutating either presenter. If hiding
            // an already-visible WebView raises its callback synchronously, the
            // callback must preserve the external presenter's logical visible
            // lease rather than reporting a false close to the provider.
            var previousPresentation = _browserPresentation;
            _browserPresentation = decision;

            // Always park the non-owner before revealing the selected owner.
            // This ordering prevents even a transient frame where both the
            // topmost WebView HWND and the in-game compositor publish pixels.
            if (decision.ExternalGpuVisible)
            {
                var transitioningToExternal =
                    !previousPresentation.ExternalGpuVisible;
                if (transitioningToExternal)
                {
                    _externalGpuBrowserSession?.SetVisible(false);
                    window.SetExternalPresentationOwnership(true);
                    // This is logical desired visibility only. OverlayWindow's
                    // external-owner gate keeps its HWND physically parked.
                    window.SetOverlayVisible(true);
                }
                _externalGpuBrowserSession?.SetVisible(true);
            }
            else if (externalPresentationRequested &&
                !(_requireNativePresenter && !externalGpuPresentationEligible) &&
                !_options.BootstrapHarnessWebViewPresenter)
            {
                // A fresh-frame readiness callback is queued back through this
                // STA before native output may be enabled. Keeping wrapper
                // desired visibility false here prevents its producer event
                // from auto-promoting the texture ahead of the session, size,
                // and exact-presentation-ID checks above.
                _externalGpuBrowserSession?.SetVisible(false);
                window.SetExternalPresentationOwnership(true);
                window.SetOverlayVisible(true);
            }
            else if (decision.WebViewVisible)
            {
                _externalGpuBrowserSession?.SetVisible(false);
                window.SetExternalPresentationOwnership(false);
                window.SetOverlayVisible(true);
            }
            else
            {
                _externalGpuBrowserSession?.SetVisible(false);
                // Set the explicit close before releasing ownership so the
                // WebView cannot transiently reveal during the hand-back.
                window.SetOverlayVisible(false);
                window.SetExternalPresentationOwnership(false);
            }

            // WebView2 still owns the bridge and both documents remain in the
            // existing presentation-ready barrier. This value describes the
            // player-visible presenter, not which browser may acknowledge or
            // transport authorized messages.
            _hostServer?.PublishVisibility(decision.IsVisible);
            Program.Trace(
                _logDirectory,
                "exclusive_presenter_selected",
                $"trigger={trigger} requested_visible={requestedVisible} " +
                $"effective_visible={visible} " +
                $"owner={decision.OwnerTraceValue} reason={decision.Reason} " +
                $"surface={normalizedHostSurface} " +
                $"provider_connected={providerConnected} " +
                $"disconnected_surface_less_clamped=" +
                $"{disconnectedSurfaceLessRequestClamped} " +
                $"external_gpu_active={externalGpuActive} " +
                $"external_gpu_eligible={externalGpuPresentationEligible} " +
                $"external_gpu_ready={externalGpuPresentationReady} " +
                $"external_gpu_fresh={externalFrameFreshForPresentation} " +
                $"external_gpu_bootstrap_requested={externalInitializerRequested} " +
                $"external_gpu_bootstrap_ready={externalGpuBootstrapReady} " +
                $"external_gpu_replacement_pending=" +
                $"{externalProviderReplacementPending} " +
                $"external_gpu_replacement_ready=" +
                $"{externalProviderReplacementReady} " +
                $"external_gpu_size_synchronized={externalSurfaceSynchronized} " +
                $"fallback_to_webview={_externalPresentationFallbackToWebView} " +
                $"webview_visible={decision.WebViewVisible} " +
                $"external_gpu_visible={decision.ExternalGpuVisible} " +
                "bridge_authority=webview dual_readiness=preserved");
            if (decision.Owner == BrowserPresentationOwner.ExternalGpuProvider)
            {
                if (externalProviderReplacementReady)
                    _externalReplacementPresentationId = null;
                TryCommitExternalProviderPresentation(window, trigger);
            }
        }

        private void RetireExternalProviderProof(string reason)
        {
            // Only an authoritative close/cancellation boundary retires exact
            // provider evidence. Internal arbitration is allowed to remain
            // hidden while a newly prepared presentation renders its fresh
            // frame; clearing these fields from SetBrowserVisible(false)
            // would discard that proof before the later reveal request.
            var presentationId = _dualBrowserReadyPresentationId;
            _dualBrowserReadyPresentationId = null;
            _dualBrowserReadyProviderSessionGeneration = 0;
            _awaitingExternalPostAcceptPaintPresentationId = null;
            _awaitingExternalPostAcceptPaintProviderSessionGeneration = 0;
            _externalFreshPresentationId = null;
            _externalCommittedPresentationId = null;
            _externalReplacementPresentationId = null;
            _hiddenExternalPreparationPresentationId = null;
            _queuedExternalReplacementPresentationId = null;
            _queuedExternalReplacementProviderSessionGeneration = 0;
            Program.Trace(
                _logDirectory,
                "external_gpu_provider_proof_retired",
                $"reason={reason} presentation={presentationId ?? "none"}");
        }

        private static bool TryResolveExternalGpuSurfaceSize(
            OverlayWindow window,
            out int width,
            out int height) =>
            window.TryGetTargetClientSize(out width, out height) &&
            width > 0 && height > 0;

        private void TrySynchronizeExternalGpuSurfaceFromTargetWindow(
            TargetWindowLifecycleState state,
            string reason)
        {
            var session = _externalGpuBrowserSession;
            var window = _hostWindow;
            if (window == null || session?.IsActive != true ||
                !state.Exists || state.ClientWidth <= 0 ||
                state.ClientHeight <= 0 ||
                (session.SurfaceWidth == state.ClientWidth &&
                 session.SurfaceHeight == state.ClientHeight))
            {
                return;
            }

            // The lifecycle probe already resolved the authoritative GTA HWND
            // and its client rectangle. Apply that size while CEF is still
            // hidden so the first initializer/provider reveal cannot trigger a
            // late fallback-surface resize on the presentation boundary.
            TrySynchronizeExternalGpuSurfaceSize(
                window,
                "target-window-lifecycle-" + reason,
                state.ClientWidth,
                state.ClientHeight);
        }

        private bool TrySynchronizeExternalGpuSurfaceSize(
            OverlayWindow window,
            string trigger,
            int knownWidth = 0,
            int knownHeight = 0)
        {
            var session = _externalGpuBrowserSession;
            var width = knownWidth;
            var height = knownHeight;
            var knownTargetSize = width > 0 && height > 0;
            if (session?.IsActive != true ||
                (!knownTargetSize &&
                 !TryResolveExternalGpuSurfaceSize(window, out width, out height)))
            {
                Program.Trace(
                    _logDirectory,
                    "external_gpu_surface_sync_deferred",
                    $"trigger={trigger} reason=" +
                    $"{(session?.IsActive == true ? "target-size-unavailable" : "session-inactive")}");
                return false;
            }

            if (session.SurfaceWidth == width && session.SurfaceHeight == height)
                return true;

            var cancelledReplacementPresentationId =
                _externalReplacementPresentationId;
            var cancelledQueuedPresentationId =
                _queuedExternalReplacementPresentationId;
            var cancelledHiddenPreparationPresentationId =
                _hiddenExternalPreparationPresentationId;
            if (ProviderPresentationCommitContract.IsValidPresentationId(
                    cancelledReplacementPresentationId) ||
                ProviderPresentationCommitContract.IsValidPresentationId(
                    cancelledQueuedPresentationId) ||
                ProviderPresentationCommitContract.IsValidPresentationId(
                    cancelledHiddenPreparationPresentationId))
            {
                Program.Trace(
                    _logDirectory,
                    "external_gpu_replacement_aborted_for_resize",
                    $"trigger={trigger} presentation=" +
                    $"{cancelledReplacementPresentationId ?? "none"} " +
                    $"queued_presentation=" +
                    $"{cancelledQueuedPresentationId ?? "none"} " +
                    $"hidden_preparation=" +
                    $"{cancelledHiddenPreparationPresentationId ?? "none"} " +
                    $"requested={width}x{height} " +
                    $"current={session.SurfaceWidth}x{session.SurfaceHeight}");
                // Resize cannot retain the old-dimension texture. Clear the
                // retained-owner contract and every older queued identity
                // before hiding it so arbitration reports owner=None until an
                // exact-size frame is ready. A later direct presentation must
                // never be replaced by a pre-resize queued generation.
                // The old refresh proof is identity-bound as well: after C
                // supersedes A in the browser, a resize frame may contain C
                // pixels even though the preceding retained refresh still
                // names A. Invalidate the entire proof tuple so that frame
                // cannot commit A while waiting for the next exact-ID event.
                _dualBrowserReadyPresentationId = null;
                _dualBrowserReadyProviderSessionGeneration = 0;
                _externalFreshPresentationId = null;
                _externalCommittedPresentationId = null;
                _hiddenExternalPreparationPresentationId = null;
            }
            _externalReplacementPresentationId = null;
            _queuedExternalReplacementPresentationId = null;
            _queuedExternalReplacementProviderSessionGeneration = 0;

            // Prevent the wrapper's readiness observer from auto-promoting a
            // resized texture before the STA has rechecked the target size,
            // provider session, and exact presentation identity.
            session.SetVisible(false);
            var previousWidth = session.SurfaceWidth;
            var previousHeight = session.SurfaceHeight;
            var accepted = session.Resize(width, height);
            if (!accepted)
                _externalPresentationFallbackToWebView = true;
            Program.Trace(
                _logDirectory,
                "external_gpu_surface_sync_requested",
                $"trigger={trigger} target={width}x{height} " +
                $"previous={previousWidth}x{previousHeight} " +
                $"accepted={accepted} " +
                $"fallback_to_webview=" +
                $"{_externalPresentationFallbackToWebView}");
            return accepted;
        }

        private bool TryRecordDualBrowserPresentationReady(
            string presentationId,
            int providerSessionGeneration)
        {
            var currentProviderSessionGeneration =
                Volatile.Read(ref _providerSessionGeneration);
            if (!ProviderPresentationCommitContract.IsValidPresentationId(
                    presentationId) ||
                _hostServer?.IsConnected != true ||
                providerSessionGeneration != currentProviderSessionGeneration)
            {
                Program.Trace(
                    _logDirectory,
                    "external_gpu_provider_exact_id_stale",
                    $"presentation={presentationId} " +
                    $"provider_session_generation=" +
                    $"{providerSessionGeneration} " +
                    $"current_provider_session_generation=" +
                    $"{currentProviderSessionGeneration} " +
                    $"provider_connected={_hostServer?.IsConnected == true}");
                return false;
            }

            _awaitingExternalPostAcceptPaintPresentationId = presentationId;
            _awaitingExternalPostAcceptPaintProviderSessionGeneration =
                providerSessionGeneration;
            Program.Trace(
                _logDirectory,
                "external_gpu_provider_exact_id_awaiting_post_accept_paint",
                $"presentation={presentationId} " +
                $"provider_session_generation={providerSessionGeneration}");
            return true;
        }

        private void ContinueExternalProviderPresentationAfterPaint(
            OverlayWindow window,
            string presentationId,
            int providerSessionGeneration,
            string trigger)
        {
            var externalSession = _externalGpuBrowserSession;
            if (ExclusiveBrowserPresentationPolicy.
                ShouldQueueRapidReplacement(
                    ProviderPresentationCommitContract.
                        IsValidPresentationId(
                            _externalReplacementPresentationId),
                    externalSession?.IsActive == true,
                    externalSession?.SupportsRetainedPresentationRefresh == true,
                    externalSession?.IsPresentationReady == true,
                    _browserPresentation.ExternalGpuVisible))
            {
                // Coalesce to the newest exact presentation. The in-flight
                // retained refresh keeps the last qualified native texture
                // visible until the new accepted browser pixels are ready.
                _queuedExternalReplacementPresentationId = presentationId;
                _queuedExternalReplacementProviderSessionGeneration =
                    providerSessionGeneration;
                Program.Trace(
                    _logDirectory,
                    "external_gpu_provider_replacement_queued",
                    $"presentation={presentationId} " +
                    $"provider_session_generation={providerSessionGeneration} " +
                    $"active_replacement={_externalReplacementPresentationId} " +
                    $"trigger={trigger}");
                return;
            }

            BeginExternalProviderPresentationRefresh(
                window,
                presentationId,
                providerSessionGeneration,
                trigger);
        }

        private void BeginExternalProviderPresentationRefresh(
            OverlayWindow window,
            string presentationId,
            int providerSessionGeneration,
            string trigger)
        {
            var supersededQueuedPresentationId =
                _queuedExternalReplacementPresentationId;
            _queuedExternalReplacementPresentationId = null;
            _queuedExternalReplacementProviderSessionGeneration = 0;
            if (ProviderPresentationCommitContract.IsValidPresentationId(
                    supersededQueuedPresentationId) &&
                !string.Equals(
                    supersededQueuedPresentationId,
                    presentationId,
                    StringComparison.Ordinal))
            {
                Program.Trace(
                    _logDirectory,
                    "external_gpu_provider_queued_replacement_superseded",
                    $"queued_presentation={supersededQueuedPresentationId} " +
                    $"newer_presentation={presentationId} " +
                    $"provider_session_generation={providerSessionGeneration}");
            }
            _dualBrowserReadyPresentationId = presentationId;
            _dualBrowserReadyProviderSessionGeneration =
                providerSessionGeneration;
            _externalFreshPresentationId = null;
            _externalCommittedPresentationId = null;
            _externalReplacementPresentationId = null;
            _hiddenExternalPreparationPresentationId = null;
            Program.Trace(
                _logDirectory,
                "external_gpu_provider_exact_id_ready",
                $"presentation={presentationId} " +
                $"provider_session_generation={providerSessionGeneration}");

            var externalSession = _externalGpuBrowserSession;
            var retainCurrentExternalFrame =
                externalSession?.IsActive == true &&
                externalSession.SupportsRetainedPresentationRefresh &&
                externalSession.IsPresentationReady &&
                _browserPresentation.ExternalGpuVisible;
            _externalReplacementPresentationId = retainCurrentExternalFrame
                ? presentationId
                : null;
            _hiddenExternalPreparationPresentationId =
                !retainCurrentExternalFrame &&
                !_browserPresentationRequestedVisible
                    ? presentationId
                    : null;
            if (externalSession?.IsActive == true &&
                !retainCurrentExternalFrame)
            {
                // Clear wrapper desired visibility before the producer can
                // publish its fresh-readiness event. STA arbitration is the
                // only authority allowed to reveal a cold replacement.
                externalSession.SetVisible(false);
            }
            var externalRefreshAccepted =
                externalSession?.IsActive == true &&
                externalSession.RefreshPresentation(
                    retainCurrentExternalFrame);
            if (!externalRefreshAccepted)
            {
                _externalReplacementPresentationId = null;
                _hiddenExternalPreparationPresentationId = null;
            }
            _externalPresentationFallbackToWebView =
                externalSession?.IsActive == true &&
                !externalRefreshAccepted;
            if (externalRefreshAccepted)
                _externalFreshPresentationId = presentationId;
            Program.Trace(
                _logDirectory,
                externalRefreshAccepted
                    ? "external_gpu_provider_fresh_frame_requested"
                    : "external_gpu_provider_fresh_frame_not_required",
                $"presentation={presentationId} " +
                $"provider_session_generation={providerSessionGeneration} " +
                $"external_gpu_active={externalSession?.IsActive == true} " +
                $"refresh_accepted={externalRefreshAccepted} " +
                $"retained_frame={retainCurrentExternalFrame} " +
                $"prepared_hidden=" +
                $"{string.Equals(_hiddenExternalPreparationPresentationId, presentationId, StringComparison.Ordinal)}");

            var revealWebViewFallback =
                !_requireNativePresenter &&
                !externalRefreshAccepted &&
                !_browserPresentationRequestedVisible;
            if (revealWebViewFallback)
            {
                // The public/Legacy host and a faulted native producer cannot
                // stage an external texture while hidden. Dual-browser-ready
                // still proves the exact WebView document is prepared, so let
                // the WebView run its existing desktop-presentation proof. The
                // managed script remains non-interactive until that exact
                // provider commit returns.
                Program.Trace(
                    _logDirectory,
                    "webview_provider_reveal_after_browser_prepare",
                    $"presentation={presentationId} " +
                    $"provider_session_generation={providerSessionGeneration}");
            }
            SetBrowserVisible(
                window,
                revealWebViewFallback ||
                    _browserPresentationRequestedVisible,
                "dual-browser-fresh-frame");
            TryCommitExternalProviderPresentation(window, trigger);
        }

        private bool TryStartQueuedExternalProviderReplacement(
            OverlayWindow window)
        {
            var presentationId = _queuedExternalReplacementPresentationId;
            var providerSessionGeneration =
                _queuedExternalReplacementProviderSessionGeneration;
            if (!ProviderPresentationCommitContract.IsValidPresentationId(
                    presentationId))
            {
                return false;
            }

            _queuedExternalReplacementPresentationId = null;
            _queuedExternalReplacementProviderSessionGeneration = 0;
            var currentProviderSessionGeneration =
                Volatile.Read(ref _providerSessionGeneration);
            if (_hostServer?.IsConnected != true ||
                providerSessionGeneration != currentProviderSessionGeneration)
            {
                Program.Trace(
                    _logDirectory,
                    "external_gpu_provider_queued_replacement_stale",
                    $"presentation={presentationId} " +
                    $"provider_session_generation={providerSessionGeneration} " +
                    $"current_provider_session_generation=" +
                    $"{currentProviderSessionGeneration}");
                return false;
            }

            BeginExternalProviderPresentationRefresh(
                window,
                presentationId!,
                providerSessionGeneration,
                "queued-dual-browser-ready");
            return true;
        }

        private void TryCommitExternalProviderPresentation(
            OverlayWindow window,
            string trigger)
        {
            var presentationId = _dualBrowserReadyPresentationId;
            var providerSessionGeneration =
                _dualBrowserReadyProviderSessionGeneration;
            var session = _externalGpuBrowserSession;
            var hostServer = _hostServer;
            var visibleProviderCommit =
                _browserPresentation.Owner ==
                    BrowserPresentationOwner.ExternalGpuProvider &&
                _browserPresentationRequestedVisible &&
                hostServer?.IsVisible == true;
            var hiddenPreparationCommit =
                _browserPresentation.Owner == BrowserPresentationOwner.None &&
                !_browserPresentationRequestedVisible &&
                hostServer?.IsVisible != true &&
                string.Equals(
                    _hiddenExternalPreparationPresentationId,
                    presentationId,
                    StringComparison.Ordinal);
            if ((!visibleProviderCommit && !hiddenPreparationCommit) ||
                session?.IsPresentationReady != true ||
                hostServer == null ||
                !ProviderPresentationCommitContract.IsValidPresentationId(
                    presentationId) ||
                !string.Equals(
                    _externalFreshPresentationId,
                    presentationId,
                    StringComparison.Ordinal) ||
                providerSessionGeneration !=
                    Volatile.Read(ref _providerSessionGeneration) ||
                string.Equals(
                    _externalCommittedPresentationId,
                    presentationId,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (!TryResolveExternalGpuSurfaceSize(
                    window,
                    out var width,
                    out var height) ||
                session.SurfaceWidth != width ||
                session.SurfaceHeight != height)
            {
                TrySynchronizeExternalGpuSurfaceSize(window, trigger);
                return;
            }

            _externalCommittedPresentationId = presentationId;
            if (hiddenPreparationCommit)
                _hiddenExternalPreparationPresentationId = null;
            var userIntentAuthorized =
                window.IsProviderPresentationAuthorizedByUserIntent(
                    presentationId!);
            hostServer.PublishProviderPresentationCommitted(
                presentationId!,
                userIntentAuthorized);
            Program.Trace(
                _logDirectory,
                "external_gpu_provider_presentation_committed",
                $"trigger={trigger} presentation={presentationId} " +
                $"provider_session_generation={providerSessionGeneration} " +
                $"surface={width}x{height} user_intent={userIntentAuthorized} " +
                $"prepared_hidden={hiddenPreparationCommit} " +
                "webview_hwnd=parked bridge_authority=webview");
        }

        private void OnWebViewVisibilityApplied(bool visible)
        {
            // A parked WebView HWND is expected while the native compositor is
            // the selected presenter. Do not let that physical hidden edge
            // revoke the provider's logical visibility lease.
            var logicalVisible = _browserPresentation.ExternalGpuVisible ||
                (_browserPresentation.WebViewVisible && visible);
            _hostServer?.PublishVisibility(logicalVisible);
            Program.Trace(
                _logDirectory,
                "exclusive_webview_visibility_observed",
                $"webview_visible={visible} logical_visible={logicalVisible} " +
                $"owner={_browserPresentation.OwnerTraceValue}");
        }

        private static void TryCancel(CancellationTokenSource? cancellation)
        {
            if (cancellation == null)
            {
                return;
            }
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race and already released its token.
            }
        }

        private static DateTime SafeStartTimeUtc(Process process)
        {
            try
            {
                return process.StartTime.ToUniversalTime();
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static bool SafeHasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        private static string SafeProcessName(Process process)
        {
            try
            {
                return process.ProcessName;
            }
            catch
            {
                return "unknown";
            }
        }
    }

    internal sealed class PreloaderSettings
    {
        [JsonProperty("processWaitTimeoutSeconds")]
        public int ProcessWaitTimeoutSeconds { get; set; } = 180;

        [JsonProperty("maximumLifetimeSeconds")]
        public int MaximumLifetimeSeconds { get; set; } = 300;

        [JsonProperty("externalGpuBrowserShadow")]
        public bool ExternalGpuBrowserShadow { get; set; } = true;

        [JsonProperty("externalGpuFrameRate")]
        public int ExternalGpuFrameRate { get; set; } = 30;

        public static PreloaderSettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return JsonConvert.DeserializeObject<PreloaderSettings>(File.ReadAllText(path))
                        ?? new PreloaderSettings();
                }
            }
            catch
            {
            }

            return new PreloaderSettings();
        }
    }

    internal sealed class PreloaderOptions
    {
        public string? WaitForProcess { get; private set; }
        public int? ParentProcessId { get; private set; }
        public string UiDirectory { get; private set; } = string.Empty;
        public string UserDataDirectory { get; private set; } = string.Empty;
        public TimeSpan ProcessWaitTimeout { get; private set; }
        public TimeSpan MaximumLifetime { get; private set; }
        public bool SelfTest { get; private set; }
        public bool CacheOnly { get; private set; }
        public bool PersistentHost { get; private set; }
        public bool ExternalGpuBrowserShadow { get; private set; }
        public int ExternalGpuFrameRate { get; private set; }
        public bool BootstrapHarnessWebViewPresenter { get; private set; }
        public string? GtaRoot { get; private set; }
        public string? CacheRootOverride { get; private set; }
        public string LogDirectory { get; private set; } = string.Empty;
        public string InstanceId { get; private set; } = "production";

        public static bool TryParse(
            string[] args,
            PreloaderSettings settings,
            string executableDirectory,
            out PreloaderOptions options,
            out string error)
        {
            options = new PreloaderOptions
            {
                UiDirectory = Path.Combine(executableDirectory, "ui"),
                UserDataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ReactorV",
                    "WebView2"),
                LogDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ReactorV"),
                ProcessWaitTimeout = TimeSpan.FromSeconds(Math.Max(5, settings.ProcessWaitTimeoutSeconds)),
                MaximumLifetime = TimeSpan.FromSeconds(Math.Max(15, settings.MaximumLifetimeSeconds)),
                ExternalGpuBrowserShadow = settings.ExternalGpuBrowserShadow,
                ExternalGpuFrameRate = Math.Max(15, Math.Min(60, settings.ExternalGpuFrameRate)),
            };
            error = string.Empty;

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    options.SelfTest = true;
                }
                else if (string.Equals(argument, "--cache-only", StringComparison.OrdinalIgnoreCase))
                {
                    options.CacheOnly = true;
                }
                else if (string.Equals(argument, "--persistent-host", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "--host", StringComparison.OrdinalIgnoreCase))
                {
                    options.PersistentHost = true;
                }
                else if (string.Equals(
                    argument,
                    "--external-gpu-browser-shadow",
                    StringComparison.OrdinalIgnoreCase))
                {
                    options.ExternalGpuBrowserShadow = true;
                }
                else if (string.Equals(
                    argument,
                    "--no-external-gpu-browser-shadow",
                    StringComparison.OrdinalIgnoreCase))
                {
                    options.ExternalGpuBrowserShadow = false;
                }
                else if (string.Equals(
                    argument,
                    "--bootstrap-harness-webview-presenter",
                    StringComparison.OrdinalIgnoreCase))
                {
                    options.BootstrapHarnessWebViewPresenter = true;
                }
                else if (string.Equals(argument, "--wait-for-process", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value))
                    {
                        error = "--wait-for-process requires a process name.";
                        return false;
                    }
                    options.WaitForProcess = NormalizeProcessName(value);
                }
                else if (string.Equals(argument, "--parent-pid", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value) ||
                        !int.TryParse(value, out var processId) || processId <= 0)
                    {
                        error = "--parent-pid requires a positive integer.";
                        return false;
                    }
                    options.ParentProcessId = processId;
                }
                else if (string.Equals(argument, "--timeout-seconds", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadPositiveSeconds(args, ref index, out var timeout))
                    {
                        error = "--timeout-seconds requires a positive integer.";
                        return false;
                    }
                    options.MaximumLifetime = timeout;
                }
                else if (string.Equals(argument, "--process-wait-timeout-seconds", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadPositiveSeconds(args, ref index, out var timeout))
                    {
                        error = "--process-wait-timeout-seconds requires a positive integer.";
                        return false;
                    }
                    options.ProcessWaitTimeout = timeout;
                }
                else if (string.Equals(argument, "--ui-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value))
                    {
                        error = "--ui-dir requires a directory.";
                        return false;
                    }
                    options.UiDirectory = Path.GetFullPath(value);
                }
                else if (string.Equals(argument, "--user-data-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value))
                    {
                        error = "--user-data-dir requires a directory.";
                        return false;
                    }
                    options.UserDataDirectory = Path.GetFullPath(value);
                }
                else if (string.Equals(argument, "--log-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value))
                    {
                        error = "--log-dir requires a directory.";
                        return false;
                    }
                    options.LogDirectory = Path.GetFullPath(value);
                }
                else if (string.Equals(argument, "--gta-root", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value))
                    {
                        error = "--gta-root requires a directory.";
                        return false;
                    }
                    options.GtaRoot = Path.GetFullPath(value);
                }
                else if (string.Equals(argument, "--cache-root", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value))
                    {
                        error = "--cache-root requires a directory.";
                        return false;
                    }
                    options.CacheRootOverride = Path.GetFullPath(value);
                }
                else if (string.Equals(argument, "--instance-id", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value) ||
                        value.Length > 64 ||
                        value.Any(character =>
                            !char.IsLetterOrDigit(character) &&
                            character != '.' && character != '_' && character != '-'))
                    {
                        error = "--instance-id requires 1-64 letters, numbers, dots, underscores, or dashes.";
                        return false;
                    }
                    options.InstanceId = value;
                }
                else
                {
                    error = "Unknown argument: " + argument;
                    return false;
                }
            }

            if (options.GtaRoot != null && !options.SelfTest && !options.CacheOnly)
            {
                error = "--gta-root is accepted only by self-test or cache-only runs.";
                return false;
            }
            if (options.CacheRootOverride != null && !options.SelfTest && !options.CacheOnly)
            {
                error = "--cache-root is accepted only by self-test or cache-only runs.";
                return false;
            }
            if (options.CacheOnly)
            {
                if (options.GtaRoot == null || !options.ParentProcessId.HasValue)
                {
                    error = "--cache-only requires --gta-root and --parent-pid.";
                    return false;
                }
                return true;
            }

            if (options.PersistentHost && !options.ParentProcessId.HasValue)
            {
                error = "--persistent-host requires --parent-pid.";
                return false;
            }
            if (options.BootstrapHarnessWebViewPresenter &&
                !options.PersistentHost)
            {
                error =
                    "--bootstrap-harness-webview-presenter requires " +
                    "--persistent-host.";
                return false;
            }

            if (!Directory.Exists(options.UiDirectory) ||
                !File.Exists(Path.Combine(options.UiDirectory, "index.html")))
            {
                error = "The local ReactorV UI is missing: " + options.UiDirectory;
                return false;
            }

            if (options.SelfTest)
            {
                return true;
            }

            if (options.ParentProcessId.HasValue == !string.IsNullOrWhiteSpace(options.WaitForProcess))
            {
                error = "Specify exactly one of --wait-for-process or --parent-pid.";
                return false;
            }

            return true;
        }

        private static bool TryReadPositiveSeconds(
            string[] args,
            ref int index,
            out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (!TryReadValue(args, ref index, out var raw) ||
                !int.TryParse(raw, out var seconds) || seconds <= 0)
            {
                return false;
            }
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        private static bool TryReadValue(string[] args, ref int index, out string value)
        {
            value = string.Empty;
            if (index + 1 >= args.Length)
            {
                return false;
            }
            value = args[++index];
            return !string.IsNullOrWhiteSpace(value);
        }

        private static string NormalizeProcessName(string processName)
        {
            var name = Path.GetFileName(processName.Trim());
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4)
                : name;
        }
    }
}
