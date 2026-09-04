#include "BootstrapPolicy.h"

#include <algorithm>
#include <cwctype>
#include <limits>

namespace reactorv::bootstrap {
namespace {

std::wstring Lowercase(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](const wchar_t valueCharacter) {
        return static_cast<wchar_t>(std::towlower(valueCharacter));
    });
    return value;
}

bool Contains(const std::string& value, const char* marker) {
    return value.find(marker) != std::string::npos;
}

} // namespace

bool IsSupportedGameExecutable(const std::filesystem::path& executablePath) {
    const auto name = Lowercase(executablePath.filename().wstring());
    return name == L"gta5.exe" || name == L"gta5_enhanced.exe";
}

std::uint32_t F9PollIntervalMilliseconds() {
    return 16;
}

std::uint64_t F9RouteClaimDebounceMilliseconds() {
    // ScriptHook's window callback and the bootstrap worker can observe the
    // same physical key edge on different threads. Keep the arbitration
    // window comfortably above one frame-scale poll while remaining shorter
    // than the harness/user cadence for a deliberate second press.
    return 150;
}

std::uint32_t MaintenancePollIntervalMilliseconds() {
    return 250;
}

std::uint32_t BootstrapGameStateProbeWaitMilliseconds(const bool enabled) {
    // ScriptHook script callbacks are fiber entrypoints and must never return.
    // Once disabled, park the fiber for the maximum representable wait rather
    // than continuing native sampling or falling out of its entrypoint.
    return enabled ? 100 : std::numeric_limits<std::uint32_t>::max();
}

std::uint64_t BootstrapProcessExitIntentLifetimeMilliseconds() {
    // Alt+F4 can open a cancellable GTA confirmation. Keep the intent only
    // long enough to observe an immediate keyboard confirmation; never turn
    // the initial Alt+F4 edge into an irreversible bootstrap shutdown.
    return 5000;
}

std::uint64_t BootstrapGameWindowLossGraceMilliseconds() {
    // Fullscreen/display-mode changes can replace GTA's top-level HWND. Give
    // that replacement time to appear before treating owner loss as shutdown.
    return 5000;
}

std::uint64_t BootstrapGameStateProbeMaximumAgeMilliseconds() {
    // A ScriptHook script-fiber snapshot is refreshed on a bounded cadence.
    // If the fiber stops during a loading transition, its old frontend result
    // must not remain authoritative indefinitely.
    return 1000;
}

bool IsBootstrapGameStateProbeFresh(
    const std::uint64_t sampledAtMilliseconds,
    const std::uint64_t nowMilliseconds) {
    return sampledAtMilliseconds != 0 &&
        nowMilliseconds >= sampledAtMilliseconds &&
        nowMilliseconds - sampledAtMilliseconds <=
            BootstrapGameStateProbeMaximumAgeMilliseconds();
}

bool DecodeScriptHookBoolResult(const std::uint64_t rawResult) {
    // GTA BOOL returns occupy the low 32 bits of ScriptHook's 64-bit result
    // slot. The upper bits are not part of the value and may contain residue.
    return static_cast<std::uint32_t>(rawResult) != 0;
}

F9InputDecision EvaluateF9Input(
    const bool runtimeReady,
    const bool physicalKeyDown,
    const bool pressedSinceLastPoll,
    const bool previousPhysicalKeyDown,
    const bool gameForeground) {
    const bool pressedEdge = pressedSinceLastPoll ||
        (physicalKeyDown && !previousPhysicalKeyDown);
    // The bootstrap loop itself is the ownership boundary. RuntimeReady only
    // authorizes transfer after an idle physical sample; it must not swallow
    // a press that arrives at the boundary. Deferring release for one poll
    // after a short-tap low bit also prevents that same tap from crossing into
    // managed input after native has already handled it.
    const bool routeToBootstrap = gameForeground && pressedEdge;
    const bool releaseOwnership =
        runtimeReady && !physicalKeyDown && !pressedEdge;
    return {routeToBootstrap, releaseOwnership};
}

