using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using RageWebUI.Runtime;
using ReactorV.Windowing;

namespace RageWebUI.Harness
{
    /// <summary>
    /// Exercises the same hosting constraint as ScriptHookVDotNet: the managed
    /// script runs in a secondary AppDomain, where CefSharp must not start and
    /// the WebView2 renderer must preserve visibility requests made before its
    /// UI thread has created a window handle.
    /// </summary>
    internal static class SecondaryAppDomainHarness
    {
        private const string OverlayWindowTitle = "REACTOR V Overlay";

        public static int Run(HarnessOptions options)
        {
            var runtimeDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var uiDirectory = options.UiDirectory ?? Path.Combine(runtimeDirectory, "ui");
            if (!File.Exists(Path.Combine(uiDirectory, "index.html")))
            {
                Console.Error.WriteLine($"React UI was not found at '{uiDirectory}'.");
                return 3;
            }

            // Isolate WebView2 state and the trace just like the DirectX/CEF
            // harness profile. This allows build and developer harnesses to run
            // side-by-side without sharing browser locks or log contents.
            var localDataDirectory = options.LocalDataDirectory ??
                HarnessRunDirectory.For("ShvdnFallback");
            Directory.CreateDirectory(localDataDirectory);
            var logPath = Path.Combine(localDataDirectory, "reactorv-runtime.log");
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }

            using var host = new Form
            {
                ClientSize = new System.Drawing.Size(options.Width, options.Height),
                StartPosition = FormStartPosition.CenterScreen,
                Text = "Grand Theft Auto V - REACTOR V SHVDN fallback harness",
            };
            using var transientHostWindow = new Form
            {
                ClientSize = new System.Drawing.Size(560, 68),
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(8, 8),
                Text = "Rockstar startup status",
            };
            host.Show();
            transientHostWindow.Show();
            Application.DoEvents();

            // The production overlay deliberately remains hidden unless GTA
            // (or one of its owned windows) is foreground. Developer and
            // packaged smoke tests are often launched by a background terminal
            // or CI-style runner, so merely calling Form.Show() is racy: the
            // browser can become ready while another application still owns
            // foreground activation. Give the synthetic GTA host foreground
            // deterministically instead of weakening the runtime gate.
            var hostForeground = WindowProbe.EnsureForeground(
                host.Handle,
                TimeSpan.FromSeconds(3));
            Console.WriteLine($"Harness host foreground: {hostForeground}");
            if (!hostForeground)
            {
                Console.Error.WriteLine(
                    "Could not activate the synthetic GTA host window; " +
                    "the production foreground visibility gate cannot be tested deterministically.");
                return 5;
            }

