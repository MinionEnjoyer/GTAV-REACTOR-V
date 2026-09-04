using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using RageWebUI.Core;
using RageWebUI.DirectX.Native;
using ReactorV.BootstrapHost;

namespace RageWebUI.Harness
{
    /// <summary>
    /// Qualifies the production split-host path. The packaged preloader is
    /// launched externally with this process as its GTA target. This harness
    /// intentionally waits beyond the old cache-warm release window before it
    /// creates the secondary-AppDomain provider, proving that the same browser
    /// remains available for the delayed SHVDN handoff.
    /// </summary>
    internal static class BootstrapHostHarness
    {
        private const string OverlayWindowTitle = "REACTOR V Overlay";
        private const string GbayPresentationId = "bootstrap-gbay-handoff";
        private const string ReadyReopenPresentationId = "bootstrap-gbay-ready-reopen";
        private const string LateOrderingPresentationId = "bootstrap-gbay-late-ordering";
        private const string CancelledPresentationId = "bootstrap-gbay-cancelled";
        private static readonly Color HostColor = Color.FromArgb(72, 54, 88);

        public static int Run(HarnessOptions options)
        {
            var runtimeDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var uiDirectory = options.UiDirectory ?? Path.Combine(runtimeDirectory, "ui");
            if (!File.Exists(Path.Combine(uiDirectory, "index.html")))
            {
                Console.Error.WriteLine($"React UI was not found at '{uiDirectory}'.");
                return 3;
            }

            var localDataDirectory = options.LocalDataDirectory ??
                HarnessRunDirectory.For("BootstrapHost");
            Directory.CreateDirectory(localDataDirectory);
            var runtimeLog = Path.Combine(localDataDirectory, "reactorv-runtime.log");
            var preloaderLogDirectory = Path.Combine(
                Directory.GetParent(localDataDirectory)?.FullName ?? localDataDirectory,
                "Logs");
            var preloaderLog = Path.Combine(
                preloaderLogDirectory,
                "reactorv-preloader.log");
            if (File.Exists(runtimeLog)) File.Delete(runtimeLog);

            var processId = Process.GetCurrentProcess().Id;
            using var visualCapture = HarnessVisualCaptureSession.Enable(
                HostColor,
                localDataDirectory);

            using var host = new Form
            {
                BackColor = HostColor,
                ClientSize = new System.Drawing.Size(options.Width, options.Height),
                StartPosition = FormStartPosition.CenterScreen,
                Text = "Grand Theft Auto V - REACTOR V bootstrap-host harness",
            };
            host.Show();
            Application.DoEvents();
            var hostForeground = WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromSeconds(3));
            if (!hostForeground)
            {
                Console.Error.WriteLine("Could not activate the synthetic GTA host window.");
                return 5;
            }
            visualCapture.QualifyDesktop(host);

            // The packaged Enhanced preloader now refuses to guess a GPU. In
            // production GTA's captured D3D11/D3D12 device publishes the
            // authoritative adapter LUID and consumes the shared CEF frames.
            // This synthetic GTA process must model that same boundary rather
            // than disabling the external-GPU route for the lifecycle gate.
            using var adapterConsumer = AdapterConsumerFixture.Start(
                checked((uint)processId));
            if (!adapterConsumer.Ready)
            {
                Console.Error.WriteLine(
                    "RESULT FAIL: bootstrap host could not publish and bind " +
                    "its authoritative GPU adapter consumer.");
                return 7;
            }
            WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromSeconds(2));

            using var runtimeReady = PreloadHandoff.CreateRuntimeReadyWaitHandle(processId);
            using var f9OwnershipReleased =
                PreloadHandoff.CreateF9OwnershipReleasedWaitHandle(processId);
            runtimeReady.Reset();
            f9OwnershipReleased.Reset();
            var readyTimeout = options.Duration ?? TimeSpan.FromSeconds(15);
            if (!WaitForNamedEvent(BootstrapHostNames.ReadyEvent(processId), readyTimeout))
            {
                Console.Error.WriteLine(
                    $"RESULT FAIL: bootstrap host did not publish readiness for PID {processId}.");
                return 6;
            }

            var packagedRoutePolicy = QualifyPackagedRoutePolicy(runtimeDirectory);

            // Before Story loading, an unresolved boundary must remain on the
            // neutral verification surface. Once the companion publishes a
            // fresh frontend snapshot, native promotes that same surface to
            // Reactor About without hiding or reopening the host window. This
            // synthetic host exercises the exact process-scoped event and
            // manual-reset acknowledgement contract; the packaged route probe
            // independently qualifies the native routing decision.
            // The packaged Preloader correctly suppresses presentation while
            // GTA is not foreground. Build runners can briefly foreground the
            // invoking console while the Ready event is being awaited, so
            // reacquire the synthetic GTA host immediately before starting.
            var aboutHostForeground = WindowProbe.EnsureForeground(
                host.Handle,
                TimeSpan.FromSeconds(2));
            var neutralToggleSignaled = aboutHostForeground &&
                packagedRoutePolicy.Qualified &&
                TrySignalPackagedRoute(processId, packagedRoutePolicy.NeutralRoute);
            var neutralVisible = neutralToggleSignaled &&
                WaitForVisibleWithForeground(host, TimeSpan.FromSeconds(4));
            var verificationActive = neutralVisible && WaitForNamedEvent(
                BootstrapHostNames.VerifyActiveEvent(processId),
                TimeSpan.FromSeconds(2));

