using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using ReactorV.BootstrapHost;

namespace RageWebUI.Harness
{
    /// <summary>
    /// One-shot, opt-in acceptance runner for a real GTA session. The runner
    /// must be started before GTA. It never starts, closes, or modifies GTA;
    /// it only sends the same bounded F9/mouse inputs a tester would send and
    /// observes process-scoped Reactor events and logs.
    /// </summary>
    internal static class LiveAcceptanceHarness
    {
        private const int VirtualKeyF9 = 0x78;
        private const int VirtualKeyUp = 0x26;
        private const int VirtualKeyDown = 0x28;
        private const int VirtualKeyEnter = 0x0D;
        private const int VirtualKeyEscape = 0x1B;
        private const int InputKeyboard = 1;
        private const int InputMouse = 0;
        private const uint KeyUp = 0x0002;
        private const uint MouseMove = 0x0001;
        private const uint MouseLeftDown = 0x0002;
        private const uint MouseLeftUp = 0x0004;
        private const uint MouseAbsolute = 0x8000;
        private const int ScreenWidth = 0;
        private const int ScreenHeight = 1;
        private const string ArmMutexName = @"Local\ReactorV.LiveAcceptance.Arm";
        private static readonly string[] InstalledPayloadPaths =
        {
            "ReactorV.Bootstrap.asi",
            @"scripts\ReactorV\RageWebUI.Script.dll",
            @"scripts\ReactorV\ReactorV.contract.json",
            @"scripts\ReactorV\ReactorV.json",
            @"plugins\ReactorV\ReactorV.Preloader.exe",
            @"plugins\ReactorV\RageWebUI.Runtime.dll",
            @"plugins\ReactorV\ui\index.html",
        };
        private static readonly Regex ProcessIdPattern = new Regex(
            @"\bpid=(?<pid>[1-9][0-9]*)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MenuPresentationPattern = new Regex(
            @"\bpresentation=(?<presentation>[A-Za-z0-9][A-Za-z0-9._:-]{0,127})\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static int Run(HarnessOptions options)
        {
            var localData = options.LocalDataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ReactorV");
            var runId = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyyMMddTHHmmssfffZ}-{1}-{2}",
                DateTime.UtcNow,
                Process.GetCurrentProcess().Id,
                Guid.NewGuid().ToString("N").Substring(0, 8));
            var outputDirectory = Path.Combine(localData, "Acceptance", "Runs", runId);
            Directory.CreateDirectory(outputDirectory);
            var receiptPath = options.LiveReceiptPath ??
                Path.Combine(outputDirectory, "receipt.json");
            var armDirectory = Path.Combine(localData, "Acceptance");
            Directory.CreateDirectory(armDirectory);
            var armPath = Path.Combine(armDirectory, "armed.json");
            var runStartedUtc = DateTime.UtcNow;
            var proof = new LiveAcceptanceProof();
            var lifecycle = new LiveAcceptanceSurfaceLifecycleReceipt(runId);
            lifecycle.MarkStartup(
                LiveAcceptanceStartupMilestone.HarnessArmed,
                runStartedUtc);
            LiveAcceptanceLifecycleIdentity? storyLifecycleIdentity = null;
            LiveAcceptanceLifecycleIdentity? menuLifecycleIdentity = null;
            var receipt = new JObject
            {
                ["schemaVersion"] = LiveAcceptanceContract.SchemaVersion,
                ["scenario"] = LiveAcceptanceContract.Scenario,
                ["runId"] = runId,
                ["startedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["launchesGta"] = false,
                ["liveSessionObserved"] = false,
                ["status"] = "running",
                ["steps"] = new JArray(),
                ["artifacts"] = new JObject(),
                ["installedPayload"] = new JArray(),
                ["routes"] = new JArray(),
                ["surfaceModes"] = new JArray(),
                ["pointerPairs"] = new JArray(),
                ["sectionNavigation"] = new JArray(),
                ["semanticInput"] = new JArray(),
                ["nativeCustomizer"] = new JArray(),
                ["foregroundTimeline"] = new JArray(),
                ["inputEdgeTimeline"] = new JArray(),
                ["visualCaptures"] = new JArray(),
                ["visualCaptureFailures"] = new JArray(),
                ["desktopIdentityBrackets"] = new JArray(),
                ["coverageBoundaries"] = new JObject
                {
                    ["syntheticAndPixelChecks"] =
                        "plumbing-only: pointer forwarding, screenshots, and pixel deltas do not prove the selected provider route or its payload; each top-level click separately requires a native-bound semantic menu-state observation",
                    ["confirmationCancelAccept"] =
                        "not automated by this runner; verify cancel and accept manually with a safe operation because the live runner never confirms a persistent GTA mutation",
                    ["controllerHardware"] =
                        "the shared GTA semantic-input path is exercised with keyboard controls; a physical controller remains a tester-observed boundary",
                },
            };

            using var armMutex = new Mutex(true, ArmMutexName, out var ownsArm);
            if (!ownsArm)
            {
                receipt["status"] = "blocked";
                receipt["failure"] = "another_live_acceptance_run_is_armed";
                WriteLifecycleReceipt(receipt, lifecycle);
                WriteReceipt(receiptPath, receipt);
                Console.Error.WriteLine("Another Reactor live acceptance run is already armed.");
                return 8;
            }

            // A process-owned mutex is authoritative. If the previous runner
            // was terminated, Windows releases the mutex automatically and a
            // later run may safely replace only that stale advisory marker.
            if (File.Exists(armPath))
            {
                try { File.Delete(armPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            File.WriteAllText(
                armPath,
                new JObject
                {
                    ["schemaVersion"] = LiveAcceptanceContract.SchemaVersion,
                    ["scenario"] = LiveAcceptanceContract.Scenario,
                    ["runId"] = runId,
                    ["harnessPid"] = Process.GetCurrentProcess().Id,
                    ["armedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["commands"] = new JArray(LiveAcceptanceContract.OrderedCommands),
                }.ToString(Formatting.Indented));

            var nativeLog = new LogTail(Path.Combine(localData, "reactorv-native-bootstrap.log"));
            SessionLogTail? preloaderLog = null;
            var runtimeLog = new SessionLogTail(localData, runStartedUtc, processId: null);
            Process? gta = null;
            ForegroundJournal? foreground = null;
            LogTail? allin1Log = null;
            try
            {
                Console.WriteLine("REACTOR V live acceptance is ARMED. It will not launch GTA.");
                Console.WriteLine("Launch GTA normally and leave it foreground. Select Story Mode when the main-menu About check finishes.");
                gta = RunStep(
                    receipt,
                    "wait-for-gta",
                    "process.wait",
                    () => WaitForGta(options.LiveProcessTimeout, runStartedUtc),
                    process => $"pid={process.Id} process={process.ProcessName}");
                receipt["gtaProcessId"] = gta.Id;
                receipt["gtaProcess"] = gta.ProcessName;
                receipt["gtaProcessStartedUtc"] = SafeStartTime(gta).ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture);
                lifecycle.MarkStartup(
                    LiveAcceptanceStartupMilestone.GtaProcessObserved,
                    DateTimeOffset.UtcNow);
                runtimeLog.BindProcess(gta.Id);

                var gameWindow = RunStep(
                    receipt,
                    "gta-main-window",
                    "process.window.wait",
                    () => WaitForMainWindow(gta, options.LiveProcessTimeout),
                    window => $"hwnd=0x{window.ToInt64():X} pid={gta.Id}");
                proof.FreshGtaProcessObserved = true;
                proof.GtaMainWindowObserved = gameWindow != IntPtr.Zero;
                lifecycle.MarkStartup(
                    LiveAcceptanceStartupMilestone.GtaWindowObserved,
                    DateTimeOffset.UtcNow);
                var gameWindowBinding = new LiveAcceptanceWindowBinding(gameWindow);
                var visualCapture = new LiveAcceptanceWindowCaptureSession(
                    gameWindowBinding,
                    (JArray)receipt["visualCaptures"]!,
                    outputDirectory,
                    runId,
                    gta.Id);
                receipt["liveSessionObserved"] = true;
                receipt["gtaMainWindow"] = $"0x{gameWindow.ToInt64():X}";
                foreground = new ForegroundJournal(
                    (JArray)receipt["foregroundTimeline"]!,
                    gta.Id);
                foreground.Observe("gta-window-observed", force: true);

                var installed = RunStep(
                    receipt,
                    "installed-payload-hashes",
                    "payload.hash.wait",
                    () => HashInstalledPayload(gta),
                    hashes => $"files={hashes.Count}");
                foreach (var item in installed)
                    ((JArray)receipt["installedPayload"]!).Add(item);
                proof.InstalledHashCount = installed.Count;
                allin1Log = new LogTail(
                    Path.Combine(ResolveGtaRoot(gta), "ALLIN1_client.log"));

                var preloaderStarted = RunStep(
                    receipt,
                    "preloader-process",
                    "preloader.process.wait",
                    () =>
                    {
                        var line = nativeLog.WaitFor(
                            candidate => candidate.Contains("stage=preloader_started ") &&
                                ProcessIdPattern.IsMatch(candidate),
                            options.LiveStepTimeout,
                            gta);
                        Require(line != null, "The native bootstrap did not report the preloader PID.");
                        var match = ProcessIdPattern.Match(line!);
                        Require(int.TryParse(
                            match.Groups["pid"].Value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var preloaderPid) && preloaderPid > 0,
                            "The native preloader PID was malformed.");
                        return new KeyValuePair<int, string>(preloaderPid, line!);
                    },
                    value => value.Value);
                preloaderLog = new SessionLogTail(
                    localData,
                    runStartedUtc,
                    preloaderStarted.Key);
                receipt["preloaderProcessId"] = preloaderStarted.Key;

                RunStep(
                    receipt,
                    "bootstrap-host-ready",
                    "bootstrap.ready.wait",
                    () =>
                    {
                        Require(
                            WaitForNamedEventSet(
                                BootstrapHostNames.ReadyEvent(gta.Id),
                                options.LiveStepTimeout,
                                gta),
                            "The process-scoped bootstrap host did not become ready.");
                        return "ready-event-set";
                    });

                RunStep(
                    receipt,
                    "frontend-about",
                    LiveAcceptanceContract.FrontendAboutToggle,
                    () =>
                    {
                        Require(WaitForForeground(gta, options.LiveProcessTimeout, foreground),
                            "GTA did not become the foreground window for the frontend check.");
                        CaptureArtifact(
                            visualCapture,
                            outputDirectory,
                            receipt,
                            proof,
                            LiveAcceptanceVisualExpectation.EvidenceOnly,
                            "frontend-route-probe-initial.png");
                        var routeLine = WaitForFrontendAboutRoute(
                            gta,
                            nativeLog,
                            preloaderLog!,
                            options.LiveProcessTimeout,
                            options.LiveStepTimeout,
                            foreground);
                        RecordRoute(receipt, routeLine, "frontend-about-open");
                        proof.AboutRouteObserved = true;
                        var ready = preloaderLog!.WaitFor(
                            line => line.Contains("stage=bootstrap_host_surface_ready ") &&
                                line.Contains("mode=about"),
                            options.LiveStepTimeout,
                            gta);
                        Require(ready != null, "The About surface did not acknowledge a painted generation.");
                        RecordSurface(receipt, ready!, proof);
                        visualCapture.ObserveSurfaceReady(ready!);
                        Require(
                            LiveAcceptanceContract.TryParseSurfaceReady(
                                ready,
                                out var aboutSurface),
                            "The About surface generation could not be bound to the lifecycle receipt.");
                        var aboutIdentity = new LiveAcceptanceLifecycleIdentity(
                            aboutSurface.Generation,
                            null);
                        Require(
                            lifecycle.TryAdvance(
                                LiveAcceptanceSurfaceLifecycleState.FrontendAbout,
                                aboutIdentity,
                                DateTimeOffset.UtcNow,
                                LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                                ready!,
                                out var lifecycleFailure),
                            "The About lifecycle transition was rejected: " +
                                lifecycleFailure + ".");
                        CaptureArtifact(
                            visualCapture,
                            outputDirectory,
                            receipt,
                            proof,
                            LiveAcceptanceVisualExpectation.ReactorAbout,
                            "frontend-about-open-1.png",
                            aboutIdentity);
                        RecordLifecycleDesktopEvidence(
                            lifecycle,
                            aboutIdentity,
                            "frontend-about-open-1-desktop.png");
                        return ready!;
                    });

                RunStep(
                    receipt,
                    "frontend-about-close",
                    LiveAcceptanceContract.FrontendAboutClose,
                    () =>
                    {
                        Require(WaitForForeground(gta, options.LiveStepTimeout, foreground),
                            "GTA was not foreground for the About close check.");
                        SendF9(foreground, "frontend-about-close-1");
                        var routeLine = WaitForBootstrapRoute(nativeLog, options.LiveStepTimeout, gta);
                        RecordRoute(receipt, routeLine, "frontend-about-close-1");
                        var closed = WaitForAboutClose(
                            preloaderLog!,
                            options.LiveStepTimeout,
                            gta,
                            "A second F9 did not fully close the frontend About surface.");
                        CaptureArtifact(
                            visualCapture,
                            outputDirectory,
                            receipt,
                            proof,
                            LiveAcceptanceVisualExpectation.EvidenceOnly,
                            "frontend-about-closed-1.png");
                        return closed;
                    });

                RunStep(
                    receipt,
                    "frontend-about-reopen",
                    LiveAcceptanceContract.FrontendAboutToggle,
                    () =>
                    {
                        Require(WaitForForeground(gta, options.LiveStepTimeout, foreground),
                            "GTA was not foreground for the About reopen check.");
                        SendF9(foreground, "frontend-about-open-2");
                        var routeLine = WaitForBootstrapRoute(nativeLog, options.LiveStepTimeout, gta);
                        Require(
                            LiveAcceptanceContract.ClassifyBootstrapRoute(routeLine) == LiveAcceptanceRoute.About,
                            "The second About open routed to the wrong surface.");
                        RecordRoute(receipt, routeLine, "frontend-about-open-2");
                        var ready = preloaderLog!.WaitFor(
                            line => line.Contains("stage=bootstrap_host_surface_ready ") &&
                                line.Contains("mode=about"),
                            options.LiveStepTimeout,
                            gta);
                        Require(ready != null, "The second About open did not paint.");
                        RecordSurface(receipt, ready!, proof);
                        visualCapture.ObserveSurfaceReady(ready!);
                        CaptureArtifact(
                            visualCapture,
                            outputDirectory,
                            receipt,
                            proof,
                            LiveAcceptanceVisualExpectation.ReactorAbout,
                            "frontend-about-open-2.png");
                        return ready!;
                    });

                RunStep(
                    receipt,
                    "frontend-about-final-close",
                    LiveAcceptanceContract.FrontendAboutClose,
                    () =>
                    {
                        Require(WaitForForeground(gta, options.LiveStepTimeout, foreground),
                            "GTA was not foreground for the final About close check.");
                        SendF9(foreground, "frontend-about-close-2");
                        var routeLine = WaitForBootstrapRoute(nativeLog, options.LiveStepTimeout, gta);
                        RecordRoute(receipt, routeLine, "frontend-about-close-2");
                        var closed = WaitForAboutClose(
                            preloaderLog!,
                            options.LiveStepTimeout,
                            gta,
                            "The second About cycle did not fully close with F9.");
                        CaptureArtifact(
                            visualCapture,
                            outputDirectory,
                            receipt,
                            proof,
                            LiveAcceptanceVisualExpectation.EvidenceOnly,
                            "frontend-about-closed-2.png");
                        return closed;
                    });

                Console.WriteLine("Frontend About PASS. Select Story Mode normally; the runner will wait for an objective Story transition before sending one early F9 probe.");
                RunStep(
                    receipt,
                    "story-transition",
                    "story.transition.wait",
                    () => WaitForStoryTransition(
                        gta,
                        nativeLog,
                        preloaderLog!,
                        options.LiveProcessTimeout),
                    value => value);
                proof.StoryTransitionObserved = true;
                var initializer = RunStep(
                    receipt,
                    "story-early-preloader",
                    LiveAcceptanceContract.StoryEarlyMenuToggle,
                    () => WaitForInitializerRoute(
                        gta,
                        nativeLog,
                        preloaderLog!,
                        options.LiveProcessTimeout,
                        options.LiveStepTimeout,
                        foreground),
                    value => value.Key + " | " + value.Value);
                RecordRoute(receipt, initializer.Key, "story-early-preloader");
                RecordSurface(receipt, initializer.Value, proof);
                visualCapture.ObserveSurfaceReady(initializer.Value);
                Require(
                    LiveAcceptanceContract.TryParseSurfaceReady(
                        initializer.Value,
                        out var initializerSurface),
                    "The Story initializer generation could not be bound to the lifecycle receipt.");
                storyLifecycleIdentity = new LiveAcceptanceLifecycleIdentity(
                    initializerSurface.Generation,
                    null);
                Require(
                    lifecycle.TryAdvance(
                        LiveAcceptanceSurfaceLifecycleState.StoryInitializing,
                        storyLifecycleIdentity,
                        DateTimeOffset.UtcNow,
                        LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                        initializer.Value,
                        out var storyLifecycleFailure),
                    "The Story initializer lifecycle transition was rejected: " +
                        storyLifecycleFailure + ".");
                proof.InitializerRouteObserved = true;
                proof.EarlyInitializerBeforeProviderObserved = true;
                CaptureArtifact(
                    visualCapture,
                    outputDirectory,
                    receipt,
                    proof,
                    LiveAcceptanceVisualExpectation.Allin1Preloader,
                    "story-preloader.png",
                    storyLifecycleIdentity);
                RecordLifecycleDesktopEvidence(
                    lifecycle,
                    storyLifecycleIdentity,
                    "story-preloader-desktop.png");

                var providerHandoff = RunStep(
                    receipt,
                    "provider-handoff",
                    "provider.handoff.wait",
                    () =>
                    {
                        var connected = preloaderLog!.WaitFor(
                            line => line.Contains("stage=bootstrap_host_provider_ready "),
                            options.LiveProcessTimeout,
                            gta);
                        Require(connected != null, "The managed provider did not connect to the bootstrap host.");
                        var menuReady = runtimeLog.WaitFor(
                            line => line.Contains("stage=menu_presentation_ready "),
                            options.LiveProcessTimeout,
                            gta);
                        Require(menuReady != null, "GBAY never reached its typed presentation-ready boundary.");
                        return new KeyValuePair<string, string>(connected!, menuReady!);
                    },
                    value => value.Key + " | " + value.Value);
                Require(storyLifecycleIdentity != null,
                    "The provider connected without a Story lifecycle identity.");
                Require(
                    lifecycle.TryAdvance(
                        LiveAcceptanceSurfaceLifecycleState.ProviderReady,
                        storyLifecycleIdentity!,
                        DateTimeOffset.UtcNow,
                        LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                        providerHandoff.Key,
                        out var providerLifecycleFailure),
                    "The provider-ready lifecycle transition was rejected: " +
                        providerLifecycleFailure + ".");
                var presentationId = ParseMenuPresentationId(providerHandoff.Value);
                menuLifecycleIdentity = new LiveAcceptanceLifecycleIdentity(
                    storyLifecycleIdentity!.Generation,
                    presentationId);
                Require(
                    lifecycle.TryAdvance(
                        LiveAcceptanceSurfaceLifecycleState.MenuPendingPaint,
                        menuLifecycleIdentity,
                        DateTimeOffset.UtcNow,
                        LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                        providerHandoff.Value,
                        out var pendingLifecycleFailure),
                    "The menu-pending lifecycle transition was rejected: " +
                        pendingLifecycleFailure + ".");
                CaptureArtifact(
                    visualCapture,
                    outputDirectory,
                    receipt,
                    proof,
                    LiveAcceptanceVisualExpectation.GbayMenu,
                    "provider-menu-ready.png",
                    menuLifecycleIdentity);
                RecordLifecycleBrowserEvidence(
                    lifecycle,
                    menuLifecycleIdentity,
                    "provider-menu-ready.png");
                Require(
                    lifecycle.TryAdvance(
                        LiveAcceptanceSurfaceLifecycleState.MenuInteractive,
                        menuLifecycleIdentity,
                        DateTimeOffset.UtcNow,
                        LiveAcceptanceLifecycleEvidenceSource.DesktopPixels,
                        "provider-menu-ready-desktop.png",
                        out var interactiveLifecycleFailure),
                    "The menu-interactive lifecycle transition was rejected: " +
                        interactiveLifecycleFailure + ".");

                RunStep(
                    receipt,
                    "top-level-section-matrix",
                    LiveAcceptanceContract.MenuSectionMatrix,
                    () =>
                    {
                        Require(WaitForForeground(gta, options.LiveStepTimeout, foreground),
                            "GTA was not foreground for the section matrix.");
                        Require(TryGetClientBounds(gameWindowBinding.Handle, out var bounds),
                            "Could not resolve the GTA client bounds.");
                        var deviceScale = ResolveWindowDeviceScale(gameWindowBinding.Handle);
                        var orderedTargets = LiveAcceptanceContract.TopLevelSections
                            .Skip(1)
                            .Concat(LiveAcceptanceContract.TopLevelSections.Take(1))
                            .ToArray();
                        foreach (var target in orderedTargets)
                        {
                            var point = LiveAcceptanceContract.ResolveSectionPoint(
                                target,
                                bounds.Width,
                                bounds.Height,
                                deviceScale);
                            var beforePath = Path.Combine(
                                outputDirectory,
                                $"section-{target.Id}-before.png");
                            var afterPath = Path.Combine(
                                outputDirectory,
                                $"section-{target.Id}-after.png");
                            using var before = visualCapture.Capture(
                                $"section-{target.Id}-before",
                                LiveAcceptanceVisualExpectation.GbayMenu);
                            before.Save(beforePath, ImageFormat.Png);
                            proof.ScreenshotCount++;
                            var foregroundStart = foreground!.Count;
                            Click(bounds, point.X, point.Y, foreground);
                            var pointer = WaitForPointerPair(
                                preloaderLog!,
                                options.LiveStepTimeout,
                                gta);
                            Require(
                                Math.Abs(pointer.Down.X - point.X) <= 0.05 &&
                                    Math.Abs(pointer.Down.Y - point.Y) <= 0.05,
                                $"The paired native click missed the requested {target.Id} section point.");
                            foreground.Observe(
                                "section-pointer-" + target.Id,
                                force: true);
                            var foreign = foreground.HasForeignProcessFrom(foregroundStart);
                            proof.ForeignForegroundDuringPointer |= foreign;
                            Require(!foreign,
                                $"The {target.Id} section click transferred foreground away from GTA.");
                            RecordPointerPair(receipt, pointer, target.Id);
                            RecordLifecyclePointerPair(
                                lifecycle,
                                receipt,
                                pointer,
                                foreground,
                                gta.Id,
                                target.Id);
                            proof.PointerPairObserved = true;
                            proof.PointerPairCount++;
                            var sectionState = WaitForSectionState(
                                preloaderLog!,
                                target,
                                options.LiveStepTimeout,
                                gta);
                            Require(
                                LiveAcceptanceContract.TryValidateSectionIdentity(
                                    target,
                                    sectionState.State,
                                    out var identityFailure),
                                $"The {target.Id} section reported the wrong provider/menu/route identity: " +
                                identityFailure + ".");
                            proof.TopLevelSectionIdentityCount++;
                            Require(
                                LiveAcceptanceContract.TryValidateSectionPayload(
                                    target,
                                    sectionState.State,
                                    out var payloadFailure),
                                $"The {target.Id} section did not expose a ready, meaningful payload: " +
                                payloadFailure + ".");
                            proof.TopLevelSectionPayloadCount++;
                            Thread.Sleep(700);
                            using var after = visualCapture.Capture(
                                $"section-{target.Id}-after",
                                LiveAcceptanceVisualExpectation.GbayMenu);
                            after.Save(afterPath, ImageFormat.Png);
                            proof.ScreenshotCount++;
                            var changed = ChangedFraction(before, after);
                            Require(changed >= 0.003,
                                $"The {target.Id} section click produced no meaningful panel change ({changed:P3}).");
                            proof.SectionPlumbingCount++;
                            ((JArray)receipt["sectionNavigation"]!).Add(new JObject
                            {
                                ["section"] = target.Id,
                                ["providerId"] = sectionState.State.ProviderId,
                                ["rootMenuId"] = sectionState.State.RootMenuId,
                                ["menuId"] = sectionState.State.MenuId,
                                ["routeId"] = sectionState.State.RouteId,
                                ["payloadStatus"] = sectionState.State.PayloadStatus,
                                ["itemCount"] = sectionState.State.ItemCount,
                                ["contentItemCount"] = sectionState.State.ContentItemCount,
                                ["actionableItemCount"] = sectionState.State.ActionableItemCount,
                                ["statusItemCount"] = sectionState.State.StatusItemCount,
                                ["semanticEvidence"] = sectionState.Line,
                                ["plumbingEvidence"] = "paired native pointer plus shell pixel delta",
                                ["normalizedX"] = point.X,
                                ["normalizedY"] = point.Y,
                                ["changedFraction"] = changed,
                                ["before"] = beforePath,
                                ["after"] = afterPath,
                            });
                            proof.TopLevelSectionCount++;
                            ObservePauseState(runtimeLog, proof);
                        }
                        Require(
                            proof.TopLevelSectionCount ==
                                LiveAcceptanceContract.RequiredTopLevelSectionCount,
                            "The complete top-level section matrix was not exercised.");
                        return $"sections={proof.TopLevelSectionCount} " +
                            $"identities={proof.TopLevelSectionIdentityCount} " +
                            $"payloads={proof.TopLevelSectionPayloadCount} " +
                            $"plumbing={proof.SectionPlumbingCount} " +
                            $"pointer_pairs={proof.PointerPairCount}";
                    });

                RunStep(
                    receipt,
                    "semantic-keyboard-and-back-close",
                    LiveAcceptanceContract.MenuSemanticNavigation,
                    () =>
                    {
                        Require(WaitForForeground(gta, options.LiveStepTimeout, foreground),
                            "GTA was not foreground for semantic navigation.");
                        var down = SendSemanticKeyAndWait(
                            gta,
                            runtimeLog,
                            VirtualKeyDown,
                            "navigate-down",
                            options.LiveStepTimeout,
                            foreground);
                        proof.SemanticNavigationObserved = true;
                        ObservePauseState(runtimeLog, proof);
                        var up = SendSemanticKeyAndWait(
                            gta,
                            runtimeLog,
                            VirtualKeyUp,
                            "navigate-up",
                            options.LiveStepTimeout,
                            foreground);
                        ObservePauseState(runtimeLog, proof);
                        SendSemanticKeyAndWait(
                            gta,
                            runtimeLog,
                            VirtualKeyDown,
                            "navigate-down",
                            options.LiveStepTimeout,
                            foreground);
                        var accept = SendSemanticKeyAndWait(
                            gta,
                            runtimeLog,
                            VirtualKeyEnter,
                            "accept",
                            options.LiveStepTimeout,
                            foreground);
                        proof.SemanticAcceptObserved = true;
                        ObservePauseState(runtimeLog, proof);
                        Thread.Sleep(500);
                        CaptureArtifact(
                            visualCapture,
                            outputDirectory,
                            receipt,
                            proof,
                            LiveAcceptanceVisualExpectation.GbayMenu,
                            "semantic-accept-route.png");

                        var backToHome = SendSemanticKeyAndWait(
                            gta,
                            runtimeLog,
                            VirtualKeyEscape,
                            "back",
                            options.LiveStepTimeout,
                            foreground);
                        proof.SemanticBackObserved = true;
                        ObservePauseState(runtimeLog, proof);
                        var backClose = SendSemanticKeyAndWait(
                            gta,
                            runtimeLog,
                            VirtualKeyEscape,
                            "back",
                            options.LiveStepTimeout,
                            foreground);
                        var hidden = runtimeLog.WaitFor(
                            line => line.Contains("stage=overlay_hide_requested "),
                            options.LiveStepTimeout,
                            gta);
                        Require(hidden != null,
                            "Semantic Back did not close GBAY from its home route.");
                        Require(menuLifecycleIdentity != null,
                            "The first menu close had no lifecycle identity.");
                        Require(
                            lifecycle.TryAdvance(
                                LiveAcceptanceSurfaceLifecycleState.Closing,
                                menuLifecycleIdentity!,
                                DateTimeOffset.UtcNow,
                                LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                                backClose,
                                out var closingLifecycleFailure),
                            "The menu-closing lifecycle transition was rejected: " +
                                closingLifecycleFailure + ".");
                        Require(
                            lifecycle.TryAdvance(
                                LiveAcceptanceSurfaceLifecycleState.Closed,
                                menuLifecycleIdentity!,
                                DateTimeOffset.UtcNow,
                                LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace,
                                hidden!,
                                out var closedLifecycleFailure),
                            "The menu-closed lifecycle transition was rejected: " +
                                closedLifecycleFailure + ".");
                        proof.BackCloseObserved = true;
                        proof.OpenCloseCycles++;
                        ObservePauseState(runtimeLog, proof);
                        CaptureArtifact(
                            visualCapture,
                            outputDirectory,
                            receipt,
                            proof,
                            LiveAcceptanceVisualExpectation.EvidenceOnly,
                            "semantic-back-closed.png");
                        ((JArray)receipt["semanticInput"]!).Add(new JObject
                        {
                            ["navigateDown"] = down,
                            ["navigateUp"] = up,
                            ["accept"] = accept,
                            ["backToHome"] = backToHome,
                            ["backClose"] = backClose,
                            ["hide"] = hidden,
                        });
                        return hidden!;
                    });

                RunStep(
                    receipt,
                    "reopen-after-semantic-back",
                    LiveAcceptanceContract.MenuReopen,
                    () =>
                    {
                        Require(WaitForForeground(gta, options.LiveStepTimeout, foreground),
                            "GTA was not foreground after the semantic Back close.");
                        SendF9(foreground, "provider-open-after-semantic-back");
                        var reopened = runtimeLog.WaitFor(
                            line => line.Contains("stage=menu_presentation_ready "),
                            options.LiveStepTimeout,
                            gta);
                        Require(reopened != null,
                            "GBAY did not reopen after the semantic Back close.");
                        CaptureArtifact(
                            visualCapture,
                            outputDirectory,
                            receipt,
                            proof,
                            LiveAcceptanceVisualExpectation.GbayMenu,
                            "semantic-back-reopened.png");
                        return reopened!;
                    });

                RunStep(
                    receipt,
                    "native-weapon-customizer-handoff",
                    LiveAcceptanceContract.MenuWeaponCustomizerOpen,
                    () =>
                    {
                        Require(allin1Log != null,
                            "The ALLIN1 client log could not be resolved.");
                        Require(WaitForForeground(gta, options.LiveStepTimeout, foreground),
                            "GTA was not foreground for the native weapon customizer handoff.");
                        Require(TryGetClientBounds(gameWindowBinding.Handle, out var bounds),
                            "Could not resolve the GTA client bounds for the native customizer.");
                        var deviceScale = ResolveWindowDeviceScale(gameWindowBinding.Handle);
                        var customization = LiveAcceptanceContract.TopLevelSections
                            .Single(section => section.Id == "customization");
                        var navPoint = LiveAcceptanceContract.ResolveSectionPoint(
                            customization,
                            bounds.Width,
                            bounds.Height,
                            deviceScale);
                        Click(bounds, navPoint.X, navPoint.Y, foreground);
                        var navPointer = WaitForPointerPair(
                            preloaderLog!,
                            options.LiveStepTimeout,
                            gta);
                        RecordPointerPair(receipt, navPointer, "customization-handoff-nav");
                        proof.PointerPairCount++;
                        Thread.Sleep(800);

                        var cardPoint = LiveAcceptanceContract.ResolveFirstCatalogCardPoint(
                            bounds.Width,
                            bounds.Height,
                            deviceScale);
                        Click(bounds, cardPoint.X, cardPoint.Y, foreground);
                        var cardPointer = WaitForPointerPair(
                            preloaderLog!,
                            options.LiveStepTimeout,
                            gta);
                        RecordPointerPair(receipt, cardPointer, "customization-owned-weapon");
                        proof.PointerPairCount++;
                        var opened = WaitForEvidenceSet(
                            allin1Log!,
                            new[]
                            {
                                "weapon_camera_activated",
                                "weapon_workbench_opened",
                                "reactor_weapon_workbench_handoff_opened",
                            },
                            options.LiveStepTimeout,
                            gta);
                        proof.NativeCustomizerCameraObserved =
                            opened.ContainsKey("weapon_camera_activated");
                        proof.NativeCustomizerHandoffObserved =
                            opened.ContainsKey("weapon_workbench_opened") &&
                            opened.ContainsKey("reactor_weapon_workbench_handoff_opened");
                        Require(proof.NativeCustomizerCameraObserved &&
                                proof.NativeCustomizerHandoffObserved,
                            "The native weapon workbench did not prove its camera and handoff boundaries.");
                        CaptureArtifact(
                            visualCapture,
                            outputDirectory,
                            receipt,
                            proof,
                            LiveAcceptanceVisualExpectation.EvidenceOnly,
                            "native-weapon-workbench.png");
                        foreach (var pair in opened)
                            ((JArray)receipt["nativeCustomizer"]!).Add(new JObject
                            {
                                ["stage"] = pair.Key,
                                ["evidence"] = pair.Value,
                            });
                        ObservePauseState(runtimeLog, proof);
                        return string.Join(" | ", opened.Values);
                    });

                RunStep(
                    receipt,
                    "native-weapon-customizer-return",
                    LiveAcceptanceContract.MenuWeaponCustomizerReturn,
                    () =>
                    {
                        Require(allin1Log != null,
                            "The ALLIN1 client log could not be resolved.");
                        SendKeyboardKey(VirtualKeyEscape, foreground, "native-workbench-back");
                        var returned = WaitForEvidenceSet(
                            allin1Log!,
                            new[]
                            {
                                "weapon_workbench_exit_reapply",
                                "reactor_weapon_workbench_handoff_returned",
                            },
                            options.LiveStepTimeout,
                            gta);
                        var menuReady = runtimeLog.WaitFor(
                            line => line.Contains("stage=menu_presentation_ready "),
                            options.LiveStepTimeout,
                            gta);
                        Require(menuReady != null,
                            "Reactor did not return to presentation-ready after the native workbench closed.");
                        proof.NativeCustomizerReturnObserved = returned.Count == 2;
                        Require(proof.NativeCustomizerReturnObserved,
                            "The native weapon workbench did not prove cleanup and Reactor return.");
                        CaptureArtifact(
                            visualCapture,
                            outputDirectory,
                            receipt,
                            proof,
                            LiveAcceptanceVisualExpectation.GbayMenu,
                            "native-weapon-workbench-returned.png");
                        foreach (var pair in returned)
                            ((JArray)receipt["nativeCustomizer"]!).Add(new JObject
                            {
                                ["stage"] = pair.Key,
                                ["evidence"] = pair.Value,
                            });
                        ObservePauseState(runtimeLog, proof);
                        return string.Join(" | ", returned.Values) + " | " + menuReady;
                    });

                for (var cycle = 1; cycle <= LiveAcceptanceContract.MinimumOpenCloseCycles; cycle++)
                {
                    var cycleNumber = cycle;
                    RunStep(
                        receipt,
                        $"close-menu-{cycleNumber}",
                        LiveAcceptanceContract.MenuClose,
                        () =>
                        {
                            var closed = ToggleAndWait(
                                gta,
                                runtimeLog,
                                "stage=overlay_hide_requested ",
                                options.LiveStepTimeout,
                                foreground,
                                $"provider-close-{cycleNumber}");
                            proof.OpenCloseCycles++;
                            CaptureArtifact(
                                visualCapture,
                                outputDirectory,
                                receipt,
                                proof,
                                LiveAcceptanceVisualExpectation.EvidenceOnly,
                                $"provider-menu-closed-{cycleNumber}.png");
                            return closed;
                        });

                    RunStep(
                        receipt,
                        $"reopen-menu-{cycleNumber}",
                        LiveAcceptanceContract.MenuReopen,
                        () =>
                        {
                            Require(WaitForForeground(gta, options.LiveStepTimeout, foreground),
                                "GTA was not foreground for the reopen check.");
                            SendF9(foreground, $"provider-open-{cycleNumber}");
                            var reopened = runtimeLog.WaitFor(
                                line => line.Contains("stage=menu_presentation_ready "),
                                options.LiveStepTimeout,
                                gta);
                            Require(reopened != null, "GBAY did not reopen to presentation-ready.");
                            CaptureArtifact(
                                visualCapture,
                                outputDirectory,
                                receipt,
                                proof,
                                LiveAcceptanceVisualExpectation.GbayMenu,
                                $"provider-menu-open-{cycleNumber}.png");
                            return reopened!;
                        });
                }

                RunStep(
                    receipt,
                    "final-close",
                    LiveAcceptanceContract.MenuFinalClose,
                    () =>
                    {
                        var closed = ToggleAndWait(
                            gta,
                            runtimeLog,
                            "stage=overlay_hide_requested ",
                            options.LiveStepTimeout,
                            foreground,
                            "provider-final-close");
                        CaptureArtifact(
                            visualCapture,
                            outputDirectory,
                            receipt,
                            proof,
                            LiveAcceptanceVisualExpectation.EvidenceOnly,
                            "provider-menu-final-close.png");
                        return closed;
                    });

                proof.ForegroundObservationCount = foreground.Count;
                proof.GtaForegroundObserved = foreground.GtaObserved;
                lifecycle.MarkStartup(
                    LiveAcceptanceStartupMilestone.HarnessCompleted,
                    DateTimeOffset.UtcNow);
                Require(
                    lifecycle.TryValidateSurfaceLifecycleCompleted(
                        out var lifecycleFailure),
                    "The authoritative surface lifecycle rejected this run: " +
                        lifecycleFailure + ".");
                var shutdownComplete = lifecycle.TryValidateShutdownCompleted(
                    out var shutdownFailure);
                receipt["shutdownValidation"] = new JObject
                {
                    ["required"] = false,
                    ["status"] = shutdownComplete
                        ? "complete"
                        : HasAnyProcessShutdownEvidence(lifecycle)
                            ? "incomplete_observed"
                            : "not_exercised",
                    ["failure"] = shutdownComplete
                        ? JValue.CreateNull()
                        : new JValue(shutdownFailure),
                };
                Require(
                    LiveAcceptanceContract.TryValidatePass(proof, out var proofFailure),
                    "The live evidence gate rejected this run: " + proofFailure + ".");
                receipt["proof"] = JObject.FromObject(proof);
                WriteLifecycleReceipt(receipt, lifecycle);

                receipt["status"] = "passed";
                ((JObject)receipt["artifacts"]!)["nativeBootstrapLog"] = nativeLog.SourcePath;
                if (allin1Log != null)
                    ((JObject)receipt["artifacts"]!)["allin1ClientLog"] = allin1Log.SourcePath;
                if (preloaderLog?.ResolvedPath != null)
                    ((JObject)receipt["artifacts"]!)["preloaderSessionLog"] = preloaderLog.ResolvedPath;
                if (runtimeLog.ResolvedPath != null)
                    ((JObject)receipt["artifacts"]!)["runtimeSessionLog"] = runtimeLog.ResolvedPath;
                receipt["completedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                WriteReceipt(receiptPath, receipt);
                Console.WriteLine(
                    "RESULT PASS: fresh live GTA window, installed hashes, About/preloader routes, " +
                    "paired native click, foreground isolation, screenshots, and repeated menu cycles validated.");
                Console.WriteLine("receipt=" + receiptPath);
                return 0;
            }
            catch (Exception error)
            {
                if (foreground != null)
                {
                    proof.ForegroundObservationCount = foreground.Count;
                    proof.GtaForegroundObserved = foreground.GtaObserved;
                }
                receipt["proof"] = JObject.FromObject(proof);
                lifecycle.MarkStartup(
                    LiveAcceptanceStartupMilestone.HarnessCompleted,
                    DateTimeOffset.UtcNow);
                if (gta != null && SafeHasExited(gta))
                    lifecycle.MarkShutdown(
                        LiveAcceptanceShutdownMilestone.GtaProcessExited,
                        DateTimeOffset.UtcNow);
                WriteLifecycleReceipt(receipt, lifecycle);
                ((JObject)receipt["artifacts"]!)["nativeBootstrapLog"] = nativeLog.SourcePath;
                if (allin1Log != null)
                    ((JObject)receipt["artifacts"]!)["allin1ClientLog"] = allin1Log.SourcePath;
                if (preloaderLog?.ResolvedPath != null)
                    ((JObject)receipt["artifacts"]!)["preloaderSessionLog"] = preloaderLog.ResolvedPath;
                if (runtimeLog.ResolvedPath != null)
                    ((JObject)receipt["artifacts"]!)["runtimeSessionLog"] = runtimeLog.ResolvedPath;
                receipt["status"] = gta != null && SafeHasExited(gta) ? "aborted" : "failed";
                receipt["failure"] = error.Message;
                receipt["completedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                WriteReceipt(receiptPath, receipt);
                Console.Error.WriteLine("RESULT FAIL: " + error.Message);
                Console.Error.WriteLine("receipt=" + receiptPath);
                return 9;
            }
            finally
            {
                gta?.Dispose();
                try
                {
                    if (File.Exists(armPath)) File.Delete(armPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                try { armMutex.ReleaseMutex(); }
                catch (ApplicationException) { }
            }
        }

        private static KeyValuePair<string, string> WaitForInitializerRoute(
            Process gta,
            LogTail nativeLog,
            SessionLogTail preloaderLog,
            TimeSpan overallTimeout,
            TimeSpan stepTimeout,
            ForegroundJournal foreground)
        {
            Require(
                WaitForForeground(gta, overallTimeout, foreground),
                "GTA did not become foreground after the Story transition.");
            Require(
                !preloaderLog.ProviderReadyObserved &&
                    !IsNamedEventSet(BootstrapHostNames.ConnectedEvent(gta.Id)),
                "The managed provider became ready before the early Story initializer could be tested.");

            foreground.Observe("story-route-probe");
            SendF9(foreground, "story-route-probe");
            var routeLine = WaitForBootstrapRoute(
                nativeLog,
                TimeSpan.FromSeconds(Math.Min(3, stepTimeout.TotalSeconds)),
                gta,
                required: false);
            var route = LiveAcceptanceContract.ClassifyBootstrapRoute(routeLine);
            if (LiveAcceptanceContract.RequiresInPlaceBootstrapPromotion(route))
            {
                // One physical edge opens the neutral boundary. Do not send a
                // second edge: wait for this generation's authoritative
                // promotion and fail if it never reaches Initializing.
                routeLine = WaitForBootstrapPromotion(nativeLog, stepTimeout, gta);
                route = LiveAcceptanceContract.ClassifyBootstrapRoute(routeLine);
                if (LiveAcceptanceContract.RequiresUnresolvedVerificationCleanup(
                        LiveAcceptanceRoute.Verifying,
                        route))
                {
                    CloseUnresolvedVerification(gta, preloaderLog, stepTimeout);
                    throw new InvalidOperationException(
                        "The objective Story transition produced no authoritative initializer route.");
                }
            }
            Require(
                route == LiveAcceptanceRoute.Initializer,
                route == LiveAcceptanceRoute.About
                    ? "The first post-transition F9 incorrectly routed back to frontend About."
                    : "The first post-transition F9 produced no typed initializer route.");

            var ready = WaitForEarlyInitializerSurface(
                preloaderLog,
                stepTimeout,
                gta);
            return new KeyValuePair<string, string>(routeLine, ready);
        }

        private static string WaitForStoryTransition(
            Process gta,
            LogTail nativeLog,
            SessionLogTail preloaderLog,
            TimeSpan timeout)
        {
            var line = nativeLog.WaitFor(
                candidate => LiveAcceptanceContract.ClassifyStoryTransition(candidate) !=
                    LiveAcceptanceStoryTransition.None,
                timeout,
                gta);
            Require(line != null,
                "No objective Story transition was observed; no Story F9 input was sent.");
            var transition = LiveAcceptanceContract.ClassifyStoryTransition(line);
            Require(
                transition == LiveAcceptanceStoryTransition.ManagedRuntimeStarting,
                "Story reached provider readiness before the early initializer boundary could be tested.");
            Require(
                !preloaderLog.ProviderReadyObserved &&
                    !IsNamedEventSet(BootstrapHostNames.ConnectedEvent(gta.Id)),
                "The managed provider was already connected at the first objective Story transition.");
            return line!;
        }

        private static string WaitForEarlyInitializerSurface(
            SessionLogTail preloaderLog,
            TimeSpan timeout,
            Process gta)
        {
            var observation = new LiveAcceptanceEarlyInitializerObservation();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                RequireProcessAlive(gta);
                foreach (var line in preloaderLog.ReadAvailable())
                {
                    observation.Observe(line);
                    if (observation.ProviderWonRace)
                        throw new InvalidOperationException(
                            "The managed provider became ready before the early initializer painted.");
                    if (observation.IsComplete)
                        return observation.SurfaceEvidence!;
                }
                Thread.Sleep(50);
            }
            throw new InvalidOperationException(
                "F9 routed to Story initialization, but the pre-provider initializer never painted.");
        }

        private static string WaitForFrontendAboutRoute(
            Process gta,
            LogTail nativeLog,
            SessionLogTail preloaderLog,
            TimeSpan overallTimeout,
            TimeSpan stepTimeout,
            ForegroundJournal foreground)
        {
            var timer = Stopwatch.StartNew();
            var attempt = 0;
            while (timer.Elapsed < overallTimeout)
            {
                RequireProcessAlive(gta);
                if (!IsForeground(gta))
                {
                    foreground.Observe("frontend-route-probe:waiting-for-foreground");
                    Thread.Sleep(250);
                    continue;
                }

                attempt++;
                SendF9(foreground, "frontend-route-probe-" + attempt);
                var routeLine = WaitForBootstrapRoute(
                    nativeLog,
                    TimeSpan.FromSeconds(Math.Min(3, stepTimeout.TotalSeconds)),
                    gta,
                    required: false);
                var route = LiveAcceptanceContract.ClassifyBootstrapRoute(routeLine);
                if (LiveAcceptanceContract.RequiresInPlaceBootstrapPromotion(route))
                {
                    routeLine = WaitForBootstrapPromotion(nativeLog, stepTimeout, gta);
                    route = LiveAcceptanceContract.ClassifyBootstrapRoute(routeLine);
                    if (LiveAcceptanceContract.RequiresUnresolvedVerificationCleanup(
                            LiveAcceptanceRoute.Verifying,
                            route))
                    {
                        CloseUnresolvedVerification(gta, preloaderLog, stepTimeout);
                        Thread.Sleep(250);
                        continue;
                    }
                }
                if (route == LiveAcceptanceRoute.About)
                    return routeLine;

                if (route == LiveAcceptanceRoute.Initializer)
                {
                    // A real game window can exist before the Enhanced landing
                    // menu is ready. Close that transient initializer and keep
                    // probing; only a typed About route proves frontend readiness.
                    SignalNamedEvent(BootstrapHostNames.CloseEvent(gta.Id));
                    preloaderLog.WaitFor(
                        line => line.Contains("stage=webview_visibility_applied ") &&
                            line.Contains("visible=False"),
                        TimeSpan.FromSeconds(Math.Min(3, stepTimeout.TotalSeconds)),
                        gta);
                }
                Thread.Sleep(1200);
            }
            throw new InvalidOperationException(
                "The GTA client window appeared, but no typed frontend About route became ready before the live timeout.");
        }

        private static string ToggleAndWait(
            Process gta,
            SessionLogTail runtimeLog,
            string expectedStage,
            TimeSpan timeout,
            ForegroundJournal foreground,
            string reason)
        {
            Require(WaitForForeground(gta, timeout, foreground), "GTA was not foreground for the toggle check.");
            SendF9(foreground, reason);
            var line = runtimeLog.WaitFor(
                candidate => candidate.Contains(expectedStage),
                timeout,
                gta);
            Require(line != null, "The managed F9 toggle did not reach " + expectedStage.Trim() + ".");
            return line!;
        }

        private static string WaitForBootstrapRoute(
            LogTail nativeLog,
            TimeSpan timeout,
            Process gta,
            bool required = true)
        {
            var line = nativeLog.WaitFor(
                candidate => LiveAcceptanceContract.ClassifyBootstrapRoute(candidate) !=
                    LiveAcceptanceRoute.None,
                timeout,
                gta);
            if (required) Require(line != null, "Native F9 routing produced no typed route evidence.");
            return line ?? string.Empty;
        }

        private static string WaitForBootstrapPromotion(
            LogTail nativeLog,
            TimeSpan timeout,
            Process gta)
        {
            var line = nativeLog.WaitFor(
                candidate =>
                {
                    var route = LiveAcceptanceContract.ClassifyBootstrapRoute(candidate);
                    return route == LiveAcceptanceRoute.About ||
                        route == LiveAcceptanceRoute.Initializer;
                },
                timeout,
                gta);
            return line ?? string.Empty;
        }

        private static void CloseUnresolvedVerification(
            Process gta,
            SessionLogTail preloaderLog,
            TimeSpan timeout)
        {
            // A missing companion snapshot is a failed attempt, not an open
            // intent that later probes may stack on top of. Close the exact
            // neutral generation before any retry and observe the host hide.
            SignalNamedEvent(BootstrapHostNames.CloseEvent(gta.Id));
            preloaderLog.WaitFor(
                line => line.Contains("stage=webview_visibility_applied ") &&
                    line.Contains("visible=False"),
                TimeSpan.FromSeconds(Math.Min(3, timeout.TotalSeconds)),
                gta);
        }

        private static Process WaitForGta(TimeSpan timeout, DateTime notBeforeUtc)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                foreach (var name in new[] { "GTA5_Enhanced", "GTA5" })
                {
                    var process = Process.GetProcessesByName(name)
                        .Where(candidate => !SafeHasExited(candidate))
                        .OrderByDescending(candidate => SafeStartTime(candidate))
                        .FirstOrDefault();
                    if (process == null) continue;
                    var startedUtc = SafeStartTime(process).ToUniversalTime();
                    if (startedUtc < notBeforeUtc)
                    {
                        process.Dispose();
                        throw new InvalidOperationException(
                            "GTA was already running before the live acceptance run was armed. " +
                            "Close GTA and arm a fresh run so stale sessions cannot satisfy acceptance.");
                    }
                    return process;
                }
                Thread.Sleep(250);
            }
            throw new TimeoutException(
                "No fresh GTA session was observed before the live acceptance timeout; acceptance failed.");
        }

        private static IntPtr WaitForMainWindow(Process gta, TimeSpan timeout)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                RequireProcessAlive(gta);
                gta.Refresh();
                var window = gta.MainWindowHandle;
                if (window != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(window, out var processId);
                    if (processId == gta.Id && TryGetClientBounds(window, out _))
                        return window;
                }
                Thread.Sleep(100);
            }
            throw new TimeoutException("The fresh GTA process never exposed a usable game window.");
        }

        private static DateTime SafeStartTime(Process process)
        {
            try { return process.StartTime; }
            catch { return DateTime.MinValue; }
        }

        private static bool SafeHasExited(Process process)
        {
            try { return process.HasExited; }
            catch { return true; }
        }

        private static void RequireProcessAlive(Process process)
        {
            if (SafeHasExited(process))
                throw new InvalidOperationException("GTA exited before the live acceptance run completed.");
        }

        private static bool WaitForForeground(
            Process gta,
            TimeSpan timeout,
            ForegroundJournal? journal = null)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                RequireProcessAlive(gta);
                journal?.Observe("wait-for-gta-foreground");
                if (IsForeground(gta)) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        private static bool IsForeground(Process gta)
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            GetWindowThreadProcessId(foreground, out var processId);
            return processId == gta.Id;
        }

        private static string ResolveGtaRoot(Process gta)
        {
            string executable;
            try
            {
                executable = gta.MainModule?.FileName ?? string.Empty;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    "Could not resolve the fresh GTA executable for installed-payload hashing.",
                    error);
            }
            Require(!string.IsNullOrWhiteSpace(executable),
                "The fresh GTA executable path was empty.");
            var root = Path.GetDirectoryName(executable);
            Require(!string.IsNullOrWhiteSpace(root) && Directory.Exists(root),
                "The fresh GTA process did not resolve to an existing game directory.");
            return root!;
        }

