using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using RageWebUI.Runtime;

namespace RageWebUI.Harness
{
    /// <summary>
    /// Exercises the production React page through the production windowed
    /// WebView2 host. The fixture starts with a fully prepared hidden browser,
    /// presents a typed ALLIN1 GBAY descriptor, drives every top-level route
    /// and one typed action per route, then qualifies route restoration,
    /// removed-route fallback, close, warm reopen, and rapid toggle behavior.
    /// Screen samples are taken only while the host reports the overlay visible,
    /// which turns a transparent/black/About/setup intermediate frame into a
    /// deterministic release failure.
    /// </summary>
    internal static class GbayLifecycleHarness
    {
        private const string OverlayWindowTitle = "REACTOR V Overlay";
        private static readonly Color HostColor = Color.FromArgb(72, 54, 88);

        public static int Run(HarnessOptions options)
        {
            var runtimeDirectory = Path.GetDirectoryName(
                typeof(GbayLifecycleHarness).Assembly.Location)!;
            var uiDirectory = options.UiDirectory ?? Path.Combine(runtimeDirectory, "ui");
            if (!File.Exists(Path.Combine(uiDirectory, "index.html")))
            {
                Console.Error.WriteLine($"React UI was not found at '{uiDirectory}'.");
                return 3;
            }

            var localDataDirectory = options.LocalDataDirectory ??
                HarnessRunDirectory.For("GbayLifecycle");
            Directory.CreateDirectory(localDataDirectory);
            var runtimeLog = Path.Combine(localDataDirectory, "reactorv-runtime.log");
            if (File.Exists(runtimeLog)) File.Delete(runtimeLog);

            var targetProcessId = Process.GetCurrentProcess().Id;
            using var visualCapture = HarnessVisualCaptureSession.Enable(
                HostColor,
                localDataDirectory);

            using var host = new Form
            {
                BackColor = HostColor,
                ClientSize = new Size(options.Width, options.Height),
                StartPosition = FormStartPosition.CenterScreen,
                Text = "Grand Theft Auto V - REACTOR V GBAY lifecycle harness",
            };
            host.Show();
            Application.DoEvents();
            var effectiveClientWidth = host.ClientSize.Width;
            var effectiveClientHeight = host.ClientSize.Height;
            var effectiveDpi = host.DeviceDpi;
            if (!WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromSeconds(3)))
            {
                Console.Error.WriteLine("Could not activate the synthetic GTA host window.");
                return 5;
            }
            visualCapture.QualifyDesktop(host);

            var broker = new BridgeBroker();
            using var runtime = new OverlayRuntime(
                "windowed",
                host.Handle,
                uiDirectory,
                runtimeDirectory,
                localDataDirectory,
                broker,
                options.Width,
                options.Height,
                60,
                false,
                false);
            var router = new GbayHarnessRouter(runtime.SetVisible);

