using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using ReactorV.WebView2Host;

namespace RageWebUI.Runtime
{
    internal sealed class OverlayWindow : Form
    {
        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;
        private const int VisibleBoundsPollMilliseconds = 250;
        private const int HiddenBoundsPollMilliseconds = 1000;
        private const int MaximumPendingMessages = 128;
        private static readonly Color ChromaKey = Color.FromArgb(
            OverlayPresentationPolicy.ChromaKeyArgb);

        private readonly IntPtr _gtaWindow;
        private readonly string _uiDirectory;
        private readonly string _userDataDirectory;
        private readonly BridgeBroker _broker;
        private readonly bool _enableDevTools;
        private readonly Action<string, string?> _trace;
        private readonly Action<bool> _visibilityChanged;
        private readonly Action _contentReady;
        private readonly Action<Exception> _startupFailed;
        private readonly Timer _boundsTimer;
        private readonly WebView2 _webView;
        private readonly Queue<string> _pendingMessages = new Queue<string>();
        private bool _desiredVisible;
        private bool _browserReady;
        private bool _actualVisible;
        private bool _preloadStarted;
        private bool _initialInlineNavigationPending = true;
        private string _lastVisibilitySuppression = string.Empty;
        private Rectangle _lastBounds = Rectangle.Empty;
        private Stopwatch? _initializationTimer;
        private Stopwatch? _navigationTimer;

        public OverlayWindow(
            IntPtr gtaWindow,
            string uiDirectory,
            string userDataDirectory,
            BridgeBroker broker,
            bool enableDevTools,
            bool startVisible,
            Action<string, string?> trace,
            Action<bool> visibilityChanged,
            Action contentReady,
            Action<Exception> startupFailed)
        {
            _gtaWindow = gtaWindow;
            _uiDirectory = uiDirectory;
            _userDataDirectory = userDataDirectory;
            _broker = broker;
            _enableDevTools = enableDevTools;
            _trace = trace;
            _visibilityChanged = visibilityChanged;
            _contentReady = contentReady;
            _startupFailed = startupFailed;
            _desiredVisible = startVisible;

            AutoScaleMode = AutoScaleMode.None;
            BackColor = ChromaKey;
            TransparencyKey = ChromaKey;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "REACTOR V Overlay";
            Opacity = 0d;
            Location = new Point(-32000, -32000);
            // The browser is fully hidden during preload. Allocating a full
            // 1440p/4K composition surface here competes with GTA's heaviest
            // startup work without improving cache warmth. SynchronizeBounds
            // expands it to the exact game client immediately before reveal.
            ClientSize = HiddenPreloadClientSize();
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = ChromaKey,
            };
            Controls.Add(_webView);
            _boundsTimer = new Timer
            {
                Interval = startVisible
                    ? VisibleBoundsPollMilliseconds
                    : HiddenBoundsPollMilliseconds,
            };
            _boundsTimer.Tick += (_, __) => SynchronizeBounds();
            FormClosed += (_, __) =>
            {
                _boundsTimer.Dispose();
                ClearPendingMessages("window_closed");
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmMouseActivate)
            {
                message.Result = new IntPtr(MaNoActivate);
                return;
            }
            base.WndProc(ref message);
        }

        public void SetOverlayVisible(bool visible)
        {
            _desiredVisible = visible;
            _boundsTimer.Interval = visible
                ? VisibleBoundsPollMilliseconds
                : HiddenBoundsPollMilliseconds;
            _trace(
                "webview_visibility_requested",
                $"visible={visible} browser_ready={_browserReady} actual_visible={_actualVisible}");
            SynchronizeBounds();
        }

        public void BeginPreload()
        {
            if (_preloadStarted || IsDisposed)
            {
                return;
            }

            _preloadStarted = true;
            _trace("webview_preload_begin", null);
            InitializeBrowserAsync();
        }

        public void PostJson(string json)
        {
            if (IsDisposed || Disposing || _webView.IsDisposed)
            {
                return;
            }

            var core = _webView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            if (!_browserReady)
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

            core.PostWebMessageAsJson(json);
        }