            var aboutOpenLatency = Stopwatch.StartNew();
            var aboutToggleSignaled = verificationActive &&
                TrySignalPackagedRoute(processId, packagedRoutePolicy.FrontendRoute);
            var verificationActiveReset = aboutToggleSignaled &&
                WaitForNamedEventReset(
                    BootstrapHostNames.VerifyActiveEvent(processId),
                    TimeSpan.FromSeconds(2));
            var aboutVisible = verificationActiveReset &&
                WaitForVisibleWithForeground(host, TimeSpan.FromSeconds(4));
            var verificationPromotedInPlace = aboutVisible &&
                WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle);
            var aboutOpenMilliseconds = aboutVisible
                ? aboutOpenLatency.Elapsed.TotalMilliseconds
                : -1d;
            var aboutObservation = aboutVisible
                ? WaitForPaintedSurface(
                    host,
                    visualCapture,
                    Path.Combine(localDataDirectory, "main-menu-about.png"),
                    TimeSpan.FromSeconds(2))
                : SurfaceObservation.Failed;
            var aboutStayedHitTestTransparent = aboutObservation.Qualified &&
                WaitForTraceCount(
                    preloaderLog,
                    "stage=webview_bootstrap_pointer_capture enabled=False",
                    1,
                    TimeSpan.FromSeconds(2)) &&
                ReadLog(preloaderLog).IndexOf(
                    "stage=webview_bootstrap_pointer_capture enabled=True",
                    StringComparison.Ordinal) < 0;
            var aboutCreatedNoIntent = aboutObservation.Qualified &&
                !PreloadHandoff.TryConsumeDefaultMenuIntent(processId) &&
                !PreloadHandoff.IsDefaultMenuIntentActive(processId);
            var aboutCloseLatency = Stopwatch.StartNew();
            var aboutCloseSignaled = aboutCreatedNoIntent &&
                TrySignalPackagedRoute(processId, packagedRoutePolicy.FrontendRoute);
            var aboutClosed = aboutCloseSignaled &&
                WaitForVisibility(false, TimeSpan.FromSeconds(2));
            var aboutCloseMilliseconds = aboutClosed
                ? aboutCloseLatency.Elapsed.TotalMilliseconds
                : -1d;
            var aboutRemainedHitTestTransparent = aboutClosed &&
                ReadLog(preloaderLog).IndexOf(
                    "stage=webview_bootstrap_pointer_capture enabled=True",
                    StringComparison.Ordinal) < 0;

            // Once Story loading begins, F9 belongs to the native bootstrap
            // until the managed provider is ready. Qualify that exact
            // pre-provider path: one initializer toggle opens the bounded
            // ALLIN1 transition surface, Escape closes it, and a second early
            // intent remains visible for the later GBAY handoff. No provider
            // or gameplay thread participates here.
            var warmDelay = Stopwatch.StartNew();
            WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromSeconds(1));
            var earlyToggleSignaled = aboutClosed && packagedRoutePolicy.Qualified &&
                TrySignalPackagedRoute(processId, packagedRoutePolicy.LoadingRoute);
            var earlyVisible = earlyToggleSignaled &&
                WaitForVisibleWithForeground(host, TimeSpan.FromSeconds(4));
            var earlyTopMost = earlyVisible && WaitForCondition(
                () => WindowProbe.IsTopMostAnyProcess(OverlayWindowTitle),
                TimeSpan.FromSeconds(1));
            var startupScreenshot = Path.Combine(
                localDataDirectory,
                "startup-initializing.png");
            var startupObservation = earlyVisible
                ? WaitForStartupSurface(
                    host,
                    visualCapture,
                    startupScreenshot,
                    () => TrySignalPackagedRoute(processId, packagedRoutePolicy.LoadingRoute),
                    TimeSpan.FromSeconds(2))
                : SurfaceObservation.Failed;
            var packagedStartupCopyContract =
                HasPackagedStartupCopyContract(uiDirectory);

            // BootstrapMain maps the physical Escape edge to this exact
            // A second F9 must close the same logical surface completely. The
            // previous refresh-on-every-edge behavior produced the live
            // hide/reveal flicker reported from GTA. Reopen once, then prove
            // Escape follows the independent close boundary as well.
            var startupCloseLatency = Stopwatch.StartNew();
            var earlyF9CloseSignaled = startupObservation.Qualified &&
                TrySignalPackagedRoute(processId, packagedRoutePolicy.LoadingRoute);
            var earlyF9Closed = earlyF9CloseSignaled &&
                WaitForVisibility(false, TimeSpan.FromSeconds(2));
            var startupCloseMilliseconds = earlyF9Closed
                ? startupCloseLatency.Elapsed.TotalMilliseconds
                : -1d;
            var startupReopenLatency = Stopwatch.StartNew();
            if (earlyF9Closed)
            {
                // The packaged host correctly rejects presentation while its
                // target is not foreground. Build runners can briefly steal
                // focus between the close and reopen edges, so restore the
                // synthetic GTA ownership before exercising that route.
                WindowProbe.EnsureForeground(
                    host.Handle,
                    TimeSpan.FromMilliseconds(500));
            }
            var earlyF9ReopenSignaled = earlyF9Closed &&
                TrySignalPackagedRoute(processId, packagedRoutePolicy.LoadingRoute);
            var earlyF9Reopened = earlyF9ReopenSignaled &&
                WaitForVisibleWithForeground(host, TimeSpan.FromSeconds(4));
            var startupReopenMilliseconds = earlyF9Reopened
                ? startupReopenLatency.Elapsed.TotalMilliseconds
                : -1d;

            // BootstrapMain maps the physical Escape edge to this exact
            // process-scoped event while it owns pre-provider input. Signaling
            // the boundary directly keeps the packaged test deterministic
            // without synthesizing global keyboard input on the test desktop.
            var escapePosted = earlyF9Reopened && TrySignalNamedEvent(
                BootstrapHostNames.CloseEvent(processId));
            var earlyEscapeClose = escapePosted &&
                WaitForVisibility(false, TimeSpan.FromSeconds(2));
            var earlyDemotedOnClose = earlyEscapeClose && WaitForCondition(
                () => !WindowProbe.IsTopMostAnyProcess(OverlayWindowTitle),
                TimeSpan.FromSeconds(1));
            var closedIntentCleared = earlyEscapeClose &&
                !PreloadHandoff.TryConsumeDefaultMenuIntent(processId) &&
                !PreloadHandoff.IsDefaultMenuIntentActive(processId);
            WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromMilliseconds(500));
            var handoffIntentSignaled = closedIntentCleared && TrySignalNamedEvent(
                BootstrapHostNames.ToggleEvent(processId));
            var handoffIntentVisible = handoffIntentSignaled &&
                WaitForVisibleWithForeground(host, TimeSpan.FromSeconds(4));
            var intentObservation = handoffIntentVisible
                ? WaitForStartupSurface(
                    host,
                    visualCapture,
                    Path.Combine(localDataDirectory, "startup-handoff-intent.png"),
                    () => TrySignalNamedEvent(BootstrapHostNames.ToggleEvent(processId)),
                    TimeSpan.FromSeconds(2))
                : SurfaceObservation.Failed;

            // The former preloader released its controller and browser as soon
            // as the page warmed. Delaying provider attachment beyond that old
            // release window turns the regression into a deterministic failure.
            while (warmDelay.Elapsed < options.BootstrapWarmDelay)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }

            // Browser presentation is deliberately a weaker health axis than
            // the persistent bootstrap transport.  Enhanced can make an
            // external DirectComposition HWND temporarily unverifiable while
            // it enters exclusive/independent flip.  A failed desktop witness
            // must not withdraw the Ready event before the much later SHVDN
            // provider gets a chance to authenticate and take over the exact
            // F9 request.
            var preProviderTransportReady =
                IsNamedEventSet(BootstrapHostNames.ReadyEvent(processId)) &&
                !IsNamedEventSet(BootstrapHostNames.ConnectedEvent(processId));

            var setup = new AppDomainSetup
            {
                ApplicationBase = runtimeDirectory,
                PrivateBinPath = runtimeDirectory,
                ShadowCopyFiles = "false",
            };
            var domain = AppDomain.CreateDomain(
                "ScriptHookVDotNet-BootstrapHost-Harness",
                null,
                setup);
            SecondaryAppDomainHarnessProxy? proxy = null;
            try
            {
                proxy = (SecondaryAppDomainHarnessProxy)domain.CreateInstanceAndUnwrap(
                    Assembly.GetExecutingAssembly().FullName,
                    typeof(SecondaryAppDomainHarnessProxy).FullName);
                var started = proxy.StartForBootstrapGbayHandoff(
                    host.Handle,
                    uiDirectory,
                    runtimeDirectory,
                    localDataDirectory,
                    options.Width,
                    options.Height);

                var connected = preProviderTransportReady && WaitForNamedEvent(
                    BootstrapHostNames.ConnectedEvent(processId),
                    TimeSpan.FromSeconds(3));
                var stopwatch = Stopwatch.StartNew();
                var requests = 0;
                while (host.Visible && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Application.DoEvents();
                    if (!WindowProbe.IsForegroundOrOwnedBy(host.Handle))
                        WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromMilliseconds(250));
                    requests = proxy.Pump();
                    if (requests >= 2 && proxy.GbaySubscriptionCount >= 1 &&
                        proxy.GbayStartupStatusRequestCount >= 1) break;
                    Thread.Sleep(10);
                }

                // Provider connection must preserve the already visible early
                // F9 intent. A real typed GBAY presentation replaces the
                // initializer in the same persistent window. Sample every
                // visible transition and permit only the already-qualified
                // initializer until GBAY replaces it; reject a hide,
                // black/transparent frame, About/setup surface, or second
                // overlay popup.
                var initializerSurfaceOwned = string.Equals(
                    proxy.CurrentHostSurface,
                    HostSurfaceMode.Initializing,
                    StringComparison.Ordinal);
                var runtimeReadyForeground =
                    WindowProbe.IsForegroundOrOwnedBy(host.Handle) ||
                    WindowProbe.EnsureForeground(
                        host.Handle,
                        TimeSpan.FromSeconds(1));

                // RuntimeReady releases native F9 ownership but preserves the
                // requested initializer. Its pixels remain visible until the
                // matching typed GBAY presentation acknowledges a complete
                // React paint in this same persistent browser.
                var contentGeneration = proxy.ReadyContentGeneration();
                var leaseState = RuntimeReadyHandoffState.Unavailable;
                var leaseDeadline = Stopwatch.StartNew();
                while (contentGeneration > 0 &&
                    leaseDeadline.Elapsed < TimeSpan.FromSeconds(2))
                {
                    if (!WindowProbe.IsForegroundOrOwnedBy(host.Handle))
                    {
                        runtimeReadyForeground &= WindowProbe.EnsureForeground(
                            host.Handle,
                            TimeSpan.FromMilliseconds(500));
                    }
                    leaseState = proxy.AdvanceRuntimeReadyHandoff(
                        contentGeneration);
                    Application.DoEvents();
                    proxy.Pump();
                    if (leaseState != RuntimeReadyHandoffState.Pending)
                        break;
                    Thread.Sleep(10);
                }
                var storyReadySignaled =
                    leaseState == RuntimeReadyHandoffState.Signaled &&
                    runtimeReady.WaitOne(TimeSpan.FromSeconds(1));
                runtimeReadyForeground &=
                    WindowProbe.IsForegroundOrOwnedBy(host.Handle) ||
                    WindowProbe.EnsureForeground(
                        host.Handle,
                        TimeSpan.FromSeconds(1));
                var runtimeReadyInitializerPreserved = storyReadySignaled &&
                    runtimeReadyForeground &&
                    WaitForCondition(
                        () =>
                        {
                            if (!WindowProbe.IsForegroundOrOwnedBy(host.Handle))
                            {
                                WindowProbe.EnsureForeground(
                                    host.Handle,
                                    TimeSpan.FromMilliseconds(250));
                            }
                            return WindowProbe.IsForegroundOrOwnedBy(host.Handle) &&
                            !proxy.BootstrapSurfaceRetirementPending &&
                            WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle) &&
                            string.Equals(
                                proxy.CurrentHostSurface,
                                HostSurfaceMode.Initializing,
                                StringComparison.Ordinal);
                        },
                        TimeSpan.FromSeconds(2));
                var managedOwnershipReleased = runtimeReadyInitializerPreserved &&
                    f9OwnershipReleased.Set();
                // Capture the authoritative provider snapshot before the
                // typed menu ends startup-status polling. Later cancellation
                // scenarios intentionally publish neutral status and must not
                // erase proof that this original one-shot request was exposed
                // to the page as requested=true.
                var providerStatusRequestedMenu =
                    proxy.GbayStartupDefaultMenuRequested;
                var providerStartupStatusRequests =
                    proxy.GbayStartupStatusRequestCount;
                var providerMenuReady = managedOwnershipReleased &&
                    proxy.GbaySubscriptionCount >= 1 &&
                    providerStartupStatusRequests >= 1 &&
                    providerStatusRequestedMenu;
                var gbayPosted = providerMenuReady &&
                    proxy.ConsumeDefaultMenuIntentAndPresentGbay(
                        processId,
                        GbayPresentationId);
                var intentConsumedOnce = gbayPosted &&
                    !PreloadHandoff.TryConsumeDefaultMenuIntent(processId);
                // Sample continuously from typed dispatch, across the native
                // logical retirement/accepted-response boundary, until exact
                // GBAY pixels own the window. Waiting for presentationReady
                // before capture concealed the transition most likely to
                // expose a transparent or black compositor frame.
                var handoffObservation = intentConsumedOnce
                    ? WaitForGbayHandoff(
                        host,
                        visualCapture,
                        proxy,
                        GbayPresentationId,
                        Path.Combine(localDataDirectory, "startup-to-gbay.png"),
                        TimeSpan.FromSeconds(3),
                        allowStartupTransition: true,
                        allowHiddenBeforeFirstFrame: true)
                    : HandoffObservation.Failed;
                var initialLogicalRetireSettled = handoffObservation.GbayQualified &&
                    WaitForCondition(
                        () =>
                        {
                            proxy.Pump();
                            return !proxy.BootstrapSurfaceRetirementPending &&
                                string.Equals(
                                    proxy.CurrentHostSurface,
                                    HostSurfaceMode.None,
                                    StringComparison.Ordinal);
                        },
                        TimeSpan.FromSeconds(2));
                var initialPresentationReady = initialLogicalRetireSettled &&
                    WaitForCondition(
                        () =>
                        {
                            proxy.Pump();
                            return proxy.IsGbayPresentationReady(
                                GbayPresentationId);
                        },
                        TimeSpan.FromSeconds(3));
                // Exercise the inverse ordering race as a separate one-shot
                // request: managed ownership is already released, its first
                // check sees no intent, and only then does the external host
                // process the queued native toggle. A bounded 250 ms retry
                // must consume it exactly once and produce one presentation.
                var initialDismissPosted = handoffObservation.GbayQualified &&
                    proxy.DismissPresentedGbay(GbayPresentationId);
                var readyIdleClearedBootstrapSurface = initialDismissPosted &&
                    WaitForCloseWithoutStartupSurface(
                        host,
                        visualCapture,
                        proxy,
                        TimeSpan.FromSeconds(2));

                // Fully managed Story-mode F9 does not pass through either
                // bootstrap surface. Exercise that phase independently of the
                // narrow native/managed handoff race below: the host must stay
                // hidden until a typed GBAY layout is acknowledged, then the
                // very first visible frame must be GBAY (never About or the
                // preloader).
                var readyReopenPosted = readyIdleClearedBootstrapSurface &&
                    proxy.PresentGbay(ReadyReopenPresentationId);
                var readyReopenAcknowledged = readyReopenPosted &&
                    WaitForCondition(
                        () =>
                        {
                            proxy.Pump();
                            return proxy.IsGbayPresentationReady(
                                ReadyReopenPresentationId);
                        },
                        TimeSpan.FromSeconds(3));
                if (readyReopenAcknowledged)
                    proxy.SetVisible(true);
                var readyReopenObservation = readyReopenAcknowledged
                    ? WaitForGbayHandoff(
                        host,
                        visualCapture,
                        proxy,
                        ReadyReopenPresentationId,
                        Path.Combine(localDataDirectory, "ready-idle-to-gbay.png"),
                        TimeSpan.FromSeconds(3),
                        allowStartupTransition: false,
                        allowHiddenBeforeFirstFrame: true)
                    : HandoffObservation.Failed;
                var readyReopenDismissPosted =
                    readyReopenObservation.GbayQualified &&
                    proxy.DismissPresentedGbay(ReadyReopenPresentationId);
                var readyReopenClosedToIdle = readyReopenDismissPosted &&
                    WaitForCloseWithoutStartupSurface(
                        host,
                        visualCapture,
                        proxy,
                        TimeSpan.FromSeconds(2));

                var releaseBeforeIntentAbsent = readyReopenClosedToIdle &&
                    !proxy.ConsumeDefaultMenuIntentAndPresentGbay(
                        processId,
                        "bootstrap-gbay-must-not-present");
                WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromMilliseconds(500));
                var lateIntentSignaled = releaseBeforeIntentAbsent &&
                    TrySignalNamedEvent(BootstrapHostNames.ToggleEvent(processId));
                // RuntimeReady is monotonic: a later native F9 edge arms the
                // managed default-menu intent while the bootstrap host remains
                // hidden. Waiting for initializer visibility here would test
                // the obsolete pre-RuntimeReady route and can also expose stale
                // preloader pixels over live Story gameplay.
                var lateIntentArmed = lateIntentSignaled &&
                    WaitForCondition(
                        () =>
                            PreloadHandoff.IsDefaultMenuIntentActive(processId) &&
                            !WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle) &&
                            string.Equals(
                                proxy.CurrentHostSurface,
                                HostSurfaceMode.None,
                                StringComparison.Ordinal),
                        TimeSpan.FromSeconds(2));
                var lateRetryTimer = Stopwatch.StartNew();
                var lateRetryChecks = 0;
                var lateIntentPresented = false;
                var nextLateRetry = TimeSpan.FromMilliseconds(250);
                while (lateIntentArmed && !lateIntentPresented &&
                    lateRetryTimer.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Application.DoEvents();
                    proxy.Pump();
                    if (lateRetryTimer.Elapsed >= nextLateRetry)
                    {
                        lateRetryChecks++;
                        lateIntentPresented =
                            proxy.ConsumeDefaultMenuIntentAndPresentGbay(
                                processId,
                                LateOrderingPresentationId);
                        nextLateRetry += TimeSpan.FromMilliseconds(250);
                    }
                    Thread.Sleep(10);
                }
                var lateIntentConsumedOnce = lateIntentPresented &&
                    !PreloadHandoff.TryConsumeDefaultMenuIntent(processId);
                var lateLogicalRetireClearedSurface = lateIntentConsumedOnce &&
                    WaitForCondition(
                        () =>
                        {
                            proxy.Pump();
                            return !proxy.BootstrapSurfaceRetirementPending &&
                                string.Equals(
                                    proxy.CurrentHostSurface,
                                    HostSurfaceMode.None,
                                    StringComparison.Ordinal);
                        },
                        TimeSpan.FromSeconds(2));
                var latePresentationReady = lateLogicalRetireClearedSurface &&
                    WaitForCondition(
                        () =>
                        {
                            proxy.Pump();
                            return proxy.IsGbayPresentationReady(
                                LateOrderingPresentationId);
                        },
                        TimeSpan.FromSeconds(3));
                if (latePresentationReady)
                    proxy.SetVisible(true);
                var lateHandoffObservation = latePresentationReady
                    ? WaitForGbayHandoff(
                        host,
                        visualCapture,
                        proxy,
                        LateOrderingPresentationId,
                        Path.Combine(localDataDirectory, "logical-retire-to-gbay.png"),
                        TimeSpan.FromSeconds(3),
                        allowStartupTransition: false,
                        allowHiddenBeforeFirstFrame: true)
                    : HandoffObservation.Failed;
                var intentClaimAcknowledgements = latePresentationReady &&
                    WaitForTraceCount(
                        preloaderLog,
                        "stage=default_menu_intent_claimed",
                        2,
                        TimeSpan.FromSeconds(2));
                var claimedMenuStayedVisible = intentClaimAcknowledgements;
                var claimSettle = Stopwatch.StartNew();
                WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromMilliseconds(500));
                while (claimedMenuStayedVisible &&
                    claimSettle.Elapsed < TimeSpan.FromMilliseconds(750))
                {
                    Application.DoEvents();
                    proxy.Pump();
                    if (!WindowProbe.IsForegroundOrOwnedBy(host.Handle))
                    {
                        WindowProbe.EnsureForeground(
                            host.Handle,
                            TimeSpan.FromMilliseconds(250));
                    }
                    claimedMenuStayedVisible =
                        WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle);
                    Thread.Sleep(10);
                }
                var lateDismissPosted = claimedMenuStayedVisible &&
                    proxy.DismissPresentedGbay(LateOrderingPresentationId);
                var lateCloseNoStalePreloader = lateDismissPosted &&
                    WaitForCloseWithoutStartupSurface(
                        host,
                        visualCapture,
                        proxy,
                        TimeSpan.FromSeconds(2));

                // Exercise the narrow cancellation race explicitly. ALLIN1
                // may reserve the auto-reset request just before native Escape
                // reaches the persistent host. The durable Active/Cancelled
                // state is authoritative at Reactor's dispatch boundary: the
                // reserved request must be rejected without a menu event, a
                // claim acknowledgement, or a later replay.
                proxy.SetVisible(false);
                var cancellationSetupHidden = lateCloseNoStalePreloader &&
                    WaitForVisibility(false, TimeSpan.FromSeconds(2));
                WindowProbe.EnsureForeground(
                    host.Handle,
                    TimeSpan.FromMilliseconds(500));
                var cancellationIntentSignaled = cancellationSetupHidden &&
                    TrySignalNamedEvent(BootstrapHostNames.ToggleEvent(processId));
                var cancellationIntentArmed = cancellationIntentSignaled &&
                    WaitForCondition(
                        () =>
                            PreloadHandoff.IsDefaultMenuIntentActive(processId) &&
                            !WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle) &&
                            string.Equals(
                                proxy.CurrentHostSurface,
                                HostSurfaceMode.None,
                                StringComparison.Ordinal),
                        TimeSpan.FromSeconds(2));
                var cancellationIntentReserved = cancellationIntentArmed &&
                    PreloadHandoff.TryConsumeDefaultMenuIntent(processId) &&
                    PreloadHandoff.CanDispatchDefaultMenuIntent(processId);
                var cancellationReadyBefore = proxy.GbayReadyAcknowledgements;
                var cancellationCloseSignaled = cancellationIntentReserved &&
                    TrySignalNamedEvent(BootstrapHostNames.CloseEvent(processId));
                var cancellationHidden = cancellationCloseSignaled &&
                    WaitForVisibility(false, TimeSpan.FromSeconds(2));
                var cancelledBeforeDispatch = cancellationHidden &&
                    WaitForCondition(
                        () => PreloadHandoff.IsDefaultMenuIntentCancelled(processId) &&
                            !PreloadHandoff.CanDispatchDefaultMenuIntent(processId),
                        TimeSpan.FromSeconds(2));
                var cancelledDispatchRejected = cancelledBeforeDispatch &&
                    !proxy.DispatchReservedDefaultMenuIntent(
                        processId,
                        CancelledPresentationId);
                var cancelledClaimRejected = cancelledDispatchRejected &&
                    !PreloadHandoff.TryCommitDefaultMenuIntentClaim(processId);
                var cancellationSettle = Stopwatch.StartNew();
                while (cancellationSettle.Elapsed < TimeSpan.FromMilliseconds(350))
                {
                    Application.DoEvents();
                    proxy.Pump();
                    Thread.Sleep(10);
                }
                var cancelledNoPresentation = cancelledClaimRejected &&
                    proxy.GbayReadyAcknowledgements == cancellationReadyBefore &&
                    !proxy.IsGbayPresentationReady(CancelledPresentationId) &&
                    !WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle);
                var cancelledStatusNeutral = cancelledNoPresentation &&
                    !StartupStatusContract.CreateSnapshot(
                        reactorReady: true,
                        nativeBridgeReady: true,
                        providerConnected: true,
                        allIn1Loaded: true,
                        defaultMenuRequested:
                            PreloadHandoff.IsDefaultMenuIntentActive(processId))
                        .Value<bool>("defaultMenuRequested");

                // A transient provider loss during a visibly owned Story
                // initializer must not erase the user's request. Disconnect,
                // reconnect the same authenticated provider, and prove its
                // startup snapshot still exposes requested=true. Escape then
                // remains the explicit cancellation boundary.
                var readyAcknowledgementsBeforeReconnect =
                    proxy.GbayReadyAcknowledgements;
                var staleAcknowledgementsBeforeReconnect =
                    proxy.GbayStaleAcknowledgements;
                WindowProbe.EnsureForeground(
                    host.Handle,
                    TimeSpan.FromMilliseconds(500));
                var reconnectIntentSignaled = cancelledStatusNeutral &&
                    TrySignalNamedEvent(BootstrapHostNames.ToggleEvent(processId));
                var reconnectIntentArmed = reconnectIntentSignaled &&
                    WaitForCondition(
                        () =>
                            PreloadHandoff.IsDefaultMenuIntentActive(processId) &&
                            !WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle) &&
                            string.Equals(
                                proxy.CurrentHostSurface,
                                HostSurfaceMode.None,
                                StringComparison.Ordinal),
                        TimeSpan.FromSeconds(2));
                proxy.DisposeRuntime();
                var transientProviderDisconnected = reconnectIntentArmed &&
                    WaitForNamedEventReset(
                        BootstrapHostNames.ConnectedEvent(processId),
                        TimeSpan.FromSeconds(2));
                var transientIntentPreserved = transientProviderDisconnected &&
                    PreloadHandoff.IsDefaultMenuIntentActive(processId) &&
                    !WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle);
                // The host publishes a monotonic provider-session generation.
                // A replacement provider therefore remains observable even if
                // React batches its disconnect/reconnect connectivity booleans
                // into one render turn.
                var transientProviderRestarted = transientIntentPreserved &&
                    proxy.StartForBootstrapGbayHandoff(
                        host.Handle,
                        uiDirectory,
                        runtimeDirectory,
                        localDataDirectory,
                        options.Width,
                        options.Height);
                var transientProviderReconnected = transientProviderRestarted &&
                    WaitForNamedEvent(
                        BootstrapHostNames.ConnectedEvent(processId),
                        TimeSpan.FromSeconds(3));
                var reconnectPump = Stopwatch.StartNew();
                while (transientProviderReconnected &&
                    reconnectPump.Elapsed < TimeSpan.FromSeconds(3))
                {
                    Application.DoEvents();
                    proxy.Pump();
                    if (proxy.GbaySubscriptionCount >= 1 &&
                        proxy.GbayStartupStatusRequestCount >= 1 &&
                        proxy.GbayStartupDefaultMenuRequested)
                        break;
                    Thread.Sleep(10);
                }
                var reconnectObservedPreservedIntent =
                    transientProviderReconnected &&
                    proxy.GbaySubscriptionCount >= 1 &&
                    proxy.GbayStartupStatusRequestCount >= 1 &&
                    proxy.GbayStartupDefaultMenuRequested;
                var reconnectCloseSignaled = reconnectObservedPreservedIntent &&
                    TrySignalNamedEvent(BootstrapHostNames.CloseEvent(processId));
                var reconnectClosed = reconnectCloseSignaled &&
                    WaitForCondition(
                        () =>
                            !WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle) &&
                            !PreloadHandoff.IsDefaultMenuIntentActive(processId),
                        TimeSpan.FromSeconds(2));

                // Once both ordering paths have been qualified, the
                // independently parent-bound Preloader and its authenticated
                // provider connection must still support normal hide/show.
                proxy.SetVisible(false);
                var hideWorked = WaitForVisibility(false, TimeSpan.FromSeconds(2));
                var handoffDelay = Stopwatch.StartNew();
                while (handoffDelay.Elapsed < TimeSpan.FromMilliseconds(500))
                {
                    Application.DoEvents();
                    proxy.Pump();
                    Thread.Sleep(10);
                }
                WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromMilliseconds(500));
                // A surface-less host must remain physically hidden. Exercise
                // ordinary show using a real, paint-qualified About identity
                // instead of treating an invisible `none` surface as success.
                var normalAboutShowSignaled = TrySignalNamedEvent(
                    BootstrapHostNames.AboutToggleEvent(processId));
                var showWorked = normalAboutShowSignaled &&
                    WaitForVisibility(true, TimeSpan.FromSeconds(2));
                var providerStillConnected = IsNamedEventSet(
                    BootstrapHostNames.ConnectedEvent(processId));
                var hostAliveAfterStoryReady = storyReadySignaled &&
                    providerStillConnected && showWorked && proxy.Pump() >= requests;
                var normalAboutCloseSignaled = hostAliveAfterStoryReady &&
                    TrySignalNamedEvent(
                        BootstrapHostNames.AboutToggleEvent(processId));
                var normalAboutClosed = normalAboutCloseSignaled &&
                    WaitForCondition(
                        () =>
                            !WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle) &&
                            string.Equals(
                                proxy.CurrentHostSurface,
                                HostSurfaceMode.None,
                                StringComparison.Ordinal),
                        TimeSpan.FromSeconds(2));

                // A provider disconnect is an authoritative cancellation
                // boundary. Seed a pending process-scoped intent, detach the
                // provider, and prove it cannot replay into a later session.
                var readyAcknowledgements = readyAcknowledgementsBeforeReconnect +
                    proxy.GbayReadyAcknowledgements;
                var staleAcknowledgements = staleAcknowledgementsBeforeReconnect +
                    proxy.GbayStaleAcknowledgements;
                // DisposeRuntime replaces the per-provider router during the
                // reconnect exercise. Preserve the authoritative first-session
                // sample instead of reporting a false zero from the replacement
                // router when a later gate short-circuits.
                var startupStatusRequests = Math.Max(
                    providerStartupStatusRequests,
                    proxy.GbayStartupStatusRequestCount);
                var renderer = proxy.RendererName;
                // The normal hide/show proof above intentionally leaves the
                // host visible with no bootstrap surface selected. Hide that
                // neutral provider-owned state before toggling About so the
                // visibility wait cannot succeed on stale pixels while the
                // named About edge is still queued. This turns the following
                // disconnect assertion into a real surface-preservation test.
                proxy.SetVisible(false);
                var disconnectAboutBaselineHidden = normalAboutClosed &&
                    WaitForVisibility(false, TimeSpan.FromSeconds(2));
                var disconnectAboutSignaled = disconnectAboutBaselineHidden &&
                    TrySignalNamedEvent(
                    BootstrapHostNames.AboutToggleEvent(processId));
                var disconnectAboutVisible = disconnectAboutSignaled &&
                    WaitForVisibility(true, TimeSpan.FromSeconds(2));
                var disconnectIntentArmed = disconnectAboutVisible &&
                    PreloadHandoff.TryArmDefaultMenuIntent(processId);
                proxy.DisposeRuntime();
                var providerDisconnected = WaitForNamedEventReset(
                    BootstrapHostNames.ConnectedEvent(processId),
                    TimeSpan.FromSeconds(2));
                var disconnectIntentCancelled = providerDisconnected &&
                    WaitForTraceMarkers(
                        preloaderLogDirectory,
                        "stage=default_menu_intent_cancelled",
                        "reason=provider-disconnected",
                        TimeSpan.FromSeconds(2));
                var noStaleIntentAfterDisconnect = disconnectIntentArmed &&
                    providerDisconnected &&
                    disconnectIntentCancelled &&
                    !PreloadHandoff.TryConsumeDefaultMenuIntent(processId);
                // ProviderDisconnected must not rewrite a visible frontend
                // About surface to the Story initializer. The same About edge
                // therefore closes it after disconnection; if it had changed
                // mode this edge would leave the overlay visible instead.
                var disconnectAboutCloseSignaled =
                    noStaleIntentAfterDisconnect && TrySignalNamedEvent(
                        BootstrapHostNames.AboutToggleEvent(processId));
                var aboutPreservedAcrossDisconnect =
                    disconnectAboutCloseSignaled &&
                    WaitForVisibility(false, TimeSpan.FromSeconds(2));

                var trace = ReadLog(runtimeLog);
                var attached = trace.Contains("stage=bootstrap_host_attached");
                var fellBack = trace.Contains("stage=bootstrap_host_fallback");
                var startupContract = StartupStatusContract.CreateSnapshot(
                    reactorReady: true,
                    nativeBridgeReady: true,
                    providerConnected: false,
                    allIn1Loaded: false);
                var startupComponentsBounded =
                    (startupContract["components"] as Newtonsoft.Json.Linq.JArray)?.Count == 4 &&
                    startupContract["console"]?.Value<int>("maxEntries") ==
                        StartupTrace.MaximumConsoleEntries &&
                    (startupContract["console"]?["entries"] as Newtonsoft.Json.Linq.JArray)?.Count <=
                        StartupTrace.MaximumConsoleEntries;
                Console.WriteLine(
                    "CONSUMER DIAGNOSTICS: scenario=bootstrap-host " +
                    adapterConsumer.TraceDetail());
                var passed = packagedRoutePolicy.Qualified &&
                    preProviderTransportReady &&
                    started && connected && attached && !fellBack &&
                    string.Equals(renderer, "Bootstrap WebView2", StringComparison.Ordinal) &&
                    neutralToggleSignaled && neutralVisible && verificationActive &&
                    verificationActiveReset && verificationPromotedInPlace &&
                    aboutToggleSignaled && aboutVisible &&
                    aboutObservation.Qualified && aboutObservation.SinglePopup &&
                    aboutStayedHitTestTransparent &&
                    aboutRemainedHitTestTransparent &&
                    aboutCreatedNoIntent && aboutClosed &&
                    startupObservation.Qualified && earlyTopMost &&
                    earlyF9Closed && earlyF9Reopened &&
                    earlyDemotedOnClose && intentObservation.Qualified &&
                    startupComponentsBounded && packagedStartupCopyContract &&
                    earlyEscapeClose &&
                    closedIntentCleared && initializerSurfaceOwned &&
                    runtimeReadyInitializerPreserved &&
                    providerMenuReady && intentConsumedOnce &&
                    initialPresentationReady &&
                    handoffObservation.GbayQualified &&
                    handoffObservation.NoBlack && handoffObservation.NoTransparent &&
                    handoffObservation.NoInterstitial && handoffObservation.SinglePopup &&
                    readyIdleClearedBootstrapSurface && readyReopenPosted &&
                    readyReopenAcknowledged && readyReopenObservation.GbayQualified &&
                    readyReopenObservation.NoBlack &&
                    readyReopenObservation.NoTransparent &&
                    readyReopenObservation.NoInterstitial &&
                    readyReopenObservation.SinglePopup && readyReopenClosedToIdle &&
                    releaseBeforeIntentAbsent && lateIntentArmed &&
                    lateIntentConsumedOnce && lateLogicalRetireClearedSurface &&
                    latePresentationReady && lateHandoffObservation.GbayQualified &&
                    lateHandoffObservation.NoBlack &&
                    lateHandoffObservation.NoTransparent &&
                    lateHandoffObservation.NoInterstitial &&
                    lateHandoffObservation.SinglePopup &&
                    intentClaimAcknowledgements && claimedMenuStayedVisible &&
                    lateDismissPosted && lateCloseNoStalePreloader &&
                    cancellationIntentReserved && cancelledBeforeDispatch &&
                    cancelledDispatchRejected && cancelledClaimRejected &&
                    cancelledNoPresentation && cancelledStatusNeutral &&
                    transientIntentPreserved && transientProviderReconnected &&
                    reconnectObservedPreservedIntent && reconnectClosed &&
                    lateRetryChecks >= 1 && readyAcknowledgements == 3 &&
                    staleAcknowledgements == 0 &&
                    hideWorked && storyReadySignaled && hostAliveAfterStoryReady &&
                    disconnectAboutBaselineHidden &&
                    providerDisconnected && disconnectIntentCancelled &&
                    noStaleIntentAfterDisconnect &&
                    disconnectAboutVisible && aboutPreservedAcrossDisconnect &&
                    requests >= 2;
                Console.WriteLine(
                    $"RESULT {(passed ? "PASS" : "FAIL")}: scenario=bootstrap-host " +
                    $"survivedWarmWindow=True warmDelayMs={warmDelay.Elapsed.TotalMilliseconds:F0} " +
                    $"preProviderTransportReady={preProviderTransportReady} " +
                    $"connected={connected} renderer={renderer} attached={attached} fallback={fellBack} " +
                    $"packagedRoutePolicy={packagedRoutePolicy.Qualified} " +
                    $"neutralVerification={neutralVisible} " +
                    $"verificationActive={verificationActive} " +
                    $"verificationActiveReset={verificationActiveReset} " +
                    $"verificationPromotedInPlace={verificationPromotedInPlace} " +
                    $"mainMenuAbout={aboutObservation.Qualified} " +
                    $"aboutSinglePopup={aboutObservation.SinglePopup} " +
                    $"aboutHitTestTransparent={aboutStayedHitTestTransparent} " +
                    $"aboutHitTestStayedTransparent={aboutRemainedHitTestTransparent} " +
                     $"aboutNoIntent={aboutCreatedNoIntent} aboutClosed={aboutClosed} " +
                     $"aboutOpenMs={aboutOpenMilliseconds:F1} " +
                     $"aboutCloseMs={aboutCloseMilliseconds:F1} " +
                     $"hide={hideWorked} preStoryInitializerSignal={earlyToggleSignaled} " +
                    $"startupSurface={startupObservation.Qualified} startupChecks=3 " +
                     $"startupF9Close={earlyF9Closed} startupF9Reopen={earlyF9Reopened} " +
                     $"startupF9CloseMs={startupCloseMilliseconds:F1} " +
                     $"startupF9ReopenMs={startupReopenMilliseconds:F1} " +
                    $"startupTopMost={earlyTopMost} startupDemotedOnClose={earlyDemotedOnClose} " +
                    $"startupConsoleBounded={startupComponentsBounded} " +
                    $"startupCopyContract={packagedStartupCopyContract} " +
                    $"earlyEscapeClose={earlyEscapeClose} closedIntentCleared={closedIntentCleared} " +
                    $"earlyIntentPreserved={handoffIntentVisible} " +
                    $"earlyIntentPainted={intentObservation.Qualified} " +
                    $"providerMenuReady={providerMenuReady} " +
                    $"providerStartupStatus={startupStatusRequests >= 1} " +
                    $"providerStatusRequestedMenu={providerStatusRequestedMenu} " +
                    $"runtimeReadyInitializerPreserved={runtimeReadyInitializerPreserved} " +
                    $"intentConsumedOnce={intentConsumedOnce} " +
                    $"initialPresentationReady={initialPresentationReady} " +
                    $"readyIdle={readyIdleClearedBootstrapSurface} " +
                    $"readyReopenPosted={readyReopenPosted} " +
                    $"readyReopenAcknowledged={readyReopenAcknowledged} " +
                    $"readyReopenGbay={readyReopenObservation.GbayQualified} " +
                    $"readyReopenNoBlack={readyReopenObservation.NoBlack} " +
                    $"readyReopenNoTransparent={readyReopenObservation.NoTransparent} " +
                    $"readyReopenNoPreloader={readyReopenObservation.NoInterstitial} " +
                    $"readyReopenSinglePopup={readyReopenObservation.SinglePopup} " +
                    $"readyReopenClosedToIdle={readyReopenClosedToIdle} " +
                    $"releaseBeforeIntentAbsent={releaseBeforeIntentAbsent} " +
                    $"lateIntentArmed={lateIntentArmed} " +
                    $"lateIntentConsumedOnce={lateIntentConsumedOnce} " +
                    $"lateLogicalRetire={lateLogicalRetireClearedSurface} " +
                    $"latePresentationReady={latePresentationReady} " +
                    $"lateGbayPainted={lateHandoffObservation.GbayQualified} " +
                    $"lateNoBlack={lateHandoffObservation.NoBlack} " +
                    $"lateNoTransparent={lateHandoffObservation.NoTransparent} " +
                    $"lateNoPreloader={lateHandoffObservation.NoInterstitial} " +
                    $"lateSinglePopup={lateHandoffObservation.SinglePopup} " +
                    $"lateDismissPosted={lateDismissPosted} " +
                    $"lateCloseNoPreloader={lateCloseNoStalePreloader} " +
                    $"lateRetryChecks={lateRetryChecks} " +
                    $"intentClaimAcks={(intentClaimAcknowledgements ? 2 : 0)} " +
                    $"claimedMenuStayedVisible={claimedMenuStayedVisible} " +
                    $"cancelAfterReserve={cancellationIntentReserved} " +
                    $"cancelledBeforeDispatch={cancelledBeforeDispatch} " +
                    $"cancelledDispatchRejected={cancelledDispatchRejected} " +
                    $"cancelledClaimRejected={cancelledClaimRejected} " +
                    $"cancelledNoPresentation={cancelledNoPresentation} " +
                    $"cancelledStatusNeutral={cancelledStatusNeutral} " +
                    $"transientIntentPreserved={transientIntentPreserved} " +
                    $"providerReconnected={transientProviderReconnected} " +
                    $"reconnectObservedIntent={reconnectObservedPreservedIntent} " +
                    $"reconnectClosed={reconnectClosed} " +
                    $"initializerSurfaceOwned={initializerSurfaceOwned} " +
                    $"startupToGbay={handoffObservation.GbayQualified} " +
                    $"transitionNoBlack={handoffObservation.NoBlack} " +
                    $"transitionNoTransparent={handoffObservation.NoTransparent} " +
                    $"transitionNoInterstitial={handoffObservation.NoInterstitial} " +
                    $"singlePopup={handoffObservation.SinglePopup} " +
                    $"transitionFrames={handoffObservation.FrameCount} " +
                    $"readyAcks={readyAcknowledgements} " +
                    $"staleAcks={staleAcknowledgements} " +
                    $"providerDisconnected={providerDisconnected} " +
                    $"disconnectAboutBaselineHidden={disconnectAboutBaselineHidden} " +
                    $"disconnectIntentCancelled={disconnectIntentCancelled} " +
                    $"aboutPreservedOnDisconnect={aboutPreservedAcrossDisconnect} " +
                    $"noStaleIntent={noStaleIntentAfterDisconnect} " +
                    $"storyReadySignaled={storyReadySignaled} " +
                    $"contentGeneration={contentGeneration} leaseState={leaseState} " +
                    $"hostAliveAfterStoryReady={hostAliveAfterStoryReady} show={showWorked} requests={requests}");
                if (!passed)
                    Console.Error.WriteLine($"Runtime trace: {runtimeLog}");
                return passed ? 0 : 4;
            }
            finally
            {
                try { proxy?.DisposeRuntime(); }
                finally { AppDomain.Unload(domain); }
            }
        }

        private static bool WaitForNamedEvent(string name, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                Application.DoEvents();
                try
                {
                    using var handle = EventWaitHandle.OpenExisting(name);
                    if (handle.WaitOne(0)) return true;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                }
                Thread.Sleep(25);
            }
            return false;
        }

        private static bool WaitForNamedEventReset(string name, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                Application.DoEvents();
                try
                {
                    using var handle = EventWaitHandle.OpenExisting(name);
                    if (!handle.WaitOne(0)) return true;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // The provider-connected event is owned by the persistent
                    // Preloader. A missing event here means the host died, not
                    // a clean provider disconnect, and therefore fails closed.
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
                Thread.Sleep(10);
            }
            return false;
        }

        private static bool WaitForVisibility(bool expected, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                Application.DoEvents();
                // The persistent OverlayWindow belongs to the external
                // Preloader process, not this synthetic GTA process.
                if (WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle) == expected) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        private static PackagedRoutePolicyObservation QualifyPackagedRoutePolicy(
            string runtimeDirectory)
        {
            var probe = Path.Combine(
                runtimeDirectory,
                "ReactorV.Bootstrap.RouteProbe.exe");
            if (!File.Exists(probe))
                return PackagedRoutePolicyObservation.Failed;

            var frontend = RunPackagedRouteProbe(
                probe,
                "core-data true false true false true true");
            var enhancedLandingMenu = RunPackagedRouteProbe(
                probe,
                "core-data true true true false true false true true");
            var ambiguousLoading = RunPackagedRouteProbe(
                probe,
                "core-data true true false false false false");
            var storyLoading = RunPackagedRouteProbe(
                probe,
                "script-threads true true false false false false");
            var authoritativeStoryLoading = RunPackagedRouteProbe(
                probe,
                "script-threads true true true false true false true false");
            var player = RunPackagedRouteProbe(
                probe,
                "core-data false false true true false false");
            var incompleteFrontend = RunPackagedRouteProbe(
                probe,
                "core-data false false false false true true");
            var unavailableFrontend = RunPackagedRouteProbe(
                probe,
                "core-data false false false false false false");
            var unavailableStory = RunPackagedRouteProbe(
                probe,
                "script-threads false false false false false false");
            var observedStoryWithFrontend = RunPackagedRouteProbe(
                probe,
                "script-threads true false true false true true");
            return new PackagedRoutePolicyObservation(
                string.Equals(frontend, "about", StringComparison.Ordinal) &&
                string.Equals(enhancedLandingMenu, "about", StringComparison.Ordinal),
                string.Equals(ambiguousLoading, "about", StringComparison.Ordinal),
                string.Equals(storyLoading, "about", StringComparison.Ordinal),
                string.Equals(authoritativeStoryLoading, "initializing", StringComparison.Ordinal),
                string.Equals(player, "initializing", StringComparison.Ordinal),
                string.Equals(incompleteFrontend, "about", StringComparison.Ordinal),
                string.Equals(unavailableFrontend, "about", StringComparison.Ordinal),
                string.Equals(unavailableStory, "about", StringComparison.Ordinal),
                // ScriptHook can create its threads while GTA is still on the
                // frontend. A complete live frontend snapshot remains
                // authoritative, otherwise F9 incorrectly opens the ALLIN1
                // Story preloader over the main menu.
                string.Equals(observedStoryWithFrontend, "about", StringComparison.Ordinal),
                frontend,
                authoritativeStoryLoading,
                // The neutral verification surface is a transient host state,
                // not a fallback routing result. Unpublished frontend input
                // now correctly resolves straight to About, so deriving this
                // test edge from ambiguousLoading would signal About twice and
                // prevent the verification-promotion contract from running.
                "verifying");
        }

        private static string RunPackagedRouteProbe(
            string executable,
            string arguments)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process == null) return string.Empty;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                process.WaitForExit(1000);
                return string.Empty;
            }
            process.WaitForExit();
            if (!System.Threading.Tasks.Task.WaitAll(
                    new System.Threading.Tasks.Task[] { output, error },
                    1000) || process.ExitCode != 0)
                return string.Empty;
            return output.Result.Trim();
        }

        private static bool TrySignalPackagedRoute(int processId, string route) =>
            string.Equals(route, "about", StringComparison.Ordinal)
                ? TrySignalNamedEvent(BootstrapHostNames.AboutToggleEvent(processId))
                : string.Equals(route, "initializing", StringComparison.Ordinal)
                    ? TrySignalNamedEvent(BootstrapHostNames.ToggleEvent(processId))
                    : string.Equals(route, "verifying", StringComparison.Ordinal) &&
                        TrySignalNamedEvent(BootstrapHostNames.VerifyToggleEvent(processId));

        private static bool WaitForVisibleWithForeground(Form host, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                Application.DoEvents();
                if (!WindowProbe.IsForegroundOrOwnedBy(host.Handle))
                    WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromMilliseconds(500));
                if (WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle)) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        private static bool WaitForCondition(Func<bool> condition, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                Application.DoEvents();
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        private static SurfaceObservation WaitForStartupSurface(
            Form host,
            HarnessVisualCaptureSession visualCapture,
            string screenshotPath,
            Action recoverVisibility,
            TimeSpan timeout)
        {
            var timer = Stopwatch.StartNew();
            var singlePopup = true;
            var strongestCandidateChangedFraction = 0d;
            var strongestCandidate = default(GbayLifecycleHarness.VisualFrame);
            var candidatePath = Path.Combine(
                Path.GetDirectoryName(screenshotPath)!,
                $"{Path.GetFileNameWithoutExtension(screenshotPath)}-candidate.png");
            while (timer.Elapsed < timeout)
            {
                if (!WindowProbe.IsForegroundOrOwnedBy(host.Handle))
                {
                    if (!WindowProbe.EnsureForeground(
                            host.Handle,
                            TimeSpan.FromMilliseconds(750)))
                        return SurfaceObservation.Failed;
                    if (!WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle))
                        recoverVisibility();
                }
                Application.DoEvents();
                var count = WindowProbe.CountVisibleAnyProcess(OverlayWindowTitle);
                singlePopup &= count <= 1;
                if (count == 1)
                {
                    using var image = visualCapture.Capture(host);
                    var frame = GbayLifecycleHarness.VisualFrame.Measure(image);
                    if (frame.ChangedFraction > strongestCandidateChangedFraction)
                    {
                        strongestCandidateChangedFraction = frame.ChangedFraction;
                        strongestCandidate = frame;
                        image.Save(candidatePath);
                    }
                    if (frame.IsStartupTransition)
                    {
                        image.Save(screenshotPath);
                        if (File.Exists(candidatePath)) File.Delete(candidatePath);
                        return new SurfaceObservation(true, singlePopup, frame);
                    }
                }
                Thread.Sleep(8);
            }
            Console.WriteLine(
                $"Startup visual candidate failed qualification: changed={strongestCandidate.ChangedFraction:F4} " +
                $"black={strongestCandidate.BlackFraction:F4} green={strongestCandidate.GreenFraction:F4} " +
                $"blue={strongestCandidate.BlueFraction:F4} white={strongestCandidate.WhiteFraction:F4} " +
                $"darkGreen={strongestCandidate.DarkGreenFraction:F4} path={candidatePath}");
            return SurfaceObservation.Failed;
        }

        private static SurfaceObservation WaitForPaintedSurface(
            Form host,
            HarnessVisualCaptureSession visualCapture,
            string screenshotPath,
            TimeSpan timeout)
        {
            var timer = Stopwatch.StartNew();
            var singlePopup = true;
            while (timer.Elapsed < timeout)
            {
                if (!WindowProbe.IsForegroundOrOwnedBy(host.Handle) &&
                    !WindowProbe.EnsureForeground(
                        host.Handle,
                        TimeSpan.FromMilliseconds(750)))
                    return SurfaceObservation.Failed;
                Application.DoEvents();
                var count = WindowProbe.CountVisibleAnyProcess(OverlayWindowTitle);
                singlePopup &= count <= 1;
                if (count == 1)
                {
                    using var image = visualCapture.Capture(host);
                    var frame = GbayLifecycleHarness.VisualFrame.Measure(image);
                    if (frame.ChangedFraction > 0.10d &&
                        frame.BlackFraction < 0.80d)
                    {
                        image.Save(screenshotPath);
                        return new SurfaceObservation(true, singlePopup, frame);
                    }
                }
                Thread.Sleep(8);
            }
            return SurfaceObservation.Failed;
        }

        private static HandoffObservation WaitForGbayHandoff(
            Form host,
            HarnessVisualCaptureSession visualCapture,
            SecondaryAppDomainHarnessProxy proxy,
            string presentationId,
            string screenshotPath,
            TimeSpan timeout,
            bool allowStartupTransition = true,
            bool allowHiddenBeforeFirstFrame = false)
        {
            var timer = Stopwatch.StartNew();
            var frames = new List<GbayLifecycleHarness.VisualFrame>();
            var noBlack = true;
            var noTransparent = true;
            var noInterstitial = true;
            var singlePopup = true;
            var sawVisibleFrame = false;
            var gbayPhaseEntered = false;
            double? stableGbaySinceMilliseconds = null;
            while (timer.Elapsed < timeout)
            {
                if (!WindowProbe.IsForegroundOrOwnedBy(host.Handle))
                {
                    if (!WindowProbe.EnsureForeground(
                            host.Handle,
                            TimeSpan.FromMilliseconds(750)))
                        return HandoffObservation.Failed;
                    proxy.SetVisible(true);
                    Application.DoEvents();
                    proxy.Pump();
                    continue;
                }

                Application.DoEvents();
                proxy.Pump();
                var windowCount = WindowProbe.CountVisibleAnyProcess(OverlayWindowTitle);
                singlePopup &= windowCount <= 1;
                if (windowCount != 1)
                {
                    if (windowCount > 1 || sawVisibleFrame ||
                        !allowHiddenBeforeFirstFrame)
                    {
                        noTransparent = false;
                    }
                    stableGbaySinceMilliseconds = null;
                    Thread.Sleep(5);
                    continue;
                }

                sawVisibleFrame = true;
                using var image = visualCapture.Capture(host);
                var frame = GbayLifecycleHarness.VisualFrame.Measure(image);
                frames.Add(frame);
                noBlack &= frame.BlackFraction < 0.80d;
                noTransparent &= frame.ChangedFraction > 0.10d;
                var permittedFrame = frame.IsGbay ||
                    GbayPresentationTimingPolicy.IsInitializerFramePermitted(
                        allowStartupTransition,
                        gbayPhaseEntered,
                        frame.IsStartupTransition);
                noInterstitial &= permittedFrame;
                gbayPhaseEntered |= frame.IsGbay;
                if (!permittedFrame)
                {
                    var diagnosticPath = Path.Combine(
                        Path.GetDirectoryName(screenshotPath)!,
                        $"{Path.GetFileNameWithoutExtension(screenshotPath)}-interstitial-{frames.Count:D2}.png");
                    image.Save(diagnosticPath);
                    Console.WriteLine(
                        $"HARNESS INFO: unexpected handoff frame path={diagnosticPath} " +
                        $"changed={frame.ChangedFraction:F4} black={frame.BlackFraction:F4} " +
                        $"green={frame.GreenFraction:F4} blue={frame.BlueFraction:F4} " +
                        $"startup={frame.IsStartupTransition} gbay={frame.IsGbay}.");
                }

                var exactPresentationReady =
                    proxy.IsGbayPresentationReady(presentationId);
                if (!exactPresentationReady || !frame.IsGbay)
                {
                    stableGbaySinceMilliseconds = null;
                }
                else
                {
                    stableGbaySinceMilliseconds ??=
                        timer.Elapsed.TotalMilliseconds;
                }

                if (stableGbaySinceMilliseconds.HasValue &&
                    GbayPresentationTimingPolicy.HasStableHandoffSettled(
                        stableGbaySinceMilliseconds.Value,
                        timer.Elapsed.TotalMilliseconds))
                {
                    image.Save(screenshotPath);
                    return new HandoffObservation(
                        true,
                        noBlack,
                        noTransparent,
                        noInterstitial,
                        singlePopup,
                        frames.Count);
                }
                Thread.Sleep(8);
            }
            return new HandoffObservation(
                false,
                noBlack,
                noTransparent,
                noInterstitial,
                singlePopup,
                frames.Count);
        }

        private static bool WaitForCloseWithoutStartupSurface(
            Form host,
            HarnessVisualCaptureSession visualCapture,
            SecondaryAppDomainHarnessProxy proxy,
            TimeSpan timeout)
        {
            var timer = Stopwatch.StartNew();
            var noStartupSurface = true;
            while (timer.Elapsed < timeout)
            {
                Application.DoEvents();
                proxy.Pump();
                if (!WindowProbe.IsVisibleAnyProcess(OverlayWindowTitle))
                {
                    return noStartupSurface && string.Equals(
                        proxy.CurrentHostSurface,
                        HostSurfaceMode.None,
                        StringComparison.Ordinal);
                }

                if (WindowProbe.CountVisibleAnyProcess(OverlayWindowTitle) == 1 &&
                    visualCapture.CanCapture(host))
                {
                    using var image = visualCapture.Capture(host);
                    noStartupSurface &=
                        !GbayLifecycleHarness.VisualFrame.Measure(image)
                            .IsStartupTransition;
                }
                Thread.Sleep(5);
            }
            return false;
        }

        private static bool TrySignalNamedEvent(string name)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(name);
                return handle.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsNamedEventSet(string name)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(name);
                return handle.WaitOne(0);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string ReadLog(string path)
        {
            try
            {
                if (!File.Exists(path)) return string.Empty;
                // StartupTrace deliberately opens aggregate logs with shared
                // read/write access so diagnostics cannot make a launch event
                // disappear. File.ReadAllText uses FileShare.Read and the
                // bootstrap-host gate polls this file while the preloader is
                // appending to it; that can exhaust the writer's bounded retry
                // window and leave the authoritative claim only in the session
                // trace. Mirror the production diagnostic-reader contract here.
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (IOException) { return string.Empty; }
            catch (UnauthorizedAccessException) { return string.Empty; }
        }

        private static bool HasPackagedStartupCopyContract(string uiDirectory)
        {
            var requiredCopy = new[]
            {
                "Reactor V",
                "Services",
                "Powered by Reactor V",
                "Startup log",
            };
            try
            {
                var scripts = Directory.EnumerateFiles(
                    uiDirectory,
                    "*.js",
                    SearchOption.AllDirectories);
                var found = new bool[requiredCopy.Length];
                foreach (var path in scripts)
                {
                    var text = File.ReadAllText(path);
                    for (var index = 0; index < requiredCopy.Length; index++)
                        found[index] |= text.Contains(requiredCopy[index]);
                }
                return Array.TrueForAll(found, value => value);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool WaitForTraceCount(
            string path,
            string marker,
            int expectedCount,
            TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                var text = ReadLog(path);
                var count = 0;
                var cursor = 0;
                while ((cursor = text.IndexOf(
                    marker,
                    cursor,
                    StringComparison.Ordinal)) >= 0)
                {
                    count++;
                    cursor += marker.Length;
                }
                if (count >= expectedCount) return true;
                Application.DoEvents();
                Thread.Sleep(10);
            }
            return false;
        }

        private static bool WaitForTraceMarkers(
            string directory,
            string firstMarker,
            string secondMarker,
            TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        foreach (var path in Directory.EnumerateFiles(directory, "*.log"))
                        {
                            var text = File.ReadAllText(path);
                            var first = text.IndexOf(firstMarker, StringComparison.Ordinal);
                            while (first >= 0)
                            {
                                var lineEnd = text.IndexOfAny(
                                    new[] { '\r', '\n' },
                                    first);
                                if (lineEnd < 0) lineEnd = text.Length;
                                if (text.IndexOf(
                                        secondMarker,
                                        first,
                                        lineEnd - first,
                                        StringComparison.Ordinal) >= 0)
                                    return true;
                                first = text.IndexOf(
                                    firstMarker,
                                    lineEnd,
                                    StringComparison.Ordinal);
                            }
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
                Application.DoEvents();
                Thread.Sleep(10);
            }
            return false;
        }

        private sealed class SurfaceObservation
        {
            public static readonly SurfaceObservation Failed =
                new SurfaceObservation(false, false, default);

            public SurfaceObservation(
                bool qualified,
                bool singlePopup,
                GbayLifecycleHarness.VisualFrame frame)
            {
                Qualified = qualified;
                SinglePopup = singlePopup;
                Frame = frame;
            }

            public bool Qualified { get; }
            public bool SinglePopup { get; }
            public GbayLifecycleHarness.VisualFrame Frame { get; }
        }

        private sealed class PackagedRoutePolicyObservation
        {
            public static readonly PackagedRoutePolicyObservation Failed =
                new PackagedRoutePolicyObservation(
                    false, false, false, false, false, false, false, false, false,
                    string.Empty, string.Empty, string.Empty);

            public PackagedRoutePolicyObservation(
                bool frontendAbout,
                bool ambiguousLoadingUsesFallback,
                bool loadingMarkerUsesFallback,
                bool loadingInitializer,
                bool playerInitializer,
                bool incompleteFrontendFallback,
                bool unavailableFrontendFallback,
                bool unavailableStoryFallback,
                bool stableFrontendAfterThreads,
                string frontendRoute,
                string loadingRoute,
                string neutralRoute)
            {
                FrontendRoute = frontendRoute;
                LoadingRoute = loadingRoute;
                NeutralRoute = neutralRoute;
                Qualified = frontendAbout && ambiguousLoadingUsesFallback &&
                    loadingMarkerUsesFallback && loadingInitializer && playerInitializer &&
                    incompleteFrontendFallback && unavailableFrontendFallback &&
                    unavailableStoryFallback && stableFrontendAfterThreads;
            }

            public bool Qualified { get; }
            public string FrontendRoute { get; }
            public string LoadingRoute { get; }
            public string NeutralRoute { get; }
        }

        private sealed class HandoffObservation
        {
            public static readonly HandoffObservation Failed =
                new HandoffObservation(false, false, false, false, false, 0);

            public HandoffObservation(
                bool gbayQualified,
                bool noBlack,
                bool noTransparent,
                bool noInterstitial,
                bool singlePopup,
                int frameCount)
            {
                GbayQualified = gbayQualified;
                NoBlack = noBlack;
                NoTransparent = noTransparent;
                NoInterstitial = noInterstitial;
                SinglePopup = singlePopup;
                FrameCount = frameCount;
            }

            public bool GbayQualified { get; }
            public bool NoBlack { get; }
            public bool NoTransparent { get; }
            public bool NoInterstitial { get; }
            public bool SinglePopup { get; }
            public int FrameCount { get; }
        }

        private sealed class AdapterConsumerFixture : IDisposable
        {
            private const string WindowTitle =
                "REACTOR V Bootstrap Adapter Consumer";
            private readonly bool _started;
            private readonly bool _armed;

            private AdapterConsumerFixture(bool started, bool armed, bool ready)
            {
                _started = started;
                _armed = armed;
                Ready = ready;
            }

            public bool Ready { get; }

            public string TraceDetail()
            {
                return NativeCompositor.TryGetSharedTextureConsumerDiagnostics(
                    out var diagnostics)
                    ? diagnostics.ToTraceDetail()
                    : "unavailable";
            }

            public static AdapterConsumerFixture Start(uint processId)
            {
                // Install the production hook/consumer before the synthetic
                // GTA surface creates its swap chain. This makes adapter
                // capture and shared-frame acknowledgement deterministic.
                var armed = NativeCompositor.ArmEnhancedHook();
                if (!armed)
                    return new AdapterConsumerFixture(false, false, false);

                var started = NativeCompositor.StartTest(
                    RenderApi.Direct3D11,
                    320,
                    240,
                    WindowTitle);
                if (!started)
                {
                    NativeCompositor.Shutdown();
                    return new AdapterConsumerFixture(false, false, false);
                }

                var surfaceTimer = Stopwatch.StartNew();
                RenderStats initializedStats = default;
                while (surfaceTimer.Elapsed < TimeSpan.FromSeconds(3) &&
                    NativeCompositor.IsTestRunning)
                {
                    if (NativeCompositor.TryGetStats(out initializedStats) &&
                        initializedStats.Api == RenderApi.Direct3D11)
                    {
                        break;
                    }
                    Thread.Sleep(10);
                }
                if (!NativeCompositor.IsTestRunning ||
                    initializedStats.Api != RenderApi.Direct3D11)
                {
                    NativeCompositor.StopTest();
                    NativeCompositor.Shutdown();
                    return new AdapterConsumerFixture(false, false, false);
                }

                var timer = Stopwatch.StartNew();
                var ready = false;
                while (timer.Elapsed < TimeSpan.FromSeconds(3) &&
                    NativeCompositor.IsTestRunning)
                {
                    if (NativeAdapterLuidDiscovery.TryQuery(processId, out _))
                    {
                        ready = true;
                        break;
                    }
                    Thread.Sleep(10);
                }

                // The consumer needs to keep presenting and acknowledging
                // shared frames, but its test swap-chain window must never
                // compete with the synthetic GTA window for pixels or focus.
                timer.Restart();
                while (timer.Elapsed < TimeSpan.FromSeconds(2))
                {
                    var window = FindWindowW(null, WindowTitle);
                    if (window != IntPtr.Zero && IsWindowVisible(window))
                    {
                        ShowWindow(window, 0); // SW_HIDE
                        break;
                    }
                    Thread.Sleep(10);
                }
                return new AdapterConsumerFixture(true, true, ready);
            }

            public void Dispose()
            {
                Console.WriteLine(
                    "CONSUMER FINAL: scenario=bootstrap-host " +
                    TraceDetail());
                if (_started) NativeCompositor.StopTest();
                if (_armed) NativeCompositor.Shutdown();
            }

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr FindWindowW(
                string? className,
                string? windowName);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool IsWindowVisible(IntPtr window);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool ShowWindow(IntPtr window, int command);
        }
    }
}
