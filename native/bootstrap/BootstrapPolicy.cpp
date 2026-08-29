#include "BootstrapPolicy.h"

#include <algorithm>
#include <cwctype>

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
        L" --instance-id production";
}

StartupStage DetectStartupStage(
    const std::string& scriptHookLog,
    const std::string& scriptHookDotNetLog,
    const std::string& reactorRuntimeLog,
    const bool preloaderStarted,
    const bool preloadDataReady) {
    if (Contains(reactorRuntimeLog, "story_mode_ready")) {
        return StartupStage::StoryModeReady;
    }
    if (Contains(scriptHookDotNetLog, "Loading scripts from") ||
        Contains(scriptHookDotNetLog, "Started script RageWebUI.Script.RageWebUiScript.")) {
        return StartupStage::ManagedRuntimeReady;
    }
    if (Contains(scriptHookLog, "CORE: Creating threads") ||
        Contains(scriptHookLog, "CORE: Launching main()")) {
        return StartupStage::ScriptThreadsStarting;
    }
    if (Contains(scriptHookLog, "INIT: Success")) {
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

} // namespace reactorv::bootstrap