            try
            {
                var coldTimer = Stopwatch.StartNew();
                if (!runtime.Start())
                    throw new InvalidOperationException("The windowed Reactor host did not start.");
                if (!WaitForTrace(runtimeLog, "stage=webview_content_ready", TimeSpan.FromSeconds(8)))
                    throw new InvalidOperationException("The hidden GBAY browser did not become content-ready.");
                var coldReadyMs = coldTimer.Elapsed.TotalMilliseconds;
                Application.DoEvents();

                var coldPrepared = !runtime.IsVisible &&
                    !WindowProbe.IsVisible(OverlayWindowTitle) &&
                    coldReadyMs <= options.GbayColdReadyBudget.TotalMilliseconds;

                // Connect the managed provider only after the browser is warm.
                // This is the same ordering as the persistent preloader handoff.
                runtime.PostEvent("host.provider", new JObject { ["connected"] = true });
                runtime.PostEvent("host.surface", new JObject { ["mode"] = "none" });
                runtime.PostEvent("overlay.snapshot", Snapshot());
                PumpUntil(
                    broker,
                    runtime,
                    router,
                    () => router.SubscriptionCount >= 1,
                    TimeSpan.FromSeconds(2),
                    "React did not subscribe to menu presentation events.");

                var allFrames = new List<VisualFrame>();

                var firstTimer = Stopwatch.StartNew();
                Present(runtime, router, "gbay-harness-first");
                PumpUntil(
                    broker,
                    runtime,
                    router,
                    () => router.MenuGetCount >= 1 &&
                        router.LastAcceptedPresentation == "gbay-harness-first",
                    TimeSpan.FromSeconds(2),
                    "The first GBAY presentation did not load and acknowledge its painted surface.");
                runtime.SetVisible(true);
                var firstRevealStartMs = firstTimer.Elapsed.TotalMilliseconds;
                var firstObservation = WaitForGbayVisual(
                    host,
                    visualCapture,
                    runtime,
                    broker,
                    router,
                    TimeSpan.FromMilliseconds(options.GbayFirstPresentationBudget.TotalMilliseconds),
                    Path.Combine(localDataDirectory, "first-presentation.png"));
                var firstPresentationMs = firstRevealStartMs + firstObservation.FirstPaintMilliseconds;
                allFrames.AddRange(firstObservation.Frames);
                var firstCompositionRefresh = TraceLineContainsAll(
                    runtimeLog,
                    "stage=webview_composition_refresh_requested",
                    "presentation=gbay-harness-first",
                    "committed=True");

                var visibleReplacementGate = VisibleMenuReplacementGate.Run(
                    host,
                    visualCapture,
                    runtime,
                    broker,
                    router,
                    options.GbayWarmPresentationBudget,
                    localDataDirectory,
                    initiallyCommittedPresentationId: "gbay-harness-first");
                allFrames.AddRange(visibleReplacementGate.Frames);

                // The same production React controller now walks the complete
                // GBAY shell. Each section is reached through its persistent
                // navigation item, invokes one host-owned typed action, and is
                // sampled after the invocation proves that route committed.
                // The compact fixture intentionally has one focusable content
                // action per route, keeping this matrix deterministic without
                // browser test hooks or gameplay-side mutations.
                var routeActions = new[]
                {
                    "vehicle-alpha", "weapon-alpha", "owned-weapon-alpha",
                    "gear-alpha", "stored-alpha", "addon-alpha",
                    "diagnostics-alpha", "about-alpha",
                };
                for (var routeIndex = 0; routeIndex < routeActions.Length; routeIndex++)
                {
                    ExerciseTopLevelRoute(
                        host,
                        visualCapture,
                        runtime,
                        broker,
                        router,
                        routeIndex,
                        routeActions[routeIndex],
                        options.GbayWarmPresentationBudget,
                        Path.Combine(localDataDirectory, $"route-{routeIndex + 1:D2}.png"),
                        allFrames);
                }
                var homeCovered = firstObservation.Frames.Count > 0;
                var routeMatrixPassed = homeCovered && routeActions.All(router.HasInvoked);
                var routeInvocationTotal = routeActions.Sum(router.InvocationCount);
                var dataAndActions = routeMatrixPassed &&
                    routeActions.All(action => router.InvocationCount(action) == 1) &&
                    router.TypedInvocationCount == routeInvocationTotal;

                // Rebuild a shallow Home -> About stack, then re-present the
                // same authoritative revision. The focused About action must
                // survive and invoke again. Removing About in the next revision
                // must then fall back to Home, where the initial Vehicles tile
                // remains usable. These are native end-to-end counterparts to
                // the controller's focused unit regressions.
                ReturnToHome(runtime, broker, router, routeActions.Length);
                NavigateFromHomeToSection(runtime, broker, router, routeActions.Length - 1);

                var aboutInvocationsBeforeShallowRoute = router.InvocationCount("about-alpha");
                PostInput(runtime, "accept");
                PumpUntil(
                    broker,
                    runtime,
                    router,
                    () => router.InvocationCount("about-alpha") == aboutInvocationsBeforeShallowRoute + 1,
                    TimeSpan.FromSeconds(1),
                    "The shallow Home to About route could not be established.");
                var aboutInvocationsBeforeRestore = router.InvocationCount("about-alpha");
                Present(runtime, router, "gbay-harness-restore");
                PumpUntil(
                    broker,
                    runtime,
                    router,
                    () => router.LastAcceptedPresentation == "gbay-harness-restore",
                    TimeSpan.FromSeconds(1),
                    "The GBAY route-restoration presentation was not acknowledged.");
                // The browser deliberately keeps the replacement tree inert
                // through two post-acceptance animation frames. Exercise the
                // restored route only after that exact input barrier can open;
                // sending Accept at the transport acknowledgement would test a
                // state that production now rejects by design.
                PumpFor(broker, runtime, router, TimeSpan.FromMilliseconds(50));
                PostInput(runtime, "accept");
                PumpUntil(
                    broker,
                    runtime,
                    router,
                    () => router.InvocationCount("about-alpha") == aboutInvocationsBeforeRestore + 1,
                    TimeSpan.FromSeconds(1),
                    "The active About route and focus were not restored.");
                var restoredObservation = WaitForGbayVisual(
                    host,
                    visualCapture,
                    runtime,
                    broker,
                    router,
                    options.GbayWarmPresentationBudget,
                    Path.Combine(localDataDirectory, "route-restored-about.png"));
                allFrames.AddRange(restoredObservation.Frames);
                var routeRestored = true;

                router.RemoveAboutRoute();
                Present(runtime, router, "gbay-harness-fallback");
                PumpUntil(
                    broker,
                    runtime,
                    router,
                    () => router.LastAcceptedPresentation == "gbay-harness-fallback",
                    TimeSpan.FromSeconds(1),
                    "The GBAY removed-route fallback presentation was not acknowledged.");
                PumpFor(broker, runtime, router, TimeSpan.FromMilliseconds(50));
                var vehicleInvocationsBeforeFallback = router.InvocationCount("vehicle-alpha");
                // Home restores focus to its first Vehicles route tile.
                PostInput(runtime, "accept");
                PumpFor(broker, runtime, router, TimeSpan.FromMilliseconds(30));
                // About is intentionally absent from this revision, leaving
                // seven enabled navigation entries ahead of route content.
                Move(runtime, broker, router, "navigate-down", 7);
                PostInput(runtime, "accept");
                PumpUntil(
                    broker,
                    runtime,
                    router,
                    () => router.InvocationCount("vehicle-alpha") == vehicleInvocationsBeforeFallback + 1,
                    TimeSpan.FromSeconds(1),
                    "Removing the active About route did not fall back to the Home route.");
                var fallbackObservation = WaitForGbayVisual(
                    host,
                    visualCapture,
                    runtime,
                    broker,
                    router,
                    options.GbayWarmPresentationBudget,
                    Path.Combine(localDataDirectory, "route-fallback-home-vehicles.png"));
                allFrames.AddRange(fallbackObservation.Frames);
                var routeFallback = true;

                var closeCountBeforeFirst = router.OverlayCloseCount;
                var closeMs = DriveBackToClose(
                    broker,
                    runtime,
                    router,
                    closeCountBeforeFirst + 1,
                    options.GbayCloseBudget,
                    "The first GBAY close did not hide the host.");

                PumpFor(broker, runtime, router, TimeSpan.FromMilliseconds(50));
                var warmTimer = Stopwatch.StartNew();
                Present(runtime, router, "gbay-harness-warm");
                PumpUntil(
                    broker,
                    runtime,
                    router,
                    () => router.MenuGetCount >= 2 &&
                        router.LastAcceptedPresentation == "gbay-harness-warm",
                    TimeSpan.FromSeconds(1),
                    "The warm GBAY presentation did not refresh and acknowledge its painted surface.");
                runtime.SetVisible(true);
                var warmRevealStartMs = warmTimer.Elapsed.TotalMilliseconds;
                var warmObservation = WaitForGbayVisual(
                    host,
                    visualCapture,
                    runtime,
                    broker,
                    router,
                    // Pixel capture proves the rendered frame is correct. It
                    // is deliberately allowed the independent first-paint
                    // timeout because production timing is qualified below
                    // from typed presentation observation to compositor commit.
                    options.GbayFirstPresentationBudget,
                    Path.Combine(localDataDirectory, "warm-presentation.png"));
                // Keep the pixel-observation latency for diagnostics only.
                // Copying/classifying the desktop is a correctness probe and
                // can cross the budget after the production compositor has
                // already committed a valid frame.
                var warmVisualObservationMs =
                    warmRevealStartMs + warmObservation.FirstPaintMilliseconds;
                allFrames.AddRange(warmObservation.Frames);
                var warmPresentationObservedAt = ReadLatestTraceMetric(
                    runtimeLog,
                    "stage=webview_menu_presentation_observed",
                    "presentation=gbay-harness-warm",
                    "elapsed_ms=");
                var warmRevealCommittedAt = ReadLatestTraceMetric(
                    runtimeLog,
                    "stage=webview_reveal_committed",
                    "presentation=gbay-harness-warm",
                    "elapsed_ms=");
                var warmPresentationMs =
                    GbayPresentationTimingPolicy.ElapsedBetween(
                        warmPresentationObservedAt,
                        warmRevealCommittedAt);
                var warmCompositionRefresh = TraceLineContainsAll(
                    runtimeLog,
                    "stage=webview_composition_refresh_requested",
                    "presentation=gbay-harness-warm",
                    "committed=True");

                var closeCountBeforeWarm = router.OverlayCloseCount;
                DriveBackToClose(
                    broker,
                    runtime,
                    router,
                    closeCountBeforeWarm + 1,
                    options.GbayCloseBudget,
                    "The warm GBAY close did not hide the host.");

                // Prime one hidden presentation, then hammer visibility faster
                // than the deferred composition commit. A cancelled reveal must
                // never resurface later or leave a black/translucent frame.
                Present(runtime, router, "gbay-harness-rapid");
                PumpUntil(
                    broker,
                    runtime,
                    router,
                    () => router.MenuGetCount >= 2 &&
                        router.LastAcceptedPresentation == "gbay-harness-rapid",
                    TimeSpan.FromSeconds(1),
                    "The rapid-toggle presentation did not load and acknowledge while hidden.");
                var commitsBeforeRapid = CountTrace(runtimeLog, "stage=webview_reveal_committed");
                var uiVisibilityRequestsBeforeRapid = CountTrace(
                    runtimeLog,
                    "stage=webview_visibility_requested");
                // The production runtime intentionally rejects every reveal
                // while GTA is not foreground. The synthetic host can lose
                // foreground to an unrelated desktop application between the
                // preceding pixel capture and this non-visual queue test. Make
                // the game-foreground contract an explicit precondition here;
                // otherwise every show can be correctly suppressed for an
                // unrelated reason and the queue test stops exercising the
                // announced-hide cancellation path.
                // This does not reissue a visibility request or alter any
                // exact request/commit assertion below.
                var rapidForegroundRecoveries = 0;
                EnsureHiddenHostForeground(
                    host,
                    ref rapidForegroundRecoveries,
                    "rapid-toggle visibility queue");
                var rapidTimer = Stopwatch.StartNew();
                for (var index = 0; index < 8; index++)
                {
                    rapidTimer.Stop();
                    EnsureHiddenHostForeground(
                        host,
                        ref rapidForegroundRecoveries,
                        "rapid-toggle visibility queue");
                    rapidTimer.Start();
                    runtime.SetVisible(true);
                    PumpFor(broker, runtime, router, TimeSpan.FromMilliseconds(4));
                    runtime.SetVisible(false);
                    PumpFor(broker, runtime, router, TimeSpan.FromMilliseconds(4));
                }
                PumpUntil(
                    broker,
                    runtime,
                    router,
                    () => !runtime.IsVisible && CountTrace(
                        runtimeLog,
                        "stage=webview_visibility_requested") >=
                        uiVisibilityRequestsBeforeRapid + 16,
                    // Every request must reach the STA and the final hidden
                    // state must win. The ingress barrier may intentionally
                    // coalesce a show when its paired hide is already
                    // announced; requiring eight redundant hidden commits
                    // would reject the exact anti-flicker behavior under test.
                    TimeSpan.FromSeconds(5),
                    "The rapid-toggle visibility queue did not drain deterministically.");
                PumpFor(broker, runtime, router, TimeSpan.FromMilliseconds(50));
                if (rapidForegroundRecoveries > 0)
                {
                    Console.WriteLine(
                        $"HARNESS INFO: recovered synthetic GTA foreground " +
                        $"{rapidForegroundRecoveries} time(s) before/during rapid-toggle qualification.");
                }
                var rapidToggleMs = rapidTimer.Elapsed.TotalMilliseconds;
                var commitsAfterRapid = CountTrace(runtimeLog, "stage=webview_reveal_committed");
                var rapidNoIntermediate = commitsAfterRapid == commitsBeforeRapid;
                // A cancelled opacity-zero WinForms reveal remains WS_VISIBLE
                // briefly while its queued UI-thread transition drains. The
                // runtime's committed visibility callback plus the absence of
                // any reveal-commit trace is the authoritative pixel boundary.
                var rapidToggleStable = !runtime.IsVisible && rapidNoIntermediate;

                // Run one hundred hidden presentation lifecycles against the
                // same warm browser and authoritative menu revision. Every
                // cycle must return the exact presentation token without
                // re-fetching the unchanged tree; every tenth cycle also
                // performs a physical reveal/hide and contributes visual
                // samples. This keeps the gate exhaustive without adding a
                // hundred compositor-settle waits to every release build.
                const int stressCycles = 100;
                var menuGetsBeforeStress = router.MenuGetCount;
                var revisionBeforeStress = router.MenuRevision;
                var stressReadyLatencies = new List<double>(stressCycles);
                var stressRevealLatencies = new List<double>(stressCycles / 10);
                var stressEndToEndLatencies = new List<double>(stressCycles / 10);
                for (var cycle = 0; cycle < stressCycles; cycle++)
                {
                    var presentationId = $"gbay-stress-{cycle:D3}";
                    var exactAcksBefore = router.ExactPresentationReadyCount;
                    var cycleTimer = Stopwatch.StartNew();
                    Present(runtime, router, presentationId);
                    PumpUntil(
                        broker,
                        runtime,
                        router,
                        () => router.ExactPresentationReadyCount > exactAcksBefore &&
                            router.LastAcceptedPresentation == presentationId,
                        TimeSpan.FromSeconds(1),
                        $"GBAY stress cycle {cycle} did not produce an exact ready acknowledgement.");
                    var readyMs = cycleTimer.Elapsed.TotalMilliseconds;
                    stressReadyLatencies.Add(readyMs);

                    if (cycle % 10 != 0) continue;
                    runtime.SetVisible(true);
                    var stressObservation = WaitForGbayVisual(
                        host,
                        visualCapture,
                        runtime,
                        broker,
                        router,
                        // Visual capture is a correctness probe and may run
                        // after the production budget has expired. The strict
                        // stress timing below comes from the runtime's own
                        // request-to-commit trace, not foreground repair,
                        // CopyFromScreen, PNG work, or harness message pumping.
                        options.GbayFirstPresentationBudget,
                        Path.Combine(localDataDirectory, $"stress-{cycle:D3}.png"));
                    var revealCommittedAt = ReadLatestTraceMetric(
                        runtimeLog,
                        "stage=webview_reveal_committed",
                        $"presentation={presentationId}",
                        "elapsed_ms=");
                    var presentationObservedAt = ReadLatestTraceMetric(
                        runtimeLog,
                        "stage=webview_menu_presentation_observed",
                        $"presentation={presentationId}",
                        "elapsed_ms=");
                    // The reveal commit records the production request-to-
                    // commit duration against this exact presentation. Do not
                    // reconstruct it from the latest generic visibility event:
                    // desktop capture can recover synthetic foreground after
                    // the commit and legitimately issue another Show request.
                    var productionRevealMs = ReadLatestTraceMetric(
                        runtimeLog,
                        "stage=webview_reveal_committed",
                        $"presentation={presentationId}",
                        "request_to_commit_ms=");
                    stressRevealLatencies.Add(productionRevealMs);
                    stressEndToEndLatencies.Add(
                        GbayPresentationTimingPolicy.ElapsedBetween(
                            presentationObservedAt,
                            revealCommittedAt));
                    allFrames.AddRange(stressObservation.Frames);
                    // IsVisible reflects the public requested state before the
                    // WinForms host necessarily applies the queued hide. Drain
                    // that physical transition before starting the next
                    // presentation so prior-cycle z-order/compositor work is
                    // never charged to a later reveal measurement.
                    var hiddenCommitsBeforeStressHide = CountTrace(
                        runtimeLog,
                        "stage=webview_visibility_applied visible=False");
                    runtime.SetVisible(false);
                    PumpUntil(
                        broker,
                        runtime,
                        router,
                        () => !runtime.IsVisible && CountTrace(
                            runtimeLog,
                            "stage=webview_visibility_applied visible=False") >
                            hiddenCommitsBeforeStressHide,
                        options.GbayCloseBudget,
                        $"GBAY stress cycle {cycle} did not apply its hidden state.");
                }

                var stressReadyP50 = Percentile(stressReadyLatencies, 0.50d);
                var stressReadyP95 = Percentile(stressReadyLatencies, 0.95d);
                var stressReadyMaximum = stressReadyLatencies.Max();
                var stressRevealP50 = Percentile(stressRevealLatencies, 0.50d);
                var stressRevealP95 = Percentile(stressRevealLatencies, 0.95d);
                var stressRevealMaximum = stressRevealLatencies.Max();
                var stressEndToEndP50 = Percentile(stressEndToEndLatencies, 0.50d);
                var stressEndToEndP95 = Percentile(stressEndToEndLatencies, 0.95d);
                var stressEndToEndMaximum = stressEndToEndLatencies.Max();
                var sampledEndToEndLatencies = new[]
                {
                    warmPresentationMs,
                    stressEndToEndMaximum,
                };
                var stressMenuGets = router.MenuGetCount - menuGetsBeforeStress;
                var rootRebindCount = CountTrace(
                    runtimeLog,
                    "root_rebind_outcome=Rebound");
                var compositionRecoveryFailures = CountTrace(
                    runtimeLog,
                    "stage=webview_composition_device_recovery_failed");
                var alreadyComposedConflict = TraceLineContainsAll(
                    runtimeLog,
                    "0x88980800");
                // Warm reopen exercises the same-target root-rebind route;
                // later stress reveals may only need bounds synchronization
                // once that root has been republished. A healthy target must
                // never be replaced with a second CreateTargetForHwnd
                // registration.
                var targetReuseQualified = rootRebindCount >= 1 &&
                    compositionRecoveryFailures == 0 &&
                    !alreadyComposedConflict;

                var maximumBlack = allFrames.Count == 0 ? 1d : allFrames.Max(frame => frame.BlackFraction);
                var minimumChanged = allFrames.Count == 0 ? 0d : allFrames.Min(frame => frame.ChangedFraction);
                var minimumGreen = allFrames.Count == 0 ? 0d : allFrames.Min(frame => frame.GreenFraction);
                var maximumBlue = allFrames.Count == 0 ? 1d : allFrames.Max(frame => frame.BlueFraction);
                var noBlackIntermediate = maximumBlack < 0.12d;
                var noTransparentIntermediate = minimumChanged > 0.10d;
                var transparentSurround = allFrames.Count > 0 &&
                    allFrames.All(frame => frame.SurroundMatchesHost);
                // Each presentation phase already had to produce a green GBAY
                // frame before returning. The aggregate blue ceiling rejects
                // About/setup frames; transparent and black frames have their
                // own stricter independent gates above.
                var noAboutOrSetupInterstitial = maximumBlue < 0.025d;
                var timingPassed = coldReadyMs <= options.GbayColdReadyBudget.TotalMilliseconds &&
                    firstPresentationMs <= options.GbayFirstPresentationBudget.TotalMilliseconds &&
                    closeMs <= options.GbayCloseBudget.TotalMilliseconds &&
                    GbayPresentationTimingPolicy.MeetsBudget(
                        sampledEndToEndLatencies,
                        options.GbayWarmPresentationBudget.TotalMilliseconds);
                var routeCoverageCount = (homeCovered ? 1 : 0) + routeActions.Count(router.HasInvoked);
                var expectedMenuGets = GbayHarnessRouter.FullDescriptorCount +
                    GbayHarnessRouter.ReducedDescriptorCount +
                    visibleReplacementGate.AdditionalMenuGetCount;
                var passed = coldPrepared && dataAndActions && routeMatrixPassed &&
                    routeRestored && routeFallback && rapidToggleStable &&
                    visibleReplacementGate.CrossKeyPreserved &&
                    visibleReplacementGate.SameKeyPreserved &&
                    visibleReplacementGate.NoIntermediateFrame &&
                    firstCompositionRefresh && warmCompositionRefresh &&
                    noBlackIntermediate && noTransparentIntermediate &&
                    transparentSurround &&
                    noAboutOrSetupInterstitial && timingPassed &&
                    targetReuseQualified &&
                    routeCoverageCount == routeActions.Length + 1 &&
                    router.TypedInvocationCount >= routeActions.Length + 2 &&
                    router.MenuGetCount == expectedMenuGets &&
                    stressMenuGets == 0 && router.MenuRevision == revisionBeforeStress &&
                    router.MenuInvokeCount >= routeActions.Length + 2 &&
                    router.ExactPresentationReadyCount >= stressCycles + 5 &&
                    router.StalePresentationReadyCount == 0 &&
                    stressReadyMaximum <= options.GbayWarmPresentationBudget.TotalMilliseconds &&
                    stressRevealMaximum <= options.GbayWarmPresentationBudget.TotalMilliseconds &&
                    stressEndToEndMaximum <= options.GbayWarmPresentationBudget.TotalMilliseconds &&
                    router.OverlayCloseCount >= 2;

                Console.WriteLine(
                    $"RESULT {(passed ? "PASS" : "FAIL")}: scenario=gbay-lifecycle " +
                    $"coldPrepared={coldPrepared} coldReadyMs={coldReadyMs:F1} " +
                    $"coldBudgetMs={options.GbayColdReadyBudget.TotalMilliseconds:F0} " +
                    $"firstPresentationMs={firstPresentationMs:F1} " +
                    $"firstBudgetMs={options.GbayFirstPresentationBudget.TotalMilliseconds:F0} " +
                    $"closeMs={closeMs:F1} closeBudgetMs={options.GbayCloseBudget.TotalMilliseconds:F0} " +
                    $"warmPresentationMs={warmPresentationMs:F1} " +
                    $"warmVisualObservationMs={warmVisualObservationMs:F1} " +
                    $"warmBudgetMs={options.GbayWarmPresentationBudget.TotalMilliseconds:F0} " +
                    $"firstCompositionRefresh={firstCompositionRefresh} " +
                    $"warmCompositionRefresh={warmCompositionRefresh} " +
                    $"rapidToggleMs={rapidToggleMs:F1} rapidStable={rapidToggleStable} " +
                    $"rapidNoIntermediate={rapidNoIntermediate} " +
                    $"noBlack={noBlackIntermediate} noTransparent={noTransparentIntermediate} " +
                    $"transparentSurround={transparentSurround} " +
                    $"noInterstitial={noAboutOrSetupInterstitial} " +
                    $"blackMax={maximumBlack:F4} changedMin={minimumChanged:F4} " +
                    $"greenMin={minimumGreen:F4} blueMax={maximumBlue:F4} " +
                    $"dataActions={dataAndActions} routeMatrix={routeMatrixPassed} " +
                    $"routeRestored={routeRestored} routeFallback={routeFallback} " +
                    $"crossKeyAtomic={visibleReplacementGate.CrossKeyPreserved} " +
                    $"sameKeyAtomic={visibleReplacementGate.SameKeyPreserved} " +
                    $"replacementNoIntermediate={visibleReplacementGate.NoIntermediateFrame} " +
                    $"routeCoverage={routeCoverageCount}/{routeActions.Length + 1} " +
                    $"menuGets={router.MenuGetCount} expectedMenuGets={expectedMenuGets} " +
                    $"stressMenuGets={stressMenuGets} menuRevision={router.MenuRevision} " +
                    $"menuInvokes={router.MenuInvokeCount} typedInvokes={router.TypedInvocationCount} " +
                    $"readyAcks={router.ExactPresentationReadyCount} " +
                    $"staleAcks={router.StalePresentationReadyCount} stressCycles={stressCycles} " +
                    $"stressReadyP50Ms={stressReadyP50:F1} stressReadyP95Ms={stressReadyP95:F1} " +
                    $"stressReadyMaxMs={stressReadyMaximum:F1} " +
                    $"stressRevealP50Ms={stressRevealP50:F1} stressRevealP95Ms={stressRevealP95:F1} " +
                    $"stressRevealMaxMs={stressRevealMaximum:F1} " +
                    $"stressEndToEndP50Ms={stressEndToEndP50:F1} " +
                    $"stressEndToEndP95Ms={stressEndToEndP95:F1} " +
                    $"stressEndToEndMaxMs={stressEndToEndMaximum:F1} " +
                    $"targetReuse={targetReuseQualified} rootRebinds={rootRebindCount} " +
                    $"compositionRecoveryFailures={compositionRecoveryFailures} " +
                    $"alreadyComposedConflict={alreadyComposedConflict} " +
                    $"clientWidth={effectiveClientWidth} clientHeight={effectiveClientHeight} " +
                    $"effectiveDpi={effectiveDpi} " +
                    $"overlayCloses={router.OverlayCloseCount} " +
                    $"frames={allFrames.Count}");
                if (!passed)
                    Console.Error.WriteLine($"Runtime trace and screenshots: {localDataDirectory}");
                return passed ? 0 : 4;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("RESULT FAIL: scenario=gbay-lifecycle " + error.Message);
                Console.Error.WriteLine(
                    $"Harness counters: menuGets={router.MenuGetCount} " +
                    $"exactAcks={router.ExactPresentationReadyCount} " +
                    $"staleAcks={router.StalePresentationReadyCount} " +
                    $"lastAccepted={router.LastAcceptedPresentation}");
                Console.Error.WriteLine($"Runtime trace and screenshots: {localDataDirectory}");
                return 4;
            }
            finally
            {
                router.Dispose();
            }
        }