            var setup = new AppDomainSetup
            {
                ApplicationBase = runtimeDirectory,
                PrivateBinPath = runtimeDirectory,
                ShadowCopyFiles = "false",
            };
            var domain = AppDomain.CreateDomain(
                "ScriptHookVDotNet-ScriptDomain-Harness",
                null,
                setup);
            SecondaryAppDomainHarnessProxy? proxy = null;
            try
            {
                proxy = (SecondaryAppDomainHarnessProxy)domain.CreateInstanceAndUnwrap(
                    Assembly.GetExecutingAssembly().FullName,
                    typeof(SecondaryAppDomainHarnessProxy).FullName);
                var started = proxy.StartAndRequestEarlyVisibility(
                    // Process.MainWindowHandle can transiently report a small
                    // launcher/status HWND in Enhanced. Deliberately supply
                    // that wrong same-process handle and require the runtime
                    // to recover the actual large game surface.
                    transientHostWindow.Handle,
                    uiDirectory,
                    runtimeDirectory,
                    localDataDirectory,
                    options.Width,
                    options.Height,
                    "directx");
                Console.WriteLine(
                    $"Secondary-domain harness started: domain={proxy.DomainName}, " +
                    $"default={proxy.IsDefaultAppDomain}, renderer={proxy.RendererName}");

                var duration = options.Duration ?? TimeSpan.FromMinutes(10);
                var stopwatch = Stopwatch.StartNew();
                var nextSetupSurface = TimeSpan.Zero;
                var requests = 0;
                var browserReady = false;
                var desktopPresentationReady = false;
                var raceVisible = false;
                var prematureVisible = false;
                while (host.Visible && stopwatch.Elapsed < duration)
                {
                    Application.DoEvents();
                    // A concurrently warming WebView2 profile can briefly
                    // activate one of its helper windows after the synthetic
                    // GTA host was foregrounded. Reassert the host just as the
                    // real game does by remaining the active top-level window;
                    // otherwise this contention test measures desktop focus
                    // timing instead of the shared-profile contract.
                    if (!WindowProbe.IsForegroundOrOwnedBy(host.Handle))
                    {
                        WindowProbe.EnsureForeground(
                            host.Handle,
                            TimeSpan.FromMilliseconds(500));
                    }
                    if (requests < 2 && stopwatch.Elapsed >= nextSetupSurface)
                    {
                        proxy.PresentSetupStatus();
                        nextSetupSurface = stopwatch.Elapsed + TimeSpan.FromMilliseconds(250);
                    }
                    requests = proxy.Pump();
                    var log = ReadLog(logPath);
                    browserReady = log.Contains("webview_content_ready");
                    desktopPresentationReady = log.Contains(
                        "webview_desktop_presentation_verified");
                    // WebView2 requires its controller parent to be WS_VISIBLE
                    // at creation. Reactor satisfies that contract with an
                    // offscreen, nonactivating lease; only a window intersecting
                    // the virtual desktop counts as presented to the player.
                    var currentlyVisible = WindowProbe.IsVisible(OverlayWindowTitle);
                    if (currentlyVisible && !browserReady)
                    {
                        // The browser thread can cross its readiness/reveal
                        // boundary between the first log snapshot and the HWND
                        // sample. Re-read after observing an on-desktop window
                        // so that sampling skew is not reported as a premature
                        // reveal. A real early reveal still has no readiness
                        // record and remains a hard failure.
                        log = ReadLog(logPath);
                        browserReady = log.Contains("webview_content_ready");
                        if (!browserReady)
                        {
                            prematureVisible = true;
                        }
                    }
                    raceVisible = currentlyVisible;
                    // A top-level HWND intersecting the desktop is only the
                    // promoted candidate. Do not race ahead to hide/show until
                    // the out-of-process desktop-duplication proof has observed
                    // the expected browser pixels and committed interactivity.
                    if (browserReady && desktopPresentationReady &&
                        raceVisible && requests >= 2)
                    {
                        break;
                    }
                    Thread.Sleep(10);
                }

                // Confirm that normal F10-style transitions still work after
                // the pre-handle visibility request has been replayed.
                proxy.SetVisible(false);
                var hideWorked = WaitForVisibility(OverlayWindowTitle, false, TimeSpan.FromSeconds(2));
                var hiddenOwnerDetached = hideWorked && WindowProbe.OwnerMatches(
                    OverlayWindowTitle,
                    IntPtr.Zero);
                var verifiedPresentationCount = CountOccurrences(
                    ReadLog(logPath),
                    "webview_desktop_presentation_verified");
                var showWorked = WaitForVisibleWithDesktopProof(
                    OverlayWindowTitle,
                    host,
                    () => proxy.SetVisible(false),
                    () => proxy.SetVisible(true),
                    logPath,
                    verifiedPresentationCount + 1,
                    TimeSpan.FromSeconds(5));

                // An owned/sibling top-level window in GTA's process may take
                // foreground focus. That must not hide the overlay; only a
                // different process should trip the safety gate.
                var siblingForeground = WindowProbe.EnsureForeground(
                    transientHostWindow.Handle,
                    TimeSpan.FromSeconds(1));
                var sameProcessForegroundVisible = WaitForVisibility(
                    OverlayWindowTitle,
                    true,
                    TimeSpan.FromSeconds(1));
                WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromMilliseconds(500));