bool ShouldUseF9PollingFallback(
    const bool keyboardHandlerRegistered,
    const bool keyboardDispatchObserved) {
    // Registering the callback only proves the export accepted our handler.
    // ScriptHook does not dispatch it until its GTA thread/window pump is
    // operational, so polling must retain ownership through that startup gap.
    return !keyboardHandlerRegistered || !keyboardDispatchObserved;
}

bool TryClaimF9RouteEdge(
    std::atomic<std::uint64_t>& lastClaimedAtMilliseconds,
    std::uint64_t nowMilliseconds) {
    // Zero is the unclaimed sentinel. GetTickCount64 can only be zero during
    // the first millisecond of boot, but normalize it so a successful claim is
    // still visible to the competing callback/worker thread.
    if (nowMilliseconds == 0) nowMilliseconds = 1;
    auto previous = lastClaimedAtMilliseconds.load(std::memory_order_acquire);
    while (true) {
        if (previous != 0 &&
            nowMilliseconds >= previous &&
            nowMilliseconds - previous < F9RouteClaimDebounceMilliseconds()) {
            return false;
        }
        if (lastClaimedAtMilliseconds.compare_exchange_weak(
                previous,
                nowMilliseconds,
                std::memory_order_acq_rel,
                std::memory_order_acquire)) {
            return true;
        }
    }
}

bool EvaluateBootstrapCloseInput(
    const bool physicalKeyDown,
    const bool pressedSinceLastPoll,
    const bool previousPhysicalKeyDown,
    const bool gameForeground) {
    // The pre-provider WebView deliberately cannot activate. Escape therefore
    // follows the same native down-edge sampling as F9 while bootstrap owns
    // input, but maps only to the typed close event (never an arbitrary key
    // replay into the later managed provider).
    const bool pressedEdge = pressedSinceLastPoll ||
        (physicalKeyDown && !previousPhysicalKeyDown);
    return gameForeground && pressedEdge;
}

BootstrapProcessExitDecision EvaluateBootstrapProcessExitInput(
    const BootstrapProcessExitIntent& currentIntent,
    const std::uint64_t nowMilliseconds,
    const bool gameForeground,
    const bool altDown,
    const bool f4PressedEdge,
    const bool enterPressedEdge,
    const bool escapePressedEdge) {
    BootstrapProcessExitIntent next = currentIntent;
    if (next.pending &&
        (nowMilliseconds < next.startedAtMilliseconds ||
         nowMilliseconds - next.startedAtMilliseconds >=
             BootstrapProcessExitIntentLifetimeMilliseconds())) {
        next = {};
    }

    if (next.pending && escapePressedEdge) {
        return {{}, false};
    }
    if (next.pending && gameForeground && enterPressedEdge) {
        return {next, true};
    }
    if (!next.pending && gameForeground && altDown && f4PressedEdge) {
        next = {true, nowMilliseconds};
    }
    return {next, false};
}

bool IsBootstrapGameWindowLossConfirmed(
    const bool gameWindowWasObserved,
    const bool gameWindowAvailable,
    const std::uint64_t missingSinceMilliseconds,
    const std::uint64_t nowMilliseconds) {
    return gameWindowWasObserved &&
        !gameWindowAvailable &&
        missingSinceMilliseconds != 0 &&
        nowMilliseconds >= missingSinceMilliseconds &&
        nowMilliseconds - missingSinceMilliseconds >=
            BootstrapGameWindowLossGraceMilliseconds();
}

F9OwnershipExitDecision EvaluateF9OwnershipExit(
    const bool boundaryAvailable,
    const bool ownershipReleased,
    const F9OwnershipExitKind exitKind) {
    const bool signalBoundary = boundaryAvailable && !ownershipReleased;
    const bool abandoned = signalBoundary &&
        exitKind != F9OwnershipExitKind::RuntimeHandoff;
    return {signalBoundary, abandoned};
}

bool ShouldCloseBootstrapHostOnOwnershipExit(
    const F9OwnershipExitKind exitKind) {
    // Runtime handoff deliberately preserves the process-scoped host so the
    // managed provider can present its menu without recreating WebView. Every
    // other terminal path must retire any bootstrap pixels before the native
    // input owner disappears.
    return exitKind != F9OwnershipExitKind::RuntimeHandoff;
}