        private async void InitializeBrowserAsync()
        {
            try
            {
                _initializationTimer = Stopwatch.StartNew();
                _trace("webview_initialize_begin", null);
                NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GwlHwndParent, _gtaWindow);
                _trace(
                    "webview_environment_contract",
                    WebView2EnvironmentFactory.Describe(_userDataDirectory));
                var environment = await WebView2EnvironmentFactory.CreateAsync(
                    _userDataDirectory);
                _trace(
                    "webview_environment_ready",
                    $"version={environment.BrowserVersionString} " +
                    $"duration_ms={_initializationTimer.Elapsed.TotalMilliseconds:F3}");
                await EnsureControllerWithRetryAsync(environment);
                _trace(
                    "webview_controller_ready",
                    $"duration_ms={_initializationTimer.Elapsed.TotalMilliseconds:F3}");
                var core = _webView.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = _enableDevTools;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.NavigationStarting += OnNavigationStarting;
                core.NavigationCompleted += OnNavigationCompleted;
                core.NewWindowRequested += (_, eventArgs) => eventArgs.Handled = true;
                core.WebMessageReceived += OnWebMessageReceived;
                _navigationTimer = Stopwatch.StartNew();
                WebView2LocalPage.Navigate(core, _uiDirectory);
                _boundsTimer.Start();
                _trace(
                    "webview_navigation_begin",
                    $"initialization_ms={_initializationTimer.Elapsed.TotalMilliseconds:F3}");
            }
            catch (Exception error)
            {
                _browserReady = false;
                _desiredVisible = false;
                ClearPendingMessages("initialization_failed");
                ApplyVisibility(false);
                _startupFailed(error);
            }
        }

        private async Task EnsureControllerWithRetryAsync(
            CoreWebView2Environment environment)
        {
            var failedAttempts = 0;
            while (true)
            {
                try
                {
                    await _webView.EnsureCoreWebView2Async(environment);
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
                }
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            _trace(
                "webview_navigation_completed",
                $"success={args.IsSuccess} status={args.WebErrorStatus} " +
                $"duration_ms={(_navigationTimer?.Elapsed.TotalMilliseconds ?? 0d):F3}");
            if (!args.IsSuccess || _webView.CoreWebView2 == null)
            {
                _browserReady = false;
                ClearPendingMessages("navigation_failed");
                return;
            }

            try
            {
                var pageTiming = await WebView2PageReadiness.WaitAsync(
                    _webView.CoreWebView2,
                    TimeSpan.FromSeconds(2));
                _trace("webview_page_timing", $"metrics={pageTiming}");
            }
            catch (Exception error)
            {
                _browserReady = false;
                _desiredVisible = false;
                ClearPendingMessages("page_readiness_failed");
                ApplyVisibility(false);
                _trace(
                    "webview_page_readiness_failed",
                    $"type={error.GetType().FullName} message={error.Message}");
                _startupFailed(error);
                return;
            }

            if (IsDisposed || Disposing || _webView.IsDisposed)
            {
                ClearPendingMessages("window_closed_during_readiness");
                return;
            }

            _browserReady = true;
            FlushPendingMessages();
            _trace(
                "webview_content_ready",
                $"navigation_ms={(_navigationTimer?.Elapsed.TotalMilliseconds ?? 0d):F3} " +
                $"initialization_ms={(_initializationTimer?.Elapsed.TotalMilliseconds ?? 0d):F3} " +
                $"desired_visible={_desiredVisible}");
            _contentReady();
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
                core.PostWebMessageAsJson(_pendingMessages.Dequeue());
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

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (!WebView2LocalPage.IsAllowedNavigation(
                args.Uri,
                ref _initialInlineNavigationPending))
            {
                args.Cancel = true;
            }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!WebView2LocalPage.IsTrustedMessageSource(args.Source))
            {
                _trace("webview_message_rejected", $"source={args.Source}");
                return;
            }
            var json = args.WebMessageAsJson;
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

        private void SynchronizeBounds()
        {
            var minimized = NativeMethods.IsIconic(_gtaWindow);
            var foreground = NativeMethods.IsForegroundOrOwnedBy(_gtaWindow);
            var hasBounds = NativeMethods.TryGetClientBounds(_gtaWindow, out var target);
            var shouldPresent = OverlayPresentationPolicy.ShouldPresent(
                _desiredVisible,
                _browserReady,
                minimized,
                foreground,
                hasBounds);
            if (!shouldPresent)
            {
                TraceVisibilitySuppression(minimized, foreground, hasBounds);
                ApplyVisibility(false);
                return;
            }

            _lastVisibilitySuppression = string.Empty;

            if (target != _lastBounds)
            {
                NativeMethods.SetWindowPos(
                    Handle,
                    IntPtr.Zero,
                    target.Left,
                    target.Top,
                    target.Width,
                    target.Height,
                    NativeMethods.SwpNoActivate);
                _lastBounds = target;
            }

            ApplyVisibility(true);
        }

        private void ApplyVisibility(bool visible)
        {
            if (visible == _actualVisible)
            {
                return;
            }

            if (visible)
            {
                // Size and expose the color-keyed surface while fully
                // transparent. Raising opacity only after Show prevents a
                // not-yet-composited WebView child from flashing black.
                Opacity = 0d;
                NativeMethods.SetWindowPos(
                    Handle,
                    IntPtr.Zero,
                    _lastBounds.Left,
                    _lastBounds.Top,
                    Math.Max(1, _lastBounds.Width),
                    Math.Max(1, _lastBounds.Height),
                    NativeMethods.SwpNoActivate);
                if (!Visible)
                {
                    Show();
                }
                Opacity = 1d;
            }
            else
            {
                Opacity = 0d;
                if (Visible)
                {
                    Hide();
                }
            }

            _actualVisible = visible;
            _visibilityChanged(visible);
            _trace(
                "webview_visibility_applied",
                $"visible={visible} desired_visible={_desiredVisible} browser_ready={_browserReady} " +
                $"initialization_ms={(_initializationTimer?.Elapsed.TotalMilliseconds ?? 0d):F3}");
        }

        private void TraceVisibilitySuppression(
            bool minimized,
            bool foreground,
            bool hasBounds)
        {
            if (!_desiredVisible || !_browserReady)
            {
                _lastVisibilitySuppression = string.Empty;
                return;
            }

            var reason = minimized
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
                $"actual_visible={_actualVisible}");
        }

        internal static Size HiddenPreloadClientSize() => new Size(640, 360);
    }
}