        private static IReadOnlyList<JObject> HashInstalledPayload(Process gta)
        {
            var root = ResolveGtaRoot(gta);

            var result = new List<JObject>();
            foreach (var relativePath in InstalledPayloadPaths)
            {
                var fullPath = Path.Combine(root, relativePath);
                Require(File.Exists(fullPath),
                    "Installed Reactor payload is incomplete: " + relativePath.Replace('\\', '/'));
                var file = new FileInfo(fullPath);
                string hash;
                using (var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var sha = SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(stream))
                        .Replace("-", string.Empty)
                        .ToLowerInvariant();
                result.Add(new JObject
                {
                    ["relativePath"] = relativePath.Replace('\\', '/'),
                    ["sha256"] = hash,
                    ["length"] = file.Length,
                    ["lastWriteUtc"] = file.LastWriteTimeUtc.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                });
            }
            return result;
        }

        private static void RecordRoute(JObject receipt, string line, string reason)
        {
            var route = LiveAcceptanceContract.ClassifyBootstrapRoute(line);
            Require(route != LiveAcceptanceRoute.None,
                "A bootstrap route could not be classified for the live receipt.");
            ((JArray)receipt["routes"]!).Add(new JObject
            {
                ["observedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["reason"] = reason,
                ["route"] = route.ToString().ToLowerInvariant(),
                ["evidence"] = SanitizeEvidence(line),
            });
        }

        private static void RecordSurface(
            JObject receipt,
            string line,
            LiveAcceptanceProof proof)
        {
            Require(LiveAcceptanceContract.TryParseSurfaceReady(line, out var surface),
                "A painted bootstrap surface did not contain typed mode/generation evidence.");
            ((JArray)receipt["surfaceModes"]!).Add(new JObject
            {
                ["observedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["mode"] = surface.Mode,
                ["generation"] = surface.Generation,
                ["evidence"] = SanitizeEvidence(line),
            });
            if (string.Equals(surface.Mode, "about", StringComparison.Ordinal))
                proof.AboutSurfaceObserved = true;
            if (string.Equals(surface.Mode, "initializing", StringComparison.Ordinal))
                proof.InitializerSurfaceObserved = true;
        }

        private static void RecordPointerPair(
            JObject receipt,
            PointerPairObservation pointer,
            string target)
        {
            ((JArray)receipt["pointerPairs"]!).Add(new JObject
            {
                ["target"] = target,
                ["down"] = pointer.DownLine,
                ["up"] = pointer.UpLine,
                ["route"] = pointer.Down.Route,
                ["downX"] = pointer.Down.X,
                ["downY"] = pointer.Down.Y,
                ["upX"] = pointer.Up.X,
                ["upY"] = pointer.Up.Y,
                ["forwarded"] = pointer.Down.Forwarded && pointer.Up.Forwarded,
            });
        }

        private static void ObservePauseState(
            SessionLogTail runtimeLog,
            LiveAcceptanceProof proof)
        {
            proof.PauseStateChecked = true;
            var leaked = runtimeLog.ReadAvailable().Any(line =>
                line.Contains("stage=game_pause_state_changed ") &&
                line.Contains("paused=True"));
            proof.PauseMenuLeakObserved |= leaked;
            Require(!leaked, "A menu input leaked into GTA's pause-menu controls.");
        }

        private static string SendSemanticKeyAndWait(
            Process gta,
            SessionLogTail runtimeLog,
            int virtualKey,
            string expectedAction,
            TimeSpan timeout,
            ForegroundJournal foreground)
        {
            SendKeyboardKey(virtualKey, foreground, "semantic-" + expectedAction);
            var line = runtimeLog.WaitFor(
                candidate => candidate.Contains("stage=semantic_input ") &&
                    candidate.Contains("action=" + expectedAction + " ") &&
                    candidate.Contains("source=game"),
                timeout,
                gta);
            Require(line != null,
                "The game semantic input path did not report " + expectedAction + ".");
            return line!;
        }

        private static void SendKeyboardKey(
            int virtualKey,
            ForegroundJournal? foreground,
            string reason)
        {
            foreground?.Observe(reason + ":before", force: true);
            SendOne(Input.Keyboard((ushort)virtualKey, 0), reason + " key-down");
            Thread.Sleep(75);
            SendOne(Input.Keyboard((ushort)virtualKey, KeyUp), reason + " key-up");
            Thread.Sleep(175);
            foreground?.Observe(reason + ":settled", force: true);
        }

        private static IReadOnlyDictionary<string, string> WaitForEvidenceSet(
            LogTail log,
            IReadOnlyList<string> required,
            TimeSpan timeout,
            Process process)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                RequireProcessAlive(process);
                foreach (var line in log.ReadAvailable())
                {
                    foreach (var key in required)
                    {
                        if (!found.ContainsKey(key) && line.Contains(key))
                            found[key] = line;
                    }
                }
                if (required.All(found.ContainsKey)) return found;
                Thread.Sleep(50);
            }
            var missing = required.Where(key => !found.ContainsKey(key));
            throw new TimeoutException(
                "Missing live evidence: " + string.Join(", ", missing) + ".");
        }