std::filesystem::path ResolvePreloaderPath(const std::filesystem::path& gameExecutablePath) {
    return gameExecutablePath.parent_path() /
        L"plugins" / L"ReactorV" / L"ReactorV.Preloader.exe";
}

std::wstring QuoteCommandLineArgument(const std::wstring& value) {
    if (value.empty()) {
        return L"\"\"";
    }
    if (value.find_first_of(L" \t\n\v\"") == std::wstring::npos) {
        return value;
    }

    std::wstring result;
    result.push_back(L'\"');
    std::size_t backslashes = 0;
    for (const wchar_t character : value) {
        if (character == L'\\') {
            ++backslashes;
            continue;
        }
        if (character == L'\"') {
            result.append(backslashes * 2 + 1, L'\\');
            result.push_back(L'\"');
            backslashes = 0;
            continue;
        }
        result.append(backslashes, L'\\');
        backslashes = 0;
        result.push_back(character);
    }
    result.append(backslashes * 2, L'\\');
    result.push_back(L'\"');
    return result;
}

std::wstring BuildPreloaderCommandLine(
    const std::filesystem::path& preloaderPath,
    const std::uint32_t processId) {
    return QuoteCommandLineArgument(preloaderPath.wstring()) +
        L" --parent-pid " + std::to_wstring(processId) +
        L" --persistent-host --instance-id " + std::to_wstring(processId);
}

StartupStage DetectStartupStage(
    const std::string& scriptHookLog,
    const std::string& scriptHookDotNetLog,
    const std::string& reactorRuntimeLog,
    const bool preloaderStarted,
    const bool preloadDataReady) {
    const StartupSignals signals{
        Contains(scriptHookLog, "INIT: Success"),
        Contains(scriptHookLog, "CORE: Creating threads") ||
            Contains(scriptHookLog, "CORE: Launching main()") ||
            Contains(scriptHookLog, "INIT: GtaThread collection size"),
        Contains(scriptHookDotNetLog, "Loading scripts from") ||
            Contains(scriptHookDotNetLog, "Started script RageWebUI.Script.RageWebUiScript."),
        Contains(reactorRuntimeLog, "story_mode_ready"),
    };
    return DetectStartupStage(signals, preloaderStarted, preloadDataReady);
}

StartupStage DetectStartupStage(
    const StartupSignals& signals,
    const bool preloaderStarted,
    const bool preloadDataReady) {
    if (signals.storyModeReady) {
        return StartupStage::StoryModeReady;
    }
    if (signals.managedRuntimeReady) {
        return StartupStage::ManagedRuntimeReady;
    }
    if (signals.scriptThreadsStarting) {
        return StartupStage::ScriptThreadsStarting;
    }
    if (signals.scriptHookInitialized) {
        return preloadDataReady
            ? StartupStage::CoreDataPrepared
            : StartupStage::ScriptHookInitialized;
    }
    return preloaderStarted ? StartupStage::CacheWarmup : StartupStage::NativeBootstrap;
}

const wchar_t* StartupStageText(const StartupStage stage) {
    switch (stage) {
        case StartupStage::CacheWarmup:
            return L"Interface cache warming while GTA V loads...";
        case StartupStage::ScriptHookInitialized:
            return L"ScriptHookV initialized - waiting for Story Mode...";
        case StartupStage::CoreDataPrepared:
            return L"ALLIN1 core data prepared - waiting for Story Mode...";
        case StartupStage::ScriptThreadsStarting:
            return L"GTA script threads are starting...";
        case StartupStage::ManagedRuntimeReady:
            return L"Managed runtime ready - initializing Reactor V...";
        case StartupStage::StoryModeReady:
            return L"Story Mode ready";
        case StartupStage::NativeBootstrap:
        default:
            return L"Native bootstrap ready - starting interface cache...";
    }
}

