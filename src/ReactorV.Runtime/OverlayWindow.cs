using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using ReactorV.WebView2Host;

namespace RageWebUI.Runtime
{
    public sealed class OverlayWindow : Form
    {
        private const int WmMouseActivate = 0x0021;
        private const int WmPaint = 0x000F;
        private const int WmNcHitTest = 0x0084;
        private const int WmFinalizeRevealAfterBrowserDrain = 0x8000 + 0x52A;
        private const int MaNoActivate = 3;
        private const int HtTransparent = -1;
        private const int VisibleBoundsPollMilliseconds = 250;
        private const int HiddenBoundsPollMilliseconds = 1000;
        private const int MaximumPendingMessages = 128;
        private const int BrowserPaintCaptureTimeoutMilliseconds = 750;
        private const int MaximumBootstrapPixelProbeAttempts = 2;
        private const int MaximumFinalRevealPixelProofAttempts = 3;
        private const int MaximumProviderPaintCommitAttempts = 3;
        private const int ProviderPaintRetryMilliseconds = 16;
        // The acceptance exchange requires two correlated frames inside its
        // 1500 ms outer lease. Keep each host capture substantially below half
        // that budget so the inter-frame settle and JSON receipt remain bounded.
        private const int AcceptanceCaptureTimeoutMilliseconds = 600;
        private const int DesktopPaintSettleMilliseconds = 24;
        private const int MaximumDesktopPaintSamples = 24;
        private const int DesktopPresentationProbeTimeoutMilliseconds = 900;
        private const int ExplicitUserIntentInputLeaseMilliseconds = 2500;

        private IntPtr _gtaWindow;
        private readonly uint _gtaProcessId;
        private readonly uint _reactorProcessId;
        private readonly string _uiDirectory;
        private readonly string _userDataDirectory;
        private readonly IBridgeMessageSink _broker;
        private readonly bool _enableDevTools;
        private readonly Action<string, string?> _trace;
        private readonly Action<bool> _visibilityChanged;
        private readonly Action _contentReady;
        // This callback owns the process-scoped browser-content readiness
        // generation. It must only be invoked when the document/controller is
        // actually invalidated, never because a particular native desktop
        // presentation could not be proven.
        private readonly Action _browserContentUnavailable;
        private readonly Action<string> _presentationUnavailable;
        private readonly Action<Exception> _startupFailed;
        private readonly Func<bool>? _finalRevealIngressBoundary;
        private readonly OverlayTransferStateMachine _transferState =
            new OverlayTransferStateMachine();
        private readonly ProviderInputIntentGate _providerInputIntentGate;
        private readonly System.Windows.Forms.Timer _boundsTimer;
        private CompositionWebViewHost _webView;
        private CoreWebView2? _attachedCore;
        private readonly Queue<string> _pendingMessages = new Queue<string>();
        private bool _desiredVisible;
        private bool _browserReady;
        private bool _browserContentReadinessPublished;
        private bool _actualVisible;
        private bool _visibilityPublished;
        private bool _externalPresentationOwnsPixels;
        private bool _preloadStarted;
        private bool _initialInlineNavigationPending = true;
        private string _lastVisibilitySuppression = string.Empty;
        private Rectangle _lastBounds = Rectangle.Empty;
        private Stopwatch? _initializationTimer;
        private Stopwatch? _navigationTimer;
        private bool _gameWindowResolutionTraced;
        private IntPtr _ownedGameWindow;
        private bool _gameWindowOwnerNeedsRetry;
        private bool _surfacePrepared;
        private bool _surfaceWasPreviouslyPresented;
        private bool _revealPending;
        private volatile bool _revealDeferredForIngress;
        private int _revealGeneration;
        private int _transferGeneration;
        private readonly object _revealIngressSync = new object();
        private int _pendingRevealIngress;
        private long _revealIngressEpoch;
        private long _revealRequestedAt;
        private long _revealPreparedAt;
        private readonly Dictionary<int, Action> _nativeRevealDrainCallbacks =
            new Dictionary<int, Action>();
        private int _nativeRevealDrainToken;
        private bool _pointerInputTraced;
        private bool _browserRecoveryInProgress;
        private bool _browserRecoveryQueued;
        private int _browserRecoveryAttempts;
        private int _rendererReloadAttempts;
        private string _browserRecoveryMode = string.Empty;
        private int _browserSurfaceHealthGeneration;
        private int _controllerGeneration;
        private int _attachedControllerGeneration;
        private CoreWebView2Environment? _attachedEnvironment;
        private int _attachedEnvironmentGeneration;
        private int _browserExitObservedGeneration;
        private TaskCompletionSource<bool>? _browserExitSignal;
        private string? _activeMenuPresentationId;
        private string? _activeMenuExtensionId;
        private string? _activeMenuId;
        private string? _acceptedMenuPresentationId;
        private string? _committedProviderInputPresentationId;
        private string? _publishedProviderPresentationId;
        private string? _userIntentAuthorizedProviderPresentationId;
        private int _explicitUserIntentInputLeaseGeneration;
        private int _providerInputCommitGeneration;
        private int _providerSessionGeneration;
        private string? _pendingPresentationReadyRequestId;
        private bool _recoveryPresentationPaintAcknowledged;
        private bool _recoveredSurfaceAwaitingPaint;
        private string _activeHostSurfaceMode = "none";
        private int _activeHostSurfaceGeneration;
        private string _paintAcknowledgedHostSurfaceMode = "none";
        private int _paintAcknowledgedHostSurfaceGeneration;
        private bool _bootstrapPointerCaptureRequested;
        private bool _providerPointerShieldRequested;
        private bool? _windowPointerCaptureApplied;
        private IntPtr _webViewInputParentWindow;
        private bool? _overlayTopMostApplied;
        private int _presentationPaintProbeGeneration;
        private bool _presentationPaintProbeInProgress;
        private bool _browserPresentationPixelsVerified;
        // Desktop GDI reads can block behind GTA's swap chain for tens of
        // seconds. Live diagnostics therefore use WebView2's asynchronous
        // browser-surface capture only; desktop sampling remains dead code for
        // offline comparison helpers and must never run on the host STA.
        private bool _desktopPresentationPixelsVerified;
        private int? _rootVisualRebindAttemptedSurfaceGeneration;
        private int _desktopPaintProbeGeneration;
        private string? _paintEvidencePresentationId;
        private IReadOnlyList<DesktopPaintSample>? _desktopPaintSamples;
        private int _bootstrapPaintProbeGeneration;
        private string? _bootstrapPaintProofMode;
        private int _bootstrapPaintProofSurfaceGeneration;
        private int _bootstrapPaintProofControllerGeneration;
        private int _bootstrapPaintProofWidth;
        private int _bootstrapPaintProofHeight;
        private int _bootstrapPaintProofCompositionGeneration;
        private bool _bootstrapPaintProofConcrete;
        private bool _bootstrapPaintProofGenerationMarkerMatched;
        private bool _coldHostVisibleRootPublishRequired;
        private bool _finalRevealOffscreenLeaseActive;
        private int _finalRevealOffscreenLeaseGeneration;
        private bool _finalRevealOffscreenLeaseResumeBoundsTimer;
        private int _finalRevealOffscreenLeaseControllerGeneration;
        private int _finalRevealOffscreenLeaseProviderSessionGeneration;
        private string? _finalRevealOffscreenLeasePresentationId;
        private string _finalRevealOffscreenLeaseSurfaceMode = HostSurfaceMode.None;
        private int _finalRevealOffscreenLeaseSurfaceGeneration;
        private int _finalRevealOffscreenLeaseCompositionGeneration;
        private int _finalRevealOffscreenLeaseRootVisualRevision;
        private int _finalRevealOffscreenLeaseBrowserHealthGeneration;
        private Rectangle _finalRevealOffscreenLeaseTarget = Rectangle.Empty;
        private string _finalRevealPixelFailureIdentity = string.Empty;
        private int _finalRevealPixelFailureCount;
        /// <summary>
        /// Raised only after the exact provider presentation marker has been
        /// captured from Chromium, its DirectComposition commit has completed,
        /// the external window has been promoted, and desktop presentation was
        /// independently verified. A passively visible composition-qualified
        /// surface never raises this event. Consumers must still compare the
        /// presentation ID.
        /// </summary>
        public event Action<string>? ProviderPresentationCommitted;

        public OverlayWindow(
            IntPtr gtaWindow,
            uint gtaProcessId,
            string uiDirectory,
            string userDataDirectory,
            IBridgeMessageSink broker,
            bool enableDevTools,
            bool startVisible,
            Action<string, string?> trace,
            Action<bool> visibilityChanged,
            Action contentReady,
            Action browserContentUnavailable,
            Action<Exception> startupFailed,
            Func<bool>? finalRevealIngressBoundary = null,
            Action<string>? presentationUnavailable = null)
        {
            _gtaWindow = gtaWindow;
            if (gtaProcessId == 0)
                throw new ArgumentOutOfRangeException(nameof(gtaProcessId));
            _gtaProcessId = gtaProcessId;
            _providerInputIntentGate = new ProviderInputIntentGate(
                checked((int)gtaProcessId));
            _reactorProcessId = (uint)Process.GetCurrentProcess().Id;
            _uiDirectory = uiDirectory;
            _userDataDirectory = userDataDirectory;
            _broker = broker;
            _enableDevTools = enableDevTools;
            _trace = trace;
            _visibilityChanged = visibilityChanged;
            _contentReady = contentReady;
            _browserContentUnavailable = browserContentUnavailable;
            _presentationUnavailable = presentationUnavailable ?? (_ => { });
            _startupFailed = startupFailed;
            _finalRevealIngressBoundary = finalRevealIngressBoundary;
            _desiredVisible = startVisible;

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "REACTOR V Overlay";
            Location = new Point(-32000, -32000);
            // The browser is fully hidden during preload. Allocating a full
            // 1440p/4K composition surface here competes with GTA's heaviest
            // startup work without improving cache warmth. SynchronizeBounds
            // expands it to the exact game client immediately before reveal.
            ClientSize = HiddenPreloadClientSize();
            _webView = CreateWebViewHost();
            _boundsTimer = new System.Windows.Forms.Timer
            {
                Interval = startVisible
                    ? VisibleBoundsPollMilliseconds
                    : HiddenBoundsPollMilliseconds,
            };
            _boundsTimer.Tick += (_, __) => SynchronizeBounds();
            FormClosed += (_, __) =>
            {
                _trace(
                    "webview_shutdown_dispose_begin",
                    $"input_parent=0x{_webViewInputParentWindow.ToInt64():X}");
                _boundsTimer.Dispose();
                DetachCoreHandlers();
                DetachEnvironmentHandler();
                _webView.Dispose();
                ClearPendingMessages("window_closed");
                _nativeRevealDrainCallbacks.Clear();
                _trace("webview_shutdown_dispose_complete", "completed=True");
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs args)
        {
            base.OnHandleCreated(args);
            _windowPointerCaptureApplied = null;
            _overlayTopMostApplied = null;
            _ownedGameWindow = IntPtr.Zero;
            _gameWindowOwnerNeedsRetry = false;
            ApplyWindowPointerCapture();
        }

        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            base.OnFormClosing(args);
            if (args.Cancel)
            {
                return;
            }

            // Closing is also a terminal hide/cancel path. Detach while the
            // HWND is still valid so the external host cannot remain coupled
            // to GTA's window lifetime during teardown.
            _desiredVisible = false;
            RevokeFinalRevealOffscreenLease(
                "window-closing",
                resumeBoundsTimer: false);
            _revealGeneration++;
            _revealPending = false;
            _actualVisible = false;
            _visibilityPublished = false;
            _transferState.Hide();
            ApplyOverlayTopMost(false);
            SynchronizeGameWindowOwner();
            SynchronizeWebViewInputParent();

            var previousInputParent = _webViewInputParentWindow;
            var inputParentDetached =
                _webView.DetachExternalInputParentForShutdown();
            if (inputParentDetached)
                _webViewInputParentWindow = Handle;
            _trace(
                "webview_shutdown_input_parent_detached",
                $"previous=0x{previousInputParent.ToInt64():X} " +
                $"owner=0x{Handle.ToInt64():X} detached={inputParentDetached}");
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                // Pointer and keyboard input are forwarded by the bounded GTA
                // bridge. The host window itself must never steal focus or
                // intercept the main menu while it displays a passive About
                // surface before the managed provider is available.
                parameters.ExStyle |= NativeMethods.WsExNoActivate |
                    NativeMethods.WsExToolWindow |
                    NativeMethods.WsExTransparent |
                    NativeMethods.WsExNoRedirectionBitmap;
                return parameters;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmFinalizeRevealAfterBrowserDrain)
            {
                var token = unchecked((int)message.WParam.ToInt64());
                if (_nativeRevealDrainCallbacks.TryGetValue(token, out var callback))
                {
                    _nativeRevealDrainCallbacks.Remove(token);
                    callback();
                }
                message.Result = IntPtr.Zero;
                return;
            }
            if (message.Msg == WmPaint)
            {
                CheckCompositionDeviceForPaint();
            }
            if (message.Msg == WmNcHitTest)
            {
                // The browser is display-only at the HWND boundary. Bootstrap
                // and provider input are sampled while GTA owns foreground and
                // delivered through the typed DOM bridge below.
                message.Result = new IntPtr(HtTransparent);
                return;
            }
            if (message.Msg == WmMouseActivate)
            {
                message.Result = new IntPtr(MaNoActivate);
                return;
            }
            base.WndProc(ref message);
        }

        private void CheckCompositionDeviceForPaint()
        {
            if (!_webView.IsControllerReady || IsDisposed || Disposing)
                return;

            var health = _webView.CheckCompositionDeviceState();
            if (health.State == CompositionDeviceState.Ready ||
                health.State == CompositionDeviceState.Unavailable)
            {
                return;
            }

            var recovery = _webView.RecoverCompositionDevice();
            var recoveryHealthy =
                recovery.Outcome == CompositionDeviceRecoveryOutcome.Recovered ||
                recovery.Outcome == CompositionDeviceRecoveryOutcome.NotRequired;
            _trace(
                recovery.Outcome == CompositionDeviceRecoveryOutcome.Recovered
                    ? "webview_composition_device_recovered"
                    : recovery.Outcome == CompositionDeviceRecoveryOutcome.NotRequired
                        ? "webview_composition_device_recovery_not_required"
                        : "webview_composition_device_recovery_failed",
                $"observed_state={health.State} check_hresult=0x{health.HResult:X8} " +
                $"outcome={recovery.Outcome} recovery_hresult=0x{recovery.HResult:X8} " +
                $"recovery_mode={recovery.RecoveryMode} " +
                $"composition_generation={recovery.CompositionGeneration} " +
                "trigger=wm_paint completion_wait=False");

            if (recoveryHealthy)
            {
                if (recovery.Outcome == CompositionDeviceRecoveryOutcome.Recovered)
                {
                    ResetPresentationPaintEvidence();
                    _rootVisualRebindAttemptedSurfaceGeneration = null;
                }
                return;
            }

            _browserReady = false;
            ApplyVisibility(false);
            InvalidateBrowserContentReadiness(
                "composition-device-recovery-failed");
            ScheduleSoftwareBrowserRecovery(
                _controllerGeneration,
                disposeControllerBeforeWait: true,
                reason: "composition_device_recovery_failed");
        }

        public bool IsBootstrapPointerCaptureActive =>
            _bootstrapPointerCaptureRequested &&
            _windowPointerCaptureApplied == true;

        public bool ArmProviderInputIntent(ProviderInputIntentToken token)
        {
            var armed = _providerInputIntentGate.TryArm(
                token,
                MonotonicMilliseconds());
            _trace(
                armed
                    ? "webview_provider_input_intent_armed"
                    : "webview_provider_input_intent_rejected",
                $"pid={token.ProcessId} epoch={token.Epoch} " +
                $"lifetime_ms={token.LifetimeMilliseconds}");
            return armed;
        }

        public bool BindProviderInputIntent(
            int processId,
            long epoch,
            string presentationId)
        {
            var bound = _providerInputIntentGate.TryBind(
                processId,
                epoch,
                presentationId,
                MonotonicMilliseconds());
            _trace(
                bound
                    ? "webview_provider_input_intent_bound"
                    : "webview_provider_input_intent_bind_rejected",
                $"pid={processId} epoch={epoch} presentation=" +
                $"{presentationId ?? "none"}");
            return bound;
        }

        public void CancelProviderInputIntent(int processId, long epoch)
        {
            _providerInputIntentGate.Cancel(processId, epoch);
            _trace(
                "webview_provider_input_intent_cancelled",
                $"pid={processId} epoch={epoch}");
        }

        public bool IsProviderPresentationAuthorizedByUserIntent(
            string presentationId) =>
            ProviderPresentationCommitContract.Matches(
                Volatile.Read(ref _userIntentAuthorizedProviderPresentationId),
                presentationId);

        public int AcceptanceCaptureControllerGeneration => _controllerGeneration;

        /// <summary>
        /// Publishes a cancellation edge before a cross-thread visibility or
        /// ownership mutation is queued to this window's STA. DirectComposition
        /// commit waits are synchronous, so the normal WinForms action cannot
        /// run until the wait returns; this atomic epoch keeps that queued hide
        /// or replacement observable before Show().
        /// </summary>
        public void SignalRevealIngress()
        {
            lock (_revealIngressSync)
            {
                _pendingRevealIngress++;
                _revealIngressEpoch++;
            }
        }

        public void ReserveRevealIngressScan()
        {
            lock (_revealIngressSync)
            {
                // A polling worker needs to cover the tiny WaitOne-to-queue
                // interval, but an empty scan is not an ownership mutation.
                // It must not permanently supersede every reveal epoch.
                _pendingRevealIngress++;
            }
        }

        public bool ReleaseRevealIngressScan()
        {
            lock (_revealIngressSync)
            {
                _pendingRevealIngress = Math.Max(0, _pendingRevealIngress - 1);
                return _pendingRevealIngress == 0 &&
                    _revealDeferredForIngress;
            }
        }