        private static string ParseMenuPresentationId(string line)
        {
            var match = MenuPresentationPattern.Match(line ?? string.Empty);
            if (!match.Success)
                throw new InvalidOperationException(
                    "The menu-ready trace did not expose a bounded presentation id.");
            return match.Groups["presentation"].Value;
        }

        private static void RecordLifecycleDesktopEvidence(
            LiveAcceptanceSurfaceLifecycleReceipt lifecycle,
            LiveAcceptanceLifecycleIdentity identity,
            string artifact)
        {
            var browserArtifact = artifact.Replace("-desktop.png", ".png");
            RecordLifecycleBrowserEvidence(
                lifecycle,
                identity,
                browserArtifact);
            Require(
                lifecycle.TryRecordEvidence(
                    lifecycle.CurrentState,
                    identity,
                    DateTimeOffset.UtcNow,
                    LiveAcceptanceLifecycleEvidenceSource.DesktopPixels,
                    artifact,
                    out var failure),
                "Desktop-pixel lifecycle evidence was rejected: " + failure + ".");
        }

        private static void RecordLifecycleBrowserEvidence(
            LiveAcceptanceSurfaceLifecycleReceipt lifecycle,
            LiveAcceptanceLifecycleIdentity identity,
            string artifact)
        {
            Require(
                lifecycle.TryRecordEvidence(
                    lifecycle.CurrentState,
                    identity,
                    DateTimeOffset.UtcNow,
                    LiveAcceptanceLifecycleEvidenceSource.BrowserCapture,
                    artifact,
                    out var failure),
                "Browser-capture lifecycle evidence was rejected: " + failure + ".");
        }

