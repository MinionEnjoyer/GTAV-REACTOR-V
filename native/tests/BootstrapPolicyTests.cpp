#include "BootstrapPolicy.h"

#include <atomic>
#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <string>
#include <thread>

namespace {

void Require(const bool condition, const char* message) {
    if (!condition) {
        std::cerr << message << '\n';
        std::exit(1);
    }
}

} // namespace

int main() {
    using reactorv::bootstrap::StartupStage;

    Require(
        reactorv::bootstrap::IsSupportedGameExecutable(L"C:\\Games\\GTA5.exe"),
        "Legacy executable should be accepted.");
    Require(
        reactorv::bootstrap::IsSupportedGameExecutable(L"C:\\Games\\GTA5_Enhanced.exe"),
        "Enhanced executable should be accepted.");
    Require(
        !reactorv::bootstrap::IsSupportedGameExecutable(L"C:\\Games\\Other.exe"),
        "Unrelated hosts must be rejected.");
    Require(
        reactorv::bootstrap::F9PollIntervalMilliseconds() <= 16,
        "F9 must be sampled independently at a frame-scale interval.");
    Require(
        reactorv::bootstrap::F9RouteClaimDebounceMilliseconds() >=
                reactorv::bootstrap::F9PollIntervalMilliseconds() &&
            reactorv::bootstrap::F9RouteClaimDebounceMilliseconds() <= 250,
        "Callback/poll arbitration must cover a frame race without delaying a deliberate second press.");
    Require(
        reactorv::bootstrap::MaintenancePollIntervalMilliseconds() >= 250,
        "Log and stage maintenance must remain on the slower interval.");
    Require(
        reactorv::bootstrap::BootstrapGameStateProbeWaitMilliseconds(true) == 100,
        "An enabled game-state fiber should sample on its bounded cadence.");
    Require(
        reactorv::bootstrap::BootstrapGameStateProbeWaitMilliseconds(false) ==
            UINT32_MAX,
        "A disabled ScriptHook fiber must park instead of returning.");
    Require(
        !reactorv::bootstrap::DecodeScriptHookBoolResult(
            0xFFFF'FFFF'0000'0000ULL),
        "Nonzero high-slot residue must not turn a GTA BOOL false into true.");
    Require(
        reactorv::bootstrap::DecodeScriptHookBoolResult(
            0xFFFF'FFFF'0000'0001ULL),
        "A nonzero low 32-bit GTA BOOL result must decode true.");
    Require(
        reactorv::bootstrap::BootstrapProcessExitIntentLifetimeMilliseconds() >= 1000,
        "A cancellable GTA exit prompt needs a bounded confirmation window.");
    Require(
        reactorv::bootstrap::BootstrapGameWindowLossGraceMilliseconds() >= 1000,
        "A transient display-mode HWND replacement needs a bounded grace period.");
    Require(
        !reactorv::bootstrap::IsBootstrapGameStateProbeFresh(0, 1000),
        "An absent script-fiber snapshot must never be treated as live evidence.");
    Require(
        reactorv::bootstrap::IsBootstrapGameStateProbeFresh(1000, 1500),
        "A recent script-fiber snapshot should remain authoritative.");
    Require(
        !reactorv::bootstrap::IsBootstrapGameStateProbeFresh(1000, 2500),
        "A stalled script fiber must not leave a stale frontend result authoritative.");
    Require(
        !reactorv::bootstrap::IsBootstrapGameStateProbeFresh(1500, 1000),
        "An incoherent future timestamp must fail closed.");

    const auto shortTap = reactorv::bootstrap::EvaluateF9Input(
        false, false, true, false, true);
    Require(
        shortTap.routeToBootstrap && !shortTap.releaseOwnership,
        "A sub-poll physical tap reported by GetAsyncKeyState must route once.");

    const auto heldBeforeHandoff = reactorv::bootstrap::EvaluateF9Input(
        false, true, true, false, true);
    const auto heldRepeat = reactorv::bootstrap::EvaluateF9Input(
        false, true, false, true, true);
    Require(
        heldBeforeHandoff.routeToBootstrap && !heldRepeat.routeToBootstrap,
        "A held F9 key must produce exactly one native edge.");

    const auto heldAcrossHandoff = reactorv::bootstrap::EvaluateF9Input(
        true, true, false, true, true);
    Require(
        !heldAcrossHandoff.routeToBootstrap &&
        !heldAcrossHandoff.releaseOwnership,
        "RuntimeReady must retain native ownership while F9 remains held.");

    const auto newEdgeAfterRuntimeReady = reactorv::bootstrap::EvaluateF9Input(
        true, true, true, false, true);
    Require(
        newEdgeAfterRuntimeReady.routeToBootstrap &&
        !newEdgeAfterRuntimeReady.releaseOwnership,
        "A new F9 down-edge before ownership release must route to native exactly once.");

    const auto shortBoundaryTap = reactorv::bootstrap::EvaluateF9Input(
        true, false, true, false, true);
    Require(
        shortBoundaryTap.routeToBootstrap &&
        !shortBoundaryTap.releaseOwnership,
        "A short boundary tap must route before ownership transfers.");

    const auto idleAfterBoundaryTap = reactorv::bootstrap::EvaluateF9Input(
        true, false, false, false, true);
    Require(
        !idleAfterBoundaryTap.routeToBootstrap &&
        idleAfterBoundaryTap.releaseOwnership,
        "Ownership may transfer on the idle poll after a handled short tap.");

    const auto sampledBeforeRuntimeReady = reactorv::bootstrap::EvaluateF9Input(
        false, true, true, false, true);
    Require(
        sampledBeforeRuntimeReady.routeToBootstrap &&
        !sampledBeforeRuntimeReady.releaseOwnership,
        "A press sampled before RuntimeReady must remain accepted if readiness flips before delivery.");

    const auto releasedAfterHandoff = reactorv::bootstrap::EvaluateF9Input(
        true, false, false, true, true);
    Require(
        !releasedAfterHandoff.routeToBootstrap &&
        releasedAfterHandoff.releaseOwnership,
        "Physical key-up after RuntimeReady must release ownership.");

    const auto backgroundTap = reactorv::bootstrap::EvaluateF9Input(
        false, true, true, false, false);
    Require(
        !backgroundTap.routeToBootstrap,
        "Native F9 must ignore taps while GTA is not foreground.");

    Require(
        reactorv::bootstrap::ShouldUseF9PollingFallback(false, false),
        "Polling must own F9 when the ScriptHook handler is unavailable.");
    Require(
        reactorv::bootstrap::ShouldUseF9PollingFallback(true, false),
        "Handler registration must not create a dead zone before callback dispatch is observed.");
    Require(
        !reactorv::bootstrap::ShouldUseF9PollingFallback(true, true),
        "An observed ScriptHook callback must retire the polling fallback.");

    std::atomic<std::uint64_t> routeClaim{0};
    std::atomic<int> routeWinners{0};
    const auto raceForRoute = [&]() {
        if (reactorv::bootstrap::TryClaimF9RouteEdge(routeClaim, 1000)) {
            routeWinners.fetch_add(1, std::memory_order_acq_rel);
        }
    };
    std::thread pollingRoute(raceForRoute);
    std::thread callbackRoute(raceForRoute);
    pollingRoute.join();
    callbackRoute.join();
    Require(
        routeWinners.load(std::memory_order_acquire) == 1,
        "A callback/poll race for one F9 edge must produce exactly one route signal.");
    Require(
        !reactorv::bootstrap::TryClaimF9RouteEdge(
            routeClaim,
            1000 +
                reactorv::bootstrap::F9RouteClaimDebounceMilliseconds() - 1),
        "A duplicate observation inside the handoff debounce must be suppressed.");
    Require(
        reactorv::bootstrap::TryClaimF9RouteEdge(
            routeClaim,
            1000 + reactorv::bootstrap::F9RouteClaimDebounceMilliseconds()),
        "A later deliberate F9 edge must be accepted after the bounded debounce.");

    Require(
        reactorv::bootstrap::EvaluateBootstrapCloseInput(
            false, true, false, true),
        "A short pre-provider Escape tap must route to the typed close edge.");
    Require(
        reactorv::bootstrap::EvaluateBootstrapCloseInput(
            true, false, false, true),
        "An Escape down edge must close the non-activating initializer.");
    Require(
        !reactorv::bootstrap::EvaluateBootstrapCloseInput(
            true, false, true, true),
        "A held Escape key must produce exactly one bootstrap close edge.");
    Require(
        !reactorv::bootstrap::EvaluateBootstrapCloseInput(
            true, true, false, false),
        "Bootstrap Escape must ignore taps while GTA is not foreground.");

    const auto exitIntentArmed =
        reactorv::bootstrap::EvaluateBootstrapProcessExitInput(
            {}, 1000, true, true, true, false, false);
    Require(
        exitIntentArmed.nextIntent.pending && !exitIntentArmed.confirmed,
        "Alt+F4 must arm a reversible intent instead of stopping bootstrap immediately.");
    const auto bareF4Ignored =
        reactorv::bootstrap::EvaluateBootstrapProcessExitInput(
            {}, 1000, true, false, true, false, false);
    Require(
        !bareF4Ignored.nextIntent.pending && !bareF4Ignored.confirmed,
        "F4 without Alt must not arm process-exit cleanup.");
    const auto backgroundAltF4Ignored =
        reactorv::bootstrap::EvaluateBootstrapProcessExitInput(
            {}, 1000, false, true, true, false, false);
    Require(
        !backgroundAltF4Ignored.nextIntent.pending &&
        !backgroundAltF4Ignored.confirmed,
        "Alt+F4 in another process must not arm Reactor shutdown.");
    const auto exitIntentCancelled =
        reactorv::bootstrap::EvaluateBootstrapProcessExitInput(
            exitIntentArmed.nextIntent, 1200, true, false, false, false, true);
    Require(
        !exitIntentCancelled.nextIntent.pending && !exitIntentCancelled.confirmed,
        "Escape must re-arm bootstrap after a cancelled GTA exit prompt.");
    const auto exitIntentConfirmed =
        reactorv::bootstrap::EvaluateBootstrapProcessExitInput(
            exitIntentArmed.nextIntent, 1200, true, false, false, true, false);
    Require(
        exitIntentConfirmed.nextIntent.pending && exitIntentConfirmed.confirmed,
        "A foreground Enter following Alt+F4 must confirm process-exit cleanup.");
    const auto backgroundEnterIgnored =
        reactorv::bootstrap::EvaluateBootstrapProcessExitInput(
            exitIntentArmed.nextIntent, 1200, false, false, false, true, false);
    Require(
        backgroundEnterIgnored.nextIntent.pending &&
        !backgroundEnterIgnored.confirmed,
        "An Enter press in another process must not stop bootstrap.");
    const auto expiredExitIntent =
        reactorv::bootstrap::EvaluateBootstrapProcessExitInput(
            exitIntentArmed.nextIntent,
            1000 +
                reactorv::bootstrap::BootstrapProcessExitIntentLifetimeMilliseconds(),
            true,
            false,
            false,
            true,
            false);
    Require(
        !expiredExitIntent.nextIntent.pending && !expiredExitIntent.confirmed,
        "An unconfirmed Alt+F4 intent must expire and restore bootstrap ownership.");

    Require(
        !reactorv::bootstrap::IsBootstrapGameWindowLossConfirmed(
            true, false, 1000, 1100),
        "A transient missing GTA HWND must not stop bootstrap during its grace period.");
    Require(
        !reactorv::bootstrap::IsBootstrapGameWindowLossConfirmed(
            true, true, 1000, 10000),
        "A replacement GTA HWND must cancel window-loss shutdown.");
    Require(
        !reactorv::bootstrap::IsBootstrapGameWindowLossConfirmed(
            false, false, 1000, 10000),
        "Bootstrap must not infer shutdown before it has observed a GTA HWND.");
    Require(
        reactorv::bootstrap::IsBootstrapGameWindowLossConfirmed(
            true,
            false,
            1000,
            1000 + reactorv::bootstrap::BootstrapGameWindowLossGraceMilliseconds()),
        "A missing authoritative GTA HWND beyond the grace period must release bootstrap.");

    const auto normalF9Exit = reactorv::bootstrap::EvaluateF9OwnershipExit(
        true,
        true,
        reactorv::bootstrap::F9OwnershipExitKind::RuntimeHandoff);
    Require(
        !normalF9Exit.signalBoundary && !normalF9Exit.abandoned,
        "A completed runtime handoff must not signal the ownership boundary twice.");

    const auto processExitF9Exit = reactorv::bootstrap::EvaluateF9OwnershipExit(
        true,
        false,
        reactorv::bootstrap::F9OwnershipExitKind::ProcessExitRequested);
    Require(
        processExitF9Exit.signalBoundary && processExitF9Exit.abandoned,
        "A confirmed process exit must release native F9 ownership before teardown.");

    const auto exceptionF9Exit = reactorv::bootstrap::EvaluateF9OwnershipExit(
        true,
        false,
        reactorv::bootstrap::F9OwnershipExitKind::WorkerException);
    Require(
        exceptionF9Exit.signalBoundary && exceptionF9Exit.abandoned,
        "A worker exception must abandon native F9 ownership and release managed input.");

    const auto absentBoundaryExit = reactorv::bootstrap::EvaluateF9OwnershipExit(
        false,
        false,
        reactorv::bootstrap::F9OwnershipExitKind::WorkerException);
    Require(
        !absentBoundaryExit.signalBoundary && !absentBoundaryExit.abandoned,
        "An unavailable ownership boundary cannot be signaled.");

    Require(
        !reactorv::bootstrap::ShouldCloseBootstrapHostOnOwnershipExit(
            reactorv::bootstrap::F9OwnershipExitKind::RuntimeHandoff),
        "Runtime handoff must preserve the persistent host for the managed provider.");
    Require(
        reactorv::bootstrap::ShouldCloseBootstrapHostOnOwnershipExit(
            reactorv::bootstrap::F9OwnershipExitKind::ProcessExitRequested),
        "Process exit must hide bootstrap pixels before native F9 ownership ends.");
    Require(
        reactorv::bootstrap::ShouldCloseBootstrapHostOnOwnershipExit(
            reactorv::bootstrap::F9OwnershipExitKind::WorkerException),
        "Worker failure must hide bootstrap pixels before native F9 ownership ends.");

    const auto preloader = reactorv::bootstrap::ResolvePreloaderPath(
        L"C:\\Program Files\\Grand Theft Auto V Enhanced\\GTA5_Enhanced.exe");
    Require(
        preloader == std::filesystem::path(
            L"C:\\Program Files\\Grand Theft Auto V Enhanced\\plugins\\ReactorV\\ReactorV.Preloader.exe"),
        "Preloader path should resolve beneath the game root.");

    const auto command = reactorv::bootstrap::BuildPreloaderCommandLine(preloader, 4242);
    Require(
        command.find(L"\"C:\\Program Files\\Grand Theft Auto V Enhanced\\plugins\\ReactorV\\ReactorV.Preloader.exe\"") == 0,
        "Preloader executable must be quoted.");
    Require(
        command.find(L"--parent-pid 4242 --persistent-host --instance-id 4242") != std::wstring::npos,
        "Preloader command should bind a persistent host to the GTA process.");

    Require(
        reactorv::bootstrap::DetectStartupStage("", "", "", false) == StartupStage::NativeBootstrap,
        "Empty logs should remain at native bootstrap.");
    Require(
        reactorv::bootstrap::DetectStartupStage("", "", "", true) == StartupStage::CacheWarmup,
        "A launched preloader should report cache warmup.");
    Require(
        reactorv::bootstrap::DetectStartupStage("INIT: Success", "", "", true) == StartupStage::ScriptHookInitialized,
        "ScriptHook initialization should outrank cache warmup.");
    Require(
        reactorv::bootstrap::DetectStartupStage("INIT: Success", "", "", true, true) == StartupStage::CoreDataPrepared,
        "Prepared core data should be visible while ScriptHook waits for script threads.");
    Require(
        reactorv::bootstrap::DetectStartupStage("CORE: Creating threads", "", "", true, true) == StartupStage::ScriptThreadsStarting,
        "Script thread creation should outrank prepared core data.");
    Require(
        std::wstring(reactorv::bootstrap::StartupStageText(StartupStage::CoreDataPrepared)) ==
            L"ALLIN1 core data prepared - waiting for Story Mode...",
        "Prepared core data should have distinct user-facing status text.");
    Require(
        reactorv::bootstrap::DetectStartupStage("CORE: Creating threads", "", "", true) == StartupStage::ScriptThreadsStarting,
        "Script thread creation should be detected.");
    Require(
        reactorv::bootstrap::DetectStartupStage(
            "INIT: GtaThread collection size 189", "", "", true) ==
            StartupStage::ScriptThreadsStarting,
        "Enhanced ScriptHook's current GtaThread marker should be detected.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::CoreDataPrepared) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::About,
        "An unsampled pre-fiber game boundary must expose About on the landing menu.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::ScriptThreadsStarting) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::About,
        "ScriptHook's frontend GtaThread marker must not hide About when the companion fiber is unpublished.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            StartupStage::CoreDataPrepared,
            true,
            true,
            true).route == reactorv::bootstrap::BootstrapSurfaceRoute::About,
        "A native keyboard callback must route an unpublished pre-fiber landing menu to About.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            StartupStage::ScriptThreadsStarting,
            true,
            true,
            true).route == reactorv::bootstrap::BootstrapSurfaceRoute::About,
        "A native keyboard callback must keep the real Enhanced landing menu immediately usable when its script-fiber probe is unavailable.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::ManagedRuntimeReady) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing,
        "Managed attachment must route the Story-loading interval to the initializer when the script-fiber probe is unavailable.");
    const auto availableInitializerFallback =
        reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            StartupStage::ManagedRuntimeReady,
            true,
            true,
            true);
    Require(
        availableInitializerFallback.route ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing &&
        availableInitializerFallback.destinationAvailable,
        "Managed Story initialization must dispatch to the available initializer destination.");
    const auto missingInitializerFallback =
        reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            StartupStage::ManagedRuntimeReady,
            true,
            false,
            false);
    Require(
        missingInitializerFallback.route ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing &&
        !missingInitializerFallback.destinationAvailable,
        "Managed Story initialization must fail closed when its typed initializer destination is unavailable.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::StoryModeReady) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing,
        "Story-ready F9 must retain gameplay-menu ownership.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::StoryModeReady,
            {false, false, false, false, false, false}) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing,
        "Authoritative Story readiness must never fall back to About or verification.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::StoryModeReady,
            {true, true, true, false, true, false, true, true}) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing,
        "A trailing landing-menu snapshot must not demote authoritative Story readiness to About.");
    Require(
        reactorv::bootstrap::ClassifyBootstrapGameState(
            {true, false, true, false, true, true}) ==
            reactorv::bootstrap::BootstrapGameStateClassification::StableFrontend,
        "A stable frontend requires successful false loading/player and true frontend evidence.");
    Require(
        reactorv::bootstrap::ClassifyBootstrapGameState(
            {true, true, true, false, true, true}) ==
            reactorv::bootstrap::BootstrapGameStateClassification::StableFrontend,
        "Enhanced's sticky loading bit must not override explicit stable-frontend evidence.");
    Require(
        reactorv::bootstrap::ClassifyBootstrapGameState(
            {true, true, true, false, true, false}) ==
            reactorv::bootstrap::BootstrapGameStateClassification::Initializing,
        "Loading with an explicit non-playing player and absent frontend must select the Story preloader.");
    Require(
        reactorv::bootstrap::ClassifyBootstrapGameState(
            {true, true, true, false, true, false, true, true}) ==
            reactorv::bootstrap::BootstrapGameStateClassification::StableFrontend,
        "Enhanced's landing menu must override its sticky loading and false frontend-ready bits.");
    Require(
        reactorv::bootstrap::ClassifyBootstrapGameState(
            {true, true, true, true, true, false, true, true}) ==
            reactorv::bootstrap::BootstrapGameStateClassification::StableFrontend,
        "Enhanced's landing menu must win over a transitional true player sample.");
    Require(
        reactorv::bootstrap::ClassifyBootstrapGameState(
            {true, true, true, false, true, false, true, false}) ==
            reactorv::bootstrap::BootstrapGameStateClassification::Initializing,
        "The same Enhanced probe after leaving the landing menu must select the Story preloader.");
    Require(
        reactorv::bootstrap::ClassifyBootstrapGameState(
            {false, false, false, false, true, true}) ==
            reactorv::bootstrap::BootstrapGameStateClassification::Inconclusive,
        "A frontend result alone must not claim a stable frontend.");
    Require(
        reactorv::bootstrap::ClassifyBootstrapGameState(
            {true, true, false, false, false, false}) ==
            reactorv::bootstrap::BootstrapGameStateClassification::Inconclusive,
        "Enhanced loading=true without player evidence is ambiguous on the stable frontend.");
    Require(
        reactorv::bootstrap::ClassifyBootstrapGameState(
            {false, false, true, true, false, false}) ==
            reactorv::bootstrap::BootstrapGameStateClassification::Initializing,
        "A successful player=true result must survive unavailable sibling probes.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::CoreDataPrepared,
            {true, false, true, false, true, true}) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::About,
        "A successful stable-frontend probe must route F9 to Reactor About.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::CoreDataPrepared,
            {true, true, true, false, true, false}) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing,
        "The observed Enhanced Story-loading probe must route F9 to the ALLIN1 preloader before CORE threads.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::CoreDataPrepared,
            {true, true, true, false, true, false, true, true}) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::About,
        "The observed Enhanced landing-menu probe must route F9 to Reactor About before CORE threads.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::CoreDataPrepared,
            {true, true, false, false, false, false}) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::About,
        "Ambiguous loading=true must retain the usable pre-managed landing-menu fallback.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::ScriptThreadsStarting,
            {true, true, false, false, false, false}) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::About,
        "Ambiguous loading=true after the frontend GtaThread marker must not hide About.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::CoreDataPrepared,
            {false, false, true, true, false, false}) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing,
        "A partial successful player probe must route F9 to the ALLIN1 preloader before CORE threads.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::CoreDataPrepared,
            {false, false, false, false, false, false}) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::About,
        "An unpublished probe must expose About before managed Story initialization.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::ScriptThreadsStarting,
            {false, false, false, false, false, false}) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::About,
        "An unpublished probe must keep About available after the frontend GtaThread marker.");
    Require(
        reactorv::bootstrap::ResolveBootstrapF9Surface(
            StartupStage::ScriptThreadsStarting,
            {true, false, true, false, true, true}) ==
            reactorv::bootstrap::BootstrapSurfaceRoute::About,
        "A complete stable-frontend snapshot must keep F9 on Reactor About even when ScriptHook threads already exist.");
    const auto stableFrontendAfterThreads =
        reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            StartupStage::ScriptThreadsStarting,
            {true, false, true, false, true, true},
            true,
            true);
    Require(
        stableFrontendAfterThreads.route ==
            reactorv::bootstrap::BootstrapSurfaceRoute::About &&
        stableFrontendAfterThreads.destinationAvailable,
        "ScriptHook thread creation on the GTA frontend must not display the Story preloader.");
    const auto storyTransitionAfterThreads =
        reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            StartupStage::ScriptThreadsStarting,
            {true, true, true, false, true, false, true, false},
            true,
            true);
    Require(
        storyTransitionAfterThreads.route ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing &&
        storyTransitionAfterThreads.destinationAvailable,
        "A fresh script-fiber transition snapshot must open the Story preloader before provider handoff.");
    const auto missingInitializerDestination =
        reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            StartupStage::CoreDataPrepared,
            {false, false, true, true, false, false},
            true,
            false);
    Require(
        missingInitializerDestination.route ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing &&
        !missingInitializerDestination.destinationAvailable,
        "The callback decision must fail closed when live player evidence selects a missing initializer event.");
    const auto availableInitializerDestination =
        reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            StartupStage::CoreDataPrepared,
            {false, false, true, true, false, false},
            false,
            true);
    Require(
        availableInitializerDestination.route ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing &&
        availableInitializerDestination.destinationAvailable,
        "The callback decision must select the initializer event for live player evidence.");

    const auto unpublishedFrontend =
        reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            StartupStage::ScriptThreadsStarting,
            {},
            false,
            false,
            true,
            true,
            true);
    Require(
        unpublishedFrontend.route ==
            reactorv::bootstrap::BootstrapSurfaceRoute::About &&
        unpublishedFrontend.destinationAvailable,
        "A registered-but-unpublished companion fiber must keep the landing-menu About surface immediately available.");
    const auto failedNativeAfterFiberStart =
        reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            StartupStage::ScriptThreadsStarting,
            {},
            false,
            true,
            true,
            true,
            true);
    Require(
        failedNativeAfterFiberStart.route ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing &&
        failedNativeAfterFiberStart.destinationAvailable,
        "A companion fiber that has begun executing must route Story loading to the initializer even when its natives fail.");
    const auto staleAfterFiberStart =
        reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            StartupStage::ScriptThreadsStarting,
            {true, false, true, false, true, true, true, true},
            false,
            true,
            true,
            false,
            true);
    Require(
        staleAfterFiberStart.route ==
            reactorv::bootstrap::BootstrapSurfaceRoute::Initializing &&
        !staleAfterFiberStart.destinationAvailable,
        "A stale post-fiber sample must remain on the typed Story initializer path and fail closed if that destination is missing.");

    const auto inactivePromotion =
        reactorv::bootstrap::EvaluateVerificationPromotion(
            false,
            false,
            1,
            {reactorv::bootstrap::BootstrapSurfaceRoute::About, true});
    Require(
        !inactivePromotion.shouldPromote &&
            !inactivePromotion.nextPromotionIssued,
        "A closed verification surface must never be resurrected by a late snapshot.");

    const auto aboutPromotion =
        reactorv::bootstrap::EvaluateVerificationPromotion(
            true,
            false,
            1,
            {reactorv::bootstrap::BootstrapSurfaceRoute::About, true});
    Require(
        aboutPromotion.shouldPromote && aboutPromotion.nextPromotionIssued,
        "An active neutral surface must promote to About on fresh authoritative evidence.");
    const auto duplicatePromotion =
        reactorv::bootstrap::EvaluateVerificationPromotion(
            true,
            aboutPromotion.nextPromotionIssued,
            1,
            {reactorv::bootstrap::BootstrapSurfaceRoute::Initializing, true});
    Require(
        !duplicatePromotion.shouldPromote &&
            duplicatePromotion.nextPromotionIssued,
        "One active verification surface must receive at most one promotion edge.");

    for (const auto unavailableStatus : {std::uint8_t{0}, std::uint8_t{2}}) {
        const auto unavailablePromotion =
            reactorv::bootstrap::EvaluateVerificationPromotion(
                true,
                false,
                unavailableStatus,
                {reactorv::bootstrap::BootstrapSurfaceRoute::About, true});
        Require(
            !unavailablePromotion.shouldPromote &&
                !unavailablePromotion.nextPromotionIssued,
            "Unavailable or stale probe evidence must keep the neutral surface open.");
    }
    const auto neutralPromotion =
        reactorv::bootstrap::EvaluateVerificationPromotion(
            true,
            false,
            1,
            {reactorv::bootstrap::BootstrapSurfaceRoute::Verifying, true});
    Require(
        !neutralPromotion.shouldPromote &&
            !neutralPromotion.nextPromotionIssued,
        "Fresh but inconclusive evidence must not promote a verification surface.");

    const auto resetPromotion =
        reactorv::bootstrap::EvaluateVerificationPromotion(
            false,
            duplicatePromotion.nextPromotionIssued,
            1,
            {reactorv::bootstrap::BootstrapSurfaceRoute::About, true});
    const auto initializerPromotion =
        reactorv::bootstrap::EvaluateVerificationPromotion(
            true,
            resetPromotion.nextPromotionIssued,
            1,
            {reactorv::bootstrap::BootstrapSurfaceRoute::Initializing, true});
    Require(
        !resetPromotion.nextPromotionIssued &&
            initializerPromotion.shouldPromote &&
            initializerPromotion.nextPromotionIssued,
        "An observed inactive interval must re-arm one future Initializing promotion.");

    const auto frontendInitializer =
        reactorv::bootstrap::EvaluateInitializerPromotion(
            false,
            {reactorv::bootstrap::BootstrapSurfaceRoute::About, true});
    Require(
        !frontendInitializer.shouldPromote &&
            !frontendInitializer.nextPromotionIssued,
        "Frontend routing must not display the Story initializer.");
    const auto unavailableInitializerPromotion =
        reactorv::bootstrap::EvaluateInitializerPromotion(
            false,
            {reactorv::bootstrap::BootstrapSurfaceRoute::Initializing, false});
    Require(
        !unavailableInitializerPromotion.shouldPromote &&
            !unavailableInitializerPromotion.nextPromotionIssued,
        "Story initialization must fail closed when its typed destination is unavailable.");
    const auto storyInitializer =
        reactorv::bootstrap::EvaluateInitializerPromotion(
            false,
            {reactorv::bootstrap::BootstrapSurfaceRoute::Initializing, true});
    Require(
        storyInitializer.shouldPromote &&
            storyInitializer.nextPromotionIssued,
        "Objective Story routing must promote the initializer without depending on About state.");
    const auto duplicateStoryInitializer =
        reactorv::bootstrap::EvaluateInitializerPromotion(
            storyInitializer.nextPromotionIssued,
            {reactorv::bootstrap::BootstrapSurfaceRoute::Initializing, true});
    Require(
        !duplicateStoryInitializer.shouldPromote &&
            duplicateStoryInitializer.nextPromotionIssued,
        "One continuous Story route must receive at most one initializer promotion edge.");
    const auto returnedToFrontend =
        reactorv::bootstrap::EvaluateInitializerPromotion(
            duplicateStoryInitializer.nextPromotionIssued,
            {reactorv::bootstrap::BootstrapSurfaceRoute::About, true});
    const auto nextStoryInitializer =
        reactorv::bootstrap::EvaluateInitializerPromotion(
            returnedToFrontend.nextPromotionIssued,
            {reactorv::bootstrap::BootstrapSurfaceRoute::Initializing, true});
    Require(
        !returnedToFrontend.nextPromotionIssued &&
            nextStoryInitializer.shouldPromote &&
            nextStoryInitializer.nextPromotionIssued,
        "Leaving Story must re-arm exactly one future initializer promotion.");
    using reactorv::bootstrap::ShouldAttemptObjectiveInitializerPromotion;
    Require(
        ShouldAttemptObjectiveInitializerPromotion(
            false, true, false, true, true),
        "A grounded Story edge may signal one installed preloader before handoff.");
    Require(
        !ShouldAttemptObjectiveInitializerPromotion(
            true, true, false, true, true) &&
            !ShouldAttemptObjectiveInitializerPromotion(
                false, false, false, true, true) &&
            !ShouldAttemptObjectiveInitializerPromotion(
                false, true, true, true, true) &&
            !ShouldAttemptObjectiveInitializerPromotion(
                false, true, false, false, true) &&
            !ShouldAttemptObjectiveInitializerPromotion(
                false, true, false, true, false),
        "Duplicate, missing, post-handoff, ungrounded, and unavailable promotions must fail closed.");
    Require(
        reactorv::bootstrap::DetectStartupStage("", "Loading scripts from C:\\GTA\\scripts", "", true) == StartupStage::ManagedRuntimeReady,
        "Managed runtime readiness should be detected.");
    Require(
        reactorv::bootstrap::DetectStartupStage("", "", "stage=story_mode_ready", true) == StartupStage::StoryModeReady,
        "Story Mode readiness should be detected.");

    const auto RequireStageParity = [](
        const reactorv::bootstrap::StartupSignals signals,
        const std::string& scriptHookLog,
        const std::string& scriptHookDotNetLog,
        const std::string& runtimeLog,
        const bool preloaderStarted,
        const bool preloadDataReady,
        const char* message) {
        Require(
            reactorv::bootstrap::DetectStartupStage(
                signals,
                preloaderStarted,
                preloadDataReady) ==
            reactorv::bootstrap::DetectStartupStage(
                scriptHookLog,
                scriptHookDotNetLog,
                runtimeLog,
                preloaderStarted,
                preloadDataReady),
            message);
    };
    RequireStageParity(
        {false, false, false, false},
        "", "", "", false, false,
        "Signal policy should preserve native-bootstrap stage parity.");
    RequireStageParity(
        {true, false, false, false},
        "INIT: Success", "", "", true, false,
        "Signal policy should preserve ScriptHook stage parity.");
    RequireStageParity(
        {true, false, false, false},
        "INIT: Success", "", "", true, true,
        "Signal policy should preserve prepared-core stage parity.");
    RequireStageParity(
        {true, true, false, false},
        "INIT: Success\nCORE: Launching main()", "", "", true, true,
        "Signal policy should preserve script-thread stage parity.");
    RequireStageParity(
        {true, true, true, false},
        "INIT: Success\nCORE: Creating threads",
        "Started script RageWebUI.Script.RageWebUiScript.",
        "", true, true,
        "Signal policy should preserve managed-runtime stage parity.");
    RequireStageParity(
        {true, true, true, true},
        "INIT: Success\nCORE: Creating threads",
        "Loading scripts from C:\\GTA\\scripts",
        "stage=story_mode_ready", true, true,
        "Signal policy should preserve Story Mode stage parity.");

    return 0;
}