        public bool SignalHostMessageIngress(string json)
        {
            try
            {
                var message = JObject.Parse(json);
                if (!string.Equals(
                        message.Value<string>("kind"),
                        "event",
                        StringComparison.Ordinal))
                {
                    return false;
                }
                var eventName = message.Value<string>("event");
                if (string.Equals(eventName, "menu.presentation", StringComparison.Ordinal) ||
                    string.Equals(eventName, "menu.dismissed", StringComparison.Ordinal) ||
                    string.Equals(eventName, "host.surface", StringComparison.Ordinal) ||
                    string.Equals(eventName, "host.provider", StringComparison.Ordinal))
                {
                    SignalRevealIngress();
                    return true;
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // ObserveHostMessage performs authoritative validation on the
                // STA. Malformed input cannot qualify as an ownership edge.
            }
            return false;
        }

        public void ApplyRevealIngress(int count = 1)
        {
            if (count <= 0) return;
            lock (_revealIngressSync)
            {
                _pendingRevealIngress = Math.Max(
                    0,
                    _pendingRevealIngress - count);
            }
        }

        public void ResumeRevealAfterIngress()
        {
            if (HasPendingRevealIngress())
            {
                return;
            }
            if (!_desiredVisible || _actualVisible)
            {
                _revealDeferredForIngress = false;
                return;
            }
            if (_revealPending || !_browserReady) return;
            _revealDeferredForIngress = false;
            SynchronizeBounds();
        }

        private void InvalidateRevealOnCurrentThread()
        {
            unchecked { _browserSurfaceHealthGeneration++; }
            SignalRevealIngress();
            ApplyRevealIngress();
        }

        public bool TryGetAcceptanceCaptureHostStatus(
            out string failure,
            out string detail)
        {
            var overlay = IsHandleCreated ? Handle : IntPtr.Zero;
            var nativeVisible = overlay != IntPtr.Zero &&
                NativeMethods.IsWindowVisible(overlay);
            var style = overlay != IntPtr.Zero
                ? NativeMethods.GetWindowLongPtr(overlay, NativeMethods.GwlStyle).ToInt64()
                : 0L;
            var sameProcessParent = overlay != IntPtr.Zero &&
                _webViewInputParentWindow == overlay;
            var compositionHealth = _webView.CheckCompositionDeviceState();
            detail =
                $"overlay=0x{overlay.ToInt64():X} " +
                $"parent=0x{_webViewInputParentWindow.ToInt64():X} " +
                $"style=0x{style:X} native_visible={nativeVisible} " +
                $"form_visible={Visible} actual_visible={_actualVisible} " +
                $"browser_ready={_browserReady} " +
                $"controller_ready={_webView.IsControllerReady} " +
                $"controller_visible={_webView.IsControllerVisible} " +
                $"same_process_parent={sameProcessParent} " +
                $"controller_generation={_controllerGeneration} " +
                $"composition_generation={_webView.CompositionGeneration} " +
                $"composition_device_state={compositionHealth.State} " +
                $"composition_device_hresult=0x{compositionHealth.HResult:X8} " +
                $"dom_ready={_browserReady} " +
                $"browser_surface_pixels_verified={_browserPresentationPixelsVerified} " +
                $"desktop_presentation_pixels_verified={_desktopPresentationPixelsVerified} " +
                $"desktop_probe_enabled={OverlayPresentationPolicy.UseLiveDesktopPixelSampling} " +
                "evidence_boundary=browser_surface_not_desktop";

            if (!sameProcessParent)
            {
                failure = "capture_parent_is_not_the_reactor_overlay";
                return false;
            }
            if (!_browserReady || !_webView.IsControllerReady)
            {
                failure = "capture_controller_not_ready";
                return false;
            }
            if (!_actualVisible || !Visible || !nativeVisible ||
                (style & NativeMethods.WsVisible) == 0 ||
                !_webView.IsControllerVisible)
            {
                failure = "capture_controller_or_parent_not_visible";
                return false;
            }

            failure = string.Empty;
            return true;
        }

        /// <summary>
        /// Captures only Chromium's composition surface for the opt-in live
        /// acceptance runner.  Identity is sampled on both sides of the async
        /// WebView2 operation so a frame can never be attributed to a surface
        /// or menu generation that changed while it was being encoded.
        /// </summary>
        public async Task<OverlayAcceptancePreviewFrame> CaptureAcceptancePreviewAsync()
        {
            if (!TryGetAcceptanceCaptureHostStatus(out var failure, out var detail))
            {
                throw new InvalidOperationException(failure + ": " + detail);
            }
            var surfaceMode = _activeHostSurfaceMode;
            var surfaceGeneration = _activeHostSurfaceGeneration;
            var controllerGeneration = _controllerGeneration;
            var presentationId = _activeMenuPresentationId;
            var capture = _webView.CapturePreviewAsync();
            var completed = await Task.WhenAny(
                capture,
                Task.Delay(AcceptanceCaptureTimeoutMilliseconds));
            if (!ReferenceEquals(completed, capture))
            {
                _ = capture.ContinueWith(
                    task => { _ = task.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException(
                    "WebView2 acceptance capture exceeded its bounded " +
                    $"{AcceptanceCaptureTimeoutMilliseconds} ms deadline.");
            }
            var png = await capture;
            if (png.Length == 0)
                throw new InvalidOperationException(
                    "WebView2 returned an empty acceptance preview.");
            if (!string.Equals(surfaceMode, _activeHostSurfaceMode, StringComparison.Ordinal) ||
                surfaceGeneration != _activeHostSurfaceGeneration ||
                controllerGeneration != _controllerGeneration ||
                !string.Equals(presentationId, _activeMenuPresentationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The browser presentation changed during acceptance capture.");
            }
            return new OverlayAcceptancePreviewFrame(
                png,
                surfaceMode,
                surfaceGeneration,
                controllerGeneration,
                presentationId,
                domReady: _browserReady,
                browserSurfaceCaptured: true,
                browserPixelsVerified: _browserPresentationPixelsVerified,
                desktopPresentationVerified: _desktopPresentationPixelsVerified);
        }

        /// <summary>
        /// Qualifies the Story initializer against Chromium's current pixels
        /// before the real GTA-facing reveal. WebView2 can leave
        /// CapturePreview pending when its parent HWND is hidden, so this uses
        /// a short, nonactivating off-screen visibility lease while keeping
        /// actual visibility, ownership, topmost state, and input disabled.
        /// </summary>
        public async Task<bool> VerifyBootstrapSurfacePixelsAsync(
            string mode,
            int surfaceGeneration)
        {
            var controllerGeneration = _controllerGeneration;
            var probeGeneration = ++_bootstrapPaintProbeGeneration;
            var timer = Stopwatch.StartNew();
            if (!HostSurfaceMode.IsInitializing(mode) ||
                surfaceGeneration <= 0 ||
                !string.Equals(mode, _activeHostSurfaceMode, StringComparison.Ordinal) ||
                surfaceGeneration != _activeHostSurfaceGeneration ||
                !_browserReady || !_webView.IsControllerReady ||
                _desiredVisible || _actualVisible || _revealPending ||
                Visible ||
                !string.IsNullOrWhiteSpace(_activeMenuPresentationId))
            {
                _trace(
                    "webview_bootstrap_pixel_probe_rejected",
                    $"surface={mode} surface_generation={surfaceGeneration} " +
                    $"current_surface={_activeHostSurfaceMode} " +
                    $"current_surface_generation={_activeHostSurfaceGeneration} " +
                    $"controller_generation={controllerGeneration} " +
                    $"browser_ready={_browserReady} desired_visible={_desiredVisible} " +
                    $"actual_visible={_actualVisible} form_visible={Visible} " +
                    $"reveal_pending={_revealPending} " +
                    $"presentation={_activeMenuPresentationId ?? "none"}");
                return false;
            }

            if (!NativeMethods.TryGetClientBounds(_gtaWindow, out var restoreBounds) ||
                restoreBounds.Width <= 0 || restoreBounds.Height <= 0)
            {
                _trace(
                    "webview_bootstrap_pixel_probe_rejected",
                    $"surface={mode} surface_generation={surfaceGeneration} " +
                    "reason=target-bounds-unavailable");
                return false;
            }

            var wasFormVisible = Visible;
            var boundsTimerWasEnabled = _boundsTimer.Enabled;
            BrowserPaintEvidence evidence = BrowserPaintEvidence.Empty;
            try
            {
                if (boundsTimerWasEnabled)
                    _boundsTimer.Stop();
                ApplyOverlayTopMost(false);
                SynchronizeGameWindowOwner();
                SynchronizeWebViewInputParent();

                var leaseSize = restoreBounds.Size;
                var positioned = NativeMethods.SetWindowPos(
                    Handle,
                    IntPtr.Zero,
                    -32000,
                    -32000,
                    leaseSize.Width,
                    leaseSize.Height,
                    NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
                if (!positioned)
                {
                    _trace(
                        "webview_bootstrap_pixel_probe_failed",
                        $"surface={mode} surface_generation={surfaceGeneration} " +
                        "reason=offscreen-position");
                    return false;
                }
                if (!Visible)
                    Show();
                // Publish the final composition root before taking pixel
                // evidence. A proof captured through an older root cannot
                // qualify a later ReplaceRoot reveal even when controller,
                // surface generation, and target size are unchanged.
                var rootRebind = _webView.RebindRootVisual();
                var synchronized = rootRebind.Succeeded &&
                    _webView.SynchronizeBounds();
                var completionHResult = synchronized
                    ? _webView.WaitForCommitCompletion()
                    : unchecked((int)0x80004005);
                var compositionReady = synchronized &&
                    OverlayPresentationPolicy.DidCompositionCommitComplete(
                        completionHResult);
                if (!compositionReady)
                {
                    _trace(
                        "webview_bootstrap_pixel_probe_failed",
                        $"surface={mode} surface_generation={surfaceGeneration} " +
                        $"reason=offscreen-composition hresult=0x{completionHResult:X8} " +
                        $"root_rebind={rootRebind.Outcome} " +
                        "fence_thread=overlay-sta");
                    return false;
                }

                await Task.Yield();
                for (var attempt = 1;
                    attempt <= MaximumBootstrapPixelProbeAttempts;
                    attempt++)
                {
                    if (!OwnsBootstrapPixelProbeLease(
                            probeGeneration,
                            mode,
                            surfaceGeneration,
                            controllerGeneration))
                    {
                        return false;
                    }

                    var capture = _webView.CapturePreviewAsync();
                    var completed = await Task.WhenAny(
                        capture,
                        Task.Delay(BrowserPaintCaptureTimeoutMilliseconds));
                    var captureCompleted = ReferenceEquals(completed, capture);
                    if (!captureCompleted)
                    {
                        _ = capture.ContinueWith(
                            task => { _ = task.Exception; },
                            TaskContinuationOptions.OnlyOnFaulted);
                        evidence = BrowserPaintEvidence.Empty;
                        _trace(
                            "webview_bootstrap_pixel_probe_timeout",
                            $"surface={mode} surface_generation={surfaceGeneration} " +
                            $"attempt={attempt} " +
                            $"timeout_ms={BrowserPaintCaptureTimeoutMilliseconds}");
                    }
                    else
                    {
                        evidence = AnalyzePresentationPixels(
                            await capture,
                            OverlayPresentationPolicy.HostPaintIdentity(
                                mode,
                                surfaceGeneration));
                    }

                    var leaseCurrent = OwnsBootstrapPixelProbeLease(
                        probeGeneration,
                        mode,
                        surfaceGeneration,
                        controllerGeneration);
                    var targetSizeMatches =
                        evidence.Width == restoreBounds.Width &&
                        evidence.Height == restoreBounds.Height;
                    var concrete = captureCompleted && leaseCurrent &&
                        evidence.IsConcrete &&
                        evidence.PaintIdentityMarkerMatched &&
                        targetSizeMatches;
                    _trace(
                        concrete
                            ? "webview_bootstrap_pixels_verified"
                            : "webview_bootstrap_pixels_unverified",
                        $"surface={mode} surface_generation={surfaceGeneration} " +
                        $"controller_generation={controllerGeneration} attempt={attempt} " +
                        $"identity_current={leaseCurrent} image={evidence.Width}x{evidence.Height} " +
                        $"target={restoreBounds.Width}x{restoreBounds.Height} " +
                        $"target_size_match={targetSizeMatches} " +
                        $"samples={evidence.SampleCount} opaque={evidence.OpaqueSampleCount} " +
                        $"visible_color={evidence.VisibleColorSampleCount} " +
                        $"paint_identity_marker={evidence.PaintIdentityMarkerMatched} " +
                        $"browser_surface_concrete={evidence.IsConcrete} " +
                        $"duration_ms={timer.Elapsed.TotalMilliseconds:F3}");
                    if (concrete)
                    {
                        _bootstrapPaintProofMode = mode;
                        _bootstrapPaintProofSurfaceGeneration = surfaceGeneration;
                        _bootstrapPaintProofControllerGeneration = controllerGeneration;
                        _bootstrapPaintProofWidth = evidence.Width;
                        _bootstrapPaintProofHeight = evidence.Height;
                        _bootstrapPaintProofCompositionGeneration =
                            _webView.CompositionGeneration;
                        _bootstrapPaintProofConcrete = true;
                        _bootstrapPaintProofGenerationMarkerMatched = true;
                        return true;
                    }

                    if (!OverlayPresentationPolicy.ShouldRetryBootstrapPixelProbe(
                            attempt,
                            MaximumBootstrapPixelProbeAttempts,
                            leaseCurrent,
                            concrete))
                    {
                        return false;
                    }

                    var rebind = _webView.RebindRootVisual();
                    if (!rebind.Succeeded)
                    {
                        _trace(
                            "webview_bootstrap_pixel_probe_retry_failed",
                            $"surface={mode} surface_generation={surfaceGeneration} " +
                            $"attempt={attempt} reason=root-rebind " +
                            $"outcome={rebind.Outcome} hresult=0x{rebind.HResult:X8}");
                        return false;
                    }
                    var retryFence = _webView.WaitForCommitCompletion();
                    if (!OverlayPresentationPolicy.DidCompositionCommitComplete(retryFence))
                    {
                        _trace(
                            "webview_bootstrap_pixel_probe_retry_failed",
                            $"surface={mode} surface_generation={surfaceGeneration} " +
                            $"attempt={attempt} reason=composition-fence " +
                            $"hresult=0x{retryFence:X8} fence_thread=overlay-sta");
                        return false;
                    }
                    _trace(
                        "webview_bootstrap_pixel_probe_retry",
                        $"surface={mode} surface_generation={surfaceGeneration} " +
                        $"completed_attempt={attempt} next_attempt={attempt + 1} " +
                        $"root_rebind_outcome={rebind.Outcome}");
                }
                return false;
            }
            catch (Exception error) when (
                error is COMException ||
                error is InvalidOperationException ||
                error is ArgumentException ||
                error is IOException ||
                error is OutOfMemoryException)
            {
                _trace(
                    "webview_bootstrap_pixel_probe_failed",
                    $"surface={mode} surface_generation={surfaceGeneration} " +
                    $"type={error.GetType().FullName} message={error.Message}");
                return false;
            }
            finally
            {
                var ownsLease = OwnsBootstrapPixelProbeLease(
                    probeGeneration,
                    mode,
                    surfaceGeneration,
                    controllerGeneration);
                if (ownsLease)
                {
                    if (!wasFormVisible && Visible)
                        Hide();
                    NativeMethods.SetWindowPos(
                        Handle,
                        IntPtr.Zero,
                        restoreBounds.Left,
                        restoreBounds.Top,
                        restoreBounds.Width,
                        restoreBounds.Height,
                        NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
                    _webView.SynchronizeBounds();
                    ApplyOverlayTopMost(false);
                }
                else
                {
                    _trace(
                        "webview_bootstrap_pixel_probe_cleanup_skipped",
                        $"surface={mode} surface_generation={surfaceGeneration} " +
                        $"probe_generation={probeGeneration} reason=lease-superseded");
                }
                if (boundsTimerWasEnabled && !IsDisposed && !Disposing)
                    _boundsTimer.Start();
            }
        }

        private bool OwnsBootstrapPixelProbeLease(
            int probeGeneration,
            string mode,
            int surfaceGeneration,
            int controllerGeneration) =>
            !IsDisposed && !Disposing &&
            OverlayPresentationPolicy.OwnsBootstrapPixelProbeLease(
                probeGeneration,
                _bootstrapPaintProbeGeneration,
                mode,
                _activeHostSurfaceMode,
                surfaceGeneration,
                _activeHostSurfaceGeneration,
                controllerGeneration,
                _controllerGeneration,
                _desiredVisible,
                _actualVisible,
                _revealPending,
                !string.IsNullOrWhiteSpace(_activeMenuPresentationId));

        // Bootstrap and provider menus both receive pointer samples through
        // the typed DOM bridge. The external HWND remains transparent for its
        // entire lifetime, including the pre-provider About surface.
        private bool WindowPointerCaptureRequested =>
            WindowedInputPolicy.ShouldCaptureHostHitTest(
                _bootstrapPointerCaptureRequested,
                _providerPointerShieldRequested);

        public void SetOverlayVisible(bool visible)
        {
            if (!visible)
                SuspendProviderInputCommit("overlay-hidden");
            if (visible && !_desiredVisible)
            {
                _revealRequestedAt = Stopwatch.GetTimestamp();
            }
            _desiredVisible = visible;
            UpdateProviderPointerShield();
            _boundsTimer.Interval = visible
                ? VisibleBoundsPollMilliseconds
                : HiddenBoundsPollMilliseconds;
            _trace(
                "webview_visibility_requested",
                $"visible={visible} browser_ready={_browserReady} actual_visible={_actualVisible}");
            SynchronizeBounds();
        }

        /// <summary>
        /// Parks the topmost WebView HWND while preserving its requested
        /// visibility, provider presentation identity, and browser message
        /// authority. The persistent host uses this only after the exact CEF
        /// presentation has crossed the dual-browser readiness barrier; native
        /// compositor visibility remains a separate, fail-closed decision.
        /// </summary>
        public void SetExternalPresentationOwnership(bool ownsPixels)
        {
            if (_externalPresentationOwnsPixels == ownsPixels)
            {
                if (ownsPixels)
                    ParkForExternalPresentation("ownership-reasserted");
                return;
            }

            _externalPresentationOwnsPixels = ownsPixels;
            if (ownsPixels)
            {
                ParkForExternalPresentation("ownership-acquired");
            }
            else
            {
                _trace(
                    "webview_external_presenter_released",
                    $"desired_visible={_desiredVisible} " +
                    $"presentation={_activeMenuPresentationId ?? "none"}");
                SynchronizeBounds();
            }
        }

        public bool TryGetTargetClientSize(out int width, out int height)
        {
            RefreshGameWindow();
            if (NativeMethods.TryGetClientBounds(_gtaWindow, out var bounds) &&
                bounds.Width > 0 && bounds.Height > 0)
            {
                width = bounds.Width;
                height = bounds.Height;
                return true;
            }

            width = 0;
            height = 0;
            return false;
        }

        /// <summary>
        /// Records whether the pre-provider About sampler should be active.
        /// This is a logical input lease only; ApplyWindowPointerCapture keeps
        /// WS_EX_TRANSPARENT set regardless of the requested state.
        /// </summary>
        public void SetBootstrapPointerCapture(bool enabled)
        {
            if (!enabled && _bootstrapPointerCaptureRequested)
            {
                PostBootstrapPointerReset();
            }
            _bootstrapPointerCaptureRequested = enabled;
            ApplyWindowPointerCapture();
        }

        private void UpdateProviderPointerShield()
        {
            var enabled = WindowedInputPolicy.ShouldForwardProviderPointer(
                _desiredVisible,
                _actualVisible,
                _revealPending,
                _activeMenuPresentationId,
                _acceptedMenuPresentationId,
                _committedProviderInputPresentationId);
            if (_providerPointerShieldRequested == enabled)
            {
                return;
            }

            // A menu may dismiss itself from its button-down handler before
            // GTA produces the corresponding physical button-up sample. End
            // the DOM pointer lease before removing the provider shield so a
            // hidden page cannot retain a latched press or visible cursor.
            if (_providerPointerShieldRequested && !enabled)
            {
                ResetPointerInput("provider-pointer-shield-released");
            }

            _providerPointerShieldRequested = enabled;
            ApplyWindowPointerCapture();
            _trace(
                "webview_provider_pointer_shield",
                $"enabled={enabled} presentation={_activeMenuPresentationId ?? "none"} " +
                $"accepted={_acceptedMenuPresentationId ?? "none"} " +
                $"committed={_committedProviderInputPresentationId ?? "none"} " +
                $"requested_visible={_desiredVisible} host_hit_test=False " +
                $"input_parent=0x{_webViewInputParentWindow.ToInt64():X}");
        }

        private void SuspendProviderInputCommit(string reason)
        {
            _explicitUserIntentInputLeaseGeneration++;
            var previous = _committedProviderInputPresentationId;
            _providerInputCommitGeneration++;
            _committedProviderInputPresentationId = null;
            Volatile.Write(
                ref _userIntentAuthorizedProviderPresentationId,
                null);
            UpdateProviderPointerShield();
            if (!string.IsNullOrWhiteSpace(previous))
            {
                _trace(
                    "webview_provider_input_suspended",
                    $"reason={reason} presentation={previous}");
            }
        }

        private void ResetProviderInputAuthorization(string reason)
        {
            var accepted = _acceptedMenuPresentationId;
            var committed = _committedProviderInputPresentationId;
            _providerInputCommitGeneration++;
            _acceptedMenuPresentationId = null;
            _committedProviderInputPresentationId = null;
            _publishedProviderPresentationId = null;
            UpdateProviderPointerShield();
            if (!string.IsNullOrWhiteSpace(accepted) ||
                !string.IsNullOrWhiteSpace(committed))
            {
                _trace(
                    "webview_provider_input_authorization_reset",
                    $"reason={reason} accepted={accepted ?? "none"} " +
                    $"committed={committed ?? "none"}");
            }
        }

        private void BeginProviderInputCommit(string presentationId)
        {
            _acceptedMenuPresentationId = presentationId;
            _committedProviderInputPresentationId = null;
            var commitGeneration = ++_providerInputCommitGeneration;
            UpdateProviderPointerShield();
            if (!_browserReady || !_desiredVisible || !_actualVisible ||
                _revealPending || !Visible ||
                !string.Equals(
                    presentationId,
                    _activeMenuPresentationId,
                    StringComparison.Ordinal))
            {
                _trace(
                    "webview_provider_input_commit_deferred",
                    $"presentation={presentationId} generation={commitGeneration} " +
                    $"browser_ready={_browserReady} requested_visible={_desiredVisible} " +
                    $"actual_visible={_actualVisible} form_visible={Visible} " +
                    $"reveal_pending={_revealPending}");
                return;
            }

            if (!NativeMethods.TryGetClientBounds(_gtaWindow, out var target) ||
                target.Width <= 0 || target.Height <= 0)
            {
                _trace(
                    "webview_provider_input_commit_failed",
                    $"presentation={presentationId} generation={commitGeneration} " +
                    "reason=target-bounds-unavailable");
                return;
            }

            var transferIdentity = CreateTransferIdentity(
                ++_transferGeneration,
                _controllerGeneration,
                _providerSessionGeneration,
                presentationId,
                _activeHostSurfaceMode,
                _activeHostSurfaceGeneration,
                _webView.CompositionGeneration,
                target);
            if (!_transferState.Begin(transferIdentity))
            {
                _trace(
                    "webview_transfer_stale_begin_rejected",
                    $"boundary=warm-provider generation={commitGeneration} " +
                    $"presentation={presentationId} phase={_transferState.Phase}");
                return;
            }
            _desktopPresentationPixelsVerified = false;
            TraceTransferState("began-warm-provider", transferIdentity);

            VerifyProviderPresentationPixelsAndCommitAsync(
                presentationId,
                _controllerGeneration,
                _providerSessionGeneration,
                _webView.CompositionGeneration,
                _browserSurfaceHealthGeneration,
                commitGeneration,
                target,
                transferIdentity);
        }

        private async void VerifyProviderPresentationPixelsAndCommitAsync(
            string presentationId,
            int controllerGeneration,
            int providerSessionGeneration,
            int compositionGeneration,
            int browserSurfaceHealthGeneration,
            int commitGeneration,
            Rectangle target,
            OverlayTransferIdentity transferIdentity)
        {
            // PostJson schedules this only after the accepted response has
            // actually been sent to WebView. Yield once more so React can
            // expose the provider tree and its exact paint-identity marker.
            await Task.Yield();
            var expectedPaintIdentity = OverlayPresentationPolicy.MenuPaintIdentity(
                providerSessionGeneration,
                presentationId);
            for (var attempt = 1;
                attempt <= MaximumProviderPaintCommitAttempts;
                attempt++)
            {
                if (!OwnsProviderPaintCommit(
                        presentationId,
                        controllerGeneration,
                        providerSessionGeneration,
                        compositionGeneration,
                        browserSurfaceHealthGeneration,
                        commitGeneration,
                        target))
                {
                    return;
                }

                try
                {
                    var capture = _webView.CapturePreviewAsync();
                    var completed = await Task.WhenAny(
                        capture,
                        Task.Delay(BrowserPaintCaptureTimeoutMilliseconds));
                    if (!ReferenceEquals(completed, capture))
                    {
                        _ = capture.ContinueWith(
                            task => { _ = task.Exception; },
                            TaskContinuationOptions.OnlyOnFaulted);
                        _trace(
                            "webview_provider_paint_probe_timeout",
                            $"presentation={presentationId} generation={commitGeneration} " +
                            $"attempt={attempt} timeout_ms={BrowserPaintCaptureTimeoutMilliseconds}");
                    }
                    else
                    {
                        var evidence = AnalyzePresentationPixels(
                            await capture,
                            expectedPaintIdentity);
                        var identityCurrent = OwnsProviderPaintCommit(
                            presentationId,
                            controllerGeneration,
                            providerSessionGeneration,
                            compositionGeneration,
                            browserSurfaceHealthGeneration,
                            commitGeneration,
                            target);
                        var targetSizeMatches =
                            evidence.Width == target.Width &&
                            evidence.Height == target.Height;
                        var exactPaint = identityCurrent &&
                            expectedPaintIdentity != 0 &&
                            evidence.IsConcrete &&
                            evidence.PaintIdentityMarkerMatched &&
                            targetSizeMatches;
                        _trace(
                            exactPaint
                                ? "webview_provider_pixels_verified"
                                : "webview_provider_pixels_unverified",
                            $"presentation={presentationId} generation={commitGeneration} " +
                            $"attempt={attempt} identity_current={identityCurrent} " +
                            $"image={evidence.Width}x{evidence.Height} " +
                            $"target={target.Width}x{target.Height} " +
                            $"target_size_match={targetSizeMatches} " +
                            $"concrete={evidence.IsConcrete} " +
                            $"paint_identity_marker={evidence.PaintIdentityMarkerMatched} " +
                            $"expected_paint_identity=0x{expectedPaintIdentity:X16}");
                        if (exactPaint)
                        {
                            if (!_transferState.TryAdvance(
                                    transferIdentity,
                                    OverlayTransferPhase.Preparing,
                                    OverlayTransferPhase.BrowserPaintVerified))
                            {
                                _trace(
                                    "webview_transfer_stale_acknowledgement",
                                    $"boundary=warm-provider-browser-paint " +
                                    $"presentation={presentationId} " +
                                    $"phase={_transferState.Phase}");
                                return;
                            }
                            TraceTransferState(
                                "browser-paint-verified",
                                transferIdentity);
                            CompleteProviderInputCommit(
                                presentationId,
                                controllerGeneration,
                                providerSessionGeneration,
                                compositionGeneration,
                                browserSurfaceHealthGeneration,
                                commitGeneration,
                                target,
                                transferIdentity,
                                evidence.DesktopSamples);
                            return;
                        }
                    }
                }
                catch (Exception error) when (
                    error is COMException ||
                    error is InvalidOperationException ||
                    error is ArgumentException ||
                    error is IOException ||
                    error is OutOfMemoryException)
                {
                    _trace(
                        "webview_provider_paint_probe_failed",
                        $"presentation={presentationId} generation={commitGeneration} " +
                        $"attempt={attempt} type={error.GetType().FullName} " +
                        $"message={error.Message}");
                }

                if (attempt < MaximumProviderPaintCommitAttempts)
                    await Task.Delay(ProviderPaintRetryMilliseconds);
            }

            _trace(
                "webview_provider_input_commit_failed",
                $"presentation={presentationId} generation={commitGeneration} " +
                "reason=exact-provider-pixels-unavailable fail_closed=true");
            HandleDesktopPresentationFailure(
                transferIdentity,
                target,
                "provider-browser-pixels-unavailable");
        }

        private bool OwnsProviderPaintCommit(
            string presentationId,
            int controllerGeneration,
            int providerSessionGeneration,
            int compositionGeneration,
            int browserSurfaceHealthGeneration,
            int commitGeneration,
            Rectangle target) =>
            !IsDisposed && !Disposing &&
            commitGeneration == _providerInputCommitGeneration &&
            controllerGeneration == _controllerGeneration &&
            providerSessionGeneration == _providerSessionGeneration &&
            compositionGeneration == _webView.CompositionGeneration &&
            browserSurfaceHealthGeneration == _browserSurfaceHealthGeneration &&
            string.Equals(
                presentationId,
                _activeMenuPresentationId,
                StringComparison.Ordinal) &&
            string.Equals(
                presentationId,
                _acceptedMenuPresentationId,
                StringComparison.Ordinal) &&
            _browserReady && _desiredVisible && _actualVisible &&
            !_revealPending && Visible &&
            target == _lastBounds;

        private void CompleteProviderInputCommit(
            string presentationId,
            int controllerGeneration,
            int providerSessionGeneration,
            int compositionGeneration,
            int browserSurfaceHealthGeneration,
            int commitGeneration,
            Rectangle target,
            OverlayTransferIdentity transferIdentity,
            IReadOnlyList<DesktopPaintSample> desktopSamples)
        {
            var synchronized = _webView.SynchronizeBounds();
            var completionHResult = synchronized
                ? _webView.WaitForCommitCompletion()
                : unchecked((int)0x80004005);
            var identityCurrent =
                OwnsProviderPaintCommit(
                    presentationId,
                    controllerGeneration,
                    providerSessionGeneration,
                    compositionGeneration,
                    browserSurfaceHealthGeneration,
                    commitGeneration,
                    target);
            var committed = synchronized && identityCurrent &&
                OverlayPresentationPolicy.DidCompositionCommitComplete(
                    completionHResult);
            if (committed)
            {
                committed = _transferState.TryAdvance(
                    transferIdentity,
                    OverlayTransferPhase.BrowserPaintVerified,
                    OverlayTransferPhase.WindowPromoted);
                if (committed)
                {
                    TraceTransferState(
                        "window-promoted-awaiting-desktop",
                        transferIdentity);
                    BeginDesktopPresentationCommit(
                        transferIdentity,
                        target,
                        desktopSamples,
                        completionWaitMilliseconds: 0d);
                }
            }
            _trace(
                committed
                    ? "webview_provider_native_commit_qualified"
                    : "webview_provider_input_commit_failed",
                $"presentation={presentationId} generation={commitGeneration} " +
                $"controller_generation={controllerGeneration} " +
                $"identity_current={identityCurrent} " +
                $"synchronized={synchronized} hresult=0x{completionHResult:X8} " +
                "fence_thread=overlay-sta paint_boundary=exact-menu-marker " +
                "desktop_presentation=awaiting-proof input_enabled=False");
        }

        private void CommitProviderInputAfterRevealFence()
        {
            if (string.IsNullOrWhiteSpace(_activeMenuPresentationId) ||
                !_transferState.IsInteractive ||
                !string.Equals(
                    _acceptedMenuPresentationId,
                    _activeMenuPresentationId,
                    StringComparison.Ordinal))
            {
                return;
            }

            var transferIdentity = _transferState.Identity;
            if (!transferIdentity.HasValue ||
                transferIdentity.Value.Owner != OverlayTransferOwner.Provider ||
                !string.Equals(
                    transferIdentity.Value.PresentationId,
                    _activeMenuPresentationId,
                    StringComparison.Ordinal))
            {
                return;
            }

            _providerInputCommitGeneration++;
            _committedProviderInputPresentationId = _activeMenuPresentationId;
            UpdateProviderPointerShield();
            PublishProviderPresentationCommitted(_activeMenuPresentationId!);
            _trace(
                "webview_provider_input_committed",
                $"presentation={_activeMenuPresentationId} " +
                "boundary=deferred-reveal-fence");
        }

        private void PublishProviderPresentationCommitted(string presentationId)
        {
            if (string.Equals(
                    _publishedProviderPresentationId,
                    presentationId,
                    StringComparison.Ordinal))
            {
                return;
            }

            _publishedProviderPresentationId = presentationId;
            ProviderPresentationCommitted?.Invoke(presentationId);
            _trace(
                "webview_provider_presentation_committed",
                $"presentation={presentationId} " +
                $"provider_session_generation={_providerSessionGeneration}");
        }

        private void ApplyWindowPointerCapture()
        {
            var requested = WindowPointerCaptureRequested;
            if (_windowPointerCaptureApplied == requested ||
                !IsHandleCreated || IsDisposed || Disposing)
                return;

            var previous = NativeMethods.GetWindowLongPtr(
                Handle,
                NativeMethods.GwlExStyle).ToInt64();
            var next = requested
                ? previous & ~NativeMethods.WsExTransparent
                : previous | NativeMethods.WsExTransparent;
            if (next != previous)
            {
                NativeMethods.SetWindowLongPtr(
                    Handle,
                    NativeMethods.GwlExStyle,
                    new IntPtr(next));
                NativeMethods.SetWindowPos(
                    Handle,
                    IntPtr.Zero,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SwpNoActivate |
                    NativeMethods.SwpNoMove |
                    NativeMethods.SwpNoSize |
                    NativeMethods.SwpNoZOrder |
                    NativeMethods.SwpFrameChanged);
            }
            _windowPointerCaptureApplied = requested;
            _trace(
                // Preserve the established bootstrap diagnostic name for
                // harness/backward compatibility; provider_shield identifies
                // the new typed-menu input boundary explicitly.
                "webview_bootstrap_pointer_capture",
                $"enabled={requested} bootstrap={_bootstrapPointerCaptureRequested} " +
                $"provider_shield={_providerPointerShieldRequested} " +
                $"style_before=0x{previous:X} style_after=0x{next:X}");
        }

        public void BeginPreload()
        {
            if (_preloadStarted || IsDisposed)
            {
                return;
            }

            _preloadStarted = true;
            // WebView2 CapturePreview has a known non-completion path when the
            // parent HWND passed at controller creation lacks WS_VISIBLE. Keep
            // this nonactivating window visible offscreen only for controller
            // creation, then park it again before navigation/presentation.
            if (!Visible)
                Show();
            _trace(
                "webview_controller_parent_visible_lease",
                $"overlay=0x{Handle.ToInt64():X} " +
                $"native_visible={NativeMethods.IsWindowVisible(Handle)} " +
                $"offscreen={Location.X <= -30000 && Location.Y <= -30000}");
            _trace("webview_preload_begin", null);
            var generation = ++_controllerGeneration;
            InitializeBrowserAsync(
                _webView,
                generation,
                useSoftwareComposition:
                    WebView2ProcessFailurePolicy.ShouldUseSoftwareComposition(
                        persistentPresentedOverlay: true,
                        recovering: false));
        }

        public void PostJson(string json)
        {
            if (IsDisposed || Disposing || _webView.IsDisposed)
            {
                return;
            }

            var core = _webView.CoreWebView2;
            if (core == null || !_browserReady)
            {
                if (_pendingMessages.Count >= MaximumPendingMessages)
                {
                    _trace(
                        "webview_pending_message_rejected",
                        $"capacity={MaximumPendingMessages}");
                    return;
                }

                _pendingMessages.Enqueue(json);
                return;
            }

            // Deliver the frame before observing an accepted
            // presentationReady response. The browser changes from the
            // initializer to the provider tree only after receiving that
            // response; proving paint before this call fences the old frame.
            core.PostWebMessageAsJson(json);
            ObserveHostMessage(json);
        }

        private void ObserveHostMessage(string json)
        {
            try
            {
                var message = JObject.Parse(json);
                if (string.Equals(message.Value<string>("kind"), "event", StringComparison.Ordinal))
                {
                    var eventName = message.Value<string>("event");
                    if (string.Equals(eventName, "menu.presentation", StringComparison.Ordinal))
                    {
                        var payload = message["payload"] as JObject;
                        var previousPresentationId = _activeMenuPresentationId;
                        var nextPresentationId =
                            payload?.Value<string>("presentationId");
                        if (!string.Equals(
                                previousPresentationId,
                                nextPresentationId,
                                StringComparison.Ordinal))
                        {
                            _finalRevealPixelFailureIdentity = string.Empty;
                            _finalRevealPixelFailureCount = 0;
                            CancelPendingRevealForIdentityChange(
                                "presentation-replaced",
                                preserveDesiredVisibility: true);
                            ResetProviderInputAuthorization("presentation-replaced");
                        }
                        _activeMenuPresentationId = nextPresentationId;
                        _activeMenuExtensionId = payload?.Value<string>("extensionId");
                        _activeMenuId = payload?.Value<string>("menuId");
                        UpdateProviderPointerShield();
                        _trace(
                            "webview_menu_presentation_observed",
                            $"presentation={_activeMenuPresentationId ?? "none"} " +
                            $"extension={_activeMenuExtensionId ?? "none"} " +
                            $"menu={_activeMenuId ?? "none"} " +
                            $"previous={previousPresentationId ?? "none"} " +
                            $"desired_visible={_desiredVisible} actual_visible={_actualVisible} " +
                            $"browser_ready={_browserReady}");
                        if (_browserRecoveryInProgress || _browserRecoveryQueued ||
                            _recoveredSurfaceAwaitingPaint)
                        {
                            _recoveryPresentationPaintAcknowledged = false;
                            _recoveredSurfaceAwaitingPaint =
                                _desiredVisible &&
                                !string.IsNullOrWhiteSpace(_activeMenuPresentationId);
                        }
                    }
                    else if (string.Equals(eventName, "menu.dismissed", StringComparison.Ordinal))
                    {
                        var payload = message["payload"] as JObject;
                        var dismissedPresentationId =
                            payload?.Value<string>("presentationId");
                        var dismissalReason = payload?.Value<string>("reason") ?? string.Empty;
                        var replacementPending = string.Equals(
                            dismissalReason,
                            "superseded",
                            StringComparison.Ordinal);
                        var previousPresentationId = _activeMenuPresentationId;
                        var matched = string.Equals(
                            dismissedPresentationId,
                            previousPresentationId,
                            StringComparison.Ordinal);
                        if (matched)
                        {
                            _finalRevealPixelFailureIdentity = string.Empty;
                            _finalRevealPixelFailureCount = 0;
                            CancelPendingRevealForIdentityChange(
                                "presentation-dismissed",
                                preserveDesiredVisibility: replacementPending);
                            ResetProviderInputAuthorization("presentation-dismissed");
                            _activeMenuPresentationId = null;
                            _activeMenuExtensionId = null;
                            _activeMenuId = null;
                            UpdateProviderPointerShield();
                            _pendingPresentationReadyRequestId = null;
                            _recoveryPresentationPaintAcknowledged = true;
                            _recoveredSurfaceAwaitingPaint = false;
                        }
                        _trace(
                            "webview_menu_presentation_dismissed",
                            $"presentation={dismissedPresentationId ?? "none"} " +
                            $"active_before={previousPresentationId ?? "none"} " +
                            $"matched={matched} reason={dismissalReason} " +
                            $"replacement_pending={replacementPending} " +
                            $"desired_visible={_desiredVisible} actual_visible={_actualVisible}");
                    }
                    else if (string.Equals(eventName, "host.provider", StringComparison.Ordinal))
                    {
                        var payload = message["payload"] as JObject;
                        var connected = payload?.Value<bool?>("connected") == true;
                        var sessionGeneration = Math.Max(
                            0,
                            payload?.Value<int?>("sessionGeneration") ?? 0);
                        if (sessionGeneration < _providerSessionGeneration)
                        {
                            _trace(
                                "webview_provider_boundary_ignored",
                                $"connected={connected} session_generation={sessionGeneration} " +
                                $"current_session_generation={_providerSessionGeneration} " +
                                "reason=stale-session");
                            return;
                        }
                        var sessionChanged =
                            sessionGeneration > _providerSessionGeneration;
                        _providerSessionGeneration = sessionGeneration;
                        if (sessionChanged)
                        {
                            _providerInputIntentGate.BeginProviderSession(
                                sessionGeneration);
                        }
                        var hostSurfaceOwnsDisplay =
                            HostSurfaceMode.RequiresPaintProof(_activeHostSurfaceMode);
                        if (sessionChanged && !hostSurfaceOwnsDisplay)
                        {
                            CancelPendingRevealForIdentityChange(
                                "provider-session-replaced",
                                preserveDesiredVisibility: true);
                            ResetProviderInputAuthorization(
                                "provider-session-replaced");
                            _activeMenuPresentationId = null;
                            _activeMenuExtensionId = null;
                            _activeMenuId = null;
                            UpdateProviderPointerShield();
                        }
                        if (!connected)
                        {
                            _providerInputIntentGate.RevokeProviderSession(
                                sessionGeneration);
                            if (!hostSurfaceOwnsDisplay)
                            {
                                CancelPendingRevealForIdentityChange(
                                    "provider-disconnected",
                                    preserveDesiredVisibility: false);
                            }
                            else
                            {
                                _trace(
                                    "webview_provider_disconnect_host_surface_preserved",
                                    $"surface={_activeHostSurfaceMode} " +
                                    $"surface_generation={_activeHostSurfaceGeneration} " +
                                    $"desired_visible={_desiredVisible} " +
                                    $"actual_visible={_actualVisible}");
                            }
                            ResetProviderInputAuthorization("provider-disconnected");
                            _activeMenuPresentationId = null;
                            _activeMenuExtensionId = null;
                            _activeMenuId = null;
                            UpdateProviderPointerShield();
                        }
                    }
                    else if (string.Equals(eventName, "host.surface", StringComparison.Ordinal))
                    {
                        var payload = message["payload"] as JObject;
                        var nextMode = payload?.Value<string>("mode") ?? "none";
                        var nextGeneration = payload?.Value<int?>("generation") ?? 0;
                        var surfaceChanged =
                            nextGeneration != _activeHostSurfaceGeneration ||
                            !string.Equals(
                                nextMode,
                                _activeHostSurfaceMode,
                                StringComparison.Ordinal);
                        var previousMode = _activeHostSurfaceMode;
                        _activeHostSurfaceMode = nextMode;
                        _activeHostSurfaceGeneration = nextGeneration;
                        if (surfaceChanged)
                        {
                            if (HostSurfaceMode.IsInitializing(nextMode) &&
                                string.Equals(
                                    previousMode,
                                    HostSurfaceMode.None,
                                    StringComparison.Ordinal) &&
                                !_actualVisible)
                            {
                                // A long-lived hidden composition target can
                                // produce valid CapturePreview pixels while no
                                // longer publishing that root over GTA. Mark
                                // this cold transition for one post-Show root
                                // publication before input is authorized.
                                _coldHostVisibleRootPublishRequired = true;
                            }
                            else if (!HostSurfaceMode.IsInitializing(nextMode))
                            {
                                _coldHostVisibleRootPublishRequired = false;
                            }
                            _paintAcknowledgedHostSurfaceMode = HostSurfaceMode.None;
                            _paintAcknowledgedHostSurfaceGeneration = 0;
                            _finalRevealPixelFailureIdentity = string.Empty;
                            _finalRevealPixelFailureCount = 0;
                            CancelPendingRevealForIdentityChange(
                                "host-surface-replaced",
                                preserveDesiredVisibility: true);
                            if (!string.Equals(nextMode, "none", StringComparison.Ordinal))
                            {
                                ResetProviderInputAuthorization("bootstrap-surface-superseded");
                                _activeMenuPresentationId = null;
                                _activeMenuExtensionId = null;
                                _activeMenuId = null;
                            }
                            ResetPresentationPaintEvidence();
                            _trace(
                                "webview_presentation_evidence_reset",
                                $"surface={nextMode} surface_generation={nextGeneration} " +
                                $"root_rebind_last_generation=" +
                                $"{_rootVisualRebindAttemptedSurfaceGeneration?.ToString() ?? "none"}");
                        }
                    }
                    return;
                }

                if (!string.Equals(message.Value<string>("kind"), "response", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(_pendingPresentationReadyRequestId) ||
                    !string.Equals(
                        message.Value<string>("id"),
                        _pendingPresentationReadyRequestId,
                        StringComparison.Ordinal))
                    return;
                _pendingPresentationReadyRequestId = null;
                var result = message["result"] as JObject;
                if (message["error"] == null && result?.Value<bool>("accepted") == true &&
                    string.Equals(
                        result.Value<string>("presentationId"),
                        _activeMenuPresentationId,
                        StringComparison.Ordinal))
                {
                    var recoveredSurfaceReady = _recoveredSurfaceAwaitingPaint;
                    _recoveryPresentationPaintAcknowledged = true;
                    _recoveredSurfaceAwaitingPaint = false;
                    _trace(
                        recoveredSurfaceReady
                            ? "webview_recovered_presentation_ready"
                            : "webview_menu_presentation_acknowledged",
                        $"presentation={_activeMenuPresentationId} " +
                        $"generation={_controllerGeneration}");
                    BeginProviderInputCommit(_activeMenuPresentationId!);
                    if (_desiredVisible && _browserReady && !_actualVisible &&
                        !_revealPending)
                    {
                        SynchronizeBounds();
                    }
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // BridgeProtocol performs the authoritative validation. This
                // observer only extracts bounded recovery state.
            }
        }

        private void CancelPendingRevealForIdentityChange(
            string reason,
            bool preserveDesiredVisibility)
        {
            var desiredVisibilityBeforeSupersession = _desiredVisible;
            var hasUncommittedRevealLease =
                _revealPending || _revealDeferredForIngress;
            if (!hasUncommittedRevealLease && preserveDesiredVisibility)
            {
                return;
            }

            _trace(
                "webview_reveal_identity_superseded",
                $"reason={reason} generation={_revealGeneration} " +
                $"presentation={_activeMenuPresentationId ?? "none"} " +
                $"surface={_activeHostSurfaceMode} " +
                $"surface_generation={_activeHostSurfaceGeneration} " +
                $"preserve_desired_visibility={preserveDesiredVisibility} " +
                $"desired_visible_before={desiredVisibilityBeforeSupersession}");
            _revealDeferredForIngress = false;
            if (!preserveDesiredVisibility)
            {
                _desiredVisible = false;
            }

            // Identity replacement invalidates the currently qualified frame,
            // not the user's logical request to keep the overlay open. Hide
            // and invalidate an uncommitted native lease now; the replacement surface's
            // matching paint acknowledgement will re-enter SynchronizeBounds.
            // An already committed surface remains stable while React prepares
            // its replacement. Explicit dismiss/disconnect paths opt out and
            // clear the request and committed surface.
            ApplyVisibility(false);
        }

        private bool HasPendingRevealIngress()
        {
            lock (_revealIngressSync)
            {
                return _pendingRevealIngress > 0;
            }
        }

        private bool RevealIngressWasSuperseded(long ingressEpoch)
        {
            lock (_revealIngressSync)
            {
                return _pendingRevealIngress > 0 ||
                    ingressEpoch != _revealIngressEpoch;
            }
        }

        private long CaptureRevealIngressEpoch()
        {
            lock (_revealIngressSync)
            {
                return _revealIngressEpoch;
            }
        }

        private void DeferPendingRevealForIngress(string reason)
        {
            if (!_revealPending) return;
            _trace(
                "webview_reveal_ingress_deferred",
                $"reason={reason} generation={_revealGeneration} " +
                $"pending_ingress={_pendingRevealIngress} " +
                $"presentation={_activeMenuPresentationId ?? "none"} " +
                $"surface={_activeHostSurfaceMode} " +
                $"surface_generation={_activeHostSurfaceGeneration}");
            // Preserve the requested visibility. The queued ownership event
            // will either replace/cancel that intent or re-arm it after every
            // announced ingress item has reached this STA.
            _revealDeferredForIngress = true;
            ApplyVisibility(false);
        }

        private void DeferPendingRevealForBrowserHealth(string reason)
        {
            if (!_revealPending) return;
            _trace(
                "webview_reveal_browser_health_withheld",
                $"reason={reason} generation={_revealGeneration} " +
                $"presentation={_activeMenuPresentationId ?? "none"} " +
                $"surface={_activeHostSurfaceMode} " +
                $"surface_generation={_activeHostSurfaceGeneration}");
            // Preserve the logical request. A healthy process snapshot on a
            // later bounds tick can retry, while WebView2's failure callback
            // will poison readiness and enter its bounded recovery path.
            ApplyVisibility(false);
        }

        private bool HasLiveBrowserSurface()
        {
            var core = _attachedCore;
            var environment = _attachedEnvironment;
            if (!_browserReady || core == null || environment == null)
                return false;
            try
            {
                var browserProcessId = unchecked((int)core.BrowserProcessId);
                if (browserProcessId <= 0) return false;
                using (var browser = Process.GetProcessById(browserProcessId))
                {
                    if (browser.HasExited) return false;
                }

                var browserFound = false;
                var rendererFound = false;
                foreach (var process in environment.GetProcessInfos())
                {
                    browserFound |=
                        process.Kind == CoreWebView2ProcessKind.Browser &&
                        process.ProcessId == browserProcessId;
                    rendererFound |=
                        process.Kind == CoreWebView2ProcessKind.Renderer;
                }
                return browserFound && rendererFound;
            }
            catch (Exception error) when (
                error is ArgumentException ||
                error is InvalidOperationException ||
                error is COMException)
            {
                return false;
            }
        }

        public void PostPointerInput(
            float normalizedX,
            float normalizedY,
            bool pressed,
            bool released,
            int wheelDelta)
        {
            if (!_browserReady ||
                !WindowedInputPolicy.ShouldForwardProviderPointer(
                    _desiredVisible,
                    _actualVisible,
                    _revealPending,
                    _activeMenuPresentationId,
                    _acceptedMenuPresentationId,
                    _committedProviderInputPresentationId))
            {
                return;
            }

            normalizedX = WindowedInputPolicy.Normalize(normalizedX);
            normalizedY = WindowedInputPolicy.Normalize(normalizedY);
            wheelDelta = Math.Max(-1200, Math.Min(1200, wheelDelta));
            PostJson(WindowedInputPolicy.SerializeProviderPointerEvent(
                normalizedX,
                normalizedY,
                pressed,
                released,
                wheelDelta));
            if (pressed || released || wheelDelta != 0)
            {
                _trace(
                    "webview_pointer_edge",
                    $"x={normalizedX:F4} y={normalizedY:F4} " +
                    $"pressed={pressed} released={released} wheel={wheelDelta} " +
                    "bootstrap_hit_test_capture=False " +
                    "route=bridge-event forwarded=True");
            }
            if (!_pointerInputTraced)
            {
                _pointerInputTraced = true;
                _trace(
                    "webview_pointer_input_ready",
                    $"x={normalizedX:F4} y={normalizedY:F4} " +
                    $"pressed={pressed} released={released} wheel={wheelDelta} " +
                    "route=bridge-event forwarded=True");
            }
        }

        /// <summary>
        /// Private, pre-provider About input. This is deliberately a separate
        /// event from managed menu input so the browser can restrict it to
        /// Reactor-owned controls instead of exposing a generic bootstrap UI
        /// input channel.
        /// </summary>
        public void PostBootstrapPointerInput(
            float normalizedX,
            float normalizedY,
            bool pressed,
            bool released)
        {
            if (!_browserReady || !_desiredVisible ||
                float.IsNaN(normalizedX) || float.IsInfinity(normalizedX) ||
                float.IsNaN(normalizedY) || float.IsInfinity(normalizedY))
                return;

            PostJson(BridgeProtocol.SerializeEvent(
                WindowedInputPolicy.BootstrapPointerEventName,
                CreatePointerPayload(
                    WindowedInputPolicy.Normalize(normalizedX),
                    WindowedInputPolicy.Normalize(normalizedY),
                    pressed,
                    released,
                    wheelDelta: 0)));
        }

        public void PostBootstrapPointerReset()
        {
            if (!_browserReady) return;
            PostJson(BridgeProtocol.SerializeEvent(
                WindowedInputPolicy.BootstrapPointerResetEventName,
                JValue.CreateNull()));
            _trace(
                "webview_bootstrap_pointer_reset",
                "reason=bootstrap-reset route=bridge-event");
        }

        private void ResetPointerInput(string reason)
        {
            if (!_browserReady) return;
            PostJson(BridgeProtocol.SerializeEvent(
                WindowedInputPolicy.ProviderPointerResetEventName,
                JValue.CreateNull()));
            _trace("webview_pointer_reset", $"reason={reason} route=bridge-event");
        }

        private static JObject CreatePointerPayload(
            float normalizedX,
            float normalizedY,
            bool pressed,
            bool released,
            int wheelDelta) =>
            new JObject
            {
                ["x"] = normalizedX,
                ["y"] = normalizedY,
                ["pressed"] = pressed,
                ["released"] = released,
                ["wheelDelta"] = wheelDelta,
            };

        private async void InitializeBrowserAsync(
            CompositionWebViewHost control,
            int generation,
            bool useSoftwareComposition)
        {
            try
            {
                if (!IsCurrentController(control, generation)) return;
                _initializationTimer = Stopwatch.StartNew();
                _trace("webview_initialize_begin", null);
                RefreshGameWindow();
                _trace(
                    "webview_environment_contract",
                    WebView2EnvironmentFactory.Describe(
                        _userDataDirectory,
                        useSoftwareComposition));
                var environment = await WebView2EnvironmentFactory.CreateAsync(
                    _userDataDirectory,
                    useSoftwareComposition);
                if (!IsCurrentController(control, generation)) return;
                AttachEnvironmentHandler(environment, generation);
                _trace(
                    "webview_environment_ready",
                    $"version={environment.BrowserVersionString} " +
                    $"duration_ms={_initializationTimer.Elapsed.TotalMilliseconds:F3}");
                await EnsureControllerWithRetryAsync(environment, control, generation);
                if (!IsCurrentController(control, generation)) return;
                if (!_desiredVisible && Visible)
                {
                    Hide();
                    _trace(
                        "webview_controller_parent_parked",
                        $"overlay=0x{Handle.ToInt64():X} " +
                        $"native_visible={NativeMethods.IsWindowVisible(Handle)} " +
                        "controller_created_with_visible_parent=True");
                }
                _trace(
                    "webview_controller_ready",
                    $"duration_ms={_initializationTimer.Elapsed.TotalMilliseconds:F3}");
                var core = control.CoreWebView2;
                if (core == null) throw new InvalidOperationException(
                    "WebView2 did not publish a controller after initialization.");
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = _enableDevTools;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                AttachCoreHandlers(core, generation);
                _navigationTimer = Stopwatch.StartNew();
                WebView2LocalPage.Navigate(core, _uiDirectory);
                _boundsTimer.Start();
                _trace(
                    "webview_navigation_begin",
                    $"initialization_ms={_initializationTimer.Elapsed.TotalMilliseconds:F3} " +
                    $"composition={(useSoftwareComposition ? "software-stable" : "gpu-default")}");
            }
            catch (Exception error)
            {
                if (!IsCurrentController(control, generation)) return;
                ResetProviderInputAuthorization("initialization-failed");
                _browserReady = false;
                InvalidateBrowserContentReadiness("browser-initialization-failed");
                if (!_browserRecoveryInProgress && !_browserRecoveryQueued)
                    _desiredVisible = false;
                ClearPendingMessages("initialization_failed");
                ApplyVisibility(false);
                _startupFailed(error);
            }
        }

        private CompositionWebViewHost CreateWebViewHost()
        {
            // ParentWindow and DirectComposition remain rooted in this Reactor
            // HWND for the controller's whole lifetime. GTA is only the outer
            // window owner/z-order anchor and never a WebView2 parent.
            return new CompositionWebViewHost(this);
        }

        private bool IsCurrentController(CompositionWebViewHost control, int generation)
        {
            if (control.IsDisposed || !ReferenceEquals(control, _webView) ||
                generation != _controllerGeneration)
                return false;
            var core = control.CoreWebView2;
            return _attachedCore == null || ReferenceEquals(core, _attachedCore);
        }

        private void AttachCoreHandlers(CoreWebView2 core, int generation)
        {
            DetachCoreHandlers();
            _attachedCore = core;
            _attachedControllerGeneration = generation;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += OnWebViewProcessFailed;
        }

        private void DetachCoreHandlers()
        {
            var core = _attachedCore;
            _attachedCore = null;
            _attachedControllerGeneration = 0;
            if (core == null) return;
            try
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.WebMessageReceived -= OnWebMessageReceived;
                core.ProcessFailed -= OnWebViewProcessFailed;
            }
            catch (Exception error) when (
                error is InvalidOperationException ||
                error is COMException)
            {
                // A browser-process exit invalidates the old controller. The
                // replacement control owns all future callbacks.
            }
        }

        private void AttachEnvironmentHandler(
            CoreWebView2Environment environment,
            int generation)
        {
            DetachEnvironmentHandler();
            _attachedEnvironment = environment;
            _attachedEnvironmentGeneration = generation;
            environment.BrowserProcessExited += OnBrowserProcessExited;
        }

        private void DetachEnvironmentHandler()
        {
            var environment = _attachedEnvironment;
            _attachedEnvironment = null;
            _attachedEnvironmentGeneration = 0;
            if (environment == null) return;
            try { environment.BrowserProcessExited -= OnBrowserProcessExited; }
            catch (Exception error) when (
                error is InvalidOperationException ||
                error is COMException) { }
        }

        private void OnBrowserProcessExited(
            object? sender,
            CoreWebView2BrowserProcessExitedEventArgs args)
        {
            if (!ReferenceEquals(sender, _attachedEnvironment)) return;
            InvalidateRevealOnCurrentThread();
            _browserReady = false;
            ApplyVisibility(false);
            InvalidateBrowserContentReadiness("browser-process-exited");
            var generation = _attachedEnvironmentGeneration;
            _browserExitObservedGeneration = generation;
            _browserExitSignal?.TrySetResult(true);
            _trace(
                "webview_browser_process_exited",
                $"generation={generation} pid={args.BrowserProcessId} " +
                $"kind={args.BrowserProcessExitKind}");
        }

        private static void OnNewWindowRequested(
            object? sender,
            CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;

        private void OnWebViewProcessFailed(
            object? sender,
            CoreWebView2ProcessFailedEventArgs args)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            var kind = args.ProcessFailedKind;
            var automaticRecovery =
                kind == CoreWebView2ProcessFailedKind.FrameRenderProcessExited ||
                kind == CoreWebView2ProcessFailedKind.UtilityProcessExited ||
                kind == CoreWebView2ProcessFailedKind.SandboxHelperProcessExited ||
                kind == CoreWebView2ProcessFailedKind.GpuProcessExited ||
                kind == CoreWebView2ProcessFailedKind.PpapiPluginProcessExited ||
                kind == CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited;
            var senderIsCurrent = ReferenceEquals(sender, _attachedCore) &&
                _attachedControllerGeneration == _controllerGeneration;
            _trace(
                "webview_process_failed",
                $"kind={kind} reason={args.Reason} exit_code={args.ExitCode} " +
                $"description={args.ProcessDescription} automatic_recovery={automaticRecovery} " +
                $"recovery_attempts={_browserRecoveryAttempts} " +
                $"renderer_reload_attempts={_rendererReloadAttempts}");
            if (automaticRecovery)
            {
                // WebView2 automatically recreates these auxiliary processes.
                // Tearing down a healthy main document here would turn a
                // recoverable GPU/utility exit into a user-visible menu loss.
                return;
            }

            if (!WebView2ProcessFailurePolicy.ShouldAcceptFailure(
                    _browserRecoveryQueued || _browserRecoveryInProgress,
                    senderIsCurrent))
            {
                _trace(
                    "webview_process_failure_coalesced",
                    $"kind={kind} sender_current={senderIsCurrent} " +
                    $"queued={_browserRecoveryQueued} in_progress={_browserRecoveryInProgress} " +
                    $"generation={_controllerGeneration}");
                return;
            }

            InvalidateRevealOnCurrentThread();
            ResetProviderInputAuthorization("browser-process-failed");
            _browserReady = false;
            ApplyVisibility(false);
            ClearPendingMessages("browser_process_failed");
            _recoveryPresentationPaintAcknowledged = false;
            _recoveredSurfaceAwaitingPaint =
                _desiredVisible &&
                !string.IsNullOrWhiteSpace(_activeMenuPresentationId);
            InvalidateBrowserContentReadiness("browser-process-failed");

            var rendererFailure =
                kind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                kind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive;
            if (rendererFailure && _rendererReloadAttempts == 0)
            {
                _rendererReloadAttempts++;
                _browserRecoveryInProgress = true;
                _browserRecoveryMode = "renderer-reload";
                _initialInlineNavigationPending = true;
                _trace(
                    "webview_recovery_begin",
                    $"mode=renderer-reload attempt={_rendererReloadAttempts} kind={kind}");
                try
                {
                    _attachedCore?.Reload();
                    WatchRendererReloadAsync(_controllerGeneration);
                    return;
                }
                catch (Exception error) when (
                    error is InvalidOperationException ||
                    error is COMException)
                {
                    _trace(
                        "webview_renderer_reload_failed",
                        $"type={error.GetType().FullName} message={error.Message}");
                    _browserRecoveryInProgress = false;
                }
            }

            var browserProcessFailure =
                kind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
            ScheduleSoftwareBrowserRecovery(
                _controllerGeneration,
                disposeControllerBeforeWait: !browserProcessFailure,
                reason: kind.ToString());
        }

        private async void WatchRendererReloadAsync(int generation)
        {
            await Task.Delay(
                WebView2ProcessFailurePolicy.RendererReloadTimeoutMilliseconds);
            if (IsDisposed || Disposing ||
                generation != _controllerGeneration ||
                !_browserRecoveryInProgress ||
                !string.Equals(
                    _browserRecoveryMode,
                    "renderer-reload",
                    StringComparison.Ordinal))
            {
                return;
            }

            _browserRecoveryInProgress = false;
            _trace(
                "webview_renderer_reload_timeout",
                $"generation={generation} " +
                $"timeout_ms={WebView2ProcessFailurePolicy.RendererReloadTimeoutMilliseconds}");
            ScheduleSoftwareBrowserRecovery(
                generation,
                disposeControllerBeforeWait: true,
                reason: "renderer_reload_timeout");
        }

        private void ScheduleSoftwareBrowserRecovery(
            int failedGeneration,
            bool disposeControllerBeforeWait,
            string reason)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            if (_browserRecoveryQueued || _browserRecoveryInProgress)
            {
                _trace(
                    "webview_recovery_request_coalesced",
                    $"reason={reason} generation={failedGeneration}");
                return;
            }
            if (!WebView2ProcessFailurePolicy.CanRecover(_browserRecoveryAttempts))
            {
                OpenRecoveryCircuit(reason);
                return;
            }

            _browserRecoveryAttempts++;
            _browserRecoveryQueued = true;
            _browserRecoveryInProgress = true;
            _browserRecoveryMode = "software-recovery";
            if (disposeControllerBeforeWait)
                ReleaseFailedControllerForBrowserExit(failedGeneration);
            CoordinateBrowserExitAndRecoverAsync(failedGeneration, reason);
        }

        private async void CoordinateBrowserExitAndRecoverAsync(
            int failedGeneration,
            string reason)
        {
            var exitTimer = Stopwatch.StartNew();
            var exited = await WaitForBrowserExitAsync(failedGeneration);
            if (IsDisposed || Disposing) return;
            if (!exited || failedGeneration != _controllerGeneration)
            {
                _browserRecoveryQueued = false;
                _browserRecoveryInProgress = false;
                _trace(
                    "webview_browser_exit_wait_failed",
                    $"reason={reason} generation={failedGeneration} " +
                    $"current_generation={_controllerGeneration} " +
                    $"timeout_ms={WebView2ProcessFailurePolicy.BrowserExitTimeoutMilliseconds} " +
                    $"elapsed_ms={exitTimer.Elapsed.TotalMilliseconds:F3}");
                OpenRecoveryCircuit("browser_exit_timeout");
                return;
            }

            _trace(
                "webview_browser_exit_confirmed",
                $"reason={reason} generation={failedGeneration} " +
                $"elapsed_ms={exitTimer.Elapsed.TotalMilliseconds:F3}");
            BeginSoftwareBrowserRecovery(failedGeneration);
        }

        private async Task<bool> WaitForBrowserExitAsync(int generation)
        {
            if (_browserExitObservedGeneration == generation) return true;
            var signal = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _browserExitSignal = signal;
            if (_browserExitObservedGeneration == generation)
            {
                _browserExitSignal = null;
                return true;
            }
            var completed = await Task.WhenAny(
                signal.Task,
                Task.Delay(WebView2ProcessFailurePolicy.BrowserExitTimeoutMilliseconds));
            if (ReferenceEquals(_browserExitSignal, signal))
                _browserExitSignal = null;
            return completed == signal.Task &&
                _browserExitObservedGeneration == generation;
        }

        private void ReleaseFailedControllerForBrowserExit(int generation)
        {
            if (generation != _controllerGeneration) return;
            DetachCoreHandlers();
            var failedWebView = _webView;
            if (failedWebView.IsDisposed) return;
            try { failedWebView.Dispose(); }
            catch (Exception error) when (
                error is InvalidOperationException ||
                error is COMException) { }
        }

        private void BeginSoftwareBrowserRecovery(int failedGeneration)
        {
            if (IsDisposed || Disposing || failedGeneration != _controllerGeneration)
                return;

            _browserRecoveryQueued = false;
            ResetProviderInputAuthorization("controller-replaced");
            _trace(
                "webview_recovery_begin",
                $"mode=software-recovery attempt={_browserRecoveryAttempts} " +
                $"failed_generation={failedGeneration} desired_visible={_desiredVisible}");
            ReleaseFailedControllerForBrowserExit(failedGeneration);
            DetachEnvironmentHandler();
            _webView = CreateWebViewHost();
            _rootVisualRebindAttemptedSurfaceGeneration = null;
            _initialInlineNavigationPending = true;
            _surfacePrepared = false;
            _surfaceWasPreviouslyPresented = false;
            _lastBounds = Rectangle.Empty;
            _pointerInputTraced = false;
            ResetPresentationPaintEvidence();
            var generation = ++_controllerGeneration;
            InitializeBrowserAsync(
                _webView,
                generation,
                useSoftwareComposition:
                    WebView2ProcessFailurePolicy.ShouldUseSoftwareComposition(
                        persistentPresentedOverlay: true,
                        recovering: true));
        }

        private void ResetPresentationPaintEvidence()
        {
            _presentationPaintProbeGeneration++;
            _desktopPaintProbeGeneration++;
            _bootstrapPaintProbeGeneration++;
            _presentationPaintProbeInProgress = false;
            _browserPresentationPixelsVerified = false;
            _desktopPresentationPixelsVerified = false;
            _paintEvidencePresentationId = null;
            _desktopPaintSamples = null;
            _bootstrapPaintProofMode = null;
            _bootstrapPaintProofSurfaceGeneration = 0;
            _bootstrapPaintProofControllerGeneration = 0;
            _bootstrapPaintProofWidth = 0;
            _bootstrapPaintProofHeight = 0;
            _bootstrapPaintProofCompositionGeneration = 0;
            _bootstrapPaintProofConcrete = false;
            _bootstrapPaintProofGenerationMarkerMatched = false;
        }

        private void OpenRecoveryCircuit(string reason)
        {
            _browserRecoveryQueued = false;
            _browserRecoveryInProgress = false;
            _trace(
                "webview_recovery_circuit_open",
                $"attempts={_browserRecoveryAttempts} reason={reason}");
            _startupFailed(new InvalidOperationException(
                "The WebView2 browser could not complete its bounded recovery."));
        }

        private async Task EnsureControllerWithRetryAsync(
            CoreWebView2Environment environment,
            CompositionWebViewHost control,
            int generation)
        {
            var failedAttempts = 0;
            while (true)
            {
                try
                {
                    if (!IsCurrentController(control, generation)) return;
                    await control.EnsureCoreWebView2Async(environment);
                    if (!IsCurrentController(control, generation)) return;
                    return;
                }
                catch (COMException error)
                {
                    failedAttempts++;
                    if (!WebView2StartupPolicy.CanRetry(
                        error.HResult,
                        failedAttempts))
                    {
                        throw;
                    }
                    var delay = WebView2StartupPolicy.RetryDelayMilliseconds(
                        failedAttempts);
                    _trace(
                        "webview_controller_retry",
                        $"failed_attempt={failedAttempts} " +
                        $"next_attempt={failedAttempts + 1} " +
                        $"maximum_attempts={WebView2StartupPolicy.MaximumAttempts} " +
                        $"delay_ms={delay} hresult=0x{error.HResult:X8}");
                    await Task.Delay(delay);
                    if (!IsCurrentController(control, generation)) return;
                }
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            var core = sender as CoreWebView2;
            var control = _webView;
            var generation = _attachedControllerGeneration;
            if (!IsCurrentCallback(core, control, generation)) return;
            _trace(
                "webview_navigation_completed",
                $"success={args.IsSuccess} status={args.WebErrorStatus} " +
                $"duration_ms={(_navigationTimer?.Elapsed.TotalMilliseconds ?? 0d):F3}");
            if (!args.IsSuccess)
            {
                InvalidateRevealOnCurrentThread();
                ResetProviderInputAuthorization("navigation-failed");
                _browserReady = false;
                InvalidateBrowserContentReadiness("navigation-failed");
                ClearPendingMessages("navigation_failed");
                // A navigation can fail after a requested menu has entered
                // its deferred reveal. Cancel that reveal immediately so the
                // hidden composition controller releases GTA as its input
                // parent before recovery or shutdown begins.
                ApplyVisibility(false);
                if (_browserRecoveryInProgress)
                {
                    if (string.Equals(
                            _browserRecoveryMode,
                            "renderer-reload",
                            StringComparison.Ordinal) &&
                        WebView2ProcessFailurePolicy.CanRecover(_browserRecoveryAttempts))
                    {
                        _browserRecoveryInProgress = false;
                        ScheduleSoftwareBrowserRecovery(
                            generation,
                            disposeControllerBeforeWait: true,
                            reason: "renderer_navigation_failed");
                    }
                    else
                    {
                        OpenRecoveryCircuit("recovery_navigation_failed");
                    }
                }
                return;
            }

            try
            {
                var pageTiming = await WebView2PageReadiness.WaitAsync(
                    core!,
                    TimeSpan.FromSeconds(2));
                if (!IsCurrentCallback(core, control, generation)) return;
                _trace("webview_page_timing", $"metrics={pageTiming}");
            }
            catch (Exception error)
            {
                if (!IsCurrentCallback(core, control, generation)) return;
                ResetProviderInputAuthorization("page-readiness-failed");
                _browserReady = false;
                InvalidateBrowserContentReadiness("page-readiness-failed");
                ClearPendingMessages("page_readiness_failed");
                ApplyVisibility(false);
                _trace(
                    "webview_page_readiness_failed",
                    $"type={error.GetType().FullName} message={error.Message}");
                if (_browserRecoveryInProgress && string.Equals(
                        _browserRecoveryMode,
                        "renderer-reload",
                        StringComparison.Ordinal) &&
                    WebView2ProcessFailurePolicy.CanRecover(_browserRecoveryAttempts))
                {
                    _browserRecoveryInProgress = false;
                    ScheduleSoftwareBrowserRecovery(
                        generation,
                        disposeControllerBeforeWait: true,
                        reason: "renderer_readiness_failed");
                }
                else if (_browserRecoveryInProgress)
                {
                    OpenRecoveryCircuit("recovery_page_readiness_failed");
                }
                else
                {
                    _desiredVisible = false;
                    _startupFailed(error);
                }
                return;
            }

            if (IsDisposed || Disposing ||
                !IsCurrentCallback(core, control, generation))
            {
                return;
            }

            _browserReady = true;
            // A successful navigation publishes a new healthy browser epoch.
            // Deferred reveals capture this value and must cross a later STA
            // dispatch turn unchanged before they can expose native pixels.
            unchecked { _browserSurfaceHealthGeneration++; }
            if (_browserRecoveryInProgress)
            {
                _browserRecoveryInProgress = false;
                _trace(
                    "webview_recovery_complete",
                    $"mode={_browserRecoveryMode} attempt={_browserRecoveryAttempts} " +
                    $"renderer_reload_attempts={_rendererReloadAttempts} " +
                    $"desired_visible={_desiredVisible}");
                _browserRecoveryMode = string.Empty;
            }
            FlushPendingMessages();
            _trace(
                "webview_content_ready",
                $"navigation_ms={(_navigationTimer?.Elapsed.TotalMilliseconds ?? 0d):F3} " +
                $"initialization_ms={(_initializationTimer?.Elapsed.TotalMilliseconds ?? 0d):F3} " +
                $"desired_visible={_desiredVisible}");
            PublishBrowserContentReadiness();
            SynchronizeBounds();
        }

        private void FlushPendingMessages()
        {
            var core = _webView.CoreWebView2;
            if (!_browserReady || core == null || _pendingMessages.Count == 0)
            {
                return;
            }

            var count = _pendingMessages.Count;
            while (_pendingMessages.Count > 0)
            {
                var json = _pendingMessages.Dequeue();
                core.PostWebMessageAsJson(json);
                ObserveHostMessage(json);
            }
            _trace("webview_pending_messages_flushed", $"count={count}");
        }

        private void ClearPendingMessages(string reason)
        {
            if (_pendingMessages.Count == 0)
            {
                return;
            }

            var count = _pendingMessages.Count;
            _pendingMessages.Clear();
            _trace(
                "webview_pending_messages_cleared",
                $"count={count} reason={reason}");
        }

        private void PublishBrowserContentReadiness()
        {
            _browserContentReadinessPublished = true;
            _contentReady();
        }

        private void InvalidateBrowserContentReadiness(string reason)
        {
            // Browser/process failure notifications may overlap. Advance the
            // process-scoped generation exactly once for the document that was
            // actually published as ready.
            if (!_browserContentReadinessPublished)
            {
                _trace(
                    "webview_content_readiness_invalidation_skipped",
                    $"reason={reason} published=False");
                return;
            }

            _browserContentReadinessPublished = false;
            _browserContentUnavailable();
            _trace(
                "webview_content_readiness_invalidated",
                $"reason={reason} published=False");
        }

        private void PreserveBrowserContentReadinessAfterPresentationFailure(
            string reason)
        {
            // Native visibility, z-order, desktop proof, and provider input
            // are presentation health. A failure at those boundaries hides
            // the attempt and requires a fresh presentation request, but the
            // loaded page and process-scoped IPC endpoint remain valid for a
            // late managed provider to attach.
            _presentationUnavailable(reason);
            _trace(
                "webview_presentation_unavailable",
                $"reason={reason} browser_ready={_browserReady} " +
                $"content_ready={_browserContentReadinessPublished} " +
                "content_readiness_preserved=True pipe_attachable=True " +
                "action=hidden-rearm");
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (!ReferenceEquals(sender, _attachedCore))
            {
                args.Cancel = true;
                return;
            }
            if (!WebView2LocalPage.IsAllowedNavigation(
                    args.Uri,
                    ref _initialInlineNavigationPending))
            {
                args.Cancel = true;
                return;
            }

            // An accepted reload/navigation replaces the document that owns
            // the ready generation. Presentation retries never cross this
            // boundary and therefore cannot poison IPC attachability.
            InvalidateBrowserContentReadiness("navigation-starting");
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!ReferenceEquals(sender, _attachedCore)) return;
            if (!WebView2LocalPage.IsTrustedMessageSource(args.Source))
            {
                _trace("webview_message_rejected", $"source={args.Source}");
                return;
            }
            var json = args.WebMessageAsJson;
            if (TryObserveLiveAcceptanceMenuState(json)) return;
            ObserveBrowserRequest(json);
            if (_broker.TryEnqueue(json, out var error))
            {
                return;
            }

            var id = "invalid";
            try
            {
                var candidate = Newtonsoft.Json.Linq.JObject.Parse(json).Value<string>("id");
                if (!string.IsNullOrWhiteSpace(candidate) && candidate!.Length <= 64)
                {
                    id = candidate;
                }
            }
            catch
            {
            }

            PostJson(BridgeProtocol.SerializeResponse(BridgeResponse.Failure(
                id,
                error?.Code ?? "invalid_request",
                error?.Message ?? "The bridge request was rejected.")));
        }

        private bool TryObserveLiveAcceptanceMenuState(string json)
        {
            try
            {
                var message = JObject.Parse(json);
                if (!string.Equals(
                        message.Value<string>("kind"),
                        "acceptance",
                        StringComparison.Ordinal))
                    return false;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }

            if (!LiveAcceptanceContract.TryParseBrowserMenuState(json, out var state))
            {
                _trace(
                    "webview_acceptance_menu_state_rejected",
                    "reason=invalid_contract");
                return true;
            }

            var activeIdentityMatches =
                string.Equals(
                    state.PresentationId,
                    _activeMenuPresentationId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    state.ProviderId,
                    _activeMenuExtensionId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    state.RootMenuId,
                    _activeMenuId,
                    StringComparison.Ordinal);
            if (!activeIdentityMatches)
            {
                _trace(
                    "webview_acceptance_menu_state_rejected",
                    $"reason=inactive_presentation " +
                    $"presentation={state.PresentationId} " +
                    $"provider={state.ProviderId} root_menu={state.RootMenuId} " +
                    $"active_presentation={_activeMenuPresentationId ?? "none"} " +
                    $"active_provider={_activeMenuExtensionId ?? "none"} " +
                    $"active_root_menu={_activeMenuId ?? "none"}");
                return true;
            }

            _trace(
                "webview_acceptance_menu_state",
                $"presentation={state.PresentationId} " +
                $"provider={state.ProviderId} root_menu={state.RootMenuId} " +
                $"menu={state.MenuId} route={state.RouteId} " +
                $"section={state.SectionId} payload={state.PayloadStatus} " +
                $"items={state.ItemCount} content={state.ContentItemCount} " +
                $"actionable={state.ActionableItemCount} status={state.StatusItemCount}");
            return true;
        }

        private bool IsCurrentCallback(
            CoreWebView2? core,
            CompositionWebViewHost control,
            int generation) =>
            core != null &&
            !control.IsDisposed &&
            WebView2ProcessFailurePolicy.IsCurrentControllerGeneration(
                generation,
                _controllerGeneration,
                ReferenceEquals(core, _attachedCore),
                ReferenceEquals(control, _webView));

        private void ObserveBrowserRequest(string json)
        {
            try
            {
                var request = JObject.Parse(json);
                if (string.Equals(
                        request.Value<string>("kind"),
                        "host",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        request.Value<string>("command"),
                        "surface-ready",
                        StringComparison.Ordinal))
                {
                    ObserveHostSurfacePaintReady(request);
                    return;
                }
                if (!string.Equals(
                        request.Value<string>("method"),
                        "overlay.presentationReady",
                        StringComparison.Ordinal))
                    return;
                var presentationId =
                    (request["params"] as JObject)?.Value<string>("presentationId");
                if (!string.Equals(
                        presentationId,
                        _activeMenuPresentationId,
                        StringComparison.Ordinal))
                    return;
                _pendingPresentationReadyRequestId = request.Value<string>("id");
                _trace(
                    "webview_menu_presentation_ready_received",
                    $"presentation={presentationId} " +
                    "paint_boundary=assets-fonts-two-animation-frames");
                CommitPresentationComposition(presentationId!);
            }
            catch (Newtonsoft.Json.JsonException)
            {
            }
        }

        private void ObserveHostSurfacePaintReady(JObject request)
        {
            var mode = HostSurfaceMode.Normalize(request.Value<string>("mode"));
            var generation = request.Value<int?>("generation") ?? 0;
            if (!HostSurfaceMode.RequiresPaintProof(mode) || generation <= 0 ||
                !string.Equals(mode, _activeHostSurfaceMode, StringComparison.Ordinal) ||
                generation != _activeHostSurfaceGeneration)
            {
                _trace(
                    "webview_host_surface_paint_ack_ignored",
                    $"mode={mode} generation={generation} " +
                    $"active_mode={_activeHostSurfaceMode} " +
                    $"active_generation={_activeHostSurfaceGeneration}");
                return;
            }

            _paintAcknowledgedHostSurfaceMode = mode;
            _paintAcknowledgedHostSurfaceGeneration = generation;
            _trace(
                "webview_host_surface_paint_acknowledged",
                $"mode={mode} generation={generation} " +
                $"controller_generation={_controllerGeneration}");
            if (_desiredVisible && _browserReady && !_actualVisible && !_revealPending)
                SynchronizeBounds();
        }

        private void CommitPresentationComposition(string presentationId)
        {
            var committed = false;
            try
            {
                // This runs at the browser's exact presentationReady boundary,
                // before the managed provider can expose the host. Submit the
                // visual without blocking ordinary menu preparation. The one
                // synchronous completion fence is reserved for the later
                // reveal boundary immediately before Show().
                committed = _webView.SynchronizeBounds();
            }
            catch (Exception error) when (
                error is COMException ||
                error is InvalidOperationException)
            {
                _trace(
                    "webview_menu_composition_failed",
                    $"presentation={presentationId} type={error.GetType().FullName} " +
                    $"message={error.Message}");
            }

            _trace(
                "webview_menu_composition_committed",
                $"presentation={presentationId} committed={committed} " +
                "completion_wait=False " +
                $"visible={_actualVisible} desired_visible={_desiredVisible}");

            if (OverlayPresentationPolicy.UseLiveBrowserCaptureDiagnostics &&
                !_presentationPaintProbeInProgress)
            {
                var probeGeneration = ++_presentationPaintProbeGeneration;
                VerifyBrowserPresentationPixelsAsync(
                    presentationId,
                    _controllerGeneration,
                    probeGeneration);
            }
        }

        private async void VerifyBrowserPresentationPixelsAsync(
            string presentationId,
            int controllerGeneration,
            int probeGeneration)
        {
            _presentationPaintProbeInProgress = true;
            var timer = Stopwatch.StartNew();
            try
            {
                var capture = _webView.CapturePreviewAsync();
                var completed = await Task.WhenAny(
                    capture,
                    Task.Delay(BrowserPaintCaptureTimeoutMilliseconds));
                if (!ReferenceEquals(completed, capture))
                {
                    // Observe a late failure without retaining this window or
                    // surfacing an unobserved task exception during shutdown.
                    _ = capture.ContinueWith(
                        task => { _ = task.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted);
                    _trace(
                        "webview_menu_pixel_probe_timeout",
                        $"presentation={presentationId} " +
                        $"timeout_ms={BrowserPaintCaptureTimeoutMilliseconds}");
                    return;
                }

                var png = await capture;
                if (IsDisposed || Disposing ||
                    controllerGeneration != _controllerGeneration ||
                    probeGeneration != _presentationPaintProbeGeneration ||
                    !string.Equals(
                        presentationId,
                        _activeMenuPresentationId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                var evidence = AnalyzePresentationPixels(png, 0);
                _browserPresentationPixelsVerified = evidence.IsConcrete;
                _paintEvidencePresentationId = evidence.IsConcrete
                    ? presentationId
                    : null;
                _desktopPaintSamples = evidence.IsConcrete
                    ? evidence.DesktopSamples
                    : null;
                _trace(
                    evidence.IsConcrete
                        ? "webview_menu_pixels_verified"
                        : "webview_menu_pixels_unverified",
                    $"presentation={presentationId} image={evidence.Width}x{evidence.Height} " +
                    $"samples={evidence.SampleCount} opaque={evidence.OpaqueSampleCount} " +
                    $"visible_color={evidence.VisibleColorSampleCount} " +
                    $"desktop_samples={evidence.DesktopSamples.Count} " +
                    $"browser_surface_concrete={evidence.IsConcrete} " +
                    "desktop_presentation=unverified evidence_scope=browser_surface " +
                    $"duration_ms={timer.Elapsed.TotalMilliseconds:F3}");

                if (OverlayPresentationPolicy.UseLiveDesktopPixelSampling &&
                    evidence.IsConcrete && _actualVisible)
                {
                    BeginDesktopPaintVerification(
                        presentationId,
                        evidence.DesktopSamples,
                        _lastBounds);
                }
            }
            catch (Exception error) when (
                error is COMException ||
                error is InvalidOperationException ||
                error is ArgumentException ||
                error is IOException ||
                error is OutOfMemoryException)
            {
                _trace(
                    "webview_menu_pixel_probe_failed",
                    $"presentation={presentationId} type={error.GetType().FullName} " +
                    $"message={error.Message} duration_ms={timer.Elapsed.TotalMilliseconds:F3}");
            }
            finally
            {
                if (probeGeneration == _presentationPaintProbeGeneration)
                    _presentationPaintProbeInProgress = false;
            }
        }

        private static BrowserPaintEvidence AnalyzePresentationPixels(
            byte[] png,
            ulong expectedPaintIdentity)
        {
            if (png == null || png.Length == 0)
                return BrowserPaintEvidence.Empty;

            using var stream = new MemoryStream(png, writable: false);
            using var bitmap = new Bitmap(stream);
            // Startup/status surfaces intentionally contain only a compact
            // card and logo. A coarse 32x18 lattice can miss that content and
            // mistake a correctly painted sparse surface for transparency.
            var columns = Math.Min(128, Math.Max(1, bitmap.Width));
            var rows = Math.Min(72, Math.Max(1, bitmap.Height));
            var sampleCount = 0;
            var opaque = 0;
            var visibleColor = 0;
            var desktop = new List<DesktopPaintSample>(MaximumDesktopPaintSamples);

            for (var row = 0; row < rows; row++)
            {
                var y = Math.Min(
                    bitmap.Height - 1,
                    (int)Math.Round((row + 0.5d) * bitmap.Height / rows - 0.5d));
                for (var column = 0; column < columns; column++)
                {
                    var x = Math.Min(
                        bitmap.Width - 1,
                        (int)Math.Round((column + 0.5d) * bitmap.Width / columns - 0.5d));
                    var color = bitmap.GetPixel(x, y);
                    sampleCount++;
                    // Reactor intentionally uses translucent cards, text, and
                    // glow. Count visibly painted alpha rather than requiring
                    // a nearly opaque pixel; the exact identity marker below
                    // remains the causal stale/error-frame guard.
                    if (color.A < 32)
                        continue;
                    opaque++;
                    var brightest = Math.Max(color.R, Math.Max(color.G, color.B));
                    if (brightest < 48 || color.R + color.G + color.B < 100)
                        continue;
                    visibleColor++;
                    if (desktop.Count < MaximumDesktopPaintSamples && color.A >= 250)
                    {
                        desktop.Add(new DesktopPaintSample(
                            (x + 0.5d) / bitmap.Width,
                            (y + 0.5d) / bitmap.Height,
                            color));
                    }
                }
            }

            var markerX = 0;
            var markerY = 0;
            var markerStride = 0;
            var paintIdentityMarkerMatched = false;
            if (expectedPaintIdentity != 0)
            {
                paintIdentityMarkerMatched =
                    OverlayPresentationPolicy.TryFindPaintIdentityMarker(
                    bitmap.Width,
                    bitmap.Height,
                    expectedPaintIdentity,
                    (x, y) => unchecked((uint)bitmap.GetPixel(x, y).ToArgb()),
                    out markerX,
                    out markerY,
                    out markerStride);
            }
            if (paintIdentityMarkerMatched)
            {
                // The marker is the causal identity of this exact transfer,
                // so place its eight opaque cells ahead of generic samples.
                // The independent desktop witness can then reject a stale or
                // absent surface even when the GTA frame has similar colours.
                desktop.Clear();
                for (var byteIndex = 0; byteIndex < 8; byteIndex++)
                {
                    var x = markerX + byteIndex * markerStride;
                    desktop.Add(new DesktopPaintSample(
                        (x + 0.5d) / bitmap.Width,
                        (markerY + 0.5d) / bitmap.Height,
                        bitmap.GetPixel(x, markerY)));
                }
            }
            return new BrowserPaintEvidence(
                bitmap.Width,
                bitmap.Height,
                sampleCount,
                opaque,
                visibleColor,
                desktop,
                paintIdentityMarkerMatched);
        }

        private static bool HasPaintIdentityMarker(
            Bitmap bitmap,
            ulong paintIdentity)
        {
            return OverlayPresentationPolicy.HasPaintIdentityMarker(
                bitmap.Width,
                bitmap.Height,
                paintIdentity,
                (x, y) => unchecked((uint)bitmap.GetPixel(x, y).ToArgb()));
        }

        private void BeginDesktopPaintVerification(
            string presentationId,
            IReadOnlyList<DesktopPaintSample> samples,
            Rectangle bounds)
        {
            if (_desktopPresentationPixelsVerified || samples.Count == 0 ||
                bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }
            var generation = ++_desktopPaintProbeGeneration;
            VerifyDesktopPresentationPixelsAsync(
                presentationId,
                samples,
                bounds,
                generation);
        }

        private IReadOnlyList<DesktopPaintSample> KnownPresentationPaintSamples()
        {
            if (!string.Equals(
                    _activeMenuExtensionId,
                    "allin1.gbay",
                    StringComparison.Ordinal) ||
                _lastBounds.Width <= 0 || _lastBounds.Height <= 0)
            {
                return Array.Empty<DesktopPaintSample>();
            }

            // GBAY owns an opaque 84vw x 90vh shell. Sample only the quiet
            // 4px bands inside its header, section navigation, and footer so
            // catalog text/images cannot make this signature data-dependent.
            var width = _lastBounds.Width;
            var height = _lastBounds.Height;
            var shellLeft = width * 0.08d;
            var shellWidth = width * 0.84d;
            var shellTop = height * 0.05d;
            var shellHeight = Math.Max(1d, height * 0.90d);
            var headerY = shellTop + 4d;
            var navigationY = shellTop + 80d;
            var footerY = shellTop + shellHeight - 4d;
            var result = new List<DesktopPaintSample>(12);
            foreach (var fraction in new[] { 0.15d, 0.35d, 0.65d, 0.85d })
            {
                var normalizedX = (shellLeft + shellWidth * fraction) / width;
                result.Add(new DesktopPaintSample(
                    normalizedX,
                    headerY / height,
                    Color.FromArgb(0x12, 0x48, 0x2A)));
                result.Add(new DesktopPaintSample(
                    normalizedX,
                    navigationY / height,
                    Color.FromArgb(0x49, 0xCF, 0x75)));
                result.Add(new DesktopPaintSample(
                    normalizedX,
                    footerY / height,
                    Color.FromArgb(0x12, 0x48, 0x2A)));
            }
            return result;
        }

        private async void VerifyDesktopPresentationPixelsAsync(
            string presentationId,
            IReadOnlyList<DesktopPaintSample> samples,
            Rectangle bounds,
            int generation)
        {
            await Task.Delay(DesktopPaintSettleMilliseconds);
            if (!CanVerifyDesktopPaint(presentationId, generation))
                return;

            var first = CompareDesktopPaint(samples, bounds);
            if (first.IsConcrete)
            {
                _desktopPresentationPixelsVerified = true;
                TraceDesktopPaint(
                    "webview_menu_desktop_pixels_verified",
                    presentationId,
                    first,
                    recovery: null);
                return;
            }

            RootVisualRebindResult? rebind = null;
            if (OverlayPresentationPolicy.ShouldAttemptRootVisualRebind(
                    _activeHostSurfaceGeneration,
                    _rootVisualRebindAttemptedSurfaceGeneration,
                    hostVisible: _actualVisible && Visible,
                    desktopPresentationConcrete: first.IsConcrete))
            {
                // Claim the generation before touching COM. A failed recovery
                // is still the one permitted attempt for this surface and
                // cannot become a recurring compositor loop.
                _rootVisualRebindAttemptedSurfaceGeneration =
                    _activeHostSurfaceGeneration;
                try
                {
                    // The first settled desktop sample did not expose the
                    // browser frame. Perform one real RootVisualTarget
                    // detach/commit/rebind/bounds/commit sequence, then
                    // reassert bounded GTA z-order.
                    rebind = _webView.RebindRootVisual();
                    NativeMethods.SetWindowPos(
                        Handle,
                        NativeMethods.HwndTopMost,
                        bounds.Left,
                        bounds.Top,
                        bounds.Width,
                        bounds.Height,
                        NativeMethods.SwpNoActivate);
                    ApplyOverlayTopMost(true);
                    _trace(
                        rebind.Value.Succeeded
                            ? "webview_menu_root_visual_rebound"
                            : "webview_menu_root_visual_rebind_failed",
                        $"presentation={presentationId} " +
                        $"surface={_activeHostSurfaceMode} " +
                        $"surface_generation={_activeHostSurfaceGeneration} " +
                        $"outcome={rebind.Value.Outcome} " +
                        $"device_state={rebind.Value.DeviceState} " +
                        $"hresult=0x{rebind.Value.HResult:X8} " +
                        $"composition_generation={rebind.Value.CompositionGeneration} " +
                        $"browser_surface_concrete={_browserPresentationPixelsVerified} " +
                        "desktop_presentation_concrete=False completion_wait=False");
                }
                catch (Exception error) when (
                    error is COMException ||
                    error is InvalidOperationException)
                {
                    _trace(
                        "webview_menu_root_visual_rebind_failed",
                        $"presentation={presentationId} type={error.GetType().FullName} " +
                        $"message={error.Message} " +
                        $"surface_generation={_activeHostSurfaceGeneration}");
                }
            }

            await Task.Delay(DesktopPaintSettleMilliseconds);
            if (!CanVerifyDesktopPaint(presentationId, generation))
                return;
            var second = CompareDesktopPaint(samples, bounds);
            _desktopPresentationPixelsVerified = second.IsConcrete;
            TraceDesktopPaint(
                second.IsConcrete
                    ? "webview_menu_desktop_pixels_recovered"
                    : "webview_menu_desktop_pixels_unverified",
                presentationId,
                second,
                rebind);
            if (!second.IsConcrete && !_browserPresentationPixelsVerified &&
                !_presentationPaintProbeInProgress)
            {
                var probeGeneration = ++_presentationPaintProbeGeneration;
                VerifyBrowserPresentationPixelsAsync(
                    presentationId,
                    _controllerGeneration,
                    probeGeneration);
            }
        }

        private bool CanVerifyDesktopPaint(string presentationId, int generation) =>
            !IsDisposed && !Disposing &&
            generation == _desktopPaintProbeGeneration &&
            _actualVisible && _desiredVisible && Visible &&
            string.Equals(
                presentationId,
                _activeMenuPresentationId,
                StringComparison.Ordinal);

        private static DesktopPaintEvidence CompareDesktopPaint(
            IReadOnlyList<DesktopPaintSample> samples,
            Rectangle bounds)
        {
            var readable = 0;
            var matching = 0;
            foreach (var sample in samples)
            {
                var x = bounds.Left + Math.Max(0, Math.Min(
                    bounds.Width - 1,
                    (int)Math.Round(sample.NormalizedX * bounds.Width - 0.5d)));
                var y = bounds.Top + Math.Max(0, Math.Min(
                    bounds.Height - 1,
                    (int)Math.Round(sample.NormalizedY * bounds.Height - 0.5d)));
                if (!NativeMethods.TryReadDesktopPixel(x, y, out var observed))
                    continue;
                readable++;
                if (Math.Abs(observed.R - sample.Expected.R) <= 56 &&
                    Math.Abs(observed.G - sample.Expected.G) <= 56 &&
                    Math.Abs(observed.B - sample.Expected.B) <= 56)
                {
                    matching++;
                }
            }
            return new DesktopPaintEvidence(readable, matching);
        }

        private void TraceDesktopPaint(
            string stage,
            string presentationId,
            DesktopPaintEvidence evidence,
            RootVisualRebindResult? recovery)
        {
            _trace(
                stage,
                $"presentation={presentationId} readable={evidence.ReadableSampleCount} " +
                $"matching={evidence.MatchingSampleCount} concrete={evidence.IsConcrete} " +
                $"browser_surface_concrete={_browserPresentationPixelsVerified} " +
                $"desktop_presentation_concrete={evidence.IsConcrete} " +
                $"root_rebind_attempted={recovery.HasValue} " +
                $"root_rebind_outcome={(recovery.HasValue ? recovery.Value.Outcome.ToString() : "none")} " +
                "evidence_scope=desktop_presentation completion_wait=False");
        }

        private void SynchronizeBounds()
        {
            RefreshGameWindow();
            var minimized = NativeMethods.IsIconic(_gtaWindow);
            var foreground = IsInteractionForeground();
            var hasBounds = NativeMethods.TryGetClientBounds(_gtaWindow, out var target);
            if (_finalRevealOffscreenLeaseActive)
            {
                var retainedGeneration = _finalRevealOffscreenLeaseGeneration;
                var retainedLeaseValid = !minimized && foreground && hasBounds &&
                    target == _finalRevealOffscreenLeaseTarget &&
                    OwnsFinalRevealOffscreenLease(retainedGeneration);
                if (!retainedLeaseValid)
                {
                    if (OverlayPresentationPolicy.ShouldDismissForForegroundLoss(
                            _desiredVisible,
                            _actualVisible || _revealPending,
                            foreground))
                    {
                        _desiredVisible = false;
                    }
                    _trace(
                        "webview_final_reveal_offscreen_lease_validation_failed",
                        $"generation={retainedGeneration} minimized={minimized} " +
                        $"foreground={foreground} has_bounds={hasBounds} " +
                        $"expected_target={_finalRevealOffscreenLeaseTarget.Left}," +
                        $"{_finalRevealOffscreenLeaseTarget.Top}," +
                        $"{_finalRevealOffscreenLeaseTarget.Width}," +
                        $"{_finalRevealOffscreenLeaseTarget.Height} " +
                        $"observed_target={target.Left},{target.Top}," +
                        $"{target.Width},{target.Height}");
                    ApplyVisibility(false);
                }
                // A retained pixel proof owns an intentionally visible
                // off-screen HWND. A queued timer or duplicate visibility
                // request may validate or cancel it, but must never retire it
                // and PrepareSurface onto GTA before the ingress boundary.
                return;
            }
            if (_browserReady && !minimized && hasBounds)
            {
                // Controller creation and bootstrap pixel qualification may
                // temporarily Show this HWND off-screen while actual overlay
                // visibility is still uncommitted. Bounds synchronization is
                // the earliest path that can move it onto GTA, so retire that
                // lease before PrepareSurface performs any SetWindowPos.
                RetireUncommittedNativeVisibilityLease("bounds-sync");
                PrepareSurface(target);
            }
            if (_externalPresentationOwnsPixels)
            {
                ParkForExternalPresentation("bounds-sync");
                return;
            }
            var recoveredSurfaceReady =
                WebView2ProcessFailurePolicy.CanRevealRecoveredSurface(
                    _recoveredSurfaceAwaitingPaint,
                    !string.IsNullOrWhiteSpace(_activeMenuPresentationId),
                    _recoveryPresentationPaintAcknowledged);
            var shouldPresent = OverlayPresentationPolicy.ShouldPresent(
                _desiredVisible,
                _browserReady,
                minimized,
                foreground,
                hasBounds) && recoveredSurfaceReady;
            if (OverlayPresentationPolicy.ShouldDismissForForegroundLoss(
                    _desiredVisible,
                    _actualVisible || _revealPending,
                    foreground))
            {
                _desiredVisible = false;
                _trace(
                    "webview_visibility_dismissed",
                    "reason=game_not_foreground " +
                    DescribeInteractionForeground());
                shouldPresent = false;
            }
            if (!shouldPresent)
            {
                TraceVisibilitySuppression(
                    minimized,
                    foreground,
                    hasBounds,
                    recoveredSurfaceReady);
                ApplyVisibility(false);
                return;
            }

            _lastVisibilitySuppression = string.Empty;

            ApplyVisibility(true);
            MaintainOverlayZOrder(foreground);
        }

        private void MaintainOverlayZOrder(bool gameForeground)
        {
            if (!IsHandleCreated || IsDisposed || Disposing ||
                _gtaWindow == IntPtr.Zero)
            {
                return;
            }

            var comparisonKnown = NativeMethods.TryIsWindowAbove(
                Handle,
                _gtaWindow,
                out var overlayAboveGame);
            if (!OverlayPresentationPolicy.ShouldReassertOverlayZOrder(
                    _desiredVisible,
                    _actualVisible,
                    gameForeground,
                    comparisonKnown,
                    overlayAboveGame))
            {
                return;
            }

            ReassertOverlayZOrder("game-overtook-overlay");
        }

        private void PrepareSurface(Rectangle target)
        {
            if (target.Width <= 0 || target.Height <= 0)
            {
                return;
            }

            var changed = target != _lastBounds;
            if (changed)
            {
                var flags = NativeMethods.SwpNoActivate;
                var insertAfter = NativeMethods.HwndTopMost;
                if (!_actualVisible)
                {
                    // A pending hidden reveal is not yet allowed to alter the
                    // desktop z-order. Its exact pixel probe runs offscreen;
                    // the one TOPMOST promotion belongs to the final Show.
                    flags |= NativeMethods.SwpNoZOrder;
                    insertAfter = IntPtr.Zero;
                }
                NativeMethods.SetWindowPos(
                    Handle,
                    insertAfter,
                    target.Left,
                    target.Top,
                    target.Width,
                    target.Height,
                    flags);
                _lastBounds = target;
                _webView.SynchronizeBounds();
            }

            if (_surfacePrepared)
            {
                if (changed)
                {
                    _trace(
                        "webview_surface_resized",
                        $"bounds={target.Left},{target.Top},{target.Width},{target.Height} " +
                        $"visible={_actualVisible} reveal_pending={_revealPending}");
                }
                return;
            }

            _surfacePrepared = true;
            _trace(
                "webview_surface_prepared",
                $"bounds={target.Left},{target.Top},{target.Width},{target.Height} " +
                $"browser_ready={_browserReady} form_visible={Visible}");
        }

        private void RefreshGameWindow()
        {
            var previous = _gtaWindow;
            var resolved = NativeMethods.ResolveGameWindow(
                _gtaProcessId,
                previous,
                IsHandleCreated ? Handle : IntPtr.Zero,
                out var detail);
            if (resolved != IntPtr.Zero)
            {
                _gtaWindow = resolved;
            }
            else if (!NativeMethods.HasSubstantialClientBounds(previous))
            {
                _gtaWindow = IntPtr.Zero;
            }

            if (!_gameWindowResolutionTraced || _gtaWindow != previous)
            {
                _gameWindowResolutionTraced = true;
                _trace(
                    "game_window_resolved",
                    $"previous=0x{previous.ToInt64():X} current=0x{_gtaWindow.ToInt64():X} {detail}");
            }

            SynchronizeWebViewInputParent();

            SynchronizeGameWindowOwner();
        }

        private void SynchronizeWebViewInputParent()
        {
            if (!IsHandleCreated || IsDisposed || Disposing)
            {
                return;
            }

            var requestedParent = WindowedInputPolicy.ResolveInputParent(
                _actualVisible,
                _revealPending,
                _gtaWindow,
                Handle);
            if (_webViewInputParentWindow == requestedParent)
            {
                return;
            }

            var applied = _webView.SetInputParentWindow(requestedParent);
            if (applied)
            {
                _webViewInputParentWindow = requestedParent;
            }
            _trace(
                applied
                    ? "webview_input_parent_applied"
                    : "webview_input_parent_failed",
                $"parent=0x{requestedParent.ToInt64():X} " +
                $"overlay=0x{(IsHandleCreated ? Handle.ToInt64() : 0):X} " +
                $"game=0x{_gtaWindow.ToInt64():X} " +
                $"visible={_actualVisible} reveal_pending={_revealPending} " +
                "purpose=same-process-controller-parent");
        }

        private void SynchronizeGameWindowOwner()
        {
            if (!IsHandleCreated)
            {
                return;
            }

            var requestedOwner = OverlayPresentationPolicy.ShouldAttachToGameWindow(
                    _actualVisible,
                    _revealPending)
                ? _gtaWindow
                : IntPtr.Zero;
            if (_ownedGameWindow == requestedOwner &&
                !_gameWindowOwnerNeedsRetry)
            {
                return;
            }

            if (!NativeMethods.TrySetWindowOwner(
                Handle,
                requestedOwner,
                out var previousOwner,
                out var observedOwner,
                out var error))
            {
                // Do not cache a failed transition. The next bounds tick or
                // lifecycle synchronization retries the exact owner request.
                _gameWindowOwnerNeedsRetry = true;
                _trace(
                    "game_window_owner_failed",
                    $"previous=0x{previousOwner.ToInt64():X} " +
                    $"requested=0x{requestedOwner.ToInt64():X} " +
                    $"observed=0x{observedOwner.ToInt64():X} error={error} " +
                    $"requested_visible={_desiredVisible} actual_visible={_actualVisible} " +
                    $"reveal_pending={_revealPending}");
                return;
            }

            _ownedGameWindow = observedOwner;
            _gameWindowOwnerNeedsRetry = false;
            var parentPositionNotified =
                _webView.NotifyParentWindowPositionChanged();
            _trace(
                requestedOwner == IntPtr.Zero
                    ? "game_window_owner_detached"
                    : "game_window_owner_applied",
                $"previous=0x{previousOwner.ToInt64():X} owner=0x{observedOwner.ToInt64():X} " +
                $"requested_visible={_desiredVisible} actual_visible={_actualVisible} " +
                $"reveal_pending={_revealPending} " +
                $"parent_position_notified={parentPositionNotified}");
        }

        private void ApplyVisibility(bool visible)
        {
            if (visible)
            {
                if (_externalPresentationOwnsPixels)
                {
                    ParkForExternalPresentation("visibility-apply");
                    return;
                }
                if (_actualVisible || _revealPending || !_surfacePrepared ||
                    _lastBounds.Width <= 0 || _lastBounds.Height <= 0)
                {
                    return;
                }

                BeginDeferredReveal();
                return;
            }

            var wasVisible = _actualVisible;
            var wasPublished = _visibilityPublished;
            var wasPending = _revealPending;
            SuspendProviderInputCommit("visibility-hidden");
            RevokeFinalRevealOffscreenLease("visibility-hidden");
            _revealGeneration++;
            _revealPending = false;
            _transferState.Hide();
            if (Visible)
            {
                Hide();
            }
            ApplyOverlayTopMost(false);
            _actualVisible = false;
            _visibilityPublished = false;
            UpdateProviderPointerShield();
            SynchronizeGameWindowOwner();
            SynchronizeWebViewInputParent();
            if (wasPublished)
            {
                _visibilityChanged(false);
            }
            if (wasVisible || wasPending)
            {
                _trace(
                    "webview_visibility_applied",
                    $"visible=False desired_visible={_desiredVisible} browser_ready={_browserReady} " +
                    $"reveal_cancelled={wasPending} " +
                    $"initialization_ms={(_initializationTimer?.Elapsed.TotalMilliseconds ?? 0d):F3}");
            }
        }

        private void ParkForExternalPresentation(string reason)
        {
            var wasVisible = _actualVisible || Visible;
            var wasPending = _revealPending;
            RevokeFinalRevealOffscreenLease("external-presenter");
            _revealGeneration++;
            _revealPending = false;
            _revealDeferredForIngress = false;
            if (Visible)
                Hide();
            ApplyOverlayTopMost(false);
            _actualVisible = false;
            // Physical HWND visibility is no longer the logical provider
            // visibility source while native owns pixels. Suppress a false
            // callback, but let a later WebView reveal publish a fresh edge.
            _visibilityPublished = false;
            UpdateProviderPointerShield();
            SynchronizeGameWindowOwner();
            SynchronizeWebViewInputParent();
            if (wasVisible || wasPending ||
                string.Equals(reason, "ownership-acquired", StringComparison.Ordinal))
            {
                _trace(
                    "webview_external_presenter_parked",
                    $"reason={reason} desired_visible={_desiredVisible} " +
                    $"presentation={_activeMenuPresentationId ?? "none"} " +
                    $"reveal_cancelled={wasPending} logical_visibility=preserved");
            }
        }

        private void BeginDeferredReveal()
        {
            if (HasPendingRevealIngress())
            {
                _revealDeferredForIngress = true;
                _trace(
                    "webview_reveal_waiting_for_ingress",
                    $"pending_ingress={_pendingRevealIngress} " +
                    $"presentation={_activeMenuPresentationId ?? "none"} " +
                    $"surface={_activeHostSurfaceMode}");
                return;
            }
            if (HostSurfaceMode.RequiresPaintProof(_activeHostSurfaceMode) &&
                (_activeHostSurfaceGeneration <= 0 ||
                 _activeHostSurfaceGeneration != _paintAcknowledgedHostSurfaceGeneration ||
                 !string.Equals(
                     _activeHostSurfaceMode,
                     _paintAcknowledgedHostSurfaceMode,
                     StringComparison.Ordinal)))
            {
                _trace(
                    "webview_reveal_waiting_for_host_surface_paint",
                    $"surface={_activeHostSurfaceMode} " +
                    $"surface_generation={_activeHostSurfaceGeneration} " +
                    $"ack_surface={_paintAcknowledgedHostSurfaceMode} " +
                    $"ack_generation={_paintAcknowledgedHostSurfaceGeneration}");
                return;
            }
            if (!string.IsNullOrWhiteSpace(_activeMenuPresentationId) &&
                !string.Equals(
                    _activeMenuPresentationId,
                    _acceptedMenuPresentationId,
                    StringComparison.Ordinal))
            {
                _trace(
                    "webview_reveal_waiting_for_menu_paint",
                    $"presentation={_activeMenuPresentationId} " +
                    $"accepted={_acceptedMenuPresentationId ?? "none"} " +
                    $"provider_session_generation={_providerSessionGeneration}");
                return;
            }
            if (string.Equals(
                    _activeHostSurfaceMode,
                    HostSurfaceMode.None,
                    StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(_activeMenuPresentationId))
            {
                _trace(
                    "webview_reveal_waiting_for_render_identity",
                    $"surface={_activeHostSurfaceMode} " +
                    $"provider_session_generation={_providerSessionGeneration}");
                return;
            }
            _revealDeferredForIngress = false;
            _revealPending = true;
            UpdateProviderPointerShield();
            // Requested visibility alone never owns GTA. Establish ownership
            // exactly when a validated surface enters its deferred reveal.
            SynchronizeGameWindowOwner();
            SynchronizeWebViewInputParent();
            var generation = ++_revealGeneration;
            _revealPreparedAt = Stopwatch.GetTimestamp();
            // Bootstrap pixel qualification briefly Shows this parent far
            // off-screen so WebView2 can produce a concrete CapturePreview.
            // A typed presentation may supersede that async probe before its
            // finally block owns the cleanup lease. Never move such a
            // temporary, already-visible HWND onto GTA before the compositor
            // fence; retire the probe lease first and reveal normally only
            // after the accepted provider frame has committed.
            RetireUncommittedNativeVisibilityLease("deferred-reveal");
            // Keep the proof lease hidden and non-topmost. Promotion happens
            // exactly once at the final Show boundary, after the exact frame
            // identity has passed CapturePreview. Moving this HWND through
            // TOPMOST before and during the off-screen probe produced the
            // visible true/false/true flicker seen on GBAY reopen.
            NativeMethods.SetWindowPos(
                Handle,
                IntPtr.Zero,
                _lastBounds.Left,
                _lastBounds.Top,
                Math.Max(1, _lastBounds.Width),
                Math.Max(1, _lastBounds.Height),
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
            // A frontend -> Story transition can leave the long-lived browser
            // readable through CapturePreview while its DirectComposition root
            // is no longer published over GTA. Do not replace that root while
            // this parent is hidden: the cold initializer publishes it only
            // after the final proof lease has made this HWND WS_VISIBLE far
            // off-screen. Hidden preparation is synchronization-only.
            var publishInitializerRootAfterFinalShow =
                _coldHostVisibleRootPublishRequired &&
                HostSurfaceMode.IsInitializing(_activeHostSurfaceMode);
            var refresh = OverlayPresentationPolicy.SelectRevealCompositionRefresh(
                _browserReady,
                _surfacePrepared,
                _actualVisible,
                _revealPending,
                _surfaceWasPreviouslyPresented,
                deferFreshRootUntilVisibleLease:
                    publishInitializerRootAfterFinalShow);
            RootVisualRebindResult? rootRebind = null;
            var compositionRefreshed = false;
            switch (refresh)
            {
                case RevealCompositionRefresh.Synchronize:
                    compositionRefreshed = _webView.SynchronizeBounds();
                    break;
                case RevealCompositionRefresh.RebindRootVisual:
                    rootRebind = _webView.RebindRootVisual();
                    compositionRefreshed = rootRebind.Value.Succeeded;
                    break;
            }
            _trace(
                "webview_composition_refresh_requested",
                $"generation={generation} " +
                $"presentation={_activeMenuPresentationId ?? "none"} " +
                $"surface={_activeHostSurfaceMode} " +
                $"surface_generation={_activeHostSurfaceGeneration} " +
                $"refresh={refresh} committed={compositionRefreshed} " +
                $"root_rebind_outcome=" +
                $"{(rootRebind.HasValue ? rootRebind.Value.Outcome.ToString() : "none")} " +
                $"composition_generation=" +
                $"{(rootRebind.HasValue ? rootRebind.Value.CompositionGeneration : _webView.CompositionGeneration)}");
            if (!compositionRefreshed)
            {
                FailCompositionReveal(
                    refresh,
                    rootRebind,
                    generation);
                return;
            }
            var transferIdentity = CreateTransferIdentity(
                ++_transferGeneration,
                _controllerGeneration,
                _providerSessionGeneration,
                _activeMenuPresentationId,
                _activeHostSurfaceMode,
                _activeHostSurfaceGeneration,
                _webView.CompositionGeneration,
                _lastBounds);
            if (!_transferState.Begin(transferIdentity))
            {
                _trace(
                    "webview_transfer_stale_begin_rejected",
                    $"boundary=deferred-reveal generation={generation} " +
                    $"presentation={_activeMenuPresentationId ?? "none"} " +
                    $"surface={_activeHostSurfaceMode} phase={_transferState.Phase}");
                return;
            }
            _desktopPresentationPixelsVerified = false;
            TraceTransferState("began", transferIdentity);
            _trace(
                "webview_reveal_prepared",
                $"generation={generation} bounds={_lastBounds.Width}x{_lastBounds.Height} " +
                $"presentation={_activeMenuPresentationId ?? "none"} " +
                $"visible_root_publish_pending={publishInitializerRootAfterFinalShow} " +
                $"composition_committed={compositionRefreshed} " +
                $"request_to_prepare_ms={ElapsedMilliseconds(_revealRequestedAt):F3}");
            QueueDeferredRevealCommit(
                generation,
                transferIdentity,
                refresh,
                rootRebind,
                _controllerGeneration,
                _providerSessionGeneration,
                _activeMenuPresentationId,
                _activeHostSurfaceMode,
                _activeHostSurfaceGeneration,
                _webView.CompositionGeneration,
                CaptureRevealIngressEpoch(),
                _browserSurfaceHealthGeneration);
        }

        private void RetireUncommittedNativeVisibilityLease(string reason)
        {
            if (_finalRevealOffscreenLeaseActive)
            {
                RevokeFinalRevealOffscreenLease(reason);
                return;
            }
            if (!Visible || _actualVisible)
            {
                return;
            }

            Hide();
            _trace(
                "webview_bootstrap_probe_visibility_retired",
                $"reason={reason} generation={_revealGeneration} " +
                $"presentation={_activeMenuPresentationId ?? "none"} " +
                $"surface={_activeHostSurfaceMode} " +
                $"surface_generation={_activeHostSurfaceGeneration}");
        }

        private bool TryAcquireFinalRevealOffscreenLease(
            int revealGeneration,
            bool resumeBoundsTimer,
            int controllerGeneration,
            int providerSessionGeneration,
            string? presentationId,
            string surfaceMode,
            int surfaceGeneration,
            int compositionGeneration,
            int browserSurfaceHealthGeneration,
            Rectangle target)
        {
            if (_finalRevealOffscreenLeaseActive || Visible || _actualVisible ||
                revealGeneration != _revealGeneration || !_revealPending ||
                !_desiredVisible || !_browserReady || _browserRecoveryInProgress ||
                _browserRecoveryQueued ||
                browserSurfaceHealthGeneration != _browserSurfaceHealthGeneration ||
                target != _lastBounds ||
                !RevealIdentityMatches(
                    controllerGeneration,
                    providerSessionGeneration,
                    presentationId,
                    surfaceMode,
                    surfaceGeneration,
                    compositionGeneration))
            {
                return false;
            }

            _finalRevealOffscreenLeaseActive = true;
            _finalRevealOffscreenLeaseGeneration = revealGeneration;
            _finalRevealOffscreenLeaseResumeBoundsTimer = resumeBoundsTimer;
            _finalRevealOffscreenLeaseControllerGeneration = controllerGeneration;
            _finalRevealOffscreenLeaseProviderSessionGeneration =
                providerSessionGeneration;
            _finalRevealOffscreenLeasePresentationId = presentationId;
            _finalRevealOffscreenLeaseSurfaceMode = surfaceMode;
            _finalRevealOffscreenLeaseSurfaceGeneration = surfaceGeneration;
            _finalRevealOffscreenLeaseCompositionGeneration = compositionGeneration;
            _finalRevealOffscreenLeaseRootVisualRevision =
                _webView.RootVisualRevision;
            _finalRevealOffscreenLeaseBrowserHealthGeneration =
                browserSurfaceHealthGeneration;
            _finalRevealOffscreenLeaseTarget = target;
            _trace(
                "webview_final_reveal_offscreen_lease_acquired",
                $"generation={revealGeneration} bounds_timer_was_enabled={resumeBoundsTimer} " +
                $"presentation={_activeMenuPresentationId ?? "none"} " +
                $"surface={_activeHostSurfaceMode} " +
                $"surface_generation={_activeHostSurfaceGeneration} " +
                $"root_visual_revision={_finalRevealOffscreenLeaseRootVisualRevision}");
            return true;
        }

        private bool OwnsFinalRevealOffscreenLease(int revealGeneration) =>
            _finalRevealOffscreenLeaseActive &&
            _finalRevealOffscreenLeaseGeneration == revealGeneration &&
            revealGeneration == _revealGeneration &&
            _revealPending && _desiredVisible && _browserReady &&
            !_browserRecoveryInProgress && !_browserRecoveryQueued &&
            _finalRevealOffscreenLeaseBrowserHealthGeneration ==
                _browserSurfaceHealthGeneration &&
            _finalRevealOffscreenLeaseTarget == _lastBounds &&
            _finalRevealOffscreenLeaseRootVisualRevision != 0 &&
            _finalRevealOffscreenLeaseRootVisualRevision ==
                _webView.RootVisualRevision &&
            RevealIdentityMatches(
                _finalRevealOffscreenLeaseControllerGeneration,
                _finalRevealOffscreenLeaseProviderSessionGeneration,
                _finalRevealOffscreenLeasePresentationId,
                _finalRevealOffscreenLeaseSurfaceMode,
                _finalRevealOffscreenLeaseSurfaceGeneration,
                _finalRevealOffscreenLeaseCompositionGeneration) &&
            Visible && !_actualVisible;

        private void ClearFinalRevealOffscreenLease()
        {
            _finalRevealOffscreenLeaseActive = false;
            _finalRevealOffscreenLeaseGeneration = 0;
            _finalRevealOffscreenLeaseResumeBoundsTimer = false;
            _finalRevealOffscreenLeaseControllerGeneration = 0;
            _finalRevealOffscreenLeaseProviderSessionGeneration = 0;
            _finalRevealOffscreenLeasePresentationId = null;
            _finalRevealOffscreenLeaseSurfaceMode = HostSurfaceMode.None;
            _finalRevealOffscreenLeaseSurfaceGeneration = 0;
            _finalRevealOffscreenLeaseCompositionGeneration = 0;
            _finalRevealOffscreenLeaseRootVisualRevision = 0;
            _finalRevealOffscreenLeaseBrowserHealthGeneration = 0;
            _finalRevealOffscreenLeaseTarget = Rectangle.Empty;
        }

        private bool TryAdvanceFinalRevealOffscreenLeaseRootVisualRevision(
            int revealGeneration,
            int previousRootVisualRevision)
        {
            if (!_finalRevealOffscreenLeaseActive ||
                _finalRevealOffscreenLeaseGeneration != revealGeneration ||
                revealGeneration != _revealGeneration ||
                !_revealPending || !_desiredVisible || !_browserReady ||
                _actualVisible || !Visible ||
                _browserRecoveryInProgress || _browserRecoveryQueued ||
                _finalRevealOffscreenLeaseBrowserHealthGeneration !=
                    _browserSurfaceHealthGeneration ||
                _finalRevealOffscreenLeaseTarget != _lastBounds ||
                _finalRevealOffscreenLeaseRootVisualRevision !=
                    previousRootVisualRevision ||
                _webView.RootVisualRevision == 0 ||
                _webView.RootVisualRevision == previousRootVisualRevision ||
                !RevealIdentityMatches(
                    _finalRevealOffscreenLeaseControllerGeneration,
                    _finalRevealOffscreenLeaseProviderSessionGeneration,
                    _finalRevealOffscreenLeasePresentationId,
                    _finalRevealOffscreenLeaseSurfaceMode,
                    _finalRevealOffscreenLeaseSurfaceGeneration,
                    _finalRevealOffscreenLeaseCompositionGeneration))
            {
                return false;
            }

            _finalRevealOffscreenLeaseRootVisualRevision =
                _webView.RootVisualRevision;
            return OwnsFinalRevealOffscreenLease(revealGeneration);
        }

        private void RevokeFinalRevealOffscreenLease(
            string reason,
            int? expectedGeneration = null,
            bool resumeBoundsTimer = true)
        {
            if (!_finalRevealOffscreenLeaseActive ||
                (expectedGeneration.HasValue &&
                 expectedGeneration.Value != _finalRevealOffscreenLeaseGeneration))
            {
                return;
            }

            var generation = _finalRevealOffscreenLeaseGeneration;
            var restartBoundsTimer =
                resumeBoundsTimer && _finalRevealOffscreenLeaseResumeBoundsTimer;
            ClearFinalRevealOffscreenLease();
            if (!IsDisposed && !Disposing && Visible && !_actualVisible)
            {
                Hide();
            }
            if (restartBoundsTimer && !IsDisposed && !Disposing)
            {
                _boundsTimer.Start();
            }
            _trace(
                "webview_final_reveal_offscreen_lease_revoked",
                $"reason={reason} generation={generation} hidden=" +
                $"{IsDisposed || Disposing || !Visible} " +
                $"bounds_timer_resumed={restartBoundsTimer}");
        }

        private bool CommitFinalRevealOffscreenLease(int revealGeneration)
        {
            if (!OwnsFinalRevealOffscreenLease(revealGeneration))
            {
                return false;
            }

            var restartBoundsTimer = _finalRevealOffscreenLeaseResumeBoundsTimer;
            ClearFinalRevealOffscreenLease();
            _revealPending = false;
            _actualVisible = true;
            if (restartBoundsTimer && !IsDisposed && !Disposing)
            {
                _boundsTimer.Start();
            }
            _trace(
                "webview_final_reveal_offscreen_lease_promoted",
                $"generation={revealGeneration} bounds_timer_resumed={restartBoundsTimer} " +
                $"bounds={_lastBounds.Width}x{_lastBounds.Height}");
            return true;
        }

        private void QueueDeferredRevealCommit(
            int generation,
            OverlayTransferIdentity transferIdentity,
            RevealCompositionRefresh refresh,
            RootVisualRebindResult? rootRebind,
            int controllerGeneration,
            int providerSessionGeneration,
            string? presentationId,
            string surfaceMode,
            int surfaceGeneration,
            int compositionGeneration,
            long ingressEpoch,
            int browserSurfaceHealthGeneration)
        {
            try
            {
                // Yield exactly one WinForms dispatch turn without adding an
                // arbitrary timer. A hide already queued while bounds/root
                // synchronization was running must cancel this generation
                // before it reaches the synchronous compositor fence.
                BeginInvoke((Action)(() =>
                    CommitDeferredReveal(
                        generation,
                        transferIdentity,
                        refresh,
                        rootRebind,
                        controllerGeneration,
                        providerSessionGeneration,
                        presentationId,
                        surfaceMode,
                        surfaceGeneration,
                        compositionGeneration,
                        ingressEpoch,
                        browserSurfaceHealthGeneration)));
                _trace(
                    "webview_reveal_commit_queued",
                    $"generation={generation} dispatch_boundary=winforms-begin-invoke " +
                    $"presentation={_activeMenuPresentationId ?? "none"}");
            }
            catch (Exception error) when (
                error is InvalidOperationException ||
                error is ObjectDisposedException)
            {
                // A reveal that cannot cross the cancellation dispatch
                // boundary must remain hidden.
                FailCompositionReveal(
                    refresh,
                    rootRebind,
                    generation,
                    error.HResult != 0
                        ? error.HResult
                        : unchecked((int)0x80004005));
            }
        }

        private void CommitDeferredReveal(
            int generation,
            OverlayTransferIdentity transferIdentity,
            RevealCompositionRefresh refresh,
            RootVisualRebindResult? rootRebind,
            int controllerGeneration,
            int providerSessionGeneration,
            string? presentationId,
            string surfaceMode,
            int surfaceGeneration,
            int compositionGeneration,
            long ingressEpoch,
            int browserSurfaceHealthGeneration)
        {
            // NavigationCompleted has already completed WebView2PageReadiness,
            // and extension menus additionally pass MenuRevealGate after the
            // matching React presentation crosses its two-frame paint boundary.
            // The DirectComposition commit submitted by BeginDeferredReveal is
            // the final native boundary: wait for that exact commit before Show
            // instead of guessing with a fixed delay.
            if (IsDisposed || Disposing || generation != _revealGeneration ||
                !_revealPending || !_desiredVisible)
            {
                return;
            }
            if (!RevealIdentityMatches(
                    controllerGeneration,
                    providerSessionGeneration,
                    presentationId,
                    surfaceMode,
                    surfaceGeneration,
                    compositionGeneration))
            {
                CancelPendingRevealForIdentityChange(
                    "deferred-commit-identity-mismatch",
                    preserveDesiredVisibility: true);
                return;
            }
            if (RevealIngressWasSuperseded(ingressEpoch))
            {
                DeferPendingRevealForIngress(
                    "deferred-commit-ingress-superseded");
                ResumeRevealAfterIngress();
                return;
            }
            if (!_browserReady)
            {
                // Readiness can be revoked between prepare and commit by a
                // navigation/process failure. Do not leave a canceled hidden
                // reveal parented to the game window.
                ApplyVisibility(false);
                return;
            }
            if (!OverlayPresentationPolicy.ShouldCommitReveal(
                    _desiredVisible,
                    _browserReady,
                    _revealPending))
            {
                ApplyVisibility(false);
                return;
            }
            if (NativeMethods.IsIconic(_gtaWindow) ||
                !IsInteractionForeground() ||
                !NativeMethods.TryGetClientBounds(_gtaWindow, out var target))
            {
                ApplyVisibility(false);
                return;
            }

            PrepareSurface(target);
            if (HostSurfaceMode.IsInitializing(_activeHostSurfaceMode) &&
                (!OverlayPresentationPolicy.HasExactBootstrapPixelProof(
                    _activeHostSurfaceMode,
                    _activeHostSurfaceGeneration,
                    _controllerGeneration,
                    target.Width,
                    target.Height,
                    _bootstrapPaintProofMode,
                    _bootstrapPaintProofSurfaceGeneration,
                    _bootstrapPaintProofControllerGeneration,
                    _bootstrapPaintProofWidth,
                    _bootstrapPaintProofHeight,
                    _bootstrapPaintProofConcrete,
                    _bootstrapPaintProofGenerationMarkerMatched)))
            {
                _trace(
                    "webview_bootstrap_reveal_withheld",
                    $"generation={generation} surface={_activeHostSurfaceMode} " +
                    $"surface_generation={_activeHostSurfaceGeneration} " +
                    $"controller_generation={_controllerGeneration} " +
                    $"target={target.Width}x{target.Height} " +
                    $"proof_surface={_bootstrapPaintProofMode ?? "none"} " +
                    $"proof_surface_generation={_bootstrapPaintProofSurfaceGeneration} " +
                    $"proof_controller_generation={_bootstrapPaintProofControllerGeneration} " +
                    $"proof_size={_bootstrapPaintProofWidth}x{_bootstrapPaintProofHeight} " +
                    $"proof_composition_generation={_bootstrapPaintProofCompositionGeneration} " +
                    $"current_composition_generation={_webView.CompositionGeneration} " +
                    $"proof_marker={_bootstrapPaintProofGenerationMarkerMatched} " +
                    $"proof_concrete={_bootstrapPaintProofConcrete}");
                _desiredVisible = false;
                ApplyVisibility(false);
                return;
            }
            var completionWaitStartedAt = Stopwatch.GetTimestamp();
            var completionHResult = _webView.WaitForCommitCompletion();
            var completionWaitMilliseconds =
                ElapsedMilliseconds(completionWaitStartedAt);
            var completionSucceeded =
                OverlayPresentationPolicy.DidCompositionCommitComplete(
                    completionHResult);
            _trace(
                "webview_reveal_composition_wait_completed",
                $"generation={generation} completion_wait=True " +
                $"completion_wait_ms={completionWaitMilliseconds:F3} " +
                $"hresult=0x{completionHResult:X8} succeeded={completionSucceeded} " +
                "fence_thread=overlay-sta " +
                $"presentation={_activeMenuPresentationId ?? "none"} " +
                $"surface={_activeHostSurfaceMode} " +
                $"surface_generation={_activeHostSurfaceGeneration}");
            if (!completionSucceeded)
            {
                FailCompositionReveal(
                    refresh,
                    rootRebind,
                    generation,
                    completionHResult);
                return;
            }

            var queued = TryQueueNativeBrowserEventDrain(
                passesRemaining: 2,
                onReady: () =>
                    FinalizeDeferredRevealAfterBrowserEventDrain(
                        generation,
                        transferIdentity,
                        refresh,
                        rootRebind,
                        controllerGeneration,
                        providerSessionGeneration,
                        presentationId,
                        surfaceMode,
                        surfaceGeneration,
                        compositionGeneration,
                        ingressEpoch,
                        browserSurfaceHealthGeneration,
                        target,
                        completionWaitMilliseconds),
                onFailed: () => FailCompositionReveal(
                    refresh,
                    rootRebind,
                    generation,
                    Marshal.GetLastWin32Error() != 0
                        ? Marshal.GetLastWin32Error()
                        : unchecked((int)0x80004005)));
            if (queued)
            {
                _trace(
                    "webview_reveal_browser_event_drain_queued",
                    $"generation={generation} health_generation=" +
                    $"{browserSurfaceHealthGeneration} passes=2 " +
                    "dispatch_boundary=native-postmessage-post-fence");
            }
            else
            {
                FailCompositionReveal(
                    refresh,
                    rootRebind,
                    generation,
                    Marshal.GetLastWin32Error() != 0
                        ? Marshal.GetLastWin32Error()
                        : unchecked((int)0x80004005));
            }
            return;
        }

        private bool TryQueueNativeBrowserEventDrain(
            int passesRemaining,
            Action onReady,
            Action onFailed)
        {
            if (passesRemaining <= 0 || !IsHandleCreated ||
                IsDisposed || Disposing)
            {
                return false;
            }

            int token;
            do
            {
                token = unchecked(++_nativeRevealDrainToken);
            }
            while (token == 0 || _nativeRevealDrainCallbacks.ContainsKey(token));

            _nativeRevealDrainCallbacks[token] = () =>
            {
                if (passesRemaining == 1)
                {
                    onReady();
                    return;
                }
                if (!TryQueueNativeBrowserEventDrain(
                        passesRemaining - 1,
                        onReady,
                        onFailed))
                {
                    onFailed();
                }
            };
            if (NativeMethods.PostMessage(
                    Handle,
                    WmFinalizeRevealAfterBrowserDrain,
                    new IntPtr(token),
                    IntPtr.Zero))
            {
                return true;
            }

            _nativeRevealDrainCallbacks.Remove(token);
            return false;
        }

        private async Task<bool> VerifyFinalRevealSurfacePixelsAsync(
            int revealGeneration,
            OverlayTransferIdentity transferIdentity,
            int controllerGeneration,
            int providerSessionGeneration,
            string? presentationId,
            string surfaceMode,
            int surfaceGeneration,
            int compositionGeneration,
            int browserSurfaceHealthGeneration,
            Rectangle target)
        {
            if (!RevealIdentityMatches(
                    controllerGeneration,
                    providerSessionGeneration,
                    presentationId,
                    surfaceMode,
                    surfaceGeneration,
                    compositionGeneration) ||
                revealGeneration != _revealGeneration ||
                browserSurfaceHealthGeneration != _browserSurfaceHealthGeneration ||
                !_revealPending || !_desiredVisible || !_browserReady ||
                _actualVisible || Visible)
            {
                return false;
            }

            var timer = Stopwatch.StartNew();
            var boundsTimerWasEnabled = _boundsTimer.Enabled;
            var leaseCurrent = true;
            var verified = false;
            try
            {
                if (boundsTimerWasEnabled)
                    _boundsTimer.Stop();
                var positioned = NativeMethods.SetWindowPos(
                    Handle,
                    IntPtr.Zero,
                    -32000,
                    -32000,
                    target.Width,
                    target.Height,
                    NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
                if (!positioned)
                    return false;

                if (!TryAcquireFinalRevealOffscreenLease(
                        revealGeneration,
                        boundsTimerWasEnabled,
                        controllerGeneration,
                        providerSessionGeneration,
                        presentationId,
                        surfaceMode,
                        surfaceGeneration,
                        compositionGeneration,
                        browserSurfaceHealthGeneration,
                        target))
                {
                    return false;
                }
                Show();
                if (!Visible ||
                    !OwnsFinalRevealOffscreenLease(revealGeneration))
                {
                    return false;
                }
                var publishColdInitializerRoot =
                    _coldHostVisibleRootPublishRequired &&
                    HostSurfaceMode.IsInitializing(surfaceMode);
                if (publishColdInitializerRoot)
                {
                    // The retained lease already owns this WS_VISIBLE parent at
                    // its off-screen coordinates. Publish a fresh root at this
                    // boundary, never while hidden, then bind the lease to that
                    // exact visual revision before any asynchronous proof work.
                    var previousRootVisualRevision =
                        _webView.RootVisualRevision;
                    var visibleRootRebind = _webView.RebindRootVisual();
                    var rootIdentityAdvanced =
                        visibleRootRebind.Succeeded &&
                        TryAdvanceFinalRevealOffscreenLeaseRootVisualRevision(
                            revealGeneration,
                            previousRootVisualRevision);
                    if (!rootIdentityAdvanced)
                    {
                        _trace(
                            "webview_initializer_visible_offscreen_root_publish_failed",
                            $"generation={revealGeneration} surface_generation=" +
                            $"{surfaceGeneration} outcome={visibleRootRebind.Outcome} " +
                            $"hresult=0x{visibleRootRebind.HResult:X8} " +
                            $"previous_root_visual_revision={previousRootVisualRevision} " +
                            $"current_root_visual_revision={_webView.RootVisualRevision} " +
                            $"composition_generation={_webView.CompositionGeneration}");
                        FailCompositionReveal(
                            RevealCompositionRefresh.RebindRootVisual,
                            visibleRootRebind,
                            revealGeneration,
                            visibleRootRebind.HResult != 0
                                ? visibleRootRebind.HResult
                                : unchecked((int)0x80004005));
                        return false;
                    }
                    _trace(
                        "webview_initializer_root_published_while_visible_offscreen",
                        $"generation={revealGeneration} surface_generation=" +
                        $"{surfaceGeneration} previous_root_visual_revision=" +
                        $"{previousRootVisualRevision} root_visual_revision=" +
                        $"{_webView.RootVisualRevision} composition_generation=" +
                        $"{_webView.CompositionGeneration}");
                }
                var synchronized = _webView.SynchronizeBounds();
                var probeFence = synchronized
                    ? _webView.WaitForCommitCompletion()
                    : unchecked((int)0x80004005);
                if (!synchronized ||
                    !OverlayPresentationPolicy.DidCompositionCommitComplete(
                        probeFence) ||
                    !OwnsFinalRevealOffscreenLease(revealGeneration))
                {
                    return false;
                }

                await Task.Yield();
                var capture = _webView.CapturePreviewAsync();
                var completed = await Task.WhenAny(
                    capture,
                    Task.Delay(BrowserPaintCaptureTimeoutMilliseconds));
                if (!ReferenceEquals(completed, capture))
                {
                    _ = capture.ContinueWith(
                        task => { _ = task.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted);
                    _trace(
                        "webview_final_reveal_pixel_probe_timeout",
                        $"generation={revealGeneration} presentation=" +
                        $"{presentationId ?? "none"} timeout_ms=" +
                        $"{BrowserPaintCaptureTimeoutMilliseconds}");
                    return false;
                }

                var expectedPaintIdentity =
                    !string.IsNullOrWhiteSpace(presentationId)
                        ? OverlayPresentationPolicy.MenuPaintIdentity(
                            providerSessionGeneration,
                            presentationId)
                        : OverlayPresentationPolicy.HostPaintIdentity(
                            surfaceMode,
                            surfaceGeneration);
                var capturedPng = await capture;
                var evidence = AnalyzePresentationPixels(
                    capturedPng,
                    expectedPaintIdentity);
                leaseCurrent = !IsDisposed && !Disposing &&
                    OwnsFinalRevealOffscreenLease(revealGeneration);
                var targetSizeMatches =
                    evidence.Width == target.Width &&
                    evidence.Height == target.Height;
                var paintIdentityMarkerMatches =
                    expectedPaintIdentity != 0 &&
                    evidence.PaintIdentityMarkerMatched;
                verified = leaseCurrent && evidence.IsConcrete &&
                    targetSizeMatches && paintIdentityMarkerMatches;
                if (verified)
                {
                    verified = _transferState.TryAdvance(
                        transferIdentity,
                        OverlayTransferPhase.Preparing,
                        OverlayTransferPhase.BrowserPaintVerified);
                    if (verified)
                    {
                        _desktopPaintSamples = evidence.DesktopSamples;
                        _paintEvidencePresentationId = presentationId;
                        TraceTransferState("browser-paint-verified", transferIdentity);
                    }
                    else
                    {
                        _trace(
                            "webview_transfer_stale_acknowledgement",
                            $"boundary=browser-paint generation={revealGeneration} " +
                            $"presentation={presentationId ?? "none"} " +
                            $"phase={_transferState.Phase}");
                    }
                }
                _trace(
                    verified
                        ? "webview_final_reveal_pixels_verified"
                        : "webview_final_reveal_pixels_unverified",
                    $"generation={revealGeneration} controller_generation=" +
                    $"{controllerGeneration} presentation={presentationId ?? "none"} " +
                    $"surface={surfaceMode} surface_generation={surfaceGeneration} " +
                    $"identity_current={leaseCurrent} image={evidence.Width}x{evidence.Height} " +
                    $"target={target.Width}x{target.Height} " +
                    $"target_size_match={targetSizeMatches} " +
                    $"samples={evidence.SampleCount} " +
                    $"opaque={evidence.OpaqueSampleCount} " +
                    $"visible_color={evidence.VisibleColorSampleCount} " +
                    $"expected_paint_identity=0x{expectedPaintIdentity:X16} " +
                    $"paint_identity_marker={paintIdentityMarkerMatches} " +
                    $"root_visual_revision={_webView.RootVisualRevision} " +
                    $"browser_surface_concrete={evidence.IsConcrete} " +
                    $"duration_ms={timer.Elapsed.TotalMilliseconds:F3}");
            }
            catch (Exception error) when (
                error is COMException ||
                error is InvalidOperationException ||
                error is ArgumentException ||
                error is IOException ||
                error is OutOfMemoryException)
            {
                _trace(
                    "webview_final_reveal_pixel_probe_failed",
                    $"generation={revealGeneration} presentation=" +
                    $"{presentationId ?? "none"} type={error.GetType().FullName} " +
                    $"message={error.Message} duration_ms=" +
                    $"{timer.Elapsed.TotalMilliseconds:F3}");
                verified = false;
            }
            finally
            {
                var ownsOffscreenLease =
                    OwnsFinalRevealOffscreenLease(revealGeneration);
                if (verified && ownsOffscreenLease)
                {
                    // Keep the exact captured DirectComposition surface alive.
                    // Hiding this HWND here and showing it again over GTA can
                    // leave WebView2 logically visible while DWM presents no
                    // browser pixels, and it adds a visible blank frame on a
                    // warm GBAY replacement. The post-pixel drain retains this
                    // generation-bound lease off-screen until one atomic native
                    // move promotes it onto the game.
                    _trace(
                        "webview_final_reveal_offscreen_lease_retained",
                        $"generation={revealGeneration} presentation=" +
                        $"{presentationId ?? "none"} surface={surfaceMode} " +
                        $"surface_generation={surfaceGeneration}");
                }
                else
                {
                    verified = false;
                    RevokeFinalRevealOffscreenLease(
                        "pixel-proof-not-retained",
                        revealGeneration);
                    if (boundsTimerWasEnabled &&
                        !_finalRevealOffscreenLeaseActive &&
                        !IsDisposed && !Disposing)
                    {
                        _boundsTimer.Start();
                    }
                }
            }
            return verified;
        }

        private async void FinalizeDeferredRevealAfterBrowserEventDrain(
            int generation,
            OverlayTransferIdentity transferIdentity,
            RevealCompositionRefresh refresh,
            RootVisualRebindResult? rootRebind,
            int controllerGeneration,
            int providerSessionGeneration,
            string? presentationId,
            string surfaceMode,
            int surfaceGeneration,
            int compositionGeneration,
            long ingressEpoch,
            int browserSurfaceHealthGeneration,
            Rectangle target,
            double completionWaitMilliseconds)
        {
            if (IsDisposed || Disposing || generation != _revealGeneration ||
                !_revealPending || !_desiredVisible ||
                !RevealIdentityMatches(
                    controllerGeneration,
                    providerSessionGeneration,
                    presentationId,
                    surfaceMode,
                    surfaceGeneration,
                    compositionGeneration))
            {
                RevokeFinalRevealOffscreenLease(
                    "pre-pixel-finalizer-stale",
                    generation);
                return;
            }
            if (browserSurfaceHealthGeneration != _browserSurfaceHealthGeneration ||
                _browserRecoveryInProgress || _browserRecoveryQueued)
            {
                _trace(
                    "webview_reveal_browser_health_withheld",
                    $"reason=post-fence-health-generation-superseded " +
                    $"generation={generation} expected_health_generation=" +
                    $"{browserSurfaceHealthGeneration} current_health_generation=" +
                    $"{_browserSurfaceHealthGeneration} recovery_queued=" +
                    $"{_browserRecoveryQueued} recovery_in_progress=" +
                    $"{_browserRecoveryInProgress}");
                ApplyVisibility(false);
                return;
            }

            var surfacePixelsVerified =
                await VerifyFinalRevealSurfacePixelsAsync(
                    generation,
                    transferIdentity,
                    controllerGeneration,
                    providerSessionGeneration,
                    presentationId,
                    surfaceMode,
                    surfaceGeneration,
                    compositionGeneration,
                    browserSurfaceHealthGeneration,
                    target);
            if (IsDisposed || Disposing || generation != _revealGeneration ||
                !_revealPending || !_desiredVisible)
            {
                RevokeFinalRevealOffscreenLease(
                    "post-pixel-finalizer-stale",
                    generation);
                return;
            }
            if (!surfacePixelsVerified)
            {
                HandleFinalRevealPixelProofFailure(
                    generation,
                    controllerGeneration,
                    providerSessionGeneration,
                    presentationId,
                    surfaceMode,
                    surfaceGeneration,
                    compositionGeneration,
                    browserSurfaceHealthGeneration,
                    target);
                return;
            }

            // CapturePreview and its compositor fence can dispatch WebView2
            // process-failure and ownership callbacks. Drain those callbacks
            // once more before the retained off-screen lease is promoted so a
            // frame proved on a renderer that failed meanwhile stays hidden.
            var queued = TryQueueNativeBrowserEventDrain(
                passesRemaining: 2,
                onReady: () => CommitVerifiedRevealAfterPixelProof(
                    generation,
                    transferIdentity,
                    refresh,
                    rootRebind,
                    controllerGeneration,
                    providerSessionGeneration,
                    presentationId,
                    surfaceMode,
                    surfaceGeneration,
                    compositionGeneration,
                    ingressEpoch,
                    browserSurfaceHealthGeneration,
                    target,
                    completionWaitMilliseconds),
                onFailed: () => FailCompositionReveal(
                    refresh,
                    rootRebind,
                    generation,
                    Marshal.GetLastWin32Error() != 0
                        ? Marshal.GetLastWin32Error()
                        : unchecked((int)0x80004005)));
            if (queued)
            {
                _trace(
                    "webview_reveal_post_pixel_drain_queued",
                    $"generation={generation} health_generation=" +
                    $"{browserSurfaceHealthGeneration} passes=2 " +
                    "dispatch_boundary=native-postmessage-post-pixel-proof");
                return;
            }

            FailCompositionReveal(
                refresh,
                rootRebind,
                generation,
                Marshal.GetLastWin32Error() != 0
                    ? Marshal.GetLastWin32Error()
                    : unchecked((int)0x80004005));
        }

        private void CommitVerifiedRevealAfterPixelProof(
            int generation,
            OverlayTransferIdentity transferIdentity,
            RevealCompositionRefresh refresh,
            RootVisualRebindResult? rootRebind,
            int controllerGeneration,
            int providerSessionGeneration,
            string? presentationId,
            string surfaceMode,
            int surfaceGeneration,
            int compositionGeneration,
            long ingressEpoch,
            int browserSurfaceHealthGeneration,
            Rectangle target,
            double completionWaitMilliseconds)
        {
            // The bounded fences yielded the STA. A close, provider handoff,
            // renderer failure, or newer surface may have superseded this
            // reveal; never promote an obsolete off-screen lease.
            if (IsDisposed || Disposing || generation != _revealGeneration ||
                !_revealPending || !_desiredVisible || !_browserReady ||
                browserSurfaceHealthGeneration != _browserSurfaceHealthGeneration ||
                _browserRecoveryInProgress || _browserRecoveryQueued)
            {
                RevokeFinalRevealOffscreenLease(
                    "post-pixel-commit-stale",
                    generation);
                return;
            }
            if (!RevealIdentityMatches(
                    controllerGeneration,
                    providerSessionGeneration,
                    presentationId,
                    surfaceMode,
                    surfaceGeneration,
                    compositionGeneration))
            {
                CancelPendingRevealForIdentityChange(
                    "post-fence-identity-mismatch",
                    preserveDesiredVisibility: true);
                return;
            }
            if (RevealIngressWasSuperseded(ingressEpoch))
            {
                DeferPendingRevealForIngress(
                    "post-fence-ingress-superseded");
                ResumeRevealAfterIngress();
                return;
            }
            if (NativeMethods.IsIconic(_gtaWindow) ||
                !IsInteractionForeground() ||
                !NativeMethods.TryGetClientBounds(_gtaWindow, out var committedTarget))
            {
                _desiredVisible = false;
                ApplyVisibility(false);
                return;
            }
            if (committedTarget != target)
            {
                ApplyVisibility(false);
                SynchronizeBounds();
                return;
            }

            _finalRevealPixelFailureIdentity = string.Empty;
            _finalRevealPixelFailureCount = 0;
            var ingressSupersededAtShow = false;
            var browserSurfaceUnavailableAtShow = false;
            var offscreenLeaseUnavailableAtShow = false;
            var nativePromotionFailedAtShow = false;
            var nativePromotionError = 0;
            Exception? ingressBoundaryError = null;
            lock (_revealIngressSync)
            {
                // Consume any native ownership event that was already signaled
                // at this exact linearization boundary. The preloader callback
                // announces and queues its typed mutation before returning.
                var externalIngressObserved = false;
                try
                {
                    externalIngressObserved =
                        _finalRevealIngressBoundary?.Invoke() == true;
                }
                catch (Exception error)
                {
                    ingressBoundaryError = error;
                }
                browserSurfaceUnavailableAtShow = ingressBoundaryError == null &&
                    !HasLiveBrowserSurface();
                ingressSupersededAtShow = ingressBoundaryError == null &&
                    !browserSurfaceUnavailableAtShow &&
                    (externalIngressObserved ||
                     _pendingRevealIngress > 0 ||
                     ingressEpoch != _revealIngressEpoch);
                if (ingressBoundaryError == null &&
                    !browserSurfaceUnavailableAtShow &&
                    !ingressSupersededAtShow)
                {
                    offscreenLeaseUnavailableAtShow =
                        !OwnsFinalRevealOffscreenLease(generation);
                    if (!offscreenLeaseUnavailableAtShow)
                    {
                        // The pixel-qualified parent is already visible far
                        // off-screen. Move that same HWND directly onto GTA and
                        // promote it in one native transaction. Do not hide/show
                        // or republish the root between CapturePreview proof and
                        // this desktop presentation boundary.
                        var promoted = NativeMethods.SetWindowPos(
                            Handle,
                            NativeMethods.HwndTopMost,
                            target.Left,
                            target.Top,
                            target.Width,
                            target.Height,
                            NativeMethods.SwpNoActivate);
                        // The retained proof lease was last committed while
                        // this parent lived at its off-screen coordinates.
                        // WebView2 composition controllers do not infer a
                        // parent-window move: without this notification the
                        // HWND itself can be on-screen while DWM keeps
                        // presenting a black/empty root at the old position.
                        // Commit the parent-position mutation before the
                        // desktop witness samples the promoted window.
                        var parentPositionNotified = promoted &&
                            _webView.NotifyParentWindowPositionChanged();
                        var leaseStillCurrent = promoted &&
                            parentPositionNotified &&
                            generation == _revealGeneration &&
                            _revealPending && _desiredVisible &&
                            OwnsFinalRevealOffscreenLease(generation);
                        if (leaseStillCurrent)
                        {
                            _overlayTopMostApplied = true;
                        }
                        else
                        {
                            offscreenLeaseUnavailableAtShow =
                                promoted && parentPositionNotified;
                            nativePromotionFailedAtShow =
                                !promoted || !parentPositionNotified;
                            nativePromotionError = !promoted
                                ? Marshal.GetLastWin32Error()
                                : !parentPositionNotified
                                    ? unchecked((int)0x80004005)
                                    : 0;
                        }
                    }
                }
            }
            if (ingressBoundaryError != null)
            {
                _trace(
                    "webview_final_reveal_ingress_boundary_failed",
                    $"generation={generation} type=" +
                    $"{ingressBoundaryError.GetType().FullName} " +
                    $"message={ingressBoundaryError.Message}");
                FailCompositionReveal(
                    refresh,
                    rootRebind,
                    generation,
                    ingressBoundaryError.HResult != 0
                        ? ingressBoundaryError.HResult
                        : unchecked((int)0x80004005));
                return;
            }
            if (browserSurfaceUnavailableAtShow)
            {
                DeferPendingRevealForBrowserHealth(
                    "show-boundary-browser-or-renderer-unavailable");
                return;
            }
            if (ingressSupersededAtShow)
            {
                DeferPendingRevealForIngress("show-boundary-ingress-superseded");
                ResumeRevealAfterIngress();
                return;
            }
            if (offscreenLeaseUnavailableAtShow || nativePromotionFailedAtShow)
            {
                _trace(
                    offscreenLeaseUnavailableAtShow
                        ? "webview_final_reveal_offscreen_lease_missing"
                        : "webview_final_reveal_window_promotion_failed",
                    $"generation={generation} lease_available=" +
                    $"{!offscreenLeaseUnavailableAtShow} error={nativePromotionError} " +
                    $"target={target.Left},{target.Top},{target.Width},{target.Height}");
                FailCompositionReveal(
                    refresh,
                    rootRebind,
                    generation,
                    nativePromotionError != 0
                        ? nativePromotionError
                        : unchecked((int)0x80004005));
                return;
            }
            if (!CommitFinalRevealOffscreenLease(generation))
            {
                _revealPending = true;
                FailCompositionReveal(
                    refresh,
                    rootRebind,
                    generation,
                    unchecked((int)0x80004005));
                return;
            }
            if (!_transferState.TryAdvance(
                    transferIdentity,
                    OverlayTransferPhase.BrowserPaintVerified,
                    OverlayTransferPhase.WindowPromoted))
            {
                _trace(
                    "webview_transfer_stale_acknowledgement",
                    $"boundary=window-promoted generation={generation} " +
                    $"presentation={presentationId ?? "none"} " +
                    $"phase={_transferState.Phase}");
                ApplyVisibility(false);
                return;
            }
            TraceTransferState("window-promoted-awaiting-desktop", transferIdentity);
            _trace(
                "webview_final_reveal_window_promoted",
                $"generation={generation} target={target.Left},{target.Top}," +
                $"{target.Width},{target.Height} topmost=True " +
                "transition=retained-offscreen-to-visible " +
                "desktop_presentation=awaiting-proof input_enabled=False");
            _surfaceWasPreviouslyPresented = true;
            BeginDesktopPresentationCommit(
                transferIdentity,
                target,
                _desktopPaintSamples,
                completionWaitMilliseconds);
        }

        private async void BeginDesktopPresentationCommit(
            OverlayTransferIdentity transferIdentity,
            Rectangle target,
            IReadOnlyList<DesktopPaintSample>? samples,
            double completionWaitMilliseconds)
        {
            if (!_transferState.Matches(transferIdentity) ||
                _transferState.Phase != OverlayTransferPhase.WindowPromoted)
            {
                return;
            }

            // WS_EX_NOREDIRECTIONBITMAP is the required DirectComposition
            // hosting style, so GDI is not an authoritative readiness witness:
            // Windows deliberately does not allocate a redirected bitmap for
            // this HWND. Publish the already-qualified composition immediately
            // as passive visibility. The independent probe below may upgrade
            // this exact transfer to Interactive, but probe failure can never
            // revoke the painted surface or grant an input lease.
            if (!KeepCompositionQualifiedPresentationVisible(
                    transferIdentity,
                    target,
                    completionWaitMilliseconds,
                    "awaiting-independent-desktop-witness"))
            {
                return;
            }

            if (samples == null || samples.Count == 0)
            {
                _trace(
                    "webview_desktop_presentation_unverified",
                    $"owner={transferIdentity.Owner} generation=" +
                    $"{transferIdentity.TransferGeneration} reason=" +
                    "desktop-proof-samples-unavailable " +
                    "composition_qualified=True input_enabled=False");
                TryCompleteExplicitUserIntentReveal(
                    transferIdentity,
                    completionWaitMilliseconds,
                    "desktop-proof-samples-unavailable");
                return;
            }

            var probeSamples = new List<DesktopPresentationProbeSample>(samples.Count);
            foreach (var sample in samples)
            {
                probeSamples.Add(new DesktopPresentationProbeSample(
                    sample.NormalizedX,
                    sample.NormalizedY,
                    sample.Expected));
            }

            // The runtime can execute inside ScriptHookVDotNet's secondary
            // AppDomain, whose BaseDirectory is the GTA root (and whose
            // assembly Location may be a shadow-copy directory). The packaged
            // renderer root is the stable parent of the validated UI folder.
            var rendererDirectory = Path.GetDirectoryName(_uiDirectory);
            var probeExecutable = string.IsNullOrWhiteSpace(rendererDirectory)
                ? string.Empty
                : Path.Combine(rendererDirectory, "ReactorV.Preloader.exe");
            _trace(
                "webview_desktop_presentation_probe_begin",
                $"owner={transferIdentity.Owner} generation=" +
                $"{transferIdentity.TransferGeneration} presentation=" +
                $"{(transferIdentity.PresentationId.Length == 0 ? "none" : transferIdentity.PresentationId)} " +
                $"samples={probeSamples.Count} timeout_ms=" +
                $"{DesktopPresentationProbeTimeoutMilliseconds} input_enabled=False");

            DesktopPresentationProbeResult result;
            try
            {
                result = await DesktopPresentationProbeClient.VerifyAsync(
                    probeExecutable,
                    target,
                    probeSamples,
                    DesktopPresentationProbeTimeoutMilliseconds);
            }
            catch (Exception error) when (
                error is InvalidOperationException ||
                error is IOException ||
                error is ArgumentException)
            {
                _trace(
                    "webview_desktop_presentation_probe_failed",
                    $"generation={transferIdentity.TransferGeneration} " +
                    $"type={error.GetType().FullName} message={error.Message}");
                KeepCompositionQualifiedPresentationVisible(
                    transferIdentity,
                    target,
                    completionWaitMilliseconds,
                    "desktop-probe-exception");
                TryCompleteExplicitUserIntentReveal(
                    transferIdentity,
                    completionWaitMilliseconds,
                    "desktop-probe-exception");
                return;
            }

            if (!_transferState.Matches(transferIdentity) ||
                _transferState.Phase != OverlayTransferPhase.CompositionCommittedVisible ||
                !_desiredVisible || !_actualVisible || !Visible)
            {
                _trace(
                    "webview_transfer_stale_acknowledgement",
                    $"boundary=desktop-proof generation=" +
                    $"{transferIdentity.TransferGeneration} phase={_transferState.Phase} " +
                    $"requested_visible={_desiredVisible} actual_visible={_actualVisible}");
                return;
            }

            _trace(
                result.IsConcrete
                    ? "webview_desktop_presentation_verified"
                    : "webview_desktop_presentation_unverified",
                $"owner={transferIdentity.Owner} generation=" +
                $"{transferIdentity.TransferGeneration} presentation=" +
                $"{(transferIdentity.PresentationId.Length == 0 ? "none" : transferIdentity.PresentationId)} " +
                $"readable={result.ReadableSampleCount} matching=" +
                $"{result.MatchingSampleCount} concrete={result.IsConcrete} " +
                $"source={result.Source} error={result.Error ?? "none"} " +
                "evidence_scope=desktop-duplication");
            if (!result.IsConcrete)
            {
                KeepCompositionQualifiedPresentationVisible(
                    transferIdentity,
                    target,
                    completionWaitMilliseconds,
                    string.IsNullOrWhiteSpace(result.Error)
                        ? "desktop-pixels-missing"
                        : "desktop-probe-" + result.Error);
                TryCompleteExplicitUserIntentReveal(
                    transferIdentity,
                    completionWaitMilliseconds,
                    string.IsNullOrWhiteSpace(result.Error)
                        ? "desktop-pixels-missing"
                        : "desktop-probe-" + result.Error);
                return;
            }

            if (!_transferState.TryAdvance(
                    transferIdentity,
                    OverlayTransferPhase.CompositionCommittedVisible,
                    OverlayTransferPhase.DesktopPresentationVerified) ||
                !_transferState.TryAdvance(
                    transferIdentity,
                    OverlayTransferPhase.DesktopPresentationVerified,
                    OverlayTransferPhase.Interactive))
            {
                _trace(
                    "webview_transfer_stale_acknowledgement",
                    $"boundary=desktop-commit generation=" +
                    $"{transferIdentity.TransferGeneration} phase={_transferState.Phase}");
                return;
            }

            _desktopPresentationPixelsVerified = true;
            _providerInputIntentGate.TryConsume(
                transferIdentity.PresentationId,
                MonotonicMilliseconds(),
                out _);
            TraceTransferState("interactive", transferIdentity);
            CompleteQualifiedReveal(
                transferIdentity,
                completionWaitMilliseconds,
                "desktop-presentation-verified");
        }

        private bool TryCompleteExplicitUserIntentReveal(
            OverlayTransferIdentity transferIdentity,
            double completionWaitMilliseconds,
            string desktopProofFailure)
        {
            if (transferIdentity.Owner != OverlayTransferOwner.Provider ||
                !_transferState.Matches(transferIdentity) ||
                _transferState.Phase != OverlayTransferPhase.CompositionCommittedVisible ||
                !_desiredVisible || !_actualVisible || !Visible ||
                !_providerInputIntentGate.TryConsume(
                    transferIdentity.PresentationId,
                    MonotonicMilliseconds(),
                    out var intentEpoch))
            {
                return false;
            }

            if (!_transferState.TryAdvance(
                    transferIdentity,
                    OverlayTransferPhase.CompositionCommittedVisible,
                    OverlayTransferPhase.ExplicitUserIntentAuthorized) ||
                !_transferState.TryAdvance(
                    transferIdentity,
                    OverlayTransferPhase.ExplicitUserIntentAuthorized,
                    OverlayTransferPhase.Interactive))
            {
                return false;
            }

            Volatile.Write(
                ref _userIntentAuthorizedProviderPresentationId,
                transferIdentity.PresentationId);
            TraceTransferState("interactive-explicit-f9", transferIdentity);
            _trace(
                "webview_provider_input_intent_consumed",
                $"presentation={transferIdentity.PresentationId} " +
                $"epoch={intentEpoch} desktop_proof_failure={desktopProofFailure} " +
                "exact_browser_paint=True composition_commit=True " +
                "input_enabled=True close_contract=f9-or-escape");
            CompleteQualifiedReveal(
                transferIdentity,
                completionWaitMilliseconds,
                "explicit-f9-intent");
            BeginExplicitUserIntentInputLease(transferIdentity);
            return true;
        }

        private async void BeginExplicitUserIntentInputLease(
            OverlayTransferIdentity transferIdentity)
        {
            var leaseGeneration = ++_explicitUserIntentInputLeaseGeneration;
            await Task.Delay(ExplicitUserIntentInputLeaseMilliseconds);
            if (IsDisposed || Disposing)
                return;
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() =>
                    ExpireExplicitUserIntentInputLease(
                        transferIdentity,
                        leaseGeneration)));
                return;
            }
            ExpireExplicitUserIntentInputLease(
                transferIdentity,
                leaseGeneration);
        }

        private void ExpireExplicitUserIntentInputLease(
            OverlayTransferIdentity transferIdentity,
            int leaseGeneration)
        {
            if (leaseGeneration != _explicitUserIntentInputLeaseGeneration ||
                _desktopPresentationPixelsVerified ||
                !_transferState.Matches(transferIdentity) ||
                !_transferState.IsInteractive ||
                !ProviderPresentationCommitContract.Matches(
                    Volatile.Read(
                        ref _userIntentAuthorizedProviderPresentationId),
                    transferIdentity.PresentationId))
            {
                return;
            }

            _trace(
                "webview_provider_input_intent_lease_expired",
                $"presentation={transferIdentity.PresentationId} " +
                $"lease_ms={ExplicitUserIntentInputLeaseMilliseconds} " +
                "desktop_witness=False action=fail-closed-hide");
            _desiredVisible = false;
            ApplyVisibility(false);
            PreserveBrowserContentReadinessAfterPresentationFailure(
                "provider-input-intent-lease-expired");
        }

        private void CompleteQualifiedReveal(
            OverlayTransferIdentity transferIdentity,
            double completionWaitMilliseconds,
            string readinessContract)
        {
            if (!_transferState.Matches(transferIdentity) ||
                !_transferState.IsInteractive ||
                !_desiredVisible || !_actualVisible || !Visible)
            {
                return;
            }

            if (_coldHostVisibleRootPublishRequired &&
                HostSurfaceMode.IsInitializing(transferIdentity.SurfaceMode))
            {
                _coldHostVisibleRootPublishRequired = false;
                _trace(
                    "webview_initializer_visible_offscreen_root_committed",
                    $"generation={transferIdentity.TransferGeneration} " +
                    $"surface_generation={transferIdentity.SurfaceGeneration} " +
                    $"root_visual_revision={_webView.RootVisualRevision} " +
                    $"composition_generation={_webView.CompositionGeneration} " +
                    $"desktop_presentation_verified={_desktopPresentationPixelsVerified}");
            }

            CommitProviderInputAfterRevealFence();
            UpdateProviderPointerShield();
            // If the pending-stage owner write failed, the committed stage must
            // retry immediately instead of waiting for the next bounds tick.
            SynchronizeGameWindowOwner();
            SynchronizeWebViewInputParent();
            if (!_visibilityPublished)
            {
                _visibilityPublished = true;
                _visibilityChanged(true);
            }
            _trace(
                "webview_reveal_committed",
                $"generation={transferIdentity.TransferGeneration} " +
                $"readiness_contract={readinessContract} " +
                $"presentation={(transferIdentity.PresentationId.Length == 0 ? "none" : transferIdentity.PresentationId)} " +
                $"surface={transferIdentity.SurfaceMode} " +
                $"surface_generation={transferIdentity.SurfaceGeneration} " +
                "completion_wait=True " +
                $"completion_wait_ms={completionWaitMilliseconds:F3} " +
                $"prepare_to_commit_ms={ElapsedMilliseconds(_revealPreparedAt):F3} " +
                $"request_to_commit_ms={ElapsedMilliseconds(_revealRequestedAt):F3}");
            _trace(
                "webview_visibility_applied",
                $"visible=True desired_visible={_desiredVisible} " +
                $"browser_ready={_browserReady} " +
                $"desktop_verified={_desktopPresentationPixelsVerified} " +
                $"initialization_ms={(_initializationTimer?.Elapsed.TotalMilliseconds ?? 0d):F3}");
        }

        private void HandleDesktopPresentationFailure(
            OverlayTransferIdentity transferIdentity,
            Rectangle target,
            string reason)
        {
            if (!_transferState.TryFail(transferIdentity, reason))
                return;

            _trace(
                "webview_desktop_presentation_failed_closed",
                $"owner={transferIdentity.Owner} generation=" +
                    $"{transferIdentity.TransferGeneration} reason={reason} " +
                    $"target={target.Left},{target.Top},{target.Width},{target.Height} " +
                    "input_enabled=False recovery=none");
            SuspendProviderInputCommit("desktop-presentation-unverified");
            _desktopPresentationPixelsVerified = false;
            ApplyVisibility(false);
            _desiredVisible = false;
            PreserveBrowserContentReadinessAfterPresentationFailure(reason);
            _trace(
                "webview_desktop_presentation_abandoned",
                $"owner={transferIdentity.Owner} reason={reason} " +
                "state=hidden requires_fresh_request=True");
        }

        /// <summary>
        /// Desktop capture is not authoritative for an external HWND above an
        /// independent/exclusive-flip swap chain. Once Chromium's exact paint
        /// identity and the DirectComposition/native promotion boundaries have
        /// passed, keep the ordinary HWND promoted as a bounded best effort.
        /// It remains explicitly non-interactive and does not publish a provider
        /// presentation commit, preventing an invisible input lease when the
        /// game really is occluding the window. F9/explicit hide can still
        /// retire the visible attempt without a device-recreation loop.
        /// </summary>
        private bool KeepCompositionQualifiedPresentationVisible(
            OverlayTransferIdentity transferIdentity,
            Rectangle target,
            double completionWaitMilliseconds,
            string reason)
        {
            if (!_transferState.Matches(transferIdentity) ||
                (_transferState.Phase != OverlayTransferPhase.WindowPromoted &&
                 _transferState.Phase != OverlayTransferPhase.CompositionCommittedVisible) ||
                !_desiredVisible || !_actualVisible || !Visible)
            {
                return false;
            }
            if (_transferState.Phase == OverlayTransferPhase.WindowPromoted &&
                !_transferState.TryAdvance(
                    transferIdentity,
                    OverlayTransferPhase.WindowPromoted,
                    OverlayTransferPhase.CompositionCommittedVisible))
            {
                return false;
            }

            _desktopPresentationPixelsVerified = false;
            SuspendProviderInputCommit("desktop-presentation-unverified");
            if (_bootstrapPointerCaptureRequested)
            {
                _bootstrapPointerCaptureRequested = false;
                PostBootstrapPointerReset();
                ApplyWindowPointerCapture();
            }
            if (!_visibilityPublished)
            {
                // Publish native visibility so an F9/explicit close can retire
                // the best-effort HWND. Provider input remains gated by the
                // absent Interactive phase and provider-presentation commit.
                _visibilityPublished = true;
                _visibilityChanged(true);
            }

            var monitorBounds = Screen.FromHandle(_gtaWindow).Bounds;
            var fullscreenLike = monitorBounds == target;
            TraceTransferState("composition-qualified-passive", transferIdentity);
            _trace(
                "webview_desktop_presentation_best_effort_visible",
                $"owner={transferIdentity.Owner} generation=" +
                $"{transferIdentity.TransferGeneration} reason={reason} " +
                $"target={target.Left},{target.Top},{target.Width},{target.Height} " +
                $"fullscreen_like={fullscreenLike} " +
                "browser_paint_verified=True composition_commit=True " +
                "window_promoted=True desktop_presentation_verified=False " +
                "input_enabled=False external_hwnd_exclusive_limit=True " +
                $"completion_wait_ms={completionWaitMilliseconds:F3}");
            return true;
        }

        private void HandleFinalRevealPixelProofFailure(
            int revealGeneration,
            int controllerGeneration,
            int providerSessionGeneration,
            string? presentationId,
            string surfaceMode,
            int surfaceGeneration,
            int compositionGeneration,
            int browserSurfaceHealthGeneration,
            Rectangle target)
        {
            RevokeFinalRevealOffscreenLease(
                "pixel-proof-failure",
                revealGeneration);
            if (IsDisposed || Disposing || revealGeneration != _revealGeneration ||
                !_revealPending || !_desiredVisible || !_browserReady ||
                browserSurfaceHealthGeneration != _browserSurfaceHealthGeneration ||
                !RevealIdentityMatches(
                    controllerGeneration,
                    providerSessionGeneration,
                    presentationId,
                    surfaceMode,
                    surfaceGeneration,
                    compositionGeneration))
            {
                return;
            }

            var identity = string.Join(
                "|",
                controllerGeneration,
                providerSessionGeneration,
                presentationId ?? "none",
                surfaceMode,
                surfaceGeneration,
                compositionGeneration,
                browserSurfaceHealthGeneration,
                target.Width,
                target.Height);
            if (string.Equals(
                    identity,
                    _finalRevealPixelFailureIdentity,
                    StringComparison.Ordinal))
            {
                _finalRevealPixelFailureCount++;
            }
            else
            {
                _finalRevealPixelFailureIdentity = identity;
                _finalRevealPixelFailureCount = 1;
            }

            var attempt = _finalRevealPixelFailureCount;
            _trace(
                "webview_final_reveal_pixel_retry",
                $"attempt={attempt} maximum={MaximumFinalRevealPixelProofAttempts} " +
                $"presentation={presentationId ?? "none"} surface={surfaceMode} " +
                $"surface_generation={surfaceGeneration} " +
                $"controller_generation={controllerGeneration} " +
                "action=remain-hidden");

            // An unpainted capture is not itself a browser-process failure.
            // Retire only this native reveal lease, preserve the logical open
            // request, and allow Chromium another bounded paint opportunity.
            ApplyVisibility(false);
            if (attempt < MaximumFinalRevealPixelProofAttempts)
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (IsDisposed || Disposing || !_desiredVisible ||
                            !_browserReady || _actualVisible || _revealPending ||
                            _browserRecoveryInProgress || _browserRecoveryQueued ||
                            browserSurfaceHealthGeneration !=
                                _browserSurfaceHealthGeneration ||
                            !RevealIdentityMatches(
                                controllerGeneration,
                                providerSessionGeneration,
                                presentationId,
                                surfaceMode,
                                surfaceGeneration,
                                compositionGeneration))
                        {
                            return;
                        }
                        SynchronizeBounds();
                    }));
                    return;
                }
                catch (Exception error) when (
                    error is InvalidOperationException ||
                    error is ObjectDisposedException)
                {
                    _trace(
                        "webview_final_reveal_pixel_retry_queue_failed",
                        $"attempt={attempt} type={error.GetType().FullName} " +
                        $"message={error.Message}");
                }
            }

            // Exhausting desktop-presentation proof is not evidence that the
            // browser document or controller died. Retire only this surface
            // attempt; a later typed request can re-arm the still-warm page.
            _desiredVisible = false;
            ApplyVisibility(false);
            PreserveBrowserContentReadinessAfterPresentationFailure(
                "final-reveal-pixel-proof-failed");
            _trace(
                "webview_final_reveal_pixel_abandoned",
                $"attempt={attempt} presentation={presentationId ?? "none"} " +
                "state=hidden requires_fresh_request=True " +
                "browser_recovery=False");
        }

        private OverlayTransferIdentity CreateTransferIdentity(
            int transferGeneration,
            int controllerGeneration,
            int providerSessionGeneration,
            string? presentationId,
            string surfaceMode,
            int surfaceGeneration,
            int compositionGeneration,
            Rectangle target)
        {
            var owner = string.IsNullOrWhiteSpace(presentationId)
                ? OverlayTransferOwner.Bootstrap
                : OverlayTransferOwner.Provider;
            return new OverlayTransferIdentity(
                owner,
                transferGeneration,
                _gtaWindow.ToInt64(),
                target.Width,
                target.Height,
                controllerGeneration,
                compositionGeneration,
                providerSessionGeneration,
                surfaceMode,
                surfaceGeneration,
                presentationId);
        }

        private void TraceTransferState(
            string transition,
            OverlayTransferIdentity identity)
        {
            _trace(
                "webview_transfer_state",
                $"transition={transition} phase={_transferState.Phase} " +
                $"owner={identity.Owner} generation={identity.TransferGeneration} " +
                $"game_window=0x{identity.GameWindow:X} " +
                $"size={identity.Width}x{identity.Height} " +
                $"controller_generation={identity.ControllerGeneration} " +
                $"composition_generation={identity.CompositionGeneration} " +
                $"provider_session_generation={identity.ProviderSessionGeneration} " +
                $"surface={identity.SurfaceMode} " +
                $"surface_generation={identity.SurfaceGeneration} " +
                $"presentation={(identity.PresentationId.Length == 0 ? "none" : identity.PresentationId)}");
        }

        private bool RevealIdentityMatches(
            int controllerGeneration,
            int providerSessionGeneration,
            string? presentationId,
            string surfaceMode,
            int surfaceGeneration,
            int compositionGeneration) =>
            controllerGeneration == _controllerGeneration &&
            providerSessionGeneration == _providerSessionGeneration &&
            string.Equals(
                presentationId,
                _activeMenuPresentationId,
                StringComparison.Ordinal) &&
            string.Equals(
                surfaceMode,
                _activeHostSurfaceMode,
                StringComparison.Ordinal) &&
            surfaceGeneration == _activeHostSurfaceGeneration &&
            compositionGeneration == _webView.CompositionGeneration;

        private void FailCompositionReveal(
            RevealCompositionRefresh refresh,
            RootVisualRebindResult? rootRebind,
            int revealGeneration,
            int failureHResult = 0)
        {
            RevokeFinalRevealOffscreenLease(
                "composition-reveal-failure",
                revealGeneration);
            if (IsDisposed || Disposing ||
                revealGeneration != _revealGeneration ||
                !_revealPending || !_desiredVisible)
            {
                _trace(
                    "webview_composition_failure_ignored_stale",
                    $"generation={revealGeneration} " +
                    $"current_generation={_revealGeneration} " +
                    $"reveal_pending={_revealPending} " +
                    $"desired_visible={_desiredVisible}");
                return;
            }

            var reportedHResult = failureHResult != 0
                ? failureHResult
                : rootRebind.HasValue ? rootRebind.Value.HResult : 0;
            _trace(
                "webview_composition_refresh_failed",
                $"generation={revealGeneration} refresh={refresh} " +
                $"presentation={_activeMenuPresentationId ?? "none"} " +
                $"surface={_activeHostSurfaceMode} " +
                $"surface_generation={_activeHostSurfaceGeneration} " +
                $"root_rebind_outcome=" +
                $"{(rootRebind.HasValue ? rootRebind.Value.Outcome.ToString() : "none")} " +
                $"device_state=" +
                $"{(rootRebind.HasValue ? rootRebind.Value.DeviceState.ToString() : "unknown")} " +
                $"hresult=0x{reportedHResult:X8}");

            // A DirectComposition mutation can fail while WebView2's loaded
            // document and pipe remain healthy (for example
            // DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED). Do not turn that
            // presentation fault into a browser restart or a new content
            // generation. Hide this attempt and wait for a fresh typed surface
            // request, which can synchronize/rebind through the normal path.
            _desiredVisible = false;
            ApplyVisibility(false);
            PreserveBrowserContentReadinessAfterPresentationFailure(
                "composition-refresh-failed");
            _trace(
                "webview_composition_presentation_abandoned",
                $"generation={revealGeneration} hresult=0x{reportedHResult:X8} " +
                "state=hidden requires_fresh_request=True " +
                "browser_recovery=False");
        }

        private void ApplyOverlayTopMost(bool enabled)
        {
            if (!IsHandleCreated || IsDisposed || Disposing ||
                _overlayTopMostApplied == enabled)
            {
                return;
            }

            // A never-revealed preload window starts non-topmost already.
            // Avoid a redundant z-order mutation during hidden initialization.
            if (!enabled && _overlayTopMostApplied != true)
            {
                _overlayTopMostApplied = false;
                return;
            }

            var applied = NativeMethods.SetWindowPos(
                Handle,
                enabled ? NativeMethods.HwndTopMost : NativeMethods.HwndNoTopMost,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoSize);
            if (applied)
            {
                _overlayTopMostApplied = enabled;
            }
            _trace(
                applied ? "webview_z_order_applied" : "webview_z_order_failed",
                $"topmost={enabled} error={(applied ? 0 : Marshal.GetLastWin32Error())}");
        }

        private void ReassertOverlayZOrder(string reason)
        {
            if (!IsHandleCreated || IsDisposed || Disposing)
            {
                return;
            }

            var applied = NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndTopMost,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoSize);
            if (applied)
            {
                _overlayTopMostApplied = true;
            }
            _trace(
                applied
                    ? "webview_z_order_reasserted"
                    : "webview_z_order_reassert_failed",
                $"reason={reason} topmost=True " +
                $"error={(applied ? 0 : Marshal.GetLastWin32Error())}");
        }

        private static double ElapsedMilliseconds(long startedAt)
        {
            if (startedAt <= 0)
            {
                return 0d;
            }
            return (Stopwatch.GetTimestamp() - startedAt) * 1000d / Stopwatch.Frequency;
        }

        private static long MonotonicMilliseconds() =>
            Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

        private void TraceVisibilitySuppression(
            bool minimized,
            bool foreground,
            bool hasBounds,
            bool recoveredSurfaceReady)
        {
            if (!_desiredVisible || !_browserReady)
            {
                _lastVisibilitySuppression = string.Empty;
                return;
            }

            var reason = !recoveredSurfaceReady
                ? "recovered_menu_not_painted"
                : minimized
                ? "game_minimized"
                : !foreground
                    ? "game_not_foreground"
                    : !hasBounds
                        ? "client_bounds_unavailable"
                        : "unknown";
            if (string.Equals(
                reason,
                _lastVisibilitySuppression,
                StringComparison.Ordinal))
            {
                return;
            }

            _lastVisibilitySuppression = reason;
            _trace(
                "webview_visibility_suppressed",
                $"reason={reason} requested_visible={_desiredVisible} " +
                $"actual_visible={_actualVisible} " +
                DescribeInteractionForeground());
        }

        private bool IsInteractionForeground() =>
            NativeMethods.IsForegroundOrOwnedBy(
                _gtaWindow,
                IsHandleCreated ? Handle : IntPtr.Zero,
                _reactorProcessId,
                WindowedInputPolicy.AllowsInteractionForeground(
                    IsBootstrapPointerCaptureActive,
                    _providerPointerShieldRequested));

        private string DescribeInteractionForeground() =>
            NativeMethods.DescribeForegroundRelationship(
                _gtaWindow,
                IsHandleCreated ? Handle : IntPtr.Zero,
                _reactorProcessId,
                WindowedInputPolicy.AllowsInteractionForeground(
                    IsBootstrapPointerCaptureActive,
                    _providerPointerShieldRequested));

        private readonly struct DesktopPaintSample
        {
            internal DesktopPaintSample(
                double normalizedX,
                double normalizedY,
                Color expected)
            {
                NormalizedX = normalizedX;
                NormalizedY = normalizedY;
                Expected = expected;
            }

            internal double NormalizedX { get; }
            internal double NormalizedY { get; }
            internal Color Expected { get; }
        }

        private readonly struct BrowserPaintEvidence
        {
            internal static readonly BrowserPaintEvidence Empty =
                new BrowserPaintEvidence(
                    0,
                    0,
                    0,
                    0,
                    0,
                    Array.Empty<DesktopPaintSample>(),
                    paintIdentityMarkerMatched: false);

            internal BrowserPaintEvidence(
                int width,
                int height,
                int sampleCount,
                int opaqueSampleCount,
                int visibleColorSampleCount,
                IReadOnlyList<DesktopPaintSample> desktopSamples,
                bool paintIdentityMarkerMatched)
            {
                Width = width;
                Height = height;
                SampleCount = sampleCount;
                OpaqueSampleCount = opaqueSampleCount;
                VisibleColorSampleCount = visibleColorSampleCount;
                DesktopSamples = desktopSamples;
                PaintIdentityMarkerMatched = paintIdentityMarkerMatched;
            }

            internal int Width { get; }
            internal int Height { get; }
            internal int SampleCount { get; }
            internal int OpaqueSampleCount { get; }
            internal int VisibleColorSampleCount { get; }
            internal IReadOnlyList<DesktopPaintSample> DesktopSamples { get; }
            internal bool PaintIdentityMarkerMatched { get; }
            internal bool IsConcrete =>
                OverlayPresentationPolicy.HasConcreteBrowserPixels(
                    SampleCount,
                    OpaqueSampleCount,
                    VisibleColorSampleCount) &&
                DesktopSamples.Count > 0;
        }

        private readonly struct DesktopPaintEvidence
        {
            internal DesktopPaintEvidence(
                int readableSampleCount,
                int matchingSampleCount)
            {
                ReadableSampleCount = readableSampleCount;
                MatchingSampleCount = matchingSampleCount;
            }

            internal int ReadableSampleCount { get; }
            internal int MatchingSampleCount { get; }
            internal bool IsConcrete =>
                OverlayPresentationPolicy.HasConcreteDesktopPixels(
                    ReadableSampleCount,
                    MatchingSampleCount);
        }

        internal static Size HiddenPreloadClientSize() => new Size(640, 360);
    }
}