        private static void RecordLifecyclePointerPair(
            LiveAcceptanceSurfaceLifecycleReceipt lifecycle,
            JObject receipt,
            PointerPairObservation pointer,
            ForegroundJournal foreground,
            int gtaProcessId,
            string reason)
        {
            var timestamp = DateTimeOffset.UtcNow;
            Require(
                lifecycle.TryRecordInputEdge(
                    pointer.Down,
                    timestamp,
                    foreground.LastWindow.ToInt64(),
                    foreground.LastProcessId,
                    gtaProcessId,
                    out var downFailure),
                "The lifecycle rejected the pointer-down edge: " + downFailure + ".");
            Require(
                lifecycle.TryRecordInputEdge(
                    pointer.Up,
                    timestamp,
                    foreground.LastWindow.ToInt64(),
                    foreground.LastProcessId,
                    gtaProcessId,
                    out var upFailure),
                "The lifecycle rejected the pointer-up edge: " + upFailure + ".");
            ((JArray)receipt["inputEdgeTimeline"]!).Add(new JObject
            {
                ["reason"] = reason,
                ["observedUtc"] = timestamp.ToString("O", CultureInfo.InvariantCulture),
                ["foregroundHwnd"] = $"0x{foreground.LastWindow.ToInt64():X}",
                ["foregroundPid"] = foreground.LastProcessId,
                ["gtaForeground"] = foreground.LastProcessId == gtaProcessId,
                ["down"] = pointer.DownLine,
                ["up"] = pointer.UpLine,
            });
        }

