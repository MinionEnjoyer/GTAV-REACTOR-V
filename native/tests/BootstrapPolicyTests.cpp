#include "BootstrapPolicy.h"

#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <string>

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
        command.find(L"--parent-pid 4242 --instance-id production") != std::wstring::npos,
        "Preloader command should bind to the GTA process and production singleton.");

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
        reactorv::bootstrap::DetectStartupStage("", "Loading scripts from C:\\GTA\\scripts", "", true) == StartupStage::ManagedRuntimeReady,
        "Managed runtime readiness should be detected.");
    Require(
        reactorv::bootstrap::DetectStartupStage("", "", "stage=story_mode_ready", true) == StartupStage::StoryModeReady,
        "Story Mode readiness should be detected.");

    return 0;
}
