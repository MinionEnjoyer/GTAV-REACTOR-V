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

        private readonly BridgeBroker _broker;
        private readonly IOverlayRuntime _overlay;
        private readonly GameApiRouter _router;
        private readonly OverlayConfiguration _configuration;
        private readonly Keys _toggleKey;
        private readonly string _firstRunMarkerPath;
        private readonly string _localDataDirectory;
        private readonly Stopwatch _scriptTimer = Stopwatch.StartNew();
        private bool _firstRunPending;
        private bool _storyModeReady;
        private bool _storyModePlayable;
        private bool _overlayRequestedVisible;
        private bool _overlayWasPresented;
        private bool _overlayPreviouslyVisible;
        private bool _gameWasPaused;
        private bool _browserReady;
        private bool _runtimeReadyHandoffAttempted;
        private string _inputMode = "exclusive";
        private string _lifecyclePhase = "booting";
        private int _storyModeReadyCandidateAt = -1;
        private int _nextTelemetryAt;
        private int _nextToggleAt;
        private long _nextStoryModePollAt;

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
                    _toggleKey = Keys.F10;
                }

                _broker = new BridgeBroker();
                var gtaWindow = Process.GetCurrentProcess().MainWindowHandle;
                _localDataDirectory = ReactorVDataDirectory.Resolve();
                traceDirectory = _localDataDirectory;
                _firstRunMarkerPath = Path.Combine(_localDataDirectory, "first-run-splash.complete");
                _firstRunPending = _configuration.ShowFirstRunSplash && !File.Exists(_firstRunMarkerPath);
                TraceRuntime(
                    "construction_paths_ready",
                    $"bootstrap={bootstrapDirectory} window=0x{gtaWindow.ToInt64():X} " +
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
                        else CloseOverlay();
                    },
                    () => _overlay.IsVisible,
                    mode => _inputMode = mode,
                    () => _inputMode,
                    () => _overlay.RendererName,
                    (errorId, error) => TraceRuntime(
                        "api_failure",
                        $"error_id={errorId} type={error.GetType().FullName} message={error.Message}"),
                    MarkBrowserReady);

                Tick += OnTick;
                KeyDown += OnKeyDown;
                Aborted += OnAborted;
                TraceRuntime(
                    "construction_complete",
                    $"duration_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
            }
            catch (Exception error)
            {
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
            var scriptElapsedMilliseconds = _scriptTimer.ElapsedMilliseconds;
            if (scriptElapsedMilliseconds >= _nextStoryModePollAt)
            {
                UpdateStoryModeReadiness();
                _nextStoryModePollAt = scriptElapsedMilliseconds +
                    (_storyModeReady
                        ? StoryModeBackgroundPollMilliseconds
                        : StoryModeStartingPollMilliseconds);
            }

            for (var index = 0; index < MaximumRequestsPerFrame && _broker.TryDequeue(out var request); index++)
            {
                if (request != null)
                {
                    _overlay.PostResponse(_router.Dispatch(request));
                }
            }

            DrainExtensionEvents();
            while (_router.TryDequeueReplayEvent(out var replayName, out var replayPayload))
            {
                if (replayName != null)
                {
                    _overlay.PostEvent(replayName, replayPayload);
                }
            }

            _overlay.PumpInput();

            var overlayPresented = _overlay.IsVisible;
            if (overlayPresented != _overlayPreviouslyVisible)
            {
                _overlayPreviouslyVisible = overlayPresented;
                ReactorHostApi.NotifyLifecycle(
                    overlayPresented ? ReactorLifecycleStage.OverlayOpened : ReactorLifecycleStage.OverlayClosed,
                    new JObject { ["gameTime"] = Game.GameTime });
            }
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

            ApplyInputMode();
            EmitSemanticInput();
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
            if (!_storyModeReady)
            {
                return;
            }

            if (args.KeyCode == _toggleKey)
            {
                if (Game.GameTime < _nextToggleAt)
                {
                    return;
                }

                _nextToggleAt = Game.GameTime + ToggleDebounceMilliseconds;
                if (_overlayRequestedVisible)
                {
                    CloseOverlay();
                }
                else
                {
                    ShowOverlay("toggle");
                }
            }
            else if (args.KeyCode == Keys.Escape && _overlay.IsVisible)
            {
                if (string.Equals(_inputMode, "menu", StringComparison.Ordinal))
                {
                    // The disabled frontend-cancel control is translated to
                    // input.action by EmitSemanticInput on this same frame.
                    // Keeping the overlay open lets the active menu own Back.
                    return;
                }
                else
                {
                    CloseOverlay();
                }
            }
        }

        private void CloseOverlay()
        {
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
            _overlay.SetVisible(false);
            TraceRuntime("overlay_hide_requested", $"game_time={Game.GameTime}");
        }

        private void ShowOverlay(string reason)
        {
            if (_overlayRequestedVisible)
            {
                return;
            }

            _overlayRequestedVisible = true;
            ReactorHostApi.NotifyLifecycle(
                ReactorLifecycleStage.OverlayOpening,
                new JObject { ["reason"] = reason, ["gameTime"] = Game.GameTime });
            _overlay.SetVisible(true);
            TraceRuntime(
                "overlay_show_requested",
                $"reason={reason} game_time={Game.GameTime} " +
                $"script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
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

        private void ApplyInputMode()
        {
            if (string.Equals(_inputMode, "game", StringComparison.Ordinal))
            {
                return;
            }

            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            if (!string.Equals(_inputMode, "pointer", StringComparison.Ordinal) &&
                !string.Equals(_inputMode, "exclusive", StringComparison.Ordinal))
            {
                return;
            }

            Hud.ShowCursorThisFrame();
            var cursorX = Game.GetDisabledControlValueNormalized(GTAControl.CursorX);
            var cursorY = Game.GetDisabledControlValueNormalized(GTAControl.CursorY);
            var cursorPressed = Function.Call<bool>(
                Hash.IS_DISABLED_CONTROL_JUST_PRESSED,
                0,
                (int)GTAControl.CursorAccept);
            var cursorReleased = Function.Call<bool>(
                Hash.IS_DISABLED_CONTROL_JUST_RELEASED,
                0,
                (int)GTAControl.CursorAccept);
            var wheel = Function.Call<bool>(
                Hash.IS_DISABLED_CONTROL_JUST_PRESSED,
                0,
                (int)GTAControl.CursorScrollUp)
                    ? 120
                    : Function.Call<bool>(
                        Hash.IS_DISABLED_CONTROL_JUST_PRESSED,
                        0,
                        (int)GTAControl.CursorScrollDown) ? -120 : 0;
            _overlay.UpdateCursor(cursorX, cursorY, cursorPressed, cursorReleased, wheel);
        }

        private void EmitSemanticInput()
        {
            var disabled = !string.Equals(_inputMode, "game", StringComparison.Ordinal);
            EmitControlAction(188, "navigate-up", disabled);
            EmitControlAction(187, "navigate-down", disabled);
            EmitControlAction(189, "navigate-left", disabled);
            EmitControlAction(190, "navigate-right", disabled);
            EmitControlAction(201, "accept", disabled);
            EmitControlAction(202, "back", disabled);
            EmitControlAction(204, "previous-tab", disabled);
            EmitControlAction(205, "next-tab", disabled);
        }

        private void EmitControlAction(int control, string action, bool disabled)
        {
            var pressed = Function.Call<bool>(
                disabled ? Hash.IS_DISABLED_CONTROL_JUST_PRESSED : Hash.IS_CONTROL_JUST_PRESSED,
                0,
                control);
            if (pressed)
            {
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

        private void MarkBrowserReady()
        {
            if (_browserReady)
            {
                return;
            }

            _browserReady = true;
            ReactorHostApi.NotifyLifecycle(
                ReactorLifecycleStage.BrowserReady,
                new JObject { ["gameTime"] = Game.GameTime });
            PostLifecycle("browser-ready", "page-handshake");
            TraceRuntime(
                "browser_ready",
                $"game_time={Game.GameTime} script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
            TryCompleteRuntimeReadyHandoff();
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

            _runtimeReadyHandoffAttempted = true;
            var processId = Process.GetCurrentProcess().Id;
            var signaled = PreloadHandoff.TrySignalRuntimeReady(processId);
            TraceRuntime(
                signaled ? "runtime_ready_handoff_signaled" : "runtime_ready_handoff_unavailable",
                $"pid={processId} game_time={Game.GameTime} " +
                $"story_mode_ready={_storyModeReady} browser_ready={_browserReady} " +
                $"script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
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

        private void TraceRuntime(string stage, string? detail = null) =>
            StartupTrace.Write(
                _localDataDirectory,
                "reactorv-runtime.log",
                "script",
                stage,
                detail);

        private void OnAborted(object sender, EventArgs args)
        {
            TraceRuntime(
                "script_aborted",
                $"script_elapsed_ms={_scriptTimer.Elapsed.TotalMilliseconds:F3}");
            try
            {
                PostLifecycle("shutting-down", "script-aborted");
                ReactorHostApi.NotifyLifecycle(
                    ReactorLifecycleStage.Unloading,
                    new JObject { ["gameTime"] = Game.GameTime });
            }
            finally
            {
                try
                {
                    _overlay.Dispose();
                }
                finally
                {
                    ReactorHostApi.Reset();
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