        private static void WriteLifecycleReceipt(
            JObject receipt,
            LiveAcceptanceSurfaceLifecycleReceipt lifecycle)
        {
            var observations = new JArray();
            foreach (var observation in lifecycle.Observations)
            {
                observations.Add(new JObject
                {
                    ["sequence"] = observation.Sequence,
                    ["state"] = observation.State.ToString(),
                    ["generation"] = observation.Generation,
                    ["presentationId"] = observation.PresentationId,
                    ["lifecycleKey"] = observation.LifecycleKey,
                    ["observedUtc"] = observation.ObservedUtc.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    ["source"] = observation.Source.ToString(),
                    ["evidence"] = observation.Evidence,
                });
            }
            var inputEdges = new JArray();
            foreach (var edge in lifecycle.InputEdges)
            {
                inputEdges.Add(new JObject
                {
                    ["sequence"] = edge.Sequence,
                    ["observedUtc"] = edge.ObservedUtc.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    ["pressed"] = edge.Pressed,
                    ["released"] = edge.Released,
                    ["x"] = edge.X,
                    ["y"] = edge.Y,
                    ["route"] = edge.Route,
                    ["forwarded"] = edge.Forwarded,
                    ["foregroundHwnd"] = $"0x{edge.ForegroundWindow:X}",
                    ["foregroundPid"] = edge.ForegroundProcessId,
                    ["gtaForeground"] = edge.GtaForeground,
                });
            }
            receipt["surfaceLifecycle"] = new JObject
            {
                ["runId"] = lifecycle.RunId,
                ["currentState"] = lifecycle.CurrentState.ToString(),
                ["currentGeneration"] = lifecycle.CurrentGeneration,
                ["currentPresentationId"] = lifecycle.CurrentPresentationId,
                ["observations"] = observations,
                ["inputEdges"] = inputEdges,
            };
            receipt["startupTimestamps"] = new JObject
            {
                ["harnessArmedUtc"] = Timestamp(lifecycle.Startup.HarnessArmedUtc),
                ["gtaProcessObservedUtc"] = Timestamp(lifecycle.Startup.GtaProcessObservedUtc),
                ["gtaWindowObservedUtc"] = Timestamp(lifecycle.Startup.GtaWindowObservedUtc),
                ["frontendAboutObservedUtc"] = Timestamp(lifecycle.Startup.FrontendAboutObservedUtc),
                ["storyInitializingObservedUtc"] = Timestamp(lifecycle.Startup.StoryInitializingObservedUtc),
                ["providerReadyObservedUtc"] = Timestamp(lifecycle.Startup.ProviderReadyObservedUtc),
                ["menuPendingPaintObservedUtc"] = Timestamp(lifecycle.Startup.MenuPendingPaintObservedUtc),
                ["menuInteractiveObservedUtc"] = Timestamp(lifecycle.Startup.MenuInteractiveObservedUtc),
                ["harnessCompletedUtc"] = Timestamp(lifecycle.Startup.HarnessCompletedUtc),
            };
            receipt["shutdownTimestamps"] = new JObject
            {
                ["menuClosingObservedUtc"] = Timestamp(lifecycle.Shutdown.MenuClosingObservedUtc),
                ["menuClosedObservedUtc"] = Timestamp(lifecycle.Shutdown.MenuClosedObservedUtc),
                ["quitRequestedUtc"] = Timestamp(lifecycle.Shutdown.QuitRequestedUtc),
                ["scriptAbortObservedUtc"] = Timestamp(lifecycle.Shutdown.ScriptAbortObservedUtc),
                ["scriptHookUninitializedUtc"] = Timestamp(lifecycle.Shutdown.ScriptHookUninitializedUtc),
                ["gtaWindowDestroyedUtc"] = Timestamp(lifecycle.Shutdown.GtaWindowDestroyedUtc),
                ["gtaProcessExitedUtc"] = Timestamp(lifecycle.Shutdown.GtaProcessExitedUtc),
                ["webViewProcessExitedUtc"] = Timestamp(lifecycle.Shutdown.WebViewProcessExitedUtc),
            };
        }