BootstrapSurfaceRoute ResolveBootstrapF9Surface(const StartupStage stage) {
    // ScriptHook registers the companion ASI on the landing menu, but does not
    // guarantee that its script fiber will be scheduled there. Consequently
    // an unpublished live snapshot is the normal Enhanced main-menu state,
    // not an error that should strand F9 on "Verifying game state...".
    //
    // CORE/GtaThread log markers are also emitted on that landing menu. The
    // first process-wide boundary that proves gameplay initialization has
    // actually begun is managed-runtime attachment; Story-ready is the later
    // authoritative handoff. Fresh script-fiber evidence still overrides
    // this fallback in the overload below.
    return stage >= StartupStage::ManagedRuntimeReady
        ? BootstrapSurfaceRoute::Initializing
        : BootstrapSurfaceRoute::About;
}

BootstrapSurfaceRoute ResolveBootstrapF9Surface(
    const StartupStage fallbackStage,
    const BootstrapGameStateProbe& probe) {
    // Story-ready is an explicit, monotonic provider boundary. A snapshot
    // sampled on the immediately preceding landing-menu frame must never
    // demote that authority back to About.
    if (fallbackStage >= StartupStage::StoryModeReady) {
        return BootstrapSurfaceRoute::Initializing;
    }

    // The log stage is only a fallback. ScriptHook can create its core threads
    // while GTA is still on the frontend, so treating that marker as an
    // irreversible Story boundary displays the initializer over the main
    // menu. A complete live snapshot is more authoritative than that stale
    // process-wide marker; incomplete native availability falls back to the
    // lifecycle boundary above. That makes the real pre-fiber landing menu
    // immediately usable while still selecting Initializing as soon as the
    // managed Story runtime attaches.
    switch (ClassifyBootstrapGameState(probe)) {
        case BootstrapGameStateClassification::Initializing:
            return BootstrapSurfaceRoute::Initializing;
        case BootstrapGameStateClassification::StableFrontend:
            return BootstrapSurfaceRoute::About;
        case BootstrapGameStateClassification::Inconclusive:
        default:
            break;
    }
    return ResolveBootstrapF9Surface(fallbackStage);
}

BootstrapGameStateClassification ClassifyBootstrapGameState(
    const BootstrapGameStateProbe& probe) {
    // Enhanced's stable entry screen is FE_MENU_VERSION_LANDING_MENU. Its
    // loading-screen bit remains set and IS_FRONTEND_READY_FOR_CONTROL can
    // remain false. The dedicated landing-menu native is the authoritative
    // frontend discriminator and must also win over a transient stale/true
    // player sample captured at the same boundary.
    if (probe.landingMenuAvailable && probe.landingMenuActive) {
        return BootstrapGameStateClassification::StableFrontend;
    }

    // A live player is conclusive Story evidence once the landing-menu
    // boundary has explicitly reported inactive or is unavailable.
    if (probe.playerPlayingAvailable && probe.playerPlaying) {
        return BootstrapGameStateClassification::Initializing;
    }

    if (probe.playerPlayingAvailable && !probe.playerPlaying &&
        probe.frontendReadyAvailable) {
        // Enhanced can leave GET_IS_LOADING_SCREEN_ACTIVE set on its stable
        // frontend. The explicit frontend bit therefore owns that state and
        // keeps F9 on Reactor About regardless of the sticky loading bit.
        if (probe.frontendReady) {
            return BootstrapGameStateClassification::StableFrontend;
        }

        // The observed Story transition has all three probes available with
        // loading=true, player=false, and frontend=false. Unlike a lone sticky
        // loading bit, that complete negative frontend snapshot is conclusive
        // initialization evidence and must route F9 to the non-blocking
        // ALLIN1 preloader.
        if (probe.loadingAvailable && probe.loading) {
            return BootstrapGameStateClassification::Initializing;
        }
    }
    return BootstrapGameStateClassification::Inconclusive;
}

BootstrapF9DispatchDecision ResolveBootstrapF9Dispatch(
    const StartupStage fallbackStage,
    const bool aboutDestinationAvailable,
    const bool initializerDestinationAvailable,
    const bool verifyingDestinationAvailable) {
    // ScriptHook keyboard callbacks are not native-capable script fibers.
    // Route from the monotonic startup stage so input handling never executes
    // a GTA native. A future script-context probe can use the overload below.
    const auto route = ResolveBootstrapF9Surface(fallbackStage);
    return {
        route,
        route == BootstrapSurfaceRoute::About
            ? aboutDestinationAvailable
            : route == BootstrapSurfaceRoute::Initializing
                ? initializerDestinationAvailable
                : verifyingDestinationAvailable,
    };
}