                var trace = ReadLog(logPath);
                var skippedCef = trace.Contains("directx_skipped reason=cefsharp_requires_default_appdomain");
                var contentReadyAt = trace.IndexOf("webview_content_ready", StringComparison.Ordinal);
                var firstVisibleAt = trace.IndexOf("webview_visibility_applied visible=True", StringComparison.Ordinal);
                var replayedEarlyVisibility = contentReadyAt >= 0 && firstVisibleAt > contentReadyAt;
                var expectedResolution =
                    $"previous=0x{transientHostWindow.Handle.ToInt64():X} " +
                    $"current=0x{host.Handle.ToInt64():X}";
                var recoveredGameWindow = trace.Contains(expectedResolution);
                var recoveredGameWindowOwner = WindowProbe.OwnerMatches(
                    OverlayWindowTitle,
                    host.Handle);
                var passed = started && hostForeground && !proxy.IsDefaultAppDomain &&
                    string.Equals(proxy.RendererName, "WebView2 window", StringComparison.Ordinal) &&
                    skippedCef && replayedEarlyVisibility && !prematureVisible &&
                    browserReady && desktopPresentationReady && raceVisible &&
                    hideWorked && hiddenOwnerDetached && showWorked && recoveredGameWindow &&
                    recoveredGameWindowOwner && siblingForeground &&
                    sameProcessForegroundVisible && requests >= 2;
                Console.WriteLine(
                    $"RESULT {(passed ? "PASS" : "FAIL")}: scenario=shvdn-fallback " +
                    $"cefSkipped={skippedCef} earlyVisibility={replayedEarlyVisibility} " +
                    $"started={started} hostForeground={hostForeground} " +
                    $"secondaryDomain={!proxy.IsDefaultAppDomain} " +
                    $"browserReady={browserReady} desktopReady={desktopPresentationReady} " +
                    $"prematureVisible={prematureVisible} initiallyVisible={raceVisible} " +
                    $"hide={hideWorked} hiddenOwnerDetached={hiddenOwnerDetached} " +
                    $"show={showWorked} recoveredGameWindow={recoveredGameWindow} " +
                    $"recoveredOwner={recoveredGameWindowOwner} " +
                    $"siblingForeground={siblingForeground} sameProcessVisible={sameProcessForegroundVisible} " +
                    $"requests={requests}");
                if (!passed)
                {
                    Console.Error.WriteLine($"Runtime trace: {logPath}");
                }
                return passed ? 0 : 4;
            }
            finally
            {
                try
                {
                    proxy?.DisposeRuntime();
                }
                finally
                {
                    AppDomain.Unload(domain);
                }
            }
        }

        private static bool WaitForVisibility(string title, bool expected, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                Application.DoEvents();
                if (WindowProbe.IsVisible(title) == expected)
                {
                    return true;
                }
                Thread.Sleep(10);
            }
            return false;
        }

        private static bool WaitForVisibleWithDesktopProof(
            string title,
            Form host,
            Action requestHidden,
            Action requestVisible,
            string logPath,
            int requiredVerifiedPresentationCount,
            TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            var nextVisibilityRequest = TimeSpan.Zero;
            var unverifiedPresentationCount = CountOccurrences(
                ReadLog(logPath),
                "webview_desktop_presentation_unverified");
            var recoveryAttempts = 0;
            while (stopwatch.Elapsed < timeout)
            {
                Application.DoEvents();
                var log = ReadLog(logPath);
                if (WindowProbe.IsVisible(title) &&
                    CountOccurrences(
                        log,
                        "webview_desktop_presentation_verified") >=
                    requiredVerifiedPresentationCount)
                {
                    return true;
                }

                var currentUnverifiedCount = CountOccurrences(
                    log,
                    "webview_desktop_presentation_unverified");
                if (currentUnverifiedCount > unverifiedPresentationCount &&
                    recoveryAttempts < 2)
                {
                    // A desktop witness can lose one race to DWM composition
                    // even though the browser paint and promoted window were
                    // valid. Repeating SetVisible(true) against an already
                    // visible controller cannot create a new witness epoch, so
                    // retire it once and request a clean presentation instead
                    // of reporting a timing false positive.
                    recoveryAttempts++;
                    unverifiedPresentationCount = currentUnverifiedCount;
                    requestHidden();
                    WaitForVisibility(
                        title,
                        expected: false,
                        timeout: TimeSpan.FromMilliseconds(750));
                    requestVisible();
                    nextVisibilityRequest =
                        stopwatch.Elapsed + TimeSpan.FromMilliseconds(150);
                }

                // Browser helpers and unrelated desktop windows can briefly
                // take focus during a packaged smoke run. Production correctly
                // suppresses the overlay in that state, so keep the runtime
                // gate intact and instead restore the synthetic GTA host before
                // replaying the harness visibility request.
                if (!WindowProbe.IsForegroundOrOwnedBy(host.Handle) &&
                    !WindowProbe.EnsureForeground(
                        host.Handle,
                        TimeSpan.FromMilliseconds(500)))
                {
                    Thread.Sleep(10);
                    continue;
                }

                if (stopwatch.Elapsed >= nextVisibilityRequest)
                {
                    requestVisible();
                    nextVisibilityRequest =
                        stopwatch.Elapsed + TimeSpan.FromMilliseconds(150);
                }
                Thread.Sleep(10);
            }
            return false;
        }

        private static int CountOccurrences(string value, string marker)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(marker))
                return 0;
            var count = 0;
            var offset = 0;
            while ((offset = value.IndexOf(
                       marker,
                       offset,
                       StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += marker.Length;
            }
            return count;
        }

        private static string ReadLog(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Marshal-by-reference boundary used to keep the production runtime and
    /// browser UI thread inside the simulated SHVDN AppDomain.
    /// </summary>
    public sealed class SecondaryAppDomainHarnessProxy : MarshalByRefObject
    {
        private BridgeBroker? _broker;
        private IOverlayRuntime? _runtime;
        private GbayLifecycleHarness.GbayHarnessRouter? _gbayRouter;
        private int _handledRequests;
        private int _hostSurfaceGeneration;
        private string? _finalizedGbayPresentationId;

        public string DomainName => AppDomain.CurrentDomain.FriendlyName;

        public bool IsDefaultAppDomain => AppDomain.CurrentDomain.IsDefaultAppDomain();

        public string RendererName => _runtime?.RendererName ?? "not started";

        public bool StartAndRequestEarlyVisibility(
            IntPtr hostWindow,
            string uiDirectory,
            string runtimeDirectory,
            string localDataDirectory,
            int width,
            int height,
            string renderer)
        {
            return StartRuntime(
                hostWindow,
                uiDirectory,
                runtimeDirectory,
                localDataDirectory,
                width,
                height,
                renderer,
                requestEarlyVisibility: true,
                useGbayRouter: false);
        }

        public bool StartForBootstrapGbayHandoff(
            IntPtr hostWindow,
            string uiDirectory,
            string runtimeDirectory,
            string localDataDirectory,
            int width,
            int height)
        {
            return StartRuntime(
                hostWindow,
                uiDirectory,
                runtimeDirectory,
                localDataDirectory,
                width,
                height,
                "auto",
                requestEarlyVisibility: false,
                useGbayRouter: true);
        }

        private bool StartRuntime(
            IntPtr hostWindow,
            string uiDirectory,
            string runtimeDirectory,
            string localDataDirectory,
            int width,
            int height,
            string renderer,
            bool requestEarlyVisibility,
            bool useGbayRouter)
        {
            _broker = new BridgeBroker();
            _runtime = new OverlayRuntime(
                renderer,
                hostWindow,
                uiDirectory,
                runtimeDirectory,
                localDataDirectory,
                _broker,
                width,
                height,
                60,
                false,
                false);
            if (useGbayRouter)
            {
                _gbayRouter = new GbayLifecycleHarness.GbayHarnessRouter(
                    visible => _runtime?.SetVisible(visible));
            }
            var started = _runtime.Start();

            // This deliberately occurs immediately after Start, while the new
            // STA thread is normally still constructing its Form/WebView2.
            if (requestEarlyVisibility) _runtime.SetVisible(true);
            return started;
        }

        public int Pump()
        {
            var runtime = _runtime;
            var broker = _broker;
            if (runtime == null || broker == null)
            {
                return _handledRequests;
            }

            runtime.PumpInput();
            for (var index = 0; index < 32 && broker.TryDequeue(out var request); index++)
            {
                if (request == null)
                {
                    continue;
                }
                var response = _gbayRouter?.Dispatch(request) ?? Dispatch(request);
                var acceptedPresentationId =
                    _gbayRouter != null &&
                    string.Equals(
                        request.Method,
                        "overlay.presentationReady",
                        StringComparison.Ordinal) &&
                    response.Error == null &&
                    response.Result is JObject readyResult &&
                    readyResult.Value<bool?>("accepted") == true
                        ? readyResult.Value<string>("presentationId")
                        : null;
                runtime.PostResponse(response);
                if (!string.IsNullOrWhiteSpace(acceptedPresentationId))
                {
                    // Browser-ready is phase one only. Deliver its accepted
                    // response before requesting the reveal so Chromium can
                    // paint the provider identity that the persistent host
                    // must prove in phase two.
                    runtime.SetVisible(true);
                }
                _handledRequests++;
            }
            TryFinalizeGbayPresentationHandoff();
            runtime.PostEvent(
                "game.state",
                new JObject
                {
                    ["gameTime"] = Environment.TickCount & int.MaxValue,
                    ["paused"] = false,
                });
            return _handledRequests;
        }

        public void SetVisible(bool visible) => _runtime?.SetVisible(visible);

        public bool RetireBootstrapSurface(bool hide)
        {
            if (!(_runtime is IBootstrapSurfaceRuntime bootstrapRuntime))
                return false;
            bootstrapRuntime.RetireBootstrapSurface(hide);
            return string.Equals(
                bootstrapRuntime.CurrentHostSurface,
                HostSurfaceMode.None,
                StringComparison.Ordinal);
        }

        public bool BootstrapSurfaceRetirementPending =>
            (_runtime as IBootstrapSurfaceRuntime)?
                .BootstrapSurfaceRetirementPending == true;

        public bool PresentGbay(string presentationId)
        {
            var runtime = _runtime;
            var router = _gbayRouter;
            if (runtime == null || router == null ||
                string.IsNullOrWhiteSpace(presentationId))
            {
                return false;
            }

            router.ExpectPresentation(presentationId);
            _finalizedGbayPresentationId = null;
            runtime.PostEvent(
                "overlay.snapshot",
                new JObject
                {
                    ["runtime"] = GbayLifecycleHarness.GbayHarnessRouter.RuntimeStatus(),
                    ["state"] = GbayLifecycleHarness.GbayHarnessRouter.GameState(),
                });
            runtime.PostEvent(
                "menu.presentation",
                new JObject
                {
                    ["extensionId"] = "allin1.gbay",
                    ["menuId"] = "home",
                    ["presentationId"] = presentationId,
                    ["inputMode"] = "interactive-menu",
                    ["context"] = new JObject
                    {
                        ["route"] = "gbay/home",
                        ["presentationStyle"] = "allin1-shell",
                        ["initialSection"] = "home",
                        ["menuRevision"] = "bootstrap-handoff-1",
                    },
                });
            return true;
        }

        public bool ConsumeDefaultMenuIntentAndPresentGbay(
            int processId,
            string presentationId)
        {
            if (_gbayRouter == null || _gbayRouter.SubscriptionCount < 1 ||
                !PreloadHandoff.ManagedOwnsF9(processId) ||
                !PreloadHandoff.TryConsumeDefaultMenuIntent(processId))
            {
                return false;
            }

            return DispatchReservedDefaultMenuIntent(
                processId,
                presentationId);
        }

        /// <summary>
        /// Models the production boundary between ALLIN1 reserving the
        /// process-scoped startup request and Reactor draining the resulting
        /// typed menu presentation. Escape/provider disconnect can cancel the
        /// request in that interval; in that case no browser event or claim is
        /// allowed to escape this boundary.
        /// </summary>
        public bool DispatchReservedDefaultMenuIntent(
            int processId,
            string presentationId)
        {
            if (_gbayRouter == null || _gbayRouter.SubscriptionCount < 1 ||
                !PreloadHandoff.CanDispatchDefaultMenuIntent(processId) ||
                !PreloadHandoff.TryCommitDefaultMenuIntentClaim(processId))
            {
                return false;
            }

            // Production dispatches the typed presentation first. Browser
            // readiness requests visibility, but the initializer remains the
            // last known-good surface until the persistent host publishes an
            // exact provider-paint commit for this presentation.
            return PresentGbay(presentationId);
        }

        public bool DismissPresentedGbay(string presentationId)
        {
            var runtime = _runtime;
            if (runtime == null || string.IsNullOrWhiteSpace(presentationId))
                return false;

            // Mirror the production close boundary exactly: clear bootstrap
            // identity before unmounting the typed presentation, then hide.
            // Reversing the first two events can reveal stale initializer
            // pixels for one browser frame.
            runtime.PostEvent(
                "host.surface",
                new JObject { ["mode"] = HostSurfaceMode.None });
            runtime.PostEvent(
                "menu.dismissed",
                new JObject
                {
                    ["extensionId"] = "allin1.gbay",
                    ["menuId"] = "home",
                    ["presentationId"] = presentationId,
                    ["reason"] = "overlay-hidden",
                });
            runtime.SetVisible(false);
            return true;
        }

        public bool IsGbayPresentationReady(string presentationId) =>
            _gbayRouter != null &&
            string.Equals(
                _gbayRouter.LastAcceptedPresentation,
                presentationId,
                StringComparison.Ordinal) &&
            _runtime is IProviderPresentationCommitRuntime commitRuntime &&
            commitRuntime.IsProviderPresentationCommitted(presentationId);

        private void TryFinalizeGbayPresentationHandoff()
        {
            var runtime = _runtime;
            var presentationId = _gbayRouter?.LastAcceptedPresentation;
            if (runtime == null || string.IsNullOrWhiteSpace(presentationId) ||
                string.Equals(
                    _finalizedGbayPresentationId,
                    presentationId,
                    StringComparison.Ordinal) ||
                !(runtime is IProviderPresentationCommitRuntime commitRuntime))
            {
                return;
            }
            var exactPresentationId = presentationId!;
            if (!commitRuntime.IsProviderPresentationCommitted(
                    exactPresentationId))
            {
                return;
            }

            // This is the harness equivalent of the managed script's phase
            // two gate. Only the exact native paint commit may retire the
            // initializer and authorize the provider as presentation-ready.
            if (runtime is IBootstrapSurfaceRuntime bootstrapRuntime &&
                HostSurfaceMode.IsInitializing(
                    bootstrapRuntime.CurrentHostSurface))
            {
                bootstrapRuntime.RetireBootstrapSurface(hide: false);
            }
            runtime.SetVisible(true);
            _finalizedGbayPresentationId = exactPresentationId;
        }

        public int GbayStaleAcknowledgements =>
            _gbayRouter?.StalePresentationReadyCount ?? 0;

        public int GbaySubscriptionCount =>
            _gbayRouter?.SubscriptionCount ?? 0;

        public int GbayStartupStatusRequestCount =>
            _gbayRouter?.StartupStatusRequestCount ?? 0;

        public bool GbayStartupDefaultMenuRequested =>
            _gbayRouter?.LastStartupDefaultMenuRequested ?? false;

        public int GbayReadyAcknowledgements =>
            _gbayRouter?.ExactPresentationReadyCount ?? 0;

        public string CurrentHostSurface =>
            (_runtime as IHostSurfaceRuntime)?.CurrentHostSurface ??
            HostSurfaceMode.None;

        public int ReadyContentGeneration()
        {
            return _runtime is IContentGenerationRuntime generationRuntime &&
                generationRuntime.TryGetReadyContentGeneration(out var generation)
                    ? generation
                    : 0;
        }

        public RuntimeReadyHandoffState AdvanceRuntimeReadyHandoff(
            int expectedContentGeneration)
        {
            return _runtime is IContentGenerationRuntime generationRuntime
                ? generationRuntime.AdvanceRuntimeReadyHandoff(
                    expectedContentGeneration)
                : RuntimeReadyHandoffState.Unavailable;
        }

        public void PresentSetupStatus()
        {
            var runtime = _runtime;
            if (runtime == null) return;
            runtime.PostEvent(
                "host.provider",
                new JObject { ["connected"] = true });
            runtime.PostEvent(
                "host.surface",
                new JObject
                {
                    ["mode"] = HostSurfaceMode.SetupStatus,
                    ["generation"] = ++_hostSurfaceGeneration,
                });
        }

        public void DisposeRuntime()
        {
            _gbayRouter?.Dispose();
            _gbayRouter = null;
            _runtime?.Dispose();
            _runtime = null;
            _broker = null;
        }

        public override object InitializeLifetimeService() => null!;

        private static BridgeResponse Dispatch(BridgeRequest request)
        {
            JToken result;
            switch (request.Method)
            {
                case "overlay.ready":
                    result = new JObject
                    {
                        ["apiVersion"] = 1,
                        ["runtime"] = "Secondary AppDomain harness",
                        ["renderer"] = "WebView2 window",
                        ["edition"] = "Enhanced",
                        ["dependencies"] = new JArray(),
                    };
                    break;
                case StartupStatusContract.Method:
                    result = StartupStatusContract.CreateSnapshot(
                        reactorReady: true,
                        nativeBridgeReady: true,
                        providerConnected: true,
                        allIn1Loaded: false);
                    break;
                case "game.getState":
                    result = new JObject
                    {
                        ["gameTime"] = 42420,
                        ["paused"] = false,
                        ["player"] = new JObject
                        {
                            ["health"] = 200,
                            ["maxHealth"] = 200,
                            ["armor"] = 0,
                            ["wantedLevel"] = 0,
                            ["invincible"] = false,
                            ["position"] = new JObject { ["x"] = 0, ["y"] = 0, ["z"] = 0 },
                            ["heading"] = 0,
                        },
                        ["vehicle"] = JValue.CreateNull(),
                        ["world"] = new JObject { ["time"] = "12:00", ["weather"] = "Clear" },
                    };
                    break;
                default:
                    result = new JObject { ["ok"] = true };
                    break;
            }
            return BridgeResponse.Success(request.Id, result);
        }
    }

    internal static class WindowProbe
    {
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint GaRootOwner = 3;
        private const int GwlExStyle = -20;
        private const long WsExTopmost = 0x00000008L;

        public static bool EnsureForeground(IntPtr window, TimeSpan timeout)
        {
            if (window == IntPtr.Zero)
            {
                return false;
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                Application.DoEvents();
                if (IsForegroundOrOwnedBy(window))
                {
                    return true;
                }

                // Attach only for the duration of the activation attempt. This
                // lets a harness launched by a non-foreground terminal make the
                // same visible host window active without changing any
                // production OverlayWindow behavior.
                var foreground = GetForegroundWindow();
                var currentThread = GetCurrentThreadId();
                var foregroundThread = foreground == IntPtr.Zero
                    ? 0u
                    : GetWindowThreadProcessId(foreground, out _);
                var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                    AttachThreadInput(currentThread, foregroundThread, true);
                try
                {
                    ShowWindow(window, 9); // SW_RESTORE
                    BringWindowToTop(window);
                    SetForegroundWindow(window);
                    SetActiveWindow(window);
                    SetFocus(window);
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(currentThread, foregroundThread, false);
                    }
                }

                // Topmost is temporary and used only to make the synthetic
                // host visible during the smoke run. Immediately restore its
                // normal z-order so the harness cannot leave a sticky window.
                SetWindowPos(
                    window,
                    HwndTopmost,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
                SetWindowPos(
                    window,
                    HwndNotTopmost,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
                Thread.Sleep(25);
            }

            return IsForegroundOrOwnedBy(window);
        }

        public static bool IsForegroundOrOwnedBy(IntPtr window)
        {
            var foreground = GetForegroundWindow();
            return foreground == window ||
                (foreground != IntPtr.Zero && GetAncestor(foreground, GaRootOwner) == window);
        }

        public static bool IsVisible(string title)
        {
            var found = false;
            var processId = (uint)Process.GetCurrentProcess().Id;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var ownerProcessId);
                if (ownerProcessId == processId &&
                    string.Equals(GetTitle(window), title, StringComparison.Ordinal) &&
                    IsPresentedToDesktop(window))
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static bool IsVisibleAnyProcess(string title)
        {
            var found = false;
            EnumWindows((window, _) =>
            {
                if (string.Equals(GetTitle(window), title, StringComparison.Ordinal) &&
                    IsPresentedToDesktop(window))
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static int CountVisibleAnyProcess(string title)
        {
            var count = 0;
            EnumWindows((window, _) =>
            {
                if (string.Equals(GetTitle(window), title, StringComparison.Ordinal) &&
                    IsPresentedToDesktop(window))
                {
                    count++;
                }
                return true;
            }, IntPtr.Zero);
            return count;
        }

        private static bool IsPresentedToDesktop(IntPtr window)
        {
            if (!GetWindowRect(window, out var bounds))
            {
                return false;
            }

            var desktop = SystemInformation.VirtualScreen;
            return NativeWindowPresentationPolicy.IsPresentedToDesktop(
                IsWindowVisible(window),
                bounds.Left,
                bounds.Top,
                bounds.Right,
                bounds.Bottom,
                desktop.Left,
                desktop.Top,
                desktop.Right,
                desktop.Bottom);
        }

        public static bool IsTopMostAnyProcess(string title)
        {
            var topMost = false;
            EnumWindows((window, _) =>
            {
                if (string.Equals(GetTitle(window), title, StringComparison.Ordinal))
                {
                    topMost = (GetWindowLongPtr(window, GwlExStyle).ToInt64() &
                        WsExTopmost) != 0;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return topMost;
        }

        public static bool OwnerMatches(string title, IntPtr expectedOwner)
        {
            var matches = false;
            var processId = (uint)Process.GetCurrentProcess().Id;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var ownerProcessId);
                if (ownerProcessId == processId &&
                    string.Equals(GetTitle(window), title, StringComparison.Ordinal))
                {
                    matches = GetWindow(window, 4) == expectedOwner; // GW_OWNER
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return matches;
        }

        private static string GetTitle(IntPtr window)
        {
            var length = GetWindowTextLength(window);
            if (length <= 0)
            {
                return string.Empty;
            }
            var buffer = new System.Text.StringBuilder(length + 1);
            GetWindowText(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maximumCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr window);
    }
}
