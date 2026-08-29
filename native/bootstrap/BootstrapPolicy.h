#pragma once

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

bool IsSupportedGameExecutable(const std::filesystem::path& executablePath);
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
const wchar_t* StartupStageText(StartupStage stage);

} // namespace reactorv::bootstrap
