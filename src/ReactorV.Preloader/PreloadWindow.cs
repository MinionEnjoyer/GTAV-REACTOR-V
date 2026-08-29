using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ReactorV.WebView2Host;

namespace ReactorV.Preloader
{
    internal sealed class PreloadWindow : Form
    {
        private readonly string _uiDirectory;
        private readonly string _userDataDirectory;
        private readonly Action<string, string?> _trace;
        private readonly Action _contentReady;
        private readonly Action<Exception> _startupFailed;
        private readonly WebView2 _webView;
        private CoreWebView2Environment? _environment;
        private bool _started;
        private bool _releaseStarted;
        private bool _initialInlineNavigationPending = true;

        public PreloadWindow(
            string uiDirectory,
            string userDataDirectory,
            Action<string, string?> trace,
            Action contentReady,
            Action<Exception> startupFailed)
        {
            _uiDirectory = uiDirectory;
            _userDataDirectory = userDataDirectory;
            _trace = trace;
            _contentReady = contentReady;
            _startupFailed = startupFailed;

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            // Cache warming does not need a desktop-sized compositor surface.
            ClientSize = new Size(640, 360);
            Opacity = 0d;
            Text = "REACTOR V Preloader";
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.Transparent,
            };
            Controls.Add(_webView);
        }

        protected override bool ShowWithoutActivation => true;

        public void BeginPreload()
        {
            if (_started || IsDisposed)
            {
                return;
            }

            _started = true;
            InitializeAsync();
        }

        public async Task<bool> ReleaseBrowserAsync(TimeSpan timeout)
        {
            if (_releaseStarted)
            {
                return true;
            }
            _releaseStarted = true;
            var environment = _environment;
            var browserExited = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2BrowserProcessExitedEventArgs>? exited = null;
            if (environment != null)
            {
                exited = (_, __) => browserExited.TrySetResult(true);
                environment.BrowserProcessExited += exited;
            }

            _trace("webview_profile_release_begin", null);
            try
            {
                // The WinForms wrapper owns the CoreWebView2Controller and its
                // Dispose path closes that controller synchronously.
                _webView.Dispose();

                if (environment == null)
                {
                    _trace("webview_profile_release_complete", "browser_exited=True environment=none");
                    return true;
                }

                var timeoutTask = Task.Delay(timeout);
                var completed = await Task.WhenAny(browserExited.Task, timeoutTask);
                var released = completed == browserExited.Task && browserExited.Task.Result;
                _trace(
                    "webview_profile_release_complete",
                    $"browser_exited={released} timeout_ms={timeout.TotalMilliseconds:F0}");
                return released;
            }
            finally
            {
                if (environment != null && exited != null)
                {
                    environment.BrowserProcessExited -= exited;
                }
            }
        }

        private async void InitializeAsync()
        {
            var initialization = Stopwatch.StartNew();
            try
            {
                _trace("webview_initialize_begin", $"udf={_userDataDirectory} ui={_uiDirectory}");
                _trace(
                    "webview_environment_contract",
                    WebView2EnvironmentFactory.Describe(_userDataDirectory));
                var environment = await WebView2EnvironmentFactory.CreateAsync(
                    _userDataDirectory);
                _environment = environment;
                _trace(
                    "webview_environment_ready",
                    $"version={environment.BrowserVersionString} elapsed_ms={initialization.Elapsed.TotalMilliseconds:F3}");
                await _webView.EnsureCoreWebView2Async(environment);
                var core = _webView.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.NavigationStarting += OnNavigationStarting;
                core.NavigationCompleted += OnNavigationCompleted;
                core.NewWindowRequested += (_, eventArgs) => eventArgs.Handled = true;
                WebView2LocalPage.Navigate(core, _uiDirectory);
                _trace(
                    "webview_navigation_begin",
                    $"elapsed_ms={initialization.Elapsed.TotalMilliseconds:F3}");
            }
            catch (Exception error)
            {
                _startupFailed(error);
            }
        }

        private async void OnNavigationCompleted(
            object? sender,
            CoreWebView2NavigationCompletedEventArgs args)
        {
            var paint = Stopwatch.StartNew();
            _trace(
                "webview_navigation_completed",
                $"success={args.IsSuccess} status={args.WebErrorStatus}");
            if (!args.IsSuccess || _webView.CoreWebView2 == null)
            {
                _startupFailed(new InvalidOperationException(
                    "The local ReactorV UI navigation failed: " + args.WebErrorStatus));
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
                _trace(
                    "webview_page_readiness_failed",
                    $"type={error.GetType().FullName} message={error.Message}");
                _startupFailed(error);
                return;
            }

            _trace("webview_content_ready", $"paint_ms={paint.Elapsed.TotalMilliseconds:F3}");
            _contentReady();
        }

        private void OnNavigationStarting(
            object? sender,
            CoreWebView2NavigationStartingEventArgs args)
        {
            if (!WebView2LocalPage.IsAllowedNavigation(
                args.Uri,
                ref _initialInlineNavigationPending))
            {
                args.Cancel = true;
            }
        }
    }
}