        private static JToken Timestamp(DateTimeOffset? value) =>
            value.HasValue
                ? new JValue(value.Value.ToString("O", CultureInfo.InvariantCulture))
                : JValue.CreateNull();

        private static bool HasAnyProcessShutdownEvidence(
            LiveAcceptanceSurfaceLifecycleReceipt lifecycle) =>
            lifecycle.Shutdown.QuitRequestedUtc.HasValue ||
            lifecycle.Shutdown.ScriptAbortObservedUtc.HasValue ||
            lifecycle.Shutdown.ScriptHookUninitializedUtc.HasValue ||
            lifecycle.Shutdown.GtaWindowDestroyedUtc.HasValue ||
            lifecycle.Shutdown.GtaProcessExitedUtc.HasValue ||
            lifecycle.Shutdown.WebViewProcessExitedUtc.HasValue;

        private static void CaptureArtifact(
            LiveAcceptanceWindowCaptureSession visualCapture,
            string outputDirectory,
            JObject receipt,
            LiveAcceptanceProof proof,
            LiveAcceptanceVisualExpectation expectation,
            string fileName,
            LiveAcceptanceLifecycleIdentity? lifecycleIdentity = null)
        {
            var path = Path.Combine(outputDirectory, fileName);
            if (!LiveAcceptancePreviewCaptureContract.RequiresHostPreview(expectation))
            {
                using var evidence = visualCapture.Capture(
                    Path.GetFileNameWithoutExtension(fileName),
                    expectation);
                evidence.Save(path, ImageFormat.Png);
                ((JObject)receipt["artifacts"]!)[
                    Path.GetFileNameWithoutExtension(fileName)] = path;
                proof.ScreenshotCount++;
                return;
            }

            Exception? browserFailure = null;
            Exception? desktopFailure = null;
            LiveAcceptancePreviewIdentity? browserIdentityBeforeDesktop = null;
            try
            {
                using var browser = visualCapture.Capture(
                    Path.GetFileNameWithoutExtension(fileName),
                    expectation);
                browserIdentityBeforeDesktop =
                    visualCapture.LastHostPreviewIdentity;
                browser.Save(path, ImageFormat.Png);
                ((JObject)receipt["artifacts"]!)[
                    Path.GetFileNameWithoutExtension(fileName)] = path;
                proof.ScreenshotCount++;
                proof.BrowserCaptureEvidenceCount++;
                switch (expectation)
                {
                    case LiveAcceptanceVisualExpectation.ReactorAbout:
                        proof.AboutPixelsObserved = true;
                        break;
                    case LiveAcceptanceVisualExpectation.Allin1Preloader:
                        proof.InitializerPixelsObserved = true;
                        break;
                    case LiveAcceptanceVisualExpectation.GbayMenu:
                        proof.GbayPixelsObserved = true;
                        break;
                }
            }
            catch (Exception error)
            {
                browserFailure = error;
                RecordVisualCaptureFailure(
                    receipt,
                    fileName,
                    "browser-preview",
                    error);
            }

            var desktopName = Path.GetFileNameWithoutExtension(fileName) +
                "-desktop" + Path.GetExtension(fileName);
            var desktopPath = Path.Combine(outputDirectory, desktopName);
            try
            {
                // This capture is intentionally independent of CapturePreview.
                // It must still run, save, and enter the receipt when the
                // browser-owned diagnostic times out or fails validation.
                using var desktop = visualCapture.CaptureDesktop(
                    Path.GetFileNameWithoutExtension(desktopName),
                    expectation);
                desktop.Save(desktopPath, ImageFormat.Png);
                ((JObject)receipt["artifacts"]!)[
                    Path.GetFileNameWithoutExtension(desktopName)] = desktopPath;
                proof.ScreenshotCount++;
                proof.DesktopPixelEvidenceCount++;
                switch (expectation)
                {
                    case LiveAcceptanceVisualExpectation.ReactorAbout:
                        proof.AboutDesktopPixelsObserved = true;
                        break;
                    case LiveAcceptanceVisualExpectation.Allin1Preloader:
                        proof.InitializerDesktopPixelsObserved = true;
                        break;
                    case LiveAcceptanceVisualExpectation.GbayMenu:
                        proof.GbayDesktopPixelsObserved = true;
                        break;
                }
            }
            catch (Exception error)
            {
                desktopFailure = error;
                if (!string.IsNullOrWhiteSpace(
                        visualCapture.LastDesktopAttemptArtifact))
                {
                    ((JObject)receipt["artifacts"]!)[
                        Path.GetFileNameWithoutExtension(desktopName) +
                        "-last-attempt"] =
                        visualCapture.LastDesktopAttemptArtifact;
                }
                RecordVisualCaptureFailure(
                    receipt,
                    desktopName,
                    "desktop-compositor",
                    error);
            }

            if (browserFailure != null || desktopFailure != null)
            {
                ((JArray)receipt["desktopIdentityBrackets"]!).Add(new JObject
                {
                    ["artifact"] = desktopName,
                    ["valid"] = false,
                    ["skippedReason"] = browserFailure != null
                        ? "browser-preview-unavailable"
                        : "desktop-capture-unavailable",
                });
                var message = "Visual acceptance capture failed after both " +
                    "browser and desktop channels were attempted.";
                if (browserFailure != null)
                    message += " Browser: " + browserFailure.Message;
                if (desktopFailure != null)
                    message += " Desktop: " + desktopFailure.Message;
                throw new InvalidOperationException(
                    message,
                    browserFailure ?? desktopFailure);
            }

            // Re-sample the private browser surface after the independent
            // desktop capture. The pixels may prove desktop visibility only
            // when both browser samples name the same controller, surface,
            // generation, and typed menu presentation.
            using var bracket = visualCapture.Capture(
                Path.GetFileNameWithoutExtension(desktopName) + "-identity-after",
                expectation);
            var browserIdentityAfterDesktop =
                visualCapture.LastHostPreviewIdentity;
            var beforeDesktopIdentity =
                browserIdentityBeforeDesktop.GetValueOrDefault();
            var afterDesktopIdentity =
                browserIdentityAfterDesktop.GetValueOrDefault();
            var bracketFailure = "browser identity missing";
            var identityBracketValid =
                browserIdentityBeforeDesktop.HasValue &&
                browserIdentityAfterDesktop.HasValue &&
                (lifecycleIdentity == null
                    ? LiveAcceptancePreviewCaptureContract.TryValidateDesktopIdentityBracket(
                        beforeDesktopIdentity,
                        afterDesktopIdentity,
                        out bracketFailure)
                    : LiveAcceptancePreviewCaptureContract.TryValidateDesktopIdentityBracket(
                        beforeDesktopIdentity,
                        afterDesktopIdentity,
                        lifecycleIdentity.Generation,
                        lifecycleIdentity.PresentationId,
                        out bracketFailure));
            Require(
                identityBracketValid,
                "Desktop-pixel identity bracket failed: " +
                    bracketFailure + ".");
            ((JArray)receipt["desktopIdentityBrackets"]!).Add(new JObject
            {
                ["artifact"] = desktopName,
                ["valid"] = true,
                ["surfaceMode"] = beforeDesktopIdentity.SurfaceMode,
                ["surfaceGeneration"] = beforeDesktopIdentity.SurfaceGeneration,
                ["controllerGeneration"] = beforeDesktopIdentity.ControllerGeneration,
                ["menuPresentationId"] = beforeDesktopIdentity.MenuPresentationId,
                ["lifecycleKey"] = lifecycleIdentity == null
                    ? JValue.CreateNull()
                    : new JValue(lifecycleIdentity.Key),
                ["lifecycleMatchRequired"] = lifecycleIdentity != null,
                ["lifecycleMatched"] = lifecycleIdentity == null
                    ? JValue.CreateNull()
                    : new JValue(true),
            });
        }