BootstrapF9DispatchDecision ResolveBootstrapF9Dispatch(
    const StartupStage fallbackStage,
    const BootstrapGameStateProbe& probe,
    const bool aboutDestinationAvailable,
    const bool initializerDestinationAvailable,
    const bool verifyingDestinationAvailable) {
    const auto route = ResolveBootstrapF9Surface(fallbackStage, probe);
    return {
        route,
        route == BootstrapSurfaceRoute::About
            ? aboutDestinationAvailable
            : route == BootstrapSurfaceRoute::Initializing
                ? initializerDestinationAvailable
                : verifyingDestinationAvailable,
    };
}

BootstrapF9DispatchDecision ResolveBootstrapF9Dispatch(
    const StartupStage fallbackStage,
    const BootstrapGameStateProbe& probe,
    const bool freshProbeAvailable,
    const bool scriptFiberExecutionObserved,
    const bool aboutDestinationAvailable,
    const bool initializerDestinationAvailable,
    const bool verifyingDestinationAvailable) {
    if (freshProbeAvailable) {
        return ResolveBootstrapF9Dispatch(
            fallbackStage,
            probe,
            aboutDestinationAvailable,
            initializerDestinationAvailable,
            verifyingDestinationAvailable);
    }

    // ScriptHook can export/register the companion on the landing menu without
    // ever scheduling its ScriptMain fiber. Once that fiber has actually
    // published a snapshot, however, GTA's script VM has crossed into the
    // Story initialization lifecycle. Even if every queried native fails or
    // the last sample becomes stale during a load stall, route to the bounded
    // initializer rather than regressing to the frontend About surface.
    if (scriptFiberExecutionObserved) {
        return {
            BootstrapSurfaceRoute::Initializing,
            initializerDestinationAvailable,
        };
    }

    return ResolveBootstrapF9Dispatch(
        fallbackStage,
        aboutDestinationAvailable,
        initializerDestinationAvailable,
        verifyingDestinationAvailable);
}

BootstrapVerificationPromotionDecision EvaluateVerificationPromotion(
    const bool verificationActive,
    const bool promotionIssued,
    const std::uint8_t probeStatus,
    const BootstrapF9DispatchDecision& dispatch) {
    if (!verificationActive) return {false, false};
    if (promotionIssued) return {false, true};

    const bool authoritativeRoute =
        dispatch.route == BootstrapSurfaceRoute::About ||
        dispatch.route == BootstrapSurfaceRoute::Initializing;
    const bool shouldPromote =
        probeStatus == 1 &&
        authoritativeRoute &&
        dispatch.destinationAvailable;
    return {shouldPromote, shouldPromote};
}

BootstrapInitializerPromotionDecision EvaluateInitializerPromotion(
    const bool promotionIssued,
    const BootstrapF9DispatchDecision& dispatch) {
    // Objective Story evidence owns the initializer lifecycle independently
    // of the main-menu About surface. About can close because focus moves to
    // Reactor's own input HWND, and that presentation detail must never erase
    // the Story preloader. Re-arm only after the authoritative route leaves
    // Initializing so one continuous Story transition emits one promotion.
    const bool initializerRoute =
        dispatch.route == BootstrapSurfaceRoute::Initializing &&
        dispatch.destinationAvailable;
    if (!initializerRoute) return {false, false};
    if (promotionIssued) return {false, true};
    return {true, true};
}

bool ShouldAttemptObjectiveInitializerPromotion(
    const bool promotionIssued,
    const bool preloaderStarted,
    const bool runtimeReady,
    const bool objectiveStoryEdge,
    const bool destinationAvailable) {
    return !promotionIssued && preloaderStarted && !runtimeReady &&
        objectiveStoryEdge && destinationAvailable;
}

} // namespace reactorv::bootstrap