        private static void Present(
            IOverlayRuntime runtime,
            GbayHarnessRouter router,
            string presentationId)
        {
            router.ExpectPresentation(presentationId);
            runtime.PostEvent("host.surface", new JObject { ["mode"] = "none" });
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
                        ["menuRevision"] = $"gbay-harness-{router.MenuRevision}",
                    },
                });
        }

        private static void PostInput(IOverlayRuntime runtime, string action) =>
            runtime.PostEvent(
                "input.action",
                new JObject
                {
                    ["action"] = action,
                    ["phase"] = "pressed",
                    ["source"] = "game",
                    ["timestamp"] = Environment.TickCount & int.MaxValue,
                });

        private static void ExerciseTopLevelRoute(
            Form host,
            HarnessVisualCaptureSession visualCapture,
            IOverlayRuntime runtime,
            BridgeBroker broker,
            GbayHarnessRouter router,
            int routeIndex,
            string expectedNodeId,
            TimeSpan visualBudget,
            string screenshotPath,
            ICollection<VisualFrame> frames)
        {
            if (routeIndex == 0)
            {
                // Home initially focuses the first enabled navigation entry:
                // Vehicles. The disabled Home entry is not in the focus ring.
                PostInput(runtime, "accept");
                PumpFor(broker, runtime, router, TimeSpan.FromMilliseconds(30));
            }
            else
            {
                // A section has eight enabled navigation entries plus one
                // fixture action. The prior action remains focused, so eight
                // upward moves select Home. In a section whose own nav entry
                // is disabled, routeIndex downward moves select the next
                // sequential section.
                Move(runtime, broker, router, "navigate-up", 8);
                Move(runtime, broker, router, "navigate-down", routeIndex);
                PostInput(runtime, "accept");
                PumpFor(broker, runtime, router, TimeSpan.FromMilliseconds(30));
            }

            FocusFirstRouteContent(runtime, broker, router);
            var invocationCount = router.InvocationCount(expectedNodeId);
            PostInput(runtime, "accept");
            PumpUntil(
                broker,
                runtime,
                router,
                () => router.InvocationCount(expectedNodeId) == invocationCount + 1,
                TimeSpan.FromSeconds(1),
                $"The '{expectedNodeId}' route action was not invoked.");

            var observation = WaitForGbayVisual(
                host,
                visualCapture,
                runtime,
                broker,
                router,
                visualBudget,
                screenshotPath);
            foreach (var frame in observation.Frames) frames.Add(frame);
        }

        private static void FocusFirstRouteContent(
            IOverlayRuntime runtime,
            BridgeBroker broker,
            GbayHarnessRouter router) =>
            Move(runtime, broker, router, "navigate-down", 8);

        private static void ReturnToHome(
            IOverlayRuntime runtime,
            BridgeBroker broker,
            GbayHarnessRouter router,
            int routeDepth) =>
            Move(runtime, broker, router, "back", routeDepth);

        private static void NavigateFromHomeToSection(
            IOverlayRuntime runtime,
            BridgeBroker broker,
            GbayHarnessRouter router,
            int sectionIndex)
        {
            // Home's enabled navigation starts at Vehicles (index zero).
            Move(runtime, broker, router, "navigate-down", sectionIndex);
            PostInput(runtime, "accept");
            PumpFor(broker, runtime, router, TimeSpan.FromMilliseconds(30));
        }

        private static void Move(
            IOverlayRuntime runtime,
            BridgeBroker broker,
            GbayHarnessRouter router,
            string action,
            int count)
        {
            for (var index = 0; index < count; index++)
            {
                PostInput(runtime, action);
                PumpFor(broker, runtime, router, TimeSpan.FromMilliseconds(12));
            }
        }

        private static JObject Snapshot() => new JObject
        {
            ["runtime"] = GbayHarnessRouter.RuntimeStatus(),
            ["state"] = GbayHarnessRouter.GameState(),
        };

        private static void PumpUntil(
            BridgeBroker broker,
            IOverlayRuntime runtime,
            GbayHarnessRouter router,
            Func<bool> condition,
            TimeSpan timeout,
            string error)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                Application.DoEvents();
                Pump(broker, runtime, router);
                if (condition()) return;
                Thread.Sleep(5);
            }
            throw new InvalidOperationException(error);
        }

        private static void PumpFor(
            BridgeBroker broker,
            IOverlayRuntime runtime,
            GbayHarnessRouter router,
            TimeSpan duration)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < duration)
            {
                Application.DoEvents();
                Pump(broker, runtime, router);
                Thread.Sleep(4);
            }
        }

        private static double DriveBackToClose(
            BridgeBroker broker,
            IOverlayRuntime runtime,
            GbayHarnessRouter router,
            int expectedCloseCount,
            TimeSpan budget,
            string error)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < budget && router.OverlayCloseCount < expectedCloseCount)
            {
                PostInput(runtime, "back");
                PumpFor(broker, runtime, router, TimeSpan.FromMilliseconds(55));
            }
            while (timer.Elapsed < budget && runtime.IsVisible)
            {
                Application.DoEvents();
                Pump(broker, runtime, router);
                Thread.Sleep(5);
            }
            if (router.OverlayCloseCount < expectedCloseCount || runtime.IsVisible)
                throw new InvalidOperationException(error);
            return timer.Elapsed.TotalMilliseconds;
        }

        private static void Pump(
            BridgeBroker broker,
            IOverlayRuntime runtime,
            GbayHarnessRouter router)
        {
            for (var index = 0; index < 64 && broker.TryDequeue(out var request); index++)
            {
                if (request != null && router.TryDispatch(request, out var response))
                    runtime.PostResponse(response);
            }
        }

        private static VisualObservation WaitForGbayVisual(
            Form host,
            HarnessVisualCaptureSession visualCapture,
            IOverlayRuntime runtime,
            BridgeBroker broker,
            GbayHarnessRouter router,
            TimeSpan timeout,
            string screenshotPath)
        {
            var frames = new List<VisualFrame>();
            var foregroundRecoveries = 0;
            EnsureVisualHostForeground(host, runtime, ref foregroundRecoveries);
            var timer = Stopwatch.StartNew();
            var recognized = false;
            var firstPaintMilliseconds = double.PositiveInfinity;
            while (timer.Elapsed < timeout)
            {
                // Foreground restoration is a synthetic-host precondition,
                // not production presentation work. Pause the reveal clock
                // while the harness repairs that external condition.
                timer.Stop();
                EnsureVisualHostForeground(host, runtime, ref foregroundRecoveries);
                timer.Start();
                Application.DoEvents();
                Pump(broker, runtime, router);
                // The window is intentionally WS_VISIBLE at opacity zero while
                // the compositor probe runs. That preparation state does not
                // alter a game frame and must not be misclassified as a
                // transparent transition. Sample only after the production
                // runtime commits actual visibility.
                if (runtime.IsVisible)
                {
                    // CopyFromScreen plus full-frame pixel classification can
                    // take hundreds of milliseconds on a busy/high-DPI
                    // desktop. The pixels existed when sampling began, so use
                    // that instant as the qualified-frame observation time;
                    // keep capture/analysis as an independent visual gate.
                    var sampleStartedMilliseconds = timer.Elapsed.TotalMilliseconds;
                    using var image = visualCapture.Capture(host);
                    var frame = VisualFrame.Measure(image);
                    frames.Add(frame);
                    if (frame.IsGbay)
                    {
                        firstPaintMilliseconds = sampleStartedMilliseconds;
                        // Stop the reveal measurement at the qualified frame.
                        // Persisting harness evidence is diagnostic I/O and must
                        // not be charged to the production presentation budget.
                        image.Save(screenshotPath);
                        recognized = true;
                        break;
                    }
                }
                Thread.Sleep(5);
            }
            if (!recognized)
                throw new InvalidOperationException("GBAY did not paint a qualified visible frame within its budget.");

            // Sample the selected concrete-pixel source repeatedly to reject
            // a late composition black flash. Both GDI and the qualified
            // PrintWindow fallback apply the same per-frame classifier.
            var settle = Stopwatch.StartNew();
            while (settle.Elapsed < TimeSpan.FromMilliseconds(140))
            {
                EnsureVisualHostForeground(host, runtime, ref foregroundRecoveries);
                Application.DoEvents();
                Pump(broker, runtime, router);
                if (runtime.IsVisible)
                {
                    using var image = visualCapture.Capture(host);
                    frames.Add(VisualFrame.Measure(image));
                }
                Thread.Sleep(12);
            }
            if (foregroundRecoveries > 0)
            {
                Console.WriteLine(
                    $"HARNESS INFO: recovered synthetic GTA foreground " +
                    $"{foregroundRecoveries} time(s) before qualifying '{Path.GetFileName(screenshotPath)}'.");
            }
            return new VisualObservation(frames, firstPaintMilliseconds);
        }

        private static void EnsureVisualHostForeground(
            Form host,
            IOverlayRuntime runtime,
            ref int recoveryCount)
        {
            if (WindowProbe.IsForegroundOrOwnedBy(host.Handle))
            {
                return;
            }

            // The production overlay deliberately closes when another process
            // owns the foreground. During this desktop pixel harness a shell,
            // notification, or interactive test runner can briefly steal
            // focus from the synthetic GTA form. Restore the test precondition
            // before sampling, then reissue the visibility request that the
            // production focus-loss policy correctly cleared. We never accept
            // a frame during recovery: the normal GBAY/no-black qualification
            // below still has to pass after the runtime commits visibility.
            if (!WindowProbe.EnsureForeground(
                    host.Handle,
                    TimeSpan.FromMilliseconds(750)))
            {
                throw new InvalidOperationException(
                    "The synthetic GTA host lost foreground and could not be reactivated for visual qualification.");
            }

            recoveryCount++;
            runtime.SetVisible(true);
        }

        private static void EnsureHiddenHostForeground(
            Form host,
            ref int recoveryCount,
            string phase)
        {
            if (WindowProbe.IsForegroundOrOwnedBy(host.Handle))
            {
                return;
            }

            // Hidden lifecycle phases must repair only the synthetic game
            // precondition. Reissuing SetVisible here would contaminate the
            // exact 16-request rapid-toggle contract and conceal queue bugs.
            if (!WindowProbe.EnsureForeground(
                    host.Handle,
                    TimeSpan.FromMilliseconds(750)))
            {
                throw new InvalidOperationException(
                    $"The synthetic GTA host lost foreground and could not be reactivated for {phase}.");
            }

            recoveryCount++;
        }

        private static bool WaitForTrace(string path, string marker, TimeSpan timeout)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                Application.DoEvents();
                try
                {
                    if (File.Exists(path) && ReadTraceSnapshot(path).Contains(marker)) return true;
                }
                catch (IOException)
                {
                }
                Thread.Sleep(10);
            }
            return false;
        }

        private static int CountTrace(string path, string marker)
        {
            // StartupTrace appends from the overlay UI thread while the
            // harness samples counters from the test thread. A single
            // Read through a sharing stream so sampling never prevents
            // StartupTrace from appending the aggregate record. Returning -1
            // immediately is safe in polling predicates, but it corrupts
            // one-shot baselines such as the rapid-toggle reveal count and
            // creates a false flicker failure. Retry only a transient read;
            // the bounded wait is never on a product thread and does not
            // weaken any count assertion.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return 0;
                    var text = ReadTraceSnapshot(path);
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
                    return count;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(5);
                }
            }
            return -1;
        }

        private static bool TraceLineContainsAll(string path, params string[] markers)
        {
            try
            {
                if (!File.Exists(path)) return false;
                return TraceLines(ReadTraceSnapshot(path)).Any(line =>
                    markers.All(marker =>
                        line.IndexOf(marker, StringComparison.Ordinal) >= 0));
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static double ReadLatestTraceMetric(
            string path,
            string stageMarker,
            string identityMarker,
            string metricPrefix)
        {
            try
            {
                if (!File.Exists(path)) return double.PositiveInfinity;
                var line = TraceLines(ReadTraceSnapshot(path)).LastOrDefault(candidate =>
                    candidate.IndexOf(stageMarker, StringComparison.Ordinal) >= 0 &&
                    candidate.IndexOf(identityMarker, StringComparison.Ordinal) >= 0);
                if (line == null) return double.PositiveInfinity;
                var start = line.IndexOf(metricPrefix, StringComparison.Ordinal);
                if (start < 0) return double.PositiveInfinity;
                start += metricPrefix.Length;
                var end = line.IndexOf(' ', start);
                var raw = end < 0
                    ? line.Substring(start)
                    : line.Substring(start, end - start);
                return double.TryParse(
                    raw,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value)
                    ? value
                    : double.PositiveInfinity;
            }
            catch (IOException)
            {
                return double.PositiveInfinity;
            }
        }

        private static string ReadTraceSnapshot(string path)
        {
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

        private static IEnumerable<string> TraceLines(string snapshot)
        {
            return snapshot.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries);
        }

        private static double Percentile(IReadOnlyCollection<double> values, double percentile)
        {
            if (values.Count == 0) return double.PositiveInfinity;
            var ordered = values.OrderBy(value => value).ToArray();
            var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
            return ordered[Math.Max(0, Math.Min(ordered.Length - 1, index))];
        }

        internal readonly struct VisualFrame
        {
            private VisualFrame(
                double black,
                double changed,
                double green,
                double blue,
                double white,
                double darkGreen,
                bool surroundMatchesHost)
            {
                BlackFraction = black;
                ChangedFraction = changed;
                GreenFraction = green;
                BlueFraction = blue;
                WhiteFraction = white;
                DarkGreenFraction = darkGreen;
                SurroundMatchesHost = surroundMatchesHost;
            }

            public double BlackFraction { get; }
            public double ChangedFraction { get; }
            public double GreenFraction { get; }
            public double BlueFraction { get; }
            public double WhiteFraction { get; }
            public double DarkGreenFraction { get; }
            public bool SurroundMatchesHost { get; }
            public bool IsGbay => ChangedFraction > 0.10d && GreenFraction > 0.006d &&
                BlackFraction < 0.12d && BlueFraction < 0.025d;

            // The ALLIN1 Preloader is a bounded green/white card over the
            // synthetic GTA surface. Its fixed readable width deliberately
            // occupies a much smaller fraction at 4K than at 720p, so this
            // classifier uses its palette, a bounded footprint, and verified
            // transparent surround rather than the retired blue theme. The
            // upper bound permits the same 850px card on a 1024px-wide host;
            // requiring host pixels at both upper corners keeps a full-frame
            // or opaque fallback from qualifying.
            public bool IsStartupTransition => ChangedFraction > 0.02d &&
                ChangedFraction < 0.80d && BlackFraction < 0.35d &&
                GreenFraction > 0.001d && BlueFraction < 0.025d &&
                WhiteFraction > 0.008d && DarkGreenFraction > 0.006d &&
                SurroundMatchesHost;

            public static VisualFrame Measure(Bitmap image)
            {
                long samples = 0;
                long black = 0;
                long changed = 0;
                long green = 0;
                long blue = 0;
                long white = 0;
                long darkGreen = 0;
                for (var y = 0; y < image.Height; y += 4)
                {
                    for (var x = 0; x < image.Width; x += 4)
                    {
                        var pixel = image.GetPixel(x, y);
                        samples++;
                        if (pixel.R < 12 && pixel.G < 12 && pixel.B < 12) black++;
                        if (Math.Abs(pixel.R - HostColor.R) > 14 ||
                            Math.Abs(pixel.G - HostColor.G) > 14 ||
                            Math.Abs(pixel.B - HostColor.B) > 14) changed++;
                        if (pixel.G > pixel.R + 15 && pixel.G > pixel.B + 5) green++;
                        if (pixel.B > pixel.R + 30 && pixel.B > pixel.G + 15) blue++;
                        if (pixel.R > 220 && pixel.G > 220 && pixel.B > 220 &&
                            Math.Abs(pixel.R - pixel.G) < 35 &&
                            Math.Abs(pixel.G - pixel.B) < 35) white++;
                        if (pixel.R < 70 && pixel.G < 110 && pixel.B < 85 &&
                            pixel.G > pixel.R + 5 && pixel.G > pixel.B + 3) darkGreen++;
                    }
                }
                return new VisualFrame(
                    black / (double)samples,
                    changed / (double)samples,
                    green / (double)samples,
                    blue / (double)samples,
                    white / (double)samples,
                    darkGreen / (double)samples,
                    HasTransparentSurround(image));
            }

            private static bool HasTransparentSurround(Bitmap image)
            {
                // The React shells are deliberately inset from the client
                // edge. If the WebView is truly alpha-composed, both upper
                // corner patches expose the synthetic GTA host color. A black
                // or magenta child-HWND fallback fails this check immediately.
                var matching = 0;
                var samples = 0;
                for (var y = 4; y <= 20 && y < image.Height; y += 4)
                {
                    for (var offset = 4; offset <= 20; offset += 4)
                    {
                        var left = Math.Min(image.Width - 1, offset);
                        var right = Math.Max(0, image.Width - 1 - offset);
                        matching += IsHostColor(image.GetPixel(left, y)) ? 1 : 0;
                        matching += IsHostColor(image.GetPixel(right, y)) ? 1 : 0;
                        samples += 2;
                    }
                }
                return samples > 0 && matching >= Math.Ceiling(samples * 0.90d);
            }

            private static bool IsHostColor(Color pixel) =>
                Math.Abs(pixel.R - HostColor.R) <= 8 &&
                Math.Abs(pixel.G - HostColor.G) <= 8 &&
                Math.Abs(pixel.B - HostColor.B) <= 8;
        }

        private sealed class VisualObservation
        {
            public VisualObservation(
                IReadOnlyList<VisualFrame> frames,
                double firstPaintMilliseconds)
            {
                Frames = frames;
                FirstPaintMilliseconds = firstPaintMilliseconds;
            }

            public IReadOnlyList<VisualFrame> Frames { get; }
            public double FirstPaintMilliseconds { get; }
        }

        internal sealed class GbayHarnessRouter : IDisposable
        {
            private static readonly IReadOnlyDictionary<string, string> NodeMenus =
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["vehicle-alpha"] = "vehicles",
                    ["weapon-alpha"] = "weapons",
                    ["owned-weapon-alpha"] = "weapons.customize",
                    ["gear-alpha"] = "gear",
                    ["stored-alpha"] = "garage",
                    ["addon-alpha"] = "addons",
                    ["diagnostics-alpha"] = "diagnostics",
                    ["about-alpha"] = "about",
                };
            private readonly Action<bool> _setVisible;
            private readonly HashSet<string> _subscriptions = new HashSet<string>();
            private readonly HashSet<string> _invokedNodes = new HashSet<string>();
            private readonly Dictionary<string, int> _invocationCounts =
                new Dictionary<string, int>(StringComparer.Ordinal);
            private bool _includeAbout = true;
            private bool _holdNextMenuGet;
            private BridgeRequest? _heldMenuGet;

            public GbayHarnessRouter(Action<bool> setVisible) => _setVisible = setVisible;

            public const int FullDescriptorCount = 9;
            public const int ReducedDescriptorCount = 8;
            public int SubscriptionCount { get; private set; }
            public int StartupStatusRequestCount { get; private set; }
            public bool LastStartupDefaultMenuRequested { get; private set; }
            public int MenuGetCount { get; private set; }
            public int MenuInvokeCount { get; private set; }
            public int TypedInvocationCount { get; private set; }
            public int OverlayCloseCount { get; private set; }
            public int ExactPresentationReadyCount { get; private set; }
            public int StalePresentationReadyCount { get; private set; }
            public int MenuRevision { get; private set; } = 1;
            public string LastInvokedNode { get; private set; } = string.Empty;
            public string LastAcceptedPresentation { get; private set; } = string.Empty;
            public bool HasHeldMenuGet => _heldMenuGet != null;
            private string ExpectedPresentation { get; set; } = string.Empty;

            public bool HasInvoked(string nodeId) => _invokedNodes.Contains(nodeId);
            public int InvocationCount(string nodeId) =>
                _invocationCounts.TryGetValue(nodeId, out var count) ? count : 0;

            public void RemoveAboutRoute()
            {
                _includeAbout = false;
                MenuRevision++;
            }

            public void ExpectPresentation(string presentationId)
            {
                ExpectedPresentation = presentationId;
                // Browser acknowledgement is scoped to the presentation now
                // being prepared. Keeping the previous accepted ID here can
                // falsely satisfy the native paint-commit phase while a
                // replacement is still loading.
                LastAcceptedPresentation = string.Empty;
            }

            public void HoldNextMenuGet()
            {
                if (_holdNextMenuGet || _heldMenuGet != null)
                    throw new InvalidOperationException(
                        "A menu.get request is already held by the replacement gate.");
                _holdNextMenuGet = true;
            }

            public bool TryDispatch(BridgeRequest request, out BridgeResponse response)
            {
                if (_holdNextMenuGet &&
                    string.Equals(request.Method, "menu.get", StringComparison.Ordinal))
                {
                    _holdNextMenuGet = false;
                    _heldMenuGet = request;
                    response = null!;
                    return false;
                }

                response = Dispatch(request);
                return true;
            }

            public void ReleaseHeldMenuGet(IOverlayRuntime runtime)
            {
                if (runtime == null) throw new ArgumentNullException(nameof(runtime));
                var request = _heldMenuGet ?? throw new InvalidOperationException(
                    "The replacement gate has no held menu.get request to release.");
                _heldMenuGet = null;
                runtime.PostResponse(Dispatch(request));
            }

            public BridgeResponse Dispatch(BridgeRequest request)
            {
                try
                {
                    JToken result;
                    switch (request.Method)
                    {
                        case "overlay.ready":
                        case "runtime.handshake":
                            result = RuntimeStatus();
                            break;
                        case StartupStatusContract.Method:
                            StartupStatusRequestCount++;
                            LastStartupDefaultMenuRequested =
                                PreloadHandoff.IsDefaultMenuIntentActive(
                                    Process.GetCurrentProcess().Id);
                            result = StartupStatusContract.CreateSnapshot(
                                reactorReady: true,
                                nativeBridgeReady: true,
                                providerConnected: true,
                                allIn1Loaded: true,
                                defaultMenuRequested:
                                    LastStartupDefaultMenuRequested);
                            break;
                        case "game.getState":
                            result = GameState();
                            break;
                        case "events.subscribe":
                            var subscriptionId = "gbay-" + Guid.NewGuid().ToString("N");
                            _subscriptions.Add(subscriptionId);
                            SubscriptionCount++;
                            result = new JObject
                            {
                                ["id"] = subscriptionId,
                                ["events"] = request.Parameters["events"]?.DeepClone() ?? new JArray(),
                            };
                            break;
                        case "events.unsubscribe":
                            result = new JObject
                            {
                                ["removed"] = _subscriptions.Remove(
                                    request.Parameters.Value<string>("subscriptionId") ?? string.Empty),
                            };
                            break;
                        case "menu.get":
                            if (request.Parameters.Value<string>("extensionId") != "allin1.gbay")
                                throw new InvalidOperationException("Unexpected menu identity.");
                            var requestedMenu =
                                request.Parameters.Value<string>("menuId") ?? string.Empty;
                            if (!KnownMenu(requestedMenu, _includeAbout))
                                throw new InvalidOperationException(
                                    $"Unexpected GBAY menu '{requestedMenu}'.");
                            MenuGetCount++;
                            result = MenuDescriptor(requestedMenu, _includeAbout);
                            break;
                        case "menu.invoke":
                            var extensionId =
                                request.Parameters.Value<string>("extensionId") ?? string.Empty;
                            var menuId = request.Parameters.Value<string>("menuId") ?? string.Empty;
                            var nodeId = request.Parameters.Value<string>("nodeId") ?? string.Empty;
                            var interaction =
                                request.Parameters.Value<string>("interaction") ?? string.Empty;
                            if (extensionId != "allin1.gbay" ||
                                !NodeMenus.TryGetValue(nodeId, out var ownerMenu) ||
                                ownerMenu != menuId || interaction != "activate")
                                throw new InvalidOperationException(
                                    "The route matrix received an untyped or mismatched menu invocation.");
                            if (request.Parameters["parameters"] is JObject browserParameters &&
                                browserParameters.Count > 0)
                                throw new InvalidOperationException(
                                    "Host-bound fixture parameters leaked into the browser invocation.");
                            MenuInvokeCount++;
                            TypedInvocationCount++;
                            LastInvokedNode = nodeId;
                            _invokedNodes.Add(LastInvokedNode);
                            _invocationCounts[LastInvokedNode] = InvocationCount(LastInvokedNode) + 1;
                            result = new JObject
                            {
                                ["succeeded"] = true,
                                ["confirmationRequired"] = false,
                                ["replayed"] = false,
                                ["value"] = new JObject { ["presentation"] = "keep-open" },
                            };
                            break;
                        case "overlay.presentationReady":
                            var readyPresentation =
                                request.Parameters.Value<string>("presentationId") ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(readyPresentation))
                                throw new InvalidOperationException("Presentation id is required.");
                            var accepted = string.Equals(
                                readyPresentation,
                                ExpectedPresentation,
                                StringComparison.Ordinal);
                            if (accepted)
                            {
                                ExactPresentationReadyCount++;
                                LastAcceptedPresentation = readyPresentation;
                            }
                            else
                            {
                                StalePresentationReadyCount++;
                            }
                            result = new JObject
                            {
                                ["presentationId"] = readyPresentation,
                                ["accepted"] = accepted,
                            };
                            break;
                        case "overlay.close":
                            OverlayCloseCount++;
                            _setVisible(false);
                            result = new JObject { ["visible"] = false };
                            break;
                        default:
                            result = new JObject { ["ok"] = true };
                            break;
                    }
                    return BridgeResponse.Success(request.Id, result, request.ProtocolVersion);
                }
                catch (Exception error)
                {
                    return BridgeResponse.Failure(
                        request.Id,
                        new BridgeError("gbay_harness_error", error.Message),
                        request.ProtocolVersion);
                }
            }

            public void Dispose()
            {
                _holdNextMenuGet = false;
                _heldMenuGet = null;
                _subscriptions.Clear();
                _invokedNodes.Clear();
                _invocationCounts.Clear();
            }

            public static JObject RuntimeStatus() => new JObject
            {
                ["apiVersion"] = 2,
                ["supportedApiVersions"] = new JArray(1, 2),
                ["sessionId"] = StartupTrace.SessionId,
                ["runtime"] = "GBAY lifecycle harness",
                ["runtimeVersion"] = "0.2.0",
                ["renderer"] = "WebView2 window",
                ["edition"] = "Enhanced",
                ["dependencies"] = new JArray(),
            };

            public static JObject GameState() => new JObject
            {
                ["gameTime"] = 42420,
                ["paused"] = false,
                ["player"] = new JObject
                {
                    ["health"] = 200,
                    ["maxHealth"] = 200,
                    ["armor"] = 50,
                    ["wantedLevel"] = 0,
                    ["invincible"] = false,
                    ["position"] = new JObject { ["x"] = 0, ["y"] = 0, ["z"] = 0 },
                    ["heading"] = 0,
                },
                ["vehicle"] = JValue.CreateNull(),
                ["world"] = new JObject { ["time"] = "12:00", ["weather"] = "Clear" },
            };

            private static bool KnownMenu(string menuId, bool includeAbout) =>
                menuId == "home" || NodeMenus.Values.Contains(menuId) &&
                (includeAbout || menuId != "about");

            private static JObject MenuDescriptor(string menuId, bool includeAbout)
            {
                if (menuId == "home") return HomeDescriptor(includeAbout);
                return menuId switch
                {
                    "vehicles" => WithPreviewAsset(
                        SectionDescriptor(
                            "vehicles", "Purchase Vehicles", "vehicle", "vehicle-alpha",
                            "Transit Bus", "$125,000 · Available · Commercial",
                            "vehicle.purchase", "listingId", "fixture-vehicle-alpha"),
                        "vehicle-alpha-preview",
                        "Transit Bus preview",
                        "allin1-logo.png"),
                    "weapons" => SectionDescriptor(
                        "weapons", "Purchase Weapons", "weapon", "weapon-alpha",
                        "Carbine Rifle", "Price: $13,000 · Ownership: Available · Category: Rifles",
                        "weapon.purchase", "weaponId", "fixture-weapon-alpha"),
                    "weapons.customize" => SectionDescriptor(
                        "weapons.customize", "Customize Weapons", "customize",
                        "owned-weapon-alpha", "Owned Carbine Rifle",
                        "Category: Rifles · Ammo: 240", "weapon.customize.select",
                        "weaponHash", "0x83BF0278"),
                    "gear" => SectionDescriptor(
                        "gear", "Gear", "gear", "gear-alpha", "Heavy Armor",
                        "Category: Armor · Status: Equip · Price: $500", "gear.apply",
                        "gearId", "armor-heavy"),
                    "garage" => SectionDescriptor(
                        "garage", "My Garage", "garage", "stored-alpha", "Elegy RH8",
                        "Location: Harmony · Plate: GBAY · Retrieve", "garage.retrieve",
                        "vehicleId", "fixture-stored-alpha"),
                    "addons" => SectionDescriptor(
                        "addons", "Add-ons", "package", "addon-alpha", "Inspect package state",
                        "Read the current enabled package state.", "addons.inspect",
                        "scope", "enabled"),
                    "diagnostics" => SectionDescriptor(
                        "diagnostics", "Diagnostics", "health", "diagnostics-alpha",
                        "Inspect runtime status", "Read-only dependency and package verification.",
                        "diagnostics.inspect", "scope", "allin1"),
                    "about" when includeAbout => SectionDescriptor(
                        "about", "About", "information", "about-alpha",
                        "Copy build identity", "Read the current fixture build identity.",
                        "about.copy", "field", "build"),
                    _ => throw new InvalidOperationException($"Unknown GBAY menu '{menuId}'."),
                };
            }

            private static JObject HomeDescriptor(bool includeAbout)
            {
                var nodes = new JArray
                {
                    Node("balance", "status", "Balance", new JObject
                    {
                        ["value"] = "$2,450,000",
                        ["tone"] = "success",
                    }),
                    Submenu("open-vehicles", "Vehicles", "vehicles"),
                    Submenu("open-weapons", "Weapons", "weapons"),
                    Submenu("open-customization", "Customize", "weapons.customize"),
                    Submenu("open-gear", "Gear", "gear"),
                    Submenu("open-garage", "My Garage", "garage"),
                    Submenu("open-addons", "Add-ons", "addons"),
                    Submenu("open-diagnostics", "Diagnostics", "diagnostics"),
                };
                if (includeAbout) nodes.Add(Submenu("open-about", "About", "about"));
                return Descriptor(
                    "home",
                    "GBAY Home",
                    "ALLIN1 Story Mode marketplace and services",
                    "home",
                    nodes);
            }

            private static JObject SectionDescriptor(
                string menuId,
                string label,
                string icon,
                string nodeId,
                string nodeLabel,
                string description,
                string actionId,
                string parameterName,
                string parameterValue) =>
                Descriptor(
                    menuId,
                    label,
                    description,
                    icon,
                    new JArray
                    {
                        Node(nodeId, "action", nodeLabel, new JObject
                        {
                            ["description"] = description,
                            ["actionId"] = actionId,
                            ["boundParameters"] = new JObject
                            {
                                [parameterName] = parameterValue,
                            },
                        }),
                    });

            private static JObject WithPreviewAsset(
                JObject descriptor,
                string id,
                string label,
                string source)
            {
                var nodes = descriptor["nodes"] as JArray ?? throw new InvalidOperationException(
                    "The fixture menu descriptor did not contain a node array.");
                nodes.Add(Node(id, "media", label, new JObject
                {
                    ["source"] = source,
                    ["mediaType"] = "image/png",
                    ["alternativeText"] = label,
                }));
                return descriptor;
            }

            private static JObject Descriptor(
                string id,
                string label,
                string description,
                string icon,
                JArray nodes) => new JObject
                {
                    ["extensionId"] = "allin1.gbay",
                    ["id"] = id,
                    ["label"] = label,
                    ["description"] = description,
                    ["icon"] = icon,
                    ["order"] = 0,
                    ["nodes"] = nodes,
                };

            private static JObject Submenu(string id, string label, string menuId) =>
                Node(id, "submenu", label, new JObject
                {
                    ["description"] = $"Open {label}.",
                    ["menuId"] = menuId,
                });

            private static JObject Node(string id, string kind, string label, JObject detail)
            {
                var node = new JObject
                {
                    ["id"] = id,
                    ["kind"] = kind,
                    ["label"] = label,
                    ["description"] = string.Empty,
                    ["enabled"] = true,
                    ["visible"] = true,
                };
                node.Merge(detail);
                return node;
            }

        }
    }
}