        private static void RecordVisualCaptureFailure(
            JObject receipt,
            string artifact,
            string channel,
            Exception error)
        {
            var failures = receipt["visualCaptureFailures"] as JArray;
            if (failures == null)
            {
                failures = new JArray();
                receipt["visualCaptureFailures"] = failures;
            }
            failures.Add(new JObject
            {
                ["artifact"] = artifact,
                ["channel"] = channel,
                ["failedUtc"] = DateTime.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                ["errorType"] = error.GetType().FullName,
                ["error"] = error.Message,
            });
        }

        private static string WaitForAboutClose(
            SessionLogTail log,
            TimeSpan timeout,
            Process gta,
            string failureMessage)
        {
            var observation = new LiveAcceptanceAboutCloseObservation();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                RequireProcessAlive(gta);
                foreach (var line in log.ReadAvailable())
                {
                    if (observation.Observe(line))
                        return observation.ToEvidence();
                }
                Thread.Sleep(50);
            }
            throw new InvalidOperationException(failureMessage);
        }

        private static PointerPairObservation WaitForPointerPair(
            SessionLogTail log,
            TimeSpan timeout,
            Process gta)
        {
            var timer = Stopwatch.StartNew();
            var downLine = log.WaitFor(
                line => LiveAcceptanceContract.TryParsePointerEdge(line, out var edge) &&
                    edge.Pressed && !edge.Released,
                timeout,
                gta);
            if (downLine == null)
                throw new TimeoutException(
                    "No forwarded native WebView pointer-down edge was observed.");
            Require(LiveAcceptanceContract.TryParsePointerEdge(downLine, out var down),
                "The native WebView pointer-down evidence was malformed.");

            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException(
                    "A native WebView pointer-down edge was observed without its matching pointer-up edge.");
            var upLine = log.WaitFor(
                line => LiveAcceptanceContract.TryParsePointerEdge(line, out var edge) &&
                    !edge.Pressed && edge.Released,
                remaining,
                gta);
            if (upLine == null)
                throw new TimeoutException(
                    "A native WebView pointer-down edge was observed without its matching pointer-up edge.");
            Require(LiveAcceptanceContract.TryParsePointerEdge(upLine, out var up) &&
                    LiveAcceptanceContract.IsValidPointerPair(down, up),
                "The native WebView pointer down/up edges were not a valid ordered pair.");
            return new PointerPairObservation(downLine, upLine, down, up);
        }

        private static MenuStateObservation WaitForSectionState(
            SessionLogTail log,
            LiveAcceptanceSectionTarget target,
            TimeSpan timeout,
            Process gta)
        {
            var line = log.WaitFor(
                candidate =>
                    candidate.Contains("stage=webview_acceptance_menu_state ") ||
                    candidate.Contains("stage=webview_acceptance_menu_state_rejected "),
                timeout,
                gta);
            Require(line != null,
                $"The {target.Id} section produced no native-bound semantic route observation.");
            Require(LiveAcceptanceContract.TryParseMenuStateTrace(line, out var state),
                $"The {target.Id} section semantic observation was rejected or malformed: " +
                SanitizeEvidence(line!));
            return new MenuStateObservation(line!, state);
        }

