using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using GTA.UI;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Script.Api;
using RageWebUI.Script.Browser;
using RageWebUI.Script.Configuration;
using ReactorV.Integration;
using ReactorV.WebView2Host;
using GTAControl = GTA.Control;

namespace RageWebUI.Script
{
    public sealed class RageWebUiScript : GTA.Script
    {
        private const int MaximumRequestsPerFrame = 32;
        private const int StoryModeStableMilliseconds = 1000;
        private const int StoryModeStartingPollMilliseconds = 100;
        private const int StoryModeBackgroundPollMilliseconds = 250;
        private const int ToggleDebounceMilliseconds = 250;
        private const int ManagedStartupStatusRefreshMilliseconds = 500;

        private readonly BridgeBroker _broker;
        private readonly IOverlayRuntime _overlay;
        private readonly GameApiRouter _router;
        private readonly OverlayConfiguration _configuration;
        private readonly Keys _toggleKey;
        private readonly string _firstRunMarkerPath;
        private readonly string _localDataDirectory;
        private readonly Stopwatch _scriptTimer = Stopwatch.StartNew();
        private readonly MenuRevealGate _menuRevealGate = new MenuRevealGate();
        private readonly ProviderPresentationCommitGate
            _providerPresentationCommitGate = new ProviderPresentationCommitGate();
        private readonly MenuInputLease _menuInputLease = new MenuInputLease();
        private readonly ManagedPointerButtonPolicy _pointerButtonPolicy =
            new ManagedPointerButtonPolicy();
        private readonly IntPtr _gtaWindow;
        private bool _firstRunPending;
        private bool _storyModeReady;
        private bool _storyModePlayable;
        private bool _overlayRequestedVisible;
        private bool _overlayWasPresented;
        private bool _overlayPreviouslyVisible;
        private bool _gameWasPaused;
        private bool _browserReady;
        private int _browserContentGeneration;
        private bool _runtimeReadyHandoffAttempted;
        private bool _runtimeReadyLeaseRequested;
        private bool _windowedKeyboardInputTraced;
        private string? _presentationPreparationDismissalSuppressionId;
        private string? _providerRevealAfterCommitPresentationId;
        private string _inputMode = MenuPresentationPolicy.InitialInputMode;
        private string _lifecyclePhase = "booting";
        private int _storyModeReadyCandidateAt = -1;
        private int _nextTelemetryAt;
        private int _nextToggleAt;
        private long _nextStoryModePollAt;
        private long _nextManagedStartupStatusAt;
        private bool _managedStartupStatusComplete;
        private bool _disabledCursorCancelDown;
        private bool _disabledCursorCancelBackPostedThisFrame;
        private bool _pointerForegroundBoundaryActive;
        private bool _pointerSourceTraceInitialized;
        private bool _lastControl237Down;
        private bool _lastControl24Down;
        private bool _lastPhysicalLeftDown;
        private int _pointerEdgeSequence;
        private int _hostSurfaceGeneration;
        private long _providerInputIntentEpoch;
        private long _pendingProviderInputIntentEpoch;
        private long _pendingProviderInputIntentExpiresAt;
        private long _boundProviderInputIntentEpoch;
        private string? _boundProviderInputIntentPresentationId;
        private string? _userIntentFallbackPresentationId;

        public RageWebUiScript()
        {
            var traceDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ReactorV");
            StartupTrace.Write(
                traceDirectory,
                "reactorv-runtime.log",
                "script",
                "construction_begin",
                $"domain={AppDomain.CurrentDomain.FriendlyName} " +
                $"assembly={Assembly.GetExecutingAssembly().Location}");

            try
            {
                Interval = 0;
                var bootstrapDirectory = RuntimeDirectoryLocator.ResolveBootstrap(
                    AppDomain.CurrentDomain.BaseDirectory,
                    Environment.CurrentDirectory,
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));

                _configuration = OverlayConfiguration.Load(bootstrapDirectory);
                if (!Enum.TryParse(_configuration.ToggleKey, true, out _toggleKey))
                {
                    _toggleKey = Keys.F9;
                }

                _broker = new BridgeBroker();
                using var gameProcess = Process.GetCurrentProcess();
                var reportedGameWindow = gameProcess.MainWindowHandle;
                var resolvedGameWindow = NativeMethods.ResolveGameWindow(
                    (uint)gameProcess.Id,
                    reportedGameWindow,
                    out var gameWindowResolution);
                var gtaWindow = resolvedGameWindow != IntPtr.Zero
                    ? resolvedGameWindow
                    : reportedGameWindow;
                _gtaWindow = gtaWindow;
                _localDataDirectory = ReactorVDataDirectory.Resolve();
                traceDirectory = _localDataDirectory;
                _firstRunMarkerPath = Path.Combine(_localDataDirectory, "first-run-splash.complete");
                _firstRunPending = _configuration.ShowFirstRunSplash && !File.Exists(_firstRunMarkerPath);
                TraceRuntime(
                    "construction_paths_ready",
                    $"bootstrap={bootstrapDirectory} window=0x{gtaWindow.ToInt64():X} " +
                    $"reported_window=0x{reportedGameWindow.ToInt64():X} " +
                    $"window_resolution=({gameWindowResolution}) " +
                    $"first_run_pending={_firstRunPending} renderer={_configuration.Renderer}");

                var overlayTimer = Stopwatch.StartNew();
                TraceRuntime("overlay_create_begin");
                _overlay = CreateOverlay(
                    gtaWindow,
                    bootstrapDirectory,
                    _localDataDirectory);
                TraceRuntime(
                    "overlay_create_complete",
                    $"renderer={_overlay.RendererName} duration_ms={overlayTimer.Elapsed.TotalMilliseconds:F3}");
                _router = new GameApiRouter(
                    visible =>
                    {
                        if (visible) ShowOverlay("api");
                        else CloseOverlay("api");
                    },
                    () => _overlayRequestedVisible || _overlay.IsVisible,
                    mode => _inputMode = mode,
                    () => _inputMode,
                    () => _overlay.RendererName,
                    (errorId, error) => TraceRuntime(
                        "api_failure",
                        $"error_id={errorId} type={error.GetType().FullName} message={error.Message}"),
                    MarkBrowserReady,
                    MarkMenuPresentationReady);

                if (_overlay is IContentGenerationRuntime generationRuntime &&
                    generationRuntime.TryGetReadyContentGeneration(out var contentGeneration))
                {
                    // BootstrapOverlayRuntime can attach only after the
                    // preloader's content-ready event is signaled. Treat that
                    // authenticated host contract as browser readiness instead
                    // of leaving F9 ownership dependent on a one-shot web
                    // message that may race a renderer recovery.
                    MarkBrowserReady("bootstrap-host-contract", contentGeneration);
                }

                Tick += OnTick;
                KeyDown += OnKeyDown;
                Aborted += OnAborted;
                ReactorHostApi.SetMenuPresentationHostAvailable(true);
                TraceRuntime(
                    "construction_complete",
                    $"duration_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
            }
            catch (Exception error)
            {
                ReactorHostApi.SetMenuPresentationHostAvailable(false);
                StartupTrace.Write(
                    traceDirectory,
                    "reactorv-runtime.log",
                    "script",
                    "construction_failed",
                    $"duration_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3} " +
                    $"type={error.GetType().FullName} message={error.Message}");
                throw;
            }
        }

