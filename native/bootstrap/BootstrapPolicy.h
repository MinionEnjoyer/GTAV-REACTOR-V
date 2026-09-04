#pragma once

#include <atomic>
#include <cstdint>
#include <filesystem>
#include <string>

namespace reactorv::bootstrap {

enum class StartupStage : std::uint8_t {
    NativeBootstrap,
    CacheWarmup,
    ScriptHookInitialized,
    CoreDataPrepared,
    ScriptThreadsStarting,
    ManagedRuntimeReady,
    StoryModeReady,
};

enum class BootstrapSurfaceRoute : std::uint8_t {
    Verifying,
    About,
    Initializing,
};

struct F9InputDecision {
    bool routeToBootstrap;
    bool releaseOwnership;
};

enum class F9OwnershipExitKind : std::uint8_t {
    RuntimeHandoff,
    ProcessExitRequested,
    WorkerException,
};

struct F9OwnershipExitDecision {
    bool signalBoundary;
    bool abandoned;
};

struct BootstrapProcessExitIntent {
    bool pending{};
    std::uint64_t startedAtMilliseconds{};
};

struct BootstrapProcessExitDecision {
    BootstrapProcessExitIntent nextIntent;
    bool confirmed{};
};

struct StartupSignals {
    bool scriptHookInitialized;
    bool scriptThreadsStarting;
    bool managedRuntimeReady;
    bool storyModeReady;
};

struct BootstrapGameStateProbe {
    bool loadingAvailable{};
    bool loading{};
    bool playerPlayingAvailable{};
    bool playerPlaying{};
    bool frontendReadyAvailable{};
    bool frontendReady{};
    bool landingMenuAvailable{};
    bool landingMenuActive{};
};

enum class BootstrapGameStateClassification : std::uint8_t {
    Inconclusive,
    StableFrontend,
    Initializing,
};

struct BootstrapF9DispatchDecision {
    BootstrapSurfaceRoute route;
    bool destinationAvailable;
};

struct BootstrapVerificationPromotionDecision {
    bool shouldPromote;
    bool nextPromotionIssued;
};

struct BootstrapInitializerPromotionDecision {
    bool shouldPromote;
    bool nextPromotionIssued;
};

bool IsSupportedGameExecutable(const std::filesystem::path& executablePath);
std::uint32_t F9PollIntervalMilliseconds();
std::uint64_t F9RouteClaimDebounceMilliseconds();
std::uint32_t MaintenancePollIntervalMilliseconds();
std::uint32_t BootstrapGameStateProbeWaitMilliseconds(bool enabled);
std::uint64_t BootstrapProcessExitIntentLifetimeMilliseconds();
std::uint64_t BootstrapGameWindowLossGraceMilliseconds();
std::uint64_t BootstrapGameStateProbeMaximumAgeMilliseconds();
bool IsBootstrapGameStateProbeFresh(
    std::uint64_t sampledAtMilliseconds,
    std::uint64_t nowMilliseconds);
bool DecodeScriptHookBoolResult(std::uint64_t rawResult);
F9InputDecision EvaluateF9Input(
    bool runtimeReady,
    bool physicalKeyDown,
    bool pressedSinceLastPoll,
    bool previousPhysicalKeyDown,
    bool gameForeground);
bool ShouldUseF9PollingFallback(
    bool keyboardHandlerRegistered,
    bool keyboardDispatchObserved);
bool TryClaimF9RouteEdge(
    std::atomic<std::uint64_t>& lastClaimedAtMilliseconds,
    std::uint64_t nowMilliseconds);
bool EvaluateBootstrapCloseInput(
    bool physicalKeyDown,
    bool pressedSinceLastPoll,
    bool previousPhysicalKeyDown,
    bool gameForeground);
BootstrapProcessExitDecision EvaluateBootstrapProcessExitInput(
    const BootstrapProcessExitIntent& currentIntent,
    std::uint64_t nowMilliseconds,
    bool gameForeground,
    bool altDown,
    bool f4PressedEdge,
    bool enterPressedEdge,
    bool escapePressedEdge);
bool IsBootstrapGameWindowLossConfirmed(
    bool gameWindowWasObserved,
    bool gameWindowAvailable,
    std::uint64_t missingSinceMilliseconds,
    std::uint64_t nowMilliseconds);
F9OwnershipExitDecision EvaluateF9OwnershipExit(
    bool boundaryAvailable,
    bool ownershipReleased,
    F9OwnershipExitKind exitKind);
bool ShouldCloseBootstrapHostOnOwnershipExit(F9OwnershipExitKind exitKind);
std::filesystem::path ResolvePreloaderPath(const std::filesystem::path& gameExecutablePath);
std::wstring QuoteCommandLineArgument(const std::wstring& value);
std::wstring BuildPreloaderCommandLine(
    const std::filesystem::path& preloaderPath,
    std::uint32_t processId);
StartupStage DetectStartupStage(
    const std::string& scriptHookLog,
    const std::string& scriptHookDotNetLog,
    const std::string& reactorRuntimeLog,
    bool preloaderStarted,
    bool preloadDataReady = false);
StartupStage DetectStartupStage(
    const StartupSignals& signals,
    bool preloaderStarted,
    bool preloadDataReady = false);
const wchar_t* StartupStageText(StartupStage stage);
BootstrapSurfaceRoute ResolveBootstrapF9Surface(StartupStage stage);
BootstrapSurfaceRoute ResolveBootstrapF9Surface(
    StartupStage fallbackStage,
    const BootstrapGameStateProbe& probe);
BootstrapGameStateClassification ClassifyBootstrapGameState(
    const BootstrapGameStateProbe& probe);
BootstrapF9DispatchDecision ResolveBootstrapF9Dispatch(
    StartupStage fallbackStage,
    bool aboutDestinationAvailable,
    bool initializerDestinationAvailable,
    bool verifyingDestinationAvailable = false);
BootstrapF9DispatchDecision ResolveBootstrapF9Dispatch(
    StartupStage fallbackStage,
    const BootstrapGameStateProbe& probe,
    bool aboutDestinationAvailable,
    bool initializerDestinationAvailable,
    bool verifyingDestinationAvailable = false);
BootstrapF9DispatchDecision ResolveBootstrapF9Dispatch(
    StartupStage fallbackStage,
    const BootstrapGameStateProbe& probe,
    bool freshProbeAvailable,
    bool scriptFiberExecutionObserved,
    bool aboutDestinationAvailable,
    bool initializerDestinationAvailable,
    bool verifyingDestinationAvailable = false);
BootstrapVerificationPromotionDecision EvaluateVerificationPromotion(
    bool verificationActive,
    bool promotionIssued,
    std::uint8_t probeStatus,
    const BootstrapF9DispatchDecision& dispatch);
BootstrapInitializerPromotionDecision EvaluateInitializerPromotion(
    bool promotionIssued,
    const BootstrapF9DispatchDecision& dispatch);
bool ShouldAttemptObjectiveInitializerPromotion(
    bool promotionIssued,
    bool preloaderStarted,
    bool runtimeReady,
    bool objectiveStoryEdge,
    bool destinationAvailable);

} // namespace reactorv::bootstrap