        private static bool WaitForNamedEventSet(
            string name,
            TimeSpan timeout,
            Process process)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                RequireProcessAlive(process);
                try
                {
                    using var handle = EventWaitHandle.OpenExisting(name);
                    if (handle.WaitOne(0)) return true;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                }
                Thread.Sleep(50);
            }
            return false;
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
        }

        private static bool SignalNamedEvent(string name)
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
        }

        private static T RunStep<T>(
            JObject receipt,
            string name,
            string command,
            Func<T> action,
            Func<T, string>? evidence = null)
        {
            Require(LiveAcceptanceContract.IsSupportedCommand(command) ||
                    command.EndsWith(".wait", StringComparison.Ordinal),
                "The live acceptance plan contains an unsupported command: " + command);
            var timer = Stopwatch.StartNew();
            var step = new JObject
            {
                ["name"] = name,
                ["command"] = command,
                ["startedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["status"] = "running",
            };
            ((JArray)receipt["steps"]!).Add(step);
            try
            {
                var result = action();
                timer.Stop();
                step["status"] = "passed";
                step["durationMs"] = timer.Elapsed.TotalMilliseconds;
                step["evidence"] = SanitizeEvidence(evidence == null
                    ? Convert.ToString(result, CultureInfo.InvariantCulture) ?? string.Empty
                    : evidence(result));
                return result;
            }
            catch (Exception error)
            {
                timer.Stop();
                step["status"] = "failed";
                step["durationMs"] = timer.Elapsed.TotalMilliseconds;
                step["error"] = error.Message;
                throw;
            }
        }

        private static string SanitizeEvidence(string value)
        {
            value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 1024 ? value : value.Substring(0, 1024);
        }

        private static void WriteReceipt(string path, JObject receipt)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory!);
            File.WriteAllText(path, receipt.ToString(Formatting.Indented));
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void SendF9(ForegroundJournal? foreground, string reason)
        {
            foreground?.Observe(reason + ":before", force: true);
            SendOne(Input.Keyboard((ushort)VirtualKeyF9, 0), "F9 key-down");
            foreground?.Observe(reason + ":key-down", force: true);
            Thread.Sleep(75);
            SendOne(Input.Keyboard((ushort)VirtualKeyF9, KeyUp), "F9 key-up");
            foreground?.Observe(reason + ":key-up", force: true);
            Thread.Sleep(175);
            foreground?.Observe(reason + ":settled", force: true);
        }

        private static void Click(
            Rectangle bounds,
            double normalizedX,
            double normalizedY,
            ForegroundJournal? foreground)
        {
            Require(LiveAcceptanceContract.IsValidPoint(normalizedX, normalizedY),
                "The live click point is outside normalized client coordinates.");
            GetCursorPos(out var original);
            var x = bounds.Left + (int)Math.Round((bounds.Width - 1) * normalizedX);
            var y = bounds.Top + (int)Math.Round((bounds.Height - 1) * normalizedY);
            var screenWidth = Math.Max(1, GetSystemMetrics(ScreenWidth) - 1);
            var screenHeight = Math.Max(1, GetSystemMetrics(ScreenHeight) - 1);
            var absoluteX = (int)Math.Round(x * 65535.0 / screenWidth);
            var absoluteY = (int)Math.Round(y * 65535.0 / screenHeight);
            try
            {
                SendOne(
                    Input.Mouse(absoluteX, absoluteY, MouseMove | MouseAbsolute),
                    "pointer move");
                foreground?.Observe("pointer:move", force: true);
                Thread.Sleep(75);
                SendOne(
                    Input.Mouse(absoluteX, absoluteY, MouseLeftDown | MouseAbsolute),
                    "pointer down");
                foreground?.Observe("pointer:down", force: true);
                // Keep the target coordinate and high-bit state observable for
                // at least one zero-interval SHVDN tick. Restoring the cursor
                // immediately can otherwise turn a valid click into a click at
                // the tester's previous desktop position.
                Thread.Sleep(100);
                foreground?.Observe("pointer:held", force: true);
                SendOne(
                    Input.Mouse(absoluteX, absoluteY, MouseLeftUp | MouseAbsolute),
                    "pointer up");
                foreground?.Observe("pointer:up", force: true);
                Thread.Sleep(150);
                foreground?.Observe("pointer:settled", force: true);
            }
            finally
            {
                SetCursorPos(original.X, original.Y);
            }
        }

        private static void SendOne(Input input, string action)
        {
            var inputs = new[] { input };
            Require(
                SendInput(1, inputs, Marshal.SizeOf(typeof(Input))) == 1,
                "Windows did not accept the " + action + ".");
        }

        private static double ChangedFraction(Bitmap before, Bitmap after)
        {
            if (before.Width != after.Width || before.Height != after.Height) return 1.0;
            long changed = 0;
            long sampled = 0;
            // Measure only the opaque GBAY shell. Moving world pixels outside
            // the panel cannot turn a missed UI click into a false pass.
            var left = (int)Math.Round(before.Width * 0.08);
            var right = (int)Math.Round(before.Width * 0.92);
            var top = (int)Math.Round(before.Height * 0.05);
            var bottom = (int)Math.Round(before.Height * 0.95);
            for (var y = top; y < bottom; y += 6)
            {
                for (var x = left; x < right; x += 6)
                {
                    var a = before.GetPixel(x, y);
                    var b = after.GetPixel(x, y);
                    if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 32)
                        changed++;
                    sampled++;
                }
            }
            return sampled == 0 ? 0.0 : changed / (double)sampled;
        }

        internal static bool TryGetClientBounds(IntPtr window, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (window == IntPtr.Zero || !GetClientRect(window, out var client)) return false;
            var origin = new NativePoint { X = client.Left, Y = client.Top };
            if (!ClientToScreen(window, ref origin)) return false;
            var width = client.Right - client.Left;
            var height = client.Bottom - client.Top;
            if (width <= 0 || height <= 0) return false;
            bounds = new Rectangle(origin.X, origin.Y, width, height);
            return true;
        }

        private static double ResolveWindowDeviceScale(IntPtr window)
        {
            if (window == IntPtr.Zero) return 1.0;
            try
            {
                var dpi = GetDpiForWindow(window);
                return dpi >= 48 && dpi <= 768 ? dpi / 96.0 : 1.0;
            }
            catch (EntryPointNotFoundException)
            {
                return 1.0;
            }
            catch (DllNotFoundException)
            {
                return 1.0;
            }
        }

        private sealed class ForegroundJournal
        {
            private readonly JArray _timeline;
            private readonly int _gtaProcessId;
            private readonly List<int> _processIds = new List<int>();
            private IntPtr _lastWindow;
            private int _lastProcessId = -1;

            public ForegroundJournal(JArray timeline, int gtaProcessId)
            {
                _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
                _gtaProcessId = gtaProcessId;
            }

            public int Count => _processIds.Count;
            public bool GtaObserved => _processIds.Contains(_gtaProcessId);
            public IntPtr LastWindow => _lastWindow;
            public int LastProcessId => _lastProcessId;

            public void Observe(string reason, bool force = false)
            {
                var window = GetForegroundWindow();
                var processId = 0;
                var threadId = 0u;
                if (window != IntPtr.Zero)
                    threadId = GetWindowThreadProcessId(window, out processId);
                var changed = window != _lastWindow || processId != _lastProcessId;
                if (!force && !changed) return;
                _lastWindow = window;
                _lastProcessId = processId;
                _processIds.Add(processId);
                _timeline.Add(new JObject
                {
                    ["observedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["reason"] = reason,
                    ["hwnd"] = $"0x{window.ToInt64():X}",
                    ["pid"] = processId,
                    ["threadId"] = threadId,
                    ["isGta"] = processId == _gtaProcessId,
                    ["transition"] = changed,
                });
            }

            public bool HasForeignProcessFrom(int startIndex)
            {
                if (startIndex < 0 || startIndex > _processIds.Count)
                    throw new ArgumentOutOfRangeException(nameof(startIndex));
                return _processIds.Skip(startIndex).Any(processId =>
                    processId != _gtaProcessId);
            }
        }

        private readonly struct PointerPairObservation
        {
            public PointerPairObservation(
                string downLine,
                string upLine,
                LiveAcceptancePointerEdge down,
                LiveAcceptancePointerEdge up)
            {
                DownLine = downLine;
                UpLine = upLine;
                Down = down;
                Up = up;
            }

            public string DownLine { get; }
            public string UpLine { get; }
            public LiveAcceptancePointerEdge Down { get; }
            public LiveAcceptancePointerEdge Up { get; }
        }

        private readonly struct MenuStateObservation
        {
            public MenuStateObservation(string line, LiveAcceptanceMenuState state)
            {
                Line = line ?? throw new ArgumentNullException(nameof(line));
                State = state;
            }

            public string Line { get; }
            public LiveAcceptanceMenuState State { get; }
        }

        private sealed class LogTail
        {
            private readonly string _path;
            private readonly Action<string>? _observer;
            private readonly Queue<string> _pending = new Queue<string>();
            private long _offset;
            private string _partial = string.Empty;

            public LogTail(
                string path,
                bool startAtEnd = true,
                Action<string>? observer = null)
            {
                _path = path;
                _observer = observer;
                try
                {
                    _offset = startAtEnd && File.Exists(path)
                        ? new FileInfo(path).Length
                        : 0;
                }
                catch { _offset = 0; }
            }

            public string SourcePath => _path;

            public string? WaitFor(
                Func<string, bool> predicate,
                TimeSpan timeout,
                Process process)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < timeout)
                {
                    RequireProcessAlive(process);
                    var match = FindAvailable(predicate);
                    if (match != null) return match;
                    Thread.Sleep(50);
                }
                return null;
            }

            public IReadOnlyList<string> ReadAvailable()
            {
                var result = new List<string>();
                while (_pending.Count > 0) result.Add(_pending.Dequeue());
                try
                {
                    if (!File.Exists(_path)) return result;
                    using var stream = new FileStream(
                        _path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length < _offset)
                    {
                        _offset = 0;
                        _partial = string.Empty;
                    }
                    if (stream.Length == _offset) return result;
                    stream.Position = _offset;
                    using var reader = new StreamReader(stream);
                    var text = _partial + reader.ReadToEnd();
                    _offset = stream.Position;
                    var parts = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    _partial = parts[parts.Length - 1];
                    var completed = parts.Take(parts.Length - 1)
                        .Where(line => !string.IsNullOrWhiteSpace(line))
                        .ToArray();
                    foreach (var line in completed) _observer?.Invoke(line);
                    result.AddRange(completed);
                    return result;
                }
                catch (IOException)
                {
                    return result;
                }
                catch (UnauthorizedAccessException)
                {
                    return result;
                }
            }

            public string? FindAvailable(Func<string, bool> predicate)
            {
                var lines = ReadAvailable();
                for (var index = 0; index < lines.Count; index++)
                {
                    if (!predicate(lines[index])) continue;
                    for (var remainder = index + 1; remainder < lines.Count; remainder++)
                        _pending.Enqueue(lines[remainder]);
                    return lines[index];
                }
                return null;
            }
        }

        /// <summary>
        /// Resolves the fresh StartupTrace session belonging to one exact
        /// process. Production intentionally treats aggregate logs as best
        /// effort; receipts therefore never depend on their presence.
        /// </summary>
        private sealed class SessionLogTail
        {
            private readonly string _directory;
            private readonly DateTime _notBeforeUtc;
            private int? _processId;
            private LogTail? _tail;

            public SessionLogTail(
                string directory,
                DateTime notBeforeUtc,
                int? processId)
            {
                _directory = directory;
                _notBeforeUtc = notBeforeUtc.AddSeconds(-2);
                _processId = processId;
            }

            public string? ResolvedPath { get; private set; }
            public bool ProviderReadyObserved { get; private set; }

            public void BindProcess(int processId)
            {
                if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
                if (_processId.HasValue && _processId.Value != processId)
                    throw new InvalidOperationException("The session log is already bound to another process.");
                _processId = processId;
            }

            public string? WaitFor(
                Func<string, bool> predicate,
                TimeSpan timeout,
                Process process)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < timeout)
                {
                    RequireProcessAlive(process);
                    Resolve();
                    if (_tail != null)
                    {
                        var match = _tail.FindAvailable(predicate);
                        if (match != null) return match;
                    }
                    Thread.Sleep(50);
                }
                return null;
            }

            public IReadOnlyList<string> ReadAvailable()
            {
                Resolve();
                return _tail?.ReadAvailable() ?? Array.Empty<string>();
            }

            private void Resolve()
            {
                if (_tail != null || !_processId.HasValue || !Directory.Exists(_directory))
                    return;
                var suffix = "-" + _processId.Value.ToString(CultureInfo.InvariantCulture) + ".log";
                var match = Directory.GetFiles(_directory, "reactorv-session-*.log", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .Where(file =>
                        file.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                        file.LastWriteTimeUtc >= _notBeforeUtc)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (match == null) return;
                ResolvedPath = match.FullName;
                // The process-specific file belongs exclusively to this run;
                // retain its complete startup history even if it appeared
                // before the harness resolved the path.
                _tail = new LogTail(
                    match.FullName,
                    startAtEnd: false,
                    observer: line =>
                    {
                        if (line.IndexOf(
                                "stage=bootstrap_host_provider_ready ",
                                StringComparison.Ordinal) >= 0)
                            ProviderReadyObserved = true;
                    });
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public int Type;
            public InputUnion Data;

            public static Input Keyboard(ushort key, uint flags) => new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = flags } },
            };

            public static Input Mouse(int x, int y, uint flags) => new Input
            {
                Type = InputMouse,
                Data = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Flags = flags } },
            };
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
    }
}