        private void OnTick(object sender, EventArgs args)
        {
            // Suppression is a per-frame GTA contract. Apply the lease carried
            // from the previous tick before doing any lifecycle or extension
            // work, then apply it again below if this tick acquires ownership.
            SuppressGameInputForActiveLease();
            SynchronizeBrowserContentGeneration();
            var scriptElapsedMilliseconds = _scriptTimer.ElapsedMilliseconds;
            if (scriptElapsedMilliseconds >= _nextStoryModePollAt)
            {
                UpdateStoryModeReadiness();
                _nextStoryModePollAt = scriptElapsedMilliseconds +
                    (_storyModeReady
                        ? StoryModeBackgroundPollMilliseconds
                        : StoryModeStartingPollMilliseconds);
            }

            // The process-separated bootstrap uses an acknowledged lease.
            // Its first advance intentionally queues the request and returns
            // Pending; retry from the ordinary script tick so production does
            // not depend on another browser/story state edge to observe the
            // acknowledgement and complete F9 ownership transfer.
            if (!_runtimeReadyHandoffAttempted &&
                _storyModeReady &&
                _storyModePlayable &&
                _browserReady)
            {
                TryCompleteRuntimeReadyHandoff();
            }

            for (var index = 0; index < MaximumRequestsPerFrame && _broker.TryDequeue(out var request); index++)
            {
                if (request != null)
                {
                    _overlay.PostResponse(_router.Dispatch(request));
                }
            }

            PublishManagedStartupStatus(scriptElapsedMilliseconds);

            if (MenuPresentationPolicy.ShouldServiceExtensionMenuQueue(
                    _storyModeReady,
                    _browserReady))
            {
                DrainMenuDismissals();
                DrainMenuPresentations();
            }
            TryAdvancePendingProviderPresentation(scriptElapsedMilliseconds);
            DrainExtensionEvents();
            while (_router.TryDequeueReplayEvent(out var replayName, out var replayPayload))
            {
                if (replayName != null)
                {
                    _overlay.PostEvent(replayName, replayPayload);
                }
            }

            if (_menuRevealGate.TryExpire(scriptElapsedMilliseconds, out var expiredPresentationId))
            {
                var aborted = AbortPresentationTransfer(
                    expiredPresentationId,
                    "presentation-ready-timeout");
                TraceRuntime(
                    "menu_presentation_ready_timeout",
                    $"presentation={expiredPresentationId} timeout_ms={MenuRevealGate.DefaultTimeoutMilliseconds} " +
                    $"game_time={Game.GameTime} fail_closed=true exact_abort={aborted}");
            }

            if (_providerPresentationCommitGate.TryExpire(
                    scriptElapsedMilliseconds,
                    out var expiredProviderPresentationId,
                    out var browserPreparationWaitMilliseconds))
            {
                var aborted = AbortPresentationTransfer(
                    expiredProviderPresentationId,
                    "provider-paint-timeout");
                TraceRuntime(
                    "menu_provider_paint_timeout",
                    $"presentation={expiredProviderPresentationId} " +
                    $"browser_prepare_wait_ms={browserPreparationWaitMilliseconds} " +
                    $"timeout_ms={ProviderPresentationCommitGate.DefaultTimeoutMilliseconds} " +
                    $"game_time={Game.GameTime} fail_closed=true exact_abort={aborted}");
            }

            _overlay.PumpInput();

            var overlayPresented = _overlay.IsVisible;
            if (overlayPresented != _overlayPreviouslyVisible)
            {
                _overlayPreviouslyVisible = overlayPresented;
                if (!overlayPresented)
                {
                    // The external host can authoritatively hide a surface
                    // when GTA loses foreground (for example, after opening a
                    // support URL). Reconcile the requested state before the
                    // lease advances; otherwise a hidden menu continues to
                    // disable GTA controls after the user Alt-Tabs back.
                    if (MenuPresentationPolicy.ShouldReconcileHostHide(
                            _overlayRequestedVisible,
                            overlayPresented))
                    {
                        // Invalidate a presentation that was still waiting for
                        // its paint acknowledgement. A late browser ack must
                        // not reopen the host or reacquire input after this
                        // authoritative close edge.
                        _menuRevealGate.Cancel();
                        CancelPendingProviderPresentation(
                            "host-visibility-edge");
                        _presentationPreparationDismissalSuppressionId = null;
                        _overlayRequestedVisible = false;
                        _inputMode = MenuPresentationPolicy.HiddenInputMode;
                        TraceRuntime(
                            "overlay_hidden_state_reconciled",
                            $"reason=host-visibility-edge game_time={Game.GameTime} " +
                            $"input_mode={_inputMode}");
                    }
                    if (_presentationPreparationDismissalSuppressionId != null)
                    {
                        var suppressedPresentationId =
                            _presentationPreparationDismissalSuppressionId;
                        _presentationPreparationDismissalSuppressionId = null;
                        TraceRuntime(
                            "menu_presentation_surface_hidden",
                            $"presentation={suppressedPresentationId} " +
                            $"game_time={Game.GameTime} dismissal_suppressed=true");
                    }
                    else
                    {
                        PublishActiveMenuDismissed("overlay-hidden");
                    }
                }
                ReactorHostApi.NotifyLifecycle(
                    overlayPresented ? ReactorLifecycleStage.OverlayOpened : ReactorLifecycleStage.OverlayClosed,
                    new JObject { ["gameTime"] = Game.GameTime });
            }

            var inputLeaseFrame = UpdateMenuInputLease(
                overlayPresented,
                scriptElapsedMilliseconds);
            if (!overlayPresented)
            {
                _overlayWasPresented = false;
                return;
            }

            if (!_overlayWasPresented)
            {
                _overlayWasPresented = true;
                _nextTelemetryAt = Game.GameTime + _configuration.TelemetryIntervalMilliseconds;
                try
                {
                    PostCoreEvent("overlay.snapshot", _router.GetSnapshot());
                    TraceRuntime(
                        "overlay_first_presented",
                        $"script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3} " +
                        $"game_time={Game.GameTime} renderer={_overlay.RendererName}");
                }
                catch
                {
                    PostCoreEvent("overlay.snapshot", JValue.CreateNull());
                }
            }

            if (inputLeaseFrame.AcceptMenuInput)
            {
                _disabledCursorCancelBackPostedThisFrame = false;
                if (MenuPresentationPolicy.UsesPointer(_inputMode))
                    EmitPointerInput();
                EmitSemanticInput();
            }
            if (Game.GameTime >= _nextTelemetryAt)
            {
                _nextTelemetryAt = Game.GameTime + _configuration.TelemetryIntervalMilliseconds;
                try
                {
                    PostCoreEvent("game.state", _router.GetState());
                }
                catch
                {
                    PostCoreEvent("game.state", JValue.CreateNull());
                }
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs args)
        {
            var toggleKeyPressed = args.KeyCode == _toggleKey;
            var isPhysicalF9 = toggleKeyPressed && _toggleKey == Keys.F9;
            var hasDefaultF9Owner = isPhysicalF9 &&
                ReactorHostApi.HasExtensionCapability(
                    ReactorExtensionCapabilities.DefaultF9MenuOwner);

            // A passive initializer can make the host physically visible
            // without representing an extension menu. Do not include raw
            // _overlay.IsVisible here: the default owner still needs Reactor
            // to arm its provider-input intent when F9 opens over that
            // initializer. Requested/provider state, on the other hand,
            // means the extension owns this edge and is solely responsible
            // for closing or replacing its presentation.
            var defaultOwnerPresentationOrIntentActive =
                _overlayRequestedVisible ||
                _pendingProviderInputIntentEpoch > 0 ||
                _boundProviderInputIntentEpoch > 0 ||
                _menuRevealGate.PendingPresentationId != null ||
                _providerPresentationCommitGate.PendingPresentationId != null ||
                !string.IsNullOrWhiteSpace(
                    _userIntentFallbackPresentationId);
            var managedF9Disposition =
                MenuPresentationPolicy.ResolveManagedF9Edge(
                    isPhysicalF9,
                    hasDefaultF9Owner,
                    defaultOwnerPresentationOrIntentActive);

            if (managedF9Disposition ==
                ManagedF9EdgeDisposition.YieldToDefaultOwner)
            {
                TraceRuntime(
                    "toggle_owned_by_default_menu_extension",
                    $"key={_toggleKey} game_time={Game.GameTime} " +
                    "action=yield-no-mutation");
                return;
            }

            // Closing is a safety boundary, not a gameplay action. Process it
            // before Story readiness, native ownership, or debounce so a
            // pending/visible generic surface can never trap input behind
            // those gates. A registered default F9 extension never enters
            // this branch; that owner handles its own F9 close edge.
            if (managedF9Disposition ==
                    ManagedF9EdgeDisposition.GenericToggle &&
                toggleKeyPressed &&
                (_overlayRequestedVisible ||
                 _overlay.IsVisible ||
                 _pendingProviderInputIntentEpoch > 0 ||
                 _boundProviderInputIntentEpoch > 0))
            {
                CloseOverlay("toggle");
                return;
            }
            if (args.KeyCode == Keys.Escape &&
                (_pendingProviderInputIntentEpoch > 0 ||
                 _boundProviderInputIntentEpoch > 0 ||
                 !string.IsNullOrWhiteSpace(
                     _userIntentFallbackPresentationId)))
            {
                CloseOverlay(
                    "escape-user-intent-fallback",
                    _userIntentFallbackPresentationId ??
                        _boundProviderInputIntentPresentationId);
                return;
            }

            if (!_storyModeReady)
            {
                return;
            }

            if (toggleKeyPressed)
            {
                if (_toggleKey == Keys.F9 &&
                    !PreloadHandoff.ManagedOwnsF9(Process.GetCurrentProcess().Id))
                {
                    TraceRuntime(
                        "toggle_deferred_to_native_bootstrap",
                        $"key={_toggleKey} game_time={Game.GameTime}");
                    return;
                }

                if (Game.GameTime < _nextToggleAt)
                {
                    return;
                }

                _nextToggleAt = Game.GameTime + ToggleDebounceMilliseconds;
                if (managedF9Disposition ==
                    ManagedF9EdgeDisposition.ArmDefaultOwnerInputIntent)
                {
                    ArmProviderInputIntent();
                    TraceRuntime(
                        "toggle_deferred_to_default_menu_extension",
                        $"key={_toggleKey} game_time={Game.GameTime} " +
                        $"intent_epoch={_pendingProviderInputIntentEpoch}");
                    return;
                }
                _inputMode = MenuPresentationPolicy.InitialInputMode;
                ShowOverlay("toggle");
            }
            else if (args.KeyCode == Keys.Escape && _overlay.IsVisible)
            {
                if (!string.IsNullOrWhiteSpace(
                        _userIntentFallbackPresentationId))
                {
                    CloseOverlay(
                        "escape-user-intent-fallback",
                        _userIntentFallbackPresentationId);
                }
                else if (MenuPresentationPolicy.OwnsBack(_inputMode))
                {
                    // The disabled frontend-cancel control is translated to
                    // input.action by EmitSemanticInput on this same frame.
                    // Keeping the overlay open lets the active menu own Back.
                    return;
                }
                else
                {
                    CloseOverlay("escape");
                }
            }
            else if (_menuInputLease.CanForwardRawBrowserKey(
                         _overlay.IsVisible,
                         MenuPresentationPolicy.UsesPointer(_inputMode)))
            {
                // The WebView2 fallback intentionally leaves GTA as the
                // foreground HWND. Forward bounded key identity so focused
                // browser editors remain usable without stealing game focus.
                _overlay.PostEvent("input.keyboard", new JObject
                {
                    ["code"] = args.KeyCode.ToString(),
                    ["shift"] = args.Shift,
                    ["control"] = args.Control,
                    ["alt"] = args.Alt,
                });
                if (!_windowedKeyboardInputTraced &&
                    (string.Equals(_overlay.RendererName, "WebView2 window", StringComparison.Ordinal) ||
                     string.Equals(_overlay.RendererName, "Bootstrap WebView2", StringComparison.Ordinal)))
                {
                    _windowedKeyboardInputTraced = true;
                    TraceRuntime(
                        "windowed_keyboard_input_ready",
                        $"code={args.KeyCode} input_mode={_inputMode}");
                }
            }
        }

        private void ArmProviderInputIntent()
        {
            CancelProviderInputIntent();
            if (!(_overlay is IProviderInputIntentRuntime intentRuntime))
                return;

            var processId = Process.GetCurrentProcess().Id;
            var epoch = ++_providerInputIntentEpoch;
            var token = new ProviderInputIntentToken(
                processId,
                epoch,
                ProviderInputIntentGate.DefaultArmLifetimeMilliseconds);
            if (!intentRuntime.ArmProviderInputIntent(token))
            {
                TraceRuntime(
                    "provider_input_intent_arm_rejected",
                    $"pid={processId} epoch={epoch}");
                return;
            }

            _pendingProviderInputIntentEpoch = epoch;
            _pendingProviderInputIntentExpiresAt =
                _scriptTimer.ElapsedMilliseconds +
                ProviderInputIntentGate.DefaultArmLifetimeMilliseconds;
            TraceRuntime(
                "provider_input_intent_armed",
                $"pid={processId} epoch={epoch} " +
                $"deadline_ms={_pendingProviderInputIntentExpiresAt}");
        }

        private void TryBindProviderInputIntent(
            string extensionId,
            string presentationId)
        {
            if (_pendingProviderInputIntentEpoch <= 0 ||
                _scriptTimer.ElapsedMilliseconds >
                    _pendingProviderInputIntentExpiresAt ||
                !ReactorHostApi.ExtensionHasCapability(
                    extensionId,
                    ReactorExtensionCapabilities.DefaultF9MenuOwner) ||
                !(_overlay is IProviderInputIntentRuntime intentRuntime))
            {
                if (_pendingProviderInputIntentEpoch > 0 &&
                    _scriptTimer.ElapsedMilliseconds >
                        _pendingProviderInputIntentExpiresAt)
                {
                    CancelProviderInputIntent();
                }
                return;
            }

            var processId = Process.GetCurrentProcess().Id;
            var epoch = _pendingProviderInputIntentEpoch;
            var bound = intentRuntime.BindProviderInputIntent(
                processId,
                epoch,
                presentationId);
            _pendingProviderInputIntentEpoch = 0;
            _pendingProviderInputIntentExpiresAt = 0;
            _boundProviderInputIntentEpoch = bound ? epoch : 0;
            _boundProviderInputIntentPresentationId = bound
                ? presentationId
                : null;
            TraceRuntime(
                bound
                    ? "provider_input_intent_bound"
                    : "provider_input_intent_bind_rejected",
                $"pid={processId} epoch={epoch} " +
                $"presentation={presentationId} extension={extensionId}");
        }

        private void CancelProviderInputIntent()
        {
            var epoch = _pendingProviderInputIntentEpoch > 0
                ? _pendingProviderInputIntentEpoch
                : _boundProviderInputIntentEpoch;
            if (epoch <= 0)
                return;
            if (_overlay is IProviderInputIntentRuntime intentRuntime)
            {
                intentRuntime.CancelProviderInputIntent(
                    Process.GetCurrentProcess().Id,
                    epoch);
            }
            _pendingProviderInputIntentEpoch = 0;
            _pendingProviderInputIntentExpiresAt = 0;
            _boundProviderInputIntentEpoch = 0;
            _boundProviderInputIntentPresentationId = null;
        }

        private void CloseOverlay(
            string reason,
            string? expectedPresentationId = null)
        {
            CancelProviderInputIntent();
            _userIntentFallbackPresentationId = null;
            CancelStartupIntentIfActive(reason);
            _menuRevealGate.Cancel();
            CancelPendingProviderPresentation(reason);
            // An explicit close owns dismissal immediately. Do not let a
            // preparation token survive while the host is already hidden and
            // suppress a later, unrelated close edge.
            _presentationPreparationDismissalSuppressionId = null;
            if (_overlayRequestedVisible || _overlay.IsVisible)
            {
                ReactorHostApi.NotifyLifecycle(
                    ReactorLifecycleStage.OverlayClosing,
                    new JObject { ["gameTime"] = Game.GameTime });
            }
            if (_firstRunPending)
            {
                try
                {
                    var markerDirectory = Path.GetDirectoryName(_firstRunMarkerPath);
                    if (!string.IsNullOrEmpty(markerDirectory))
                    {
                        Directory.CreateDirectory(markerDirectory);
                    }
                    File.WriteAllText(_firstRunMarkerPath, DateTime.UtcNow.ToString("O"));
                    _firstRunPending = false;
                }
                catch
                {
                    // The splash can still close if local application data is
                    // unavailable; it will simply be shown again next launch.
                }
            }
            _overlayRequestedVisible = false;
            _inputMode = MenuPresentationPolicy.HiddenInputMode;
            // The process-separated bootstrap host authors one generation-
            // bound host.surface=none when this visibility request reaches it.
            // Sending an unversioned browser event as well caused two reset
            // pulses per close. In-process fallback renderers still need the
            // direct event because they have no authoritative host boundary.
            if (!HasAuthoritativeHostSurfaceBoundary())
            {
                _overlay.PostEvent(
                    "host.surface",
                    new JObject { ["mode"] = "none" });
            }
            _overlay.SetVisible(false);
            // The registry retains a dismissal-requested generation until the
            // host has issued its hide request.  Acknowledge the exact
            // extension-owned generation here so a later F9 cannot race a
            // queued close, and so the browser receives one dismissal event.
            PublishActiveMenuDismissed(
                expectedPresentationId == null
                    ? "overlay-hidden"
                    : reason,
                expectedPresentationId);
            TraceRuntime(
                "overlay_hide_requested",
                $"reason={reason} game_time={Game.GameTime} " +
                $"input_mode={_inputMode} " +
                $"script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
        }

        private void ShowOverlay(string reason)
        {
            if (_overlayRequestedVisible)
            {
                return;
            }

            if (string.Equals(reason, "api", StringComparison.Ordinal) &&
                !OverlayApiStatePolicy.CanExposeVisibleSurface(
                    visible: true,
                    _inputMode))
            {
                throw new InvalidOperationException(
                    "The overlay API cannot show a surface while game input mode is active.");
            }

            _overlayRequestedVisible = true;
            if (!string.Equals(reason, "extension-menu", StringComparison.Ordinal))
            {
                var surfaceGeneration = NextHostSurfaceGeneration();
                _overlay.PostEvent(
                    "host.surface",
                    new JObject
                    {
                        ["mode"] = string.Equals(reason, "first_run", StringComparison.Ordinal)
                            ? "setup-status"
                            : "about",
                        ["generation"] = surfaceGeneration,
                    });
            }
            ReactorHostApi.NotifyLifecycle(
                ReactorLifecycleStage.OverlayOpening,
                new JObject { ["reason"] = reason, ["gameTime"] = Game.GameTime });
            _overlay.SetVisible(true);
            TraceRuntime(
                "overlay_show_requested",
                $"reason={reason} game_time={Game.GameTime} " +
                $"script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
        }

        private int NextHostSurfaceGeneration()
        {
            _hostSurfaceGeneration = _hostSurfaceGeneration == int.MaxValue
                ? 1
                : _hostSurfaceGeneration + 1;
            return _hostSurfaceGeneration;
        }

        private void UpdateStoryModeReadiness()
        {
            if (_storyModeReady)
            {
                var playable = IsPlayableStoryMode();
                if (playable != _storyModePlayable)
                {
                    _storyModePlayable = playable;
                    ReactorHostApi.NotifyLifecycle(
                        playable ? ReactorLifecycleStage.Resumed : ReactorLifecycleStage.StoryUnavailable,
                        new JObject { ["gameTime"] = Game.GameTime });
                    PostLifecycle(playable ? "story-ready" : "story-loading", playable ? "resumed" : "unavailable");
                    if (playable)
                    {
                        TryCompleteRuntimeReadyHandoff();
                    }
                }

                var paused = Game.IsPaused;
                if (paused != _gameWasPaused)
                {
                    _gameWasPaused = paused;
                    TraceRuntime(
                        "game_pause_state_changed",
                        $"paused={paused} game_time={Game.GameTime} " +
                        $"overlay_visible={_overlay.IsVisible} input_mode={_inputMode}");
                    ReactorHostApi.NotifyLifecycle(
                        paused ? ReactorLifecycleStage.Suspended : ReactorLifecycleStage.Resumed,
                        new JObject { ["gameTime"] = Game.GameTime, ["reason"] = "pause" });
                    PostLifecycle(paused ? "paused" : "story-ready", paused ? "game-paused" : "game-resumed");
                }
                return;
            }

            if (!IsPlayableStoryMode())
            {
                if (_storyModeReadyCandidateAt >= 0)
                {
                    TraceRuntime(
                        "story_mode_candidate_reset",
                        $"candidate_game_time={_storyModeReadyCandidateAt} current_game_time={Game.GameTime}");
                }
                _storyModeReadyCandidateAt = -1;
                return;
            }

            if (_storyModeReadyCandidateAt < 0)
            {
                _storyModeReadyCandidateAt = Game.GameTime;
                TraceRuntime(
                    "story_mode_candidate",
                    $"game_time={_storyModeReadyCandidateAt} " +
                    $"script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
                return;
            }

            if (Game.GameTime - _storyModeReadyCandidateAt < StoryModeStableMilliseconds)
            {
                return;
            }

            _storyModeReady = true;
            _storyModePlayable = true;
            _gameWasPaused = Game.IsPaused;
            ReactorHostApi.NotifyLifecycle(
                ReactorLifecycleStage.StoryReady,
                new JObject { ["gameTime"] = Game.GameTime });
            PostLifecycle("story-ready", "initial");
            TraceRuntime(
                "story_mode_ready",
                $"candidate_duration_ms={Game.GameTime - _storyModeReadyCandidateAt} " +
                $"game_time={Game.GameTime} script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
            if (_firstRunPending || _configuration.StartVisible)
            {
                ShowOverlay(_firstRunPending ? "first_run" : "configuration");
            }
            TryCompleteRuntimeReadyHandoff();
        }

        private static bool IsPlayableStoryMode()
        {
            if (Game.IsLoading)
            {
                return false;
            }

            var character = Game.Player.Character;
            return character != null
                && character.Exists()
                && Function.Call<bool>(Hash.IS_PLAYER_CONTROL_ON, Game.Player.Handle)
                && Function.Call<bool>(Hash.IS_SCREEN_FADED_IN)
                && !Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE);
        }

        private MenuInputLeaseFrame UpdateMenuInputLease(
            bool overlayPresented,
            long elapsedMilliseconds)
        {
            var wantsInteractiveInput =
                MenuPresentationPolicy.ShouldAcquireManagedInputLease(
                    _overlayRequestedVisible,
                    overlayPresented,
                    _inputMode);

            // An existing lease has already disabled all three input groups at
            // the top of this tick, so all observations below use only GTA's
            // disabled-control APIs. A newly acquired lease is disabled before
            // its physical state is seeded.
            var relevantInputsNeutral = _menuInputLease.SuppressGameInput
                ? AreRelevantMenuInputsNeutral()
                : false;
            var frame = _menuInputLease.Advance(
                wantsInteractiveInput,
                relevantInputsNeutral,
                elapsedMilliseconds);

            if (frame.SuppressGameInput)
                SuppressGameInput();
            if (frame.SeedPhysicalState ||
                (frame.PreviousState == MenuInputLeaseState.Arming &&
                 frame.State == MenuInputLeaseState.Interactive))
            {
                SeedDisabledPointerStates();
            }
            if (frame.StateChanged)
            {
                TraceRuntime(
                    "menu_input_lease_transition",
                    $"previous={frame.PreviousState} state={frame.State} " +
                    $"input_mode={_inputMode} requested_visible={_overlayRequestedVisible} " +
                    $"actual_visible={overlayPresented} neutral={relevantInputsNeutral} " +
                    $"game_time={Game.GameTime}");
            }

            return frame;
        }

        private void SuppressGameInputForActiveLease()
        {
            if (_menuInputLease.SuppressGameInput)
                SuppressGameInput();
        }

        private static void SuppressGameInput()
        {
            foreach (var group in GameplayMenuInputBindings.ControlGroups)
            {
                Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, group);
                // Keep the pause controls explicit even though the group-wide
                // suppression currently covers them. This documents and
                // preserves the fail-closed frontend boundary if GTA changes
                // how one group aliases frontend input in a future build.
                Function.Call(
                    Hash.DISABLE_CONTROL_ACTION,
                    group,
                    GameplayMenuInputBindings.FrontendPauseControl,
                    true);
                Function.Call(
                    Hash.DISABLE_CONTROL_ACTION,
                    group,
                    GameplayMenuInputBindings.FrontendPauseAlternateControl,
                    true);
            }
            Function.Call(Hash.DISABLE_FRONTEND_THIS_FRAME);
        }

        private static bool AreRelevantMenuInputsNeutral()
        {
            foreach (var control in GameplayMenuInputBindings.RelevantControls)
            {
                if (IsDisabledControlPressed(control))
                    return false;
            }
            return true;
        }

        private static bool IsDisabledControlPressed(int control)
        {
            foreach (var group in GameplayMenuInputBindings.ControlGroups)
            {
                if (Function.Call<bool>(
                        Hash.IS_DISABLED_CONTROL_PRESSED,
                        group,
                        control))
                    return true;
            }
            return false;
        }

        private static bool IsDisabledControlJustPressed(int control)
        {
            foreach (var group in GameplayMenuInputBindings.ControlGroups)
            {
                if (Function.Call<bool>(
                        Hash.IS_DISABLED_CONTROL_JUST_PRESSED,
                        group,
                        control))
                    return true;
            }
            return false;
        }

        private void SeedDisabledPointerStates()
        {
            // The edge policy seeds all primary/fallback providers on its
            // first eligible Interactive+foreground sample. Do not poll the
            // gameplay attack or physical button while the lease is Arming.
            _pointerButtonPolicy.Reset();
            _pointerForegroundBoundaryActive = false;
            _pointerSourceTraceInitialized = false;
            _disabledCursorCancelDown = IsDisabledControlPressed(
                GameplayMenuInputBindings.CursorCancelControl);
        }

        private void EmitPointerInput()
        {
            _disabledCursorCancelBackPostedThisFrame = false;
            var strictGameForeground = NativeMethods.IsGameForeground(_gtaWindow);
            var trustedProviderForeground =
                !strictGameForeground &&
                (_overlay as IInteractionForegroundRuntime)?
                    .IsTrustedProviderForeground == true;
            var gameForeground = WindowedInputPolicy.AllowsManagedPointerSampling(
                strictGameForeground,
                _menuInputLease.State == MenuInputLeaseState.Interactive,
                _overlayRequestedVisible,
                _overlay.IsVisible,
                trustedProviderForeground);
            if (!gameForeground)
            {
                _pointerButtonPolicy.Observe(
                    eligible: false,
                    cursorAcceptDown: false,
                    gameplayAttackDown: false,
                    physicalLeftButtonDown: false,
                    physicalPressedSinceLastSample: false);
                if (_pointerForegroundBoundaryActive)
                {
                    _pointerForegroundBoundaryActive = false;
                    _pointerSourceTraceInitialized = false;
                    TraceRuntime(
                        "pointer_input_boundary",
                        $"active=false lease={_menuInputLease.State} " +
                        $"game_foreground=false trusted_provider=false " +
                        $"game_time={Game.GameTime}");
                }
                return;
            }

            if (!_pointerForegroundBoundaryActive)
            {
                _pointerForegroundBoundaryActive = true;
                TraceRuntime(
                    "pointer_input_boundary",
                    $"active=true lease={_menuInputLease.State} " +
                    $"game_foreground={strictGameForeground} " +
                    $"trusted_provider={trustedProviderForeground} " +
                    $"game_time={Game.GameTime}");
            }

            Hud.ShowCursorThisFrame();
            var cursorX = Game.GetDisabledControlValueNormalized(GTAControl.CursorX);
            var cursorY = Game.GetDisabledControlValueNormalized(GTAControl.CursorY);
            var control237Down = IsDisabledControlPressed(
                GameplayMenuInputBindings.CursorAcceptControl);
            var control24Down = IsDisabledControlPressed(
                GameplayMenuInputBindings.GameplayAttackControl);
            NativeMethods.SamplePhysicalLeftButton(
                out var physicalLeftDown,
                out var physicalPressedSinceLastSample);
            if (!_pointerSourceTraceInitialized ||
                _lastControl237Down != control237Down ||
                _lastControl24Down != control24Down ||
                _lastPhysicalLeftDown != physicalLeftDown ||
                physicalPressedSinceLastSample)
            {
                _pointerSourceTraceInitialized = true;
                _lastControl237Down = control237Down;
                _lastControl24Down = control24Down;
                _lastPhysicalLeftDown = physicalLeftDown;
                TraceRuntime(
                    "pointer_input_sample",
                    $"control_237_down={control237Down} control_24_down={control24Down} " +
                    $"physical_down={physicalLeftDown} " +
                    $"physical_transition={physicalPressedSinceLastSample} " +
                    $"lease={_menuInputLease.State} " +
                    $"game_foreground={strictGameForeground} " +
                    $"trusted_provider={trustedProviderForeground} " +
                    $"game_time={Game.GameTime}");
            }
            var pointerButton = _pointerButtonPolicy.Observe(
                eligible: true,
                cursorAcceptDown: control237Down,
                gameplayAttackDown: control24Down,
                physicalLeftButtonDown: physicalLeftDown,
                physicalPressedSinceLastSample);
            var cursorCancelDown = IsDisabledControlPressed(
                GameplayMenuInputBindings.CursorCancelControl);
            var cursorCancelPressed = GameplayMenuInputBindings.IsButtonPressEdge(
                cursorCancelDown,
                _disabledCursorCancelDown);
            _disabledCursorCancelDown = cursorCancelDown;
            if (cursorCancelPressed)
            {
                _disabledCursorCancelBackPostedThisFrame = true;
                TraceRuntime(
                    "semantic_input",
                    $"action=back source=game-disabled-control game_time={Game.GameTime}");
                PostInputAction("back", "game-disabled-control");
            }
            var wheel = IsDisabledControlJustPressed(
                    GameplayMenuInputBindings.CursorScrollUpControl)
                ? 120
                : IsDisabledControlJustPressed(
                    GameplayMenuInputBindings.CursorScrollDownControl) ? -120 : 0;
            _overlay.UpdateCursor(
                cursorX,
                cursorY,
                pointerButton.Pressed,
                pointerButton.Released,
                wheel);
            if (pointerButton.Pressed || pointerButton.Released)
            {
                // Trace success only after the runtime accepted the frame.
                // If UpdateCursor throws, the ordinary script failure path
                // records that error instead of claiming a forwarded click.
                TraceRuntime(
                    "pointer_input_edge",
                    $"sequence={++_pointerEdgeSequence} " +
                    $"pressed={pointerButton.Pressed} released={pointerButton.Released} " +
                    $"down={pointerButton.Down} sources={(int)pointerButton.Sources} " +
                    $"control_237_down={control237Down} control_24_down={control24Down} " +
                    $"physical_down={physicalLeftDown} " +
                    $"physical_transition={physicalPressedSinceLastSample} " +
                    $"lease={_menuInputLease.State} " +
                    $"game_foreground={strictGameForeground} " +
                    $"trusted_provider={trustedProviderForeground} " +
                    $"host_forwarded=true game_time={Game.GameTime}");
            }
        }

        private void EmitSemanticInput()
        {
            foreach (var binding in GameplayMenuInputBindings.All)
            {
                if (!GameplayMenuInputBindings.ShouldEmitGameSemanticAction(
                        binding.Action,
                        _disabledCursorCancelBackPostedThisFrame))
                    continue;
                EmitControlAction(binding.Control, binding.Action);
            }
        }

        private void EmitControlAction(int control, string action)
        {
            // The lease disables all GTA input groups before sampling. Never
            // inspect enabled-control state here: doing so lets the same
            // physical edge drive both React and GTA's pause/frontend menus.
            var pressed = IsDisabledControlJustPressed(control);
            if (pressed)
            {
                TraceRuntime(
                    "semantic_input",
                    $"action={action} source=game game_time={Game.GameTime}");
                PostInputAction(action, "game");
            }
        }

        private void PostInputAction(string action, string source) => PostCoreEvent(
            "input.action",
            new JObject
            {
                ["action"] = action,
                ["phase"] = "pressed",
                ["source"] = source,
                ["timestamp"] = Game.GameTime,
            });

        private void PostLifecycle(string phase, string reason)
        {
            var previous = _lifecyclePhase;
            _lifecyclePhase = phase;
            PostCoreEvent(
                "runtime.lifecycle",
                new JObject
                {
                    ["phase"] = phase,
                    ["previousPhase"] = previous,
                    ["timestamp"] = Game.GameTime,
                    ["reason"] = reason,
                });
        }

        private void MarkBrowserReady() => MarkBrowserReady("page-handshake", 0);

        private void MarkBrowserReady(string source, int contentGeneration = 0)
        {
            if (_overlay is IContentGenerationRuntime generationRuntime)
            {
                if (!generationRuntime.TryGetReadyContentGeneration(out var currentGeneration))
                {
                    // OverlayRuntime also fronts the ordinary windowed/DirectX
                    // paths. Those have no bootstrap generation and continue
                    // to use the page handshake. Once a bootstrap generation
                    // has been observed, however, losing it must fail closed.
                    if (_browserContentGeneration != 0 || contentGeneration != 0)
                    {
                        TraceRuntime(
                            "browser_ready_ignored",
                            $"source={source} reason=content_generation_not_ready");
                        return;
                    }
                }
                else if (contentGeneration > 0 && contentGeneration != currentGeneration)
                {
                    TraceRuntime(
                        "browser_ready_ignored",
                        $"source={source} reason=stale_content_generation " +
                        $"generation={contentGeneration} current_generation={currentGeneration}");
                    return;
                }
                else
                {
                    contentGeneration = currentGeneration;
                }
            }

            if (_browserReady && _browserContentGeneration == contentGeneration)
            {
                return;
            }

            if (_browserContentGeneration != contentGeneration)
                _runtimeReadyLeaseRequested = false;
            _browserReady = true;
            _browserContentGeneration = contentGeneration;
            ReactorHostApi.NotifyLifecycle(
                ReactorLifecycleStage.BrowserReady,
                new JObject { ["gameTime"] = Game.GameTime });
            PostLifecycle("browser-ready", source);
            TraceRuntime(
                "browser_ready",
                $"source={source} generation={contentGeneration} game_time={Game.GameTime} " +
                $"script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
            TryCompleteRuntimeReadyHandoff();
        }

        private void SynchronizeBrowserContentGeneration()
        {
            if (!(_overlay is IContentGenerationRuntime generationRuntime))
                return;

            if (generationRuntime.TryGetReadyContentGeneration(out var generation))
            {
                if (!_browserReady || _browserContentGeneration != generation)
                    MarkBrowserReady("bootstrap-generation-ready", generation);
                return;
            }

            if (_browserContentGeneration == 0)
                return;
            TraceRuntime(
                "browser_generation_invalidated",
                $"previous_generation={_browserContentGeneration} " +
                $"runtime_ready_handoff_attempted={_runtimeReadyHandoffAttempted}");
            CancelPendingProviderPresentation("browser-generation-invalidated");
            _inputMode = MenuPresentationPolicy.HiddenInputMode;
            _browserReady = false;
            _browserContentGeneration = 0;
            _runtimeReadyLeaseRequested = false;
        }

        private void TryCompleteRuntimeReadyHandoff()
        {
            if (_runtimeReadyHandoffAttempted ||
                !_storyModeReady ||
                !_storyModePlayable ||
                !_browserReady)
            {
                return;
            }

            var processId = Process.GetCurrentProcess().Id;
            if (_overlay is IContentGenerationRuntime generationRuntime &&
                _browserContentGeneration > 0)
            {
                if (!generationRuntime.TryGetReadyContentGeneration(out var generation) ||
                    generation != _browserContentGeneration)
                {
                    _browserReady = false;
                    _browserContentGeneration = 0;
                    _runtimeReadyLeaseRequested = false;
                    TraceRuntime(
                        "runtime_ready_handoff_deferred",
                        "reason=content_generation_changed");
                    return;
                }

                if (!_runtimeReadyLeaseRequested)
                {
                    ReleaseBootstrapSurfaceIfUnclaimed();
                    _runtimeReadyLeaseRequested = true;
                }
                if (_overlay is IBootstrapSurfaceRuntime bootstrapRuntime &&
                    bootstrapRuntime.BootstrapSurfaceRetirementPending)
                {
                    return;
                }
                var state = generationRuntime.AdvanceRuntimeReadyHandoff(generation);
                if (state == RuntimeReadyHandoffState.Pending)
                    return;
                if (state == RuntimeReadyHandoffState.Unavailable ||
                    state == RuntimeReadyHandoffState.StaleGeneration)
                {
                    _browserReady = false;
                    _browserContentGeneration = 0;
                    _runtimeReadyLeaseRequested = false;
                    TraceRuntime(
                        "runtime_ready_handoff_deferred",
                        $"reason=authoritative_lease_{state}");
                    return;
                }

                _runtimeReadyHandoffAttempted = true;
                if (state == RuntimeReadyHandoffState.Signaled)
                    ReleaseBootstrapSurfaceIfUnclaimed();
                TraceRuntime(
                    state == RuntimeReadyHandoffState.Signaled
                        ? "runtime_ready_handoff_signaled"
                        : "runtime_ready_handoff_unavailable",
                    $"pid={processId} generation={generation} " +
                    $"lease_state={state} game_time={Game.GameTime} " +
                    $"story_mode_ready={_storyModeReady} browser_ready={_browserReady} " +
                    $"script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
                return;
            }

            ReleaseBootstrapSurfaceIfUnclaimed();
            _runtimeReadyHandoffAttempted = true;
            var signaled = PreloadHandoff.TrySignalRuntimeReady(processId);
            TraceRuntime(
                signaled ? "runtime_ready_handoff_signaled" : "runtime_ready_handoff_unavailable",
                $"pid={processId} game_time={Game.GameTime} " +
                $"story_mode_ready={_storyModeReady} browser_ready={_browserReady} " +
                $"script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
        }

        private void ReleaseBootstrapSurfaceIfUnclaimed()
        {
            var processId = Process.GetCurrentProcess().Id;
            var defaultMenuIntentActive =
                PreloadHandoff.IsDefaultMenuIntentActive(processId);
            if (!MenuPresentationPolicy.ShouldReleaseBootstrapSurface(
                    _overlayRequestedVisible,
                    CurrentHostSurface,
                    defaultMenuIntentActive))
            {
                if (defaultMenuIntentActive &&
                    HostSurfaceMode.IsInitializing(CurrentHostSurface))
                {
                    TraceRuntime(
                        "bootstrap_initializer_preserved",
                        $"pid={processId} reason=pending-default-menu " +
                        "handoff=matching-presentation-paint");
                }
                return;
            }

            // This is an ownership transition, not a user close. The
            // process-separated host must retire its logical surface without
            // cancelling a pending default-menu intent. That guarantees the
            // An unclaimed surface disappears at RuntimeReady. A claimed
            // initializer instead remains visible until its typed GBAY
            // replacement reaches its exact provider-paint commit boundary.
            if (_overlay is IBootstrapSurfaceRuntime bootstrapRuntime)
                bootstrapRuntime.RetireBootstrapSurface(hide: true);
            else
            {
                _overlay.PostEvent(
                    "host.surface",
                    new JObject { ["mode"] = "none" });
                _overlay.SetVisible(false);
            }
            TraceRuntime(
                "bootstrap_surface_released",
                $"game_time={Game.GameTime} first_run_pending={_firstRunPending} " +
                $"script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
        }

        private void RetireBootstrapSurfaceForPresentation()
        {
            var currentSurface = CurrentHostSurface;
            if (string.Equals(
                    HostSurfaceMode.Normalize(currentSurface),
                    HostSurfaceMode.None,
                    StringComparison.Ordinal))
                return;

            if (_overlay is IBootstrapSurfaceRuntime bootstrapRuntime)
                bootstrapRuntime.RetireBootstrapSurface(hide: false);
            else
                _overlay.PostEvent(
                    "host.surface",
                    new JObject { ["mode"] = "none" });

            TraceRuntime(
                "bootstrap_surface_superseded",
                $"previous_surface={currentSurface} game_time={Game.GameTime}");
        }

        private void PostCoreEvent(string eventName, JToken? payload)
        {
            _router.RememberEvent(eventName, payload);
            _overlay.PostEvent(eventName, payload);
        }

        private void DrainExtensionEvents()
        {
            foreach (var record in ReactorHostApi.DrainEvents().OfType<JObject>())
            {
                var eventName = record.Value<string>("event");
                if (eventName == null)
                {
                    continue;
                }
                var payload = record["payload"];
                if (_router.ShouldPublishEvent(eventName, payload))
                {
                    _overlay.PostEvent(eventName, payload);
                }
            }
        }

        private void DrainMenuPresentations()
        {
            foreach (var record in ReactorHostApi.DrainMenuPresentations().OfType<JObject>())
            {
                if (!MenuPresentationPolicy.TryCreatePayload(record, out var payload) || payload == null)
                    continue;
                var incomingPresentationId =
                    payload.Value<string>("presentationId")!;
                var isStartupIntent =
                    MenuPresentationPolicy.TryGetStartupIntentProcessId(
                        payload,
                        out var startupIntentProcessId);
                if (isStartupIntent &&
                    !PreloadHandoff.CanDispatchDefaultMenuIntent(
                        startupIntentProcessId))
                {
                    TraceRuntime(
                        "menu_presentation_startup_cancelled_before_dispatch",
                        $"presentation={payload.Value<string>("presentationId")} " +
                        $"pid={startupIntentProcessId} game_time={Game.GameTime}");
                    continue;
                }
                var overlayVisible = _overlay.IsVisible;
                var currentHostSurface = CurrentHostSurface;
                if (MenuPresentationPolicy.RequiresHideBeforeDispatch(
                        _overlayRequestedVisible,
                        overlayVisible,
                        currentHostSurface))
                {
                    // Queue the atomic native hide before replacing browser
                    // content. This keeps About/setup/older menus from exposing
                    // the new React loading frame or chroma-key intermediates.
                    _menuRevealGate.Cancel();
                    _overlayRequestedVisible = false;
                    _presentationPreparationDismissalSuppressionId = overlayVisible
                        ? payload.Value<string>("presentationId")
                        : null;
                    if (_overlay is IReasonedVisibilityRuntime reasonedVisibility)
                    {
                        reasonedVisibility.SetVisible(
                            false,
                            HostVisibilityReason.PresentationPreparation);
                    }
                    else
                    {
                        _overlay.SetVisible(false);
                    }
                    TraceRuntime(
                        "menu_presentation_surface_hide_requested",
                        $"requested_visible=true actual_visible={overlayVisible} " +
                        $"host_surface={currentHostSurface} " +
                        $"game_time={Game.GameTime}");
                }
                else if (overlayVisible &&
                         HostSurfaceMode.IsInitializing(currentHostSurface))
                {
                    TraceRuntime(
                        "menu_presentation_initializer_preserved",
                        $"host_surface={currentHostSurface} game_time={Game.GameTime}");
                }
                if (!ReactorHostApi.MarkMenuPresentationActive(
                        payload.Value<string>("extensionId")!,
                        payload.Value<string>("menuId")!,
                        payload.Value<string>("presentationId")!,
                        out var superseded))
                {
                    if (string.Equals(
                            _presentationPreparationDismissalSuppressionId,
                            payload.Value<string>("presentationId"),
                            StringComparison.Ordinal))
                    {
                        _presentationPreparationDismissalSuppressionId = null;
                    }
                    continue;
                }
                TryBindProviderInputIntent(
                    payload.Value<string>("extensionId")!,
                    incomingPresentationId);
                if (_providerPresentationCommitGate.PendingPresentationId != null &&
                    !string.Equals(
                        _providerPresentationCommitGate.PendingPresentationId,
                        incomingPresentationId,
                        StringComparison.Ordinal))
                {
                    CancelPendingProviderPresentation("replacement-dispatched");
                }
                if (superseded != null)
                {
                    superseded["reason"] = "superseded";
                    PostCoreEvent(MenuPresentationPolicy.DismissedEventName, superseded);
                }
                // Do not capture gameplay input or emit pointer traffic while
                // React is still building the replacement surface. Its exact
                // presentationReady acknowledgement begins the native paint
                // phase; interactive-menu mode starts only after the host
                // proves that same presentation reached provider pixels.
                _inputMode = MenuPresentationPolicy.PendingPresentationInputMode;
                // The current committed frame remains visible while React
                // prepares its replacement. Browser layout acknowledgement
                // and exact provider-paint proof both precede input activation.
                _menuRevealGate.Begin(
                    incomingPresentationId,
                    _scriptTimer.ElapsedMilliseconds);
                // A typed presentation supersedes the initializer. Stop status
                // refreshes before publishing it so a late startup event can
                // never replace or regress the real menu surface.
                if (isStartupIntent &&
                    !PreloadHandoff.TryCommitDefaultMenuIntentClaim(
                        startupIntentProcessId))
                {
                    _menuRevealGate.Cancel();
                    if (string.Equals(
                            _presentationPreparationDismissalSuppressionId,
                            payload.Value<string>("presentationId"),
                            StringComparison.Ordinal))
                    {
                        _presentationPreparationDismissalSuppressionId = null;
                    }
                    var cancelled = ReactorHostApi.TakeActiveMenuPresentation();
                    if (cancelled != null)
                    {
                        cancelled["reason"] = "startup-intent-cancelled";
                        PostCoreEvent(
                            MenuPresentationPolicy.DismissedEventName,
                            cancelled);
                    }
                    TraceRuntime(
                        "menu_presentation_startup_commit_rejected",
                        $"presentation={payload.Value<string>("presentationId")} " +
                        $"pid={startupIntentProcessId} game_time={Game.GameTime}");
                    continue;
                }
                _managedStartupStatusComplete = true;
                PostCoreEvent(MenuPresentationPolicy.EventName, payload);
                TraceRuntime(
                    "menu_presentation_dispatched",
                    $"extension={payload.Value<string>("extensionId")} " +
                    $"menu={payload.Value<string>("menuId")} " +
                    $"presentation={payload.Value<string>("presentationId")} " +
                    $"input_mode={_inputMode} " +
                    $"game_time={Game.GameTime}");
            }
        }

        private void CancelStartupIntentIfActive(string reason)
        {
            var processId = Process.GetCurrentProcess().Id;
            if (!PreloadHandoff.IsDefaultMenuIntentActive(processId))
                return;
            if (PreloadHandoff.TryCancelDefaultMenuIntent(processId))
            {
                TraceRuntime(
                    "startup_menu_intent_cancelled",
                    $"reason={reason} pid={processId} game_time={Game.GameTime}");
            }
        }

        private bool MarkMenuPresentationReady(string presentationId)
        {
            if (!_menuRevealGate.TryAccept(
                    presentationId,
                    _scriptTimer.ElapsedMilliseconds,
                    out var waitMilliseconds))
            {
                TraceRuntime(
                    "menu_presentation_ready_rejected",
                    $"presentation={presentationId} pending={_menuRevealGate.PendingPresentationId ?? "none"} " +
                    $"game_time={Game.GameTime}");
                return false;
            }

            if (!ReactorHostApi.CanMarkMenuPresentationReady(presentationId))
            {
                TraceRuntime(
                    "menu_presentation_ready_rejected",
                    $"presentation={presentationId} reason=active-presentation-mismatch " +
                    $"game_time={Game.GameTime}");
                return false;
            }

            _providerPresentationCommitGate.Begin(
                presentationId,
                _scriptTimer.ElapsedMilliseconds,
                waitMilliseconds);
            _providerRevealAfterCommitPresentationId =
                !_overlayRequestedVisible
                    ? presentationId
                    : null;
            TraceRuntime(
                "menu_presentation_browser_prepared",
                $"presentation={presentationId} wait_ms={waitMilliseconds} " +
                $"input_mode={_inputMode} game_time={Game.GameTime} " +
                "awaiting_provider_paint=true " +
                $"reveal_after_commit=" +
                $"{string.Equals(_providerRevealAfterCommitPresentationId, presentationId, StringComparison.Ordinal)}");

            // Return the accepted response to the browser before authorizing
            // managed input, revealing a hidden provider, or retiring the
            // initializer. The persistent host publishes a second, exact-ID
            // commit only after the response has painted into a fresh provider
            // texture. A cold reopen remains hidden until that proof arrives.
            return true;
        }

        private void TryAdvancePendingProviderPresentation(
            long elapsedMilliseconds)
        {
            var presentationId =
                _providerPresentationCommitGate.PendingPresentationId;
            if (presentationId == null)
                return;

            // The persistent host may prove an exact fresh provider texture
            // while a cold reopen is intentionally still hidden. The pending
            // input mode and exact registry identity remain the authority;
            // visibility is published only after the proof is consumed below.
            if (!string.Equals(
                    _inputMode,
                    MenuPresentationPolicy.PendingPresentationInputMode,
                    StringComparison.Ordinal))
                return;

            if (!ReactorHostApi.CanMarkMenuPresentationReady(presentationId))
            {
                var aborted = AbortPresentationTransfer(
                    presentationId,
                    "provider-paint-active-mismatch");
                TraceRuntime(
                    "menu_provider_paint_rejected",
                    $"presentation={presentationId} " +
                    $"reason=active-presentation-mismatch fail_closed=true exact_abort={aborted}");
                return;
            }

            if (!(_overlay is IProviderPresentationCommitRuntime commitRuntime) ||
                !commitRuntime.IsProviderPresentationCommitted(presentationId))
                return;

            _userIntentFallbackPresentationId =
                _overlay is IProviderInputIntentRuntime intentRuntime &&
                intentRuntime.IsProviderPresentationAuthorizedByUserIntent(
                    presentationId)
                    ? presentationId
                    : null;
            _boundProviderInputIntentEpoch = 0;
            _boundProviderInputIntentPresentationId = null;

            if (!_providerPresentationCommitGate.TryCommit(
                    presentationId,
                    elapsedMilliseconds,
                    out var providerCommitWaitMilliseconds,
                    out var browserPreparationWaitMilliseconds))
                return;

            if (!ReactorHostApi.MarkMenuPresentationReady(presentationId))
            {
                var aborted = AbortPresentationTransfer(
                    presentationId,
                    "provider-paint-active-mismatch-after-proof");
                TraceRuntime(
                    "menu_provider_paint_rejected",
                    $"presentation={presentationId} " +
                    "reason=active-presentation-mismatch-after-proof fail_closed=true " +
                    $"exact_abort={aborted}");
                return;
            }

            _inputMode = MenuPresentationPolicy.ReadyPresentationInputMode;
            var revealAfterProviderCommit = string.Equals(
                _providerRevealAfterCommitPresentationId,
                presentationId,
                StringComparison.Ordinal);
            if (revealAfterProviderCommit)
            {
                _providerRevealAfterCommitPresentationId = null;
                ShowOverlay("extension-menu");
                TraceRuntime(
                    "menu_presentation_revealed_after_provider_commit",
                    $"presentation={presentationId} game_time={Game.GameTime}");
            }
            TraceRuntime(
                "menu_presentation_ready",
                $"presentation={presentationId} " +
                $"browser_prepare_wait_ms={browserPreparationWaitMilliseconds} " +
                $"provider_commit_wait_ms={providerCommitWaitMilliseconds} " +
                $"input_mode={_inputMode} game_time={Game.GameTime}");
            if (MenuPresentationPolicy.ShouldRetireInitializerAfterPaint(
                    matchingPresentationReady: true,
                    currentHostSurface: CurrentHostSurface))
            {
                // The initializer remains the last known-good desktop frame
                // until the native host proves this exact provider generation
                // painted after the browser received its accepted response.
                RetireBootstrapSurfaceForPresentation();
            }
            if (string.Equals(
                    _presentationPreparationDismissalSuppressionId,
                    presentationId,
                    StringComparison.Ordinal))
            {
                // If the compositor completed the preparatory hide and reveal
                // between script ticks, no hidden edge is observable here. A
                // matching provider-paint commit is the only other proof that
                // this presentation's preparation has completed; stale
                // browser-ready messages must never disarm a later real close.
                _presentationPreparationDismissalSuppressionId = null;
                TraceRuntime(
                    "menu_presentation_surface_revealed",
                    $"presentation={presentationId} game_time={Game.GameTime} " +
                    "dismissal_suppression_released=true");
            }
        }

        private void CancelPendingProviderPresentation(string reason)
        {
            var presentationId =
                _providerPresentationCommitGate.PendingPresentationId;
            if (presentationId == null)
                return;

            _providerPresentationCommitGate.Cancel();
            if (string.Equals(
                    _providerRevealAfterCommitPresentationId,
                    presentationId,
                    StringComparison.Ordinal))
            {
                _providerRevealAfterCommitPresentationId = null;
            }
            TraceRuntime(
                "menu_provider_paint_cancelled",
                $"presentation={presentationId} reason={reason} " +
                $"game_time={Game.GameTime}");
        }

        /// <summary>
        /// Fails one exact provider transfer without treating an internal
        /// renderer failure as a user-requested close. An initializing
        /// bootstrap surface remains the last known-good owner; transparent
        /// idle transfers are hidden. A stale token cannot hide, disarm, or
        /// clear a newer active presentation.
        /// </summary>
        private bool AbortPresentationTransfer(
            string? presentationId,
            string reason)
        {
            if (!MenuPresentationPolicy.IsValidPresentationId(presentationId))
            {
                TraceRuntime(
                    "menu_presentation_abort_ignored",
                    $"presentation={presentationId ?? "none"} reason={reason} " +
                    "cause=invalid-presentation-id");
                return false;
            }

            var exactPresentationId = presentationId!;
            if (string.Equals(
                    _menuRevealGate.PendingPresentationId,
                    exactPresentationId,
                    StringComparison.Ordinal))
            {
                _menuRevealGate.Cancel();
            }
            if (string.Equals(
                    _providerPresentationCommitGate.PendingPresentationId,
                    exactPresentationId,
                    StringComparison.Ordinal))
            {
                CancelPendingProviderPresentation(reason);
            }

            // Consume the registry record before changing global host state.
            // If another generation is active, this is a stale callback and
            // must not hide that generation or revoke its input ownership.
            var dismissal = ReactorHostApi.AcknowledgeMenuPresentationHidden(
                exactPresentationId);
            if (dismissal == null)
            {
                TraceRuntime(
                    "menu_presentation_abort_ignored",
                    $"presentation={exactPresentationId} reason={reason} " +
                    "cause=active-presentation-mismatch stale=true");
                return false;
            }

            _inputMode = MenuPresentationPolicy.HiddenInputMode;
            if (string.Equals(
                    _presentationPreparationDismissalSuppressionId,
                    exactPresentationId,
                    StringComparison.Ordinal))
            {
                _presentationPreparationDismissalSuppressionId = null;
            }

            dismissal["reason"] = "presentation-failed";
            dismissal["failureStage"] = reason;
            PostCoreEvent(MenuPresentationPolicy.DismissedEventName, dismissal);

            if (HostSurfaceMode.IsInitializing(CurrentHostSurface))
            {
                // Managed provider ownership failed, but the bootstrap process
                // still owns a verified initializer. Return to that passive
                // surface without cancelling its startup intent or marking a
                // first-run surface complete.
                _overlayRequestedVisible = false;
                _managedStartupStatusComplete = false;
                _nextManagedStartupStatusAt = 0;
                TraceRuntime(
                    "menu_presentation_abort_rolled_back",
                    $"presentation={exactPresentationId} reason={reason} " +
                    "fallback=story-initializer input_mode=game");
                return true;
            }

            // There is no bootstrap frame to recover. Hide only after the
            // exact failed registry generation has been consumed and its typed
            // dismissal has reached the browser.
            CancelStartupIntentIfActive(reason);
            if (_overlayRequestedVisible || _overlay.IsVisible)
            {
                ReactorHostApi.NotifyLifecycle(
                    ReactorLifecycleStage.OverlayClosing,
                    new JObject { ["gameTime"] = Game.GameTime });
            }
            _overlayRequestedVisible = false;
            if (!HasAuthoritativeHostSurfaceBoundary())
            {
                _overlay.PostEvent(
                    "host.surface",
                    new JObject { ["mode"] = "none" });
            }
            _overlay.SetVisible(false);
            TraceRuntime(
                "menu_presentation_abort_hidden",
                $"presentation={exactPresentationId} reason={reason} " +
                "fallback=none input_mode=game");
            return true;
        }

        private string CurrentHostSurface =>
            (_overlay as IHostSurfaceRuntime)?.CurrentHostSurface ?? HostSurfaceMode.None;

        private bool HasAuthoritativeHostSurfaceBoundary() =>
            _overlay is IAuthoritativeHostSurfaceRuntime authoritative &&
            authoritative.HasAuthoritativeHostSurfaceBoundary;

        private void PublishManagedStartupStatus(long elapsedMilliseconds)
        {
            if (!MenuPresentationPolicy.ShouldRefreshManagedStartupStatus(
                    _managedStartupStatusComplete,
                    elapsedMilliseconds,
                    _nextManagedStartupStatusAt,
                    CurrentHostSurface))
                return;

            _nextManagedStartupStatusAt =
                elapsedMilliseconds + ManagedStartupStatusRefreshMilliseconds;
            var snapshot = GameApiRouter.GetStartupStatus();
            PostCoreEvent(StartupStatusContract.EventName, snapshot);
            if (snapshot.Value<bool>("allIn1Loaded") &&
                !snapshot.Value<bool>("defaultMenuRequested"))
                _managedStartupStatusComplete = true;
        }

        private void DrainMenuDismissals()
        {
            foreach (var dismissal in ReactorHostApi.DrainMenuDismissals().OfType<JObject>())
            {
                var presentationId = dismissal.Value<string>("presentationId");
                if (string.IsNullOrWhiteSpace(presentationId))
                    continue;
                CloseOverlay("extension-request", presentationId);
            }
        }

        private void PublishActiveMenuDismissed(
            string reason,
            string? expectedPresentationId = null)
        {
            var dismissal = expectedPresentationId == null
                ? ReactorHostApi.TakeActiveMenuPresentation()
                : ReactorHostApi.AcknowledgeMenuPresentationHidden(
                    expectedPresentationId);
            if (dismissal == null) return;
            dismissal["reason"] = reason;
            PostCoreEvent(MenuPresentationPolicy.DismissedEventName, dismissal);
        }

        private void TraceRuntime(string stage, string? detail = null) =>
            StartupTrace.Write(
                _localDataDirectory,
                "reactorv-runtime.log",
                "script",
                stage,
                detail);

        private void OnAborted(object sender, EventArgs args)
        {
            _menuRevealGate.Cancel();
            CancelPendingProviderPresentation("script-aborted");
            ReactorHostApi.SetMenuPresentationHostAvailable(false);
            TraceRuntime(
                "script_aborted",
                $"script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
            var unloadingPayload = new JObject { ["gameTime"] = Game.GameTime };
            try
            {
                PostLifecycle("shutting-down", "script-aborted");
            }
            finally
            {
                try
                {
                    _overlay.Dispose();
                }
                finally
                {
                    // Reset owns the one and only Unloading delivery. Calling
                    // NotifyLifecycle first would run extension teardown twice
                    // on ScriptHookVDotNet's abort thread.
                    ReactorHostApi.BeginShutdown(unloadingPayload);
                }
            }
        }

        private IOverlayRuntime CreateOverlay(
            IntPtr gtaWindow,
            string bootstrapDirectory,
            string localDataDirectory)
        {
            var width = 1920;
            var height = 1080;
            if (NativeMethods.TryGetClientBounds(gtaWindow, out var bounds))
            {
                width = bounds.Width;
                height = bounds.Height;
            }

            var runtime = ExternalRuntimeLoader.Create(
                (_configuration.Renderer ?? "auto").Trim().ToLowerInvariant(),
                gtaWindow,
                bootstrapDirectory,
                localDataDirectory,
                _broker,
                width,
                height,
                _configuration.DirectXFrameRate,
                _configuration.EnableDevTools,
                false);
            if (!runtime.Start())
            {
                runtime.Dispose();
                throw new InvalidOperationException("REACTOR V could not initialize its renderer.");
            }
            return runtime;
        }
    }
}
