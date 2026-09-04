#pragma once

#include <cstdint>
#include <filesystem>
#include <string_view>

namespace reactorv::renderhook {

inline constexpr wchar_t EnhancedExecutableName[] = L"GTA5_Enhanced.exe";
inline constexpr wchar_t LegacyExecutableName[] = L"GTA5.exe";
inline constexpr wchar_t NativeRelativePath[] =
    L"plugins\\ReactorV\\RageWebUI.Native.dll";
inline constexpr wchar_t DiagnosticsRelativeDirectory[] =
    L"scripts\\ReactorV";
inline constexpr wchar_t DiagnosticsFileName[] =
    L"ReactorV.RenderHook.log";

struct RenderHookPaths {
    std::filesystem::path gameRoot;
    std::filesystem::path nativeModule;
    std::filesystem::path diagnosticsDirectory;
    std::filesystem::path diagnosticsFile;
};

struct RenderTargetWindowCandidate {
    std::uint32_t processId{};
    std::uint32_t expectedProcessId{};
    std::wstring_view className;
    bool topLevel{};
    bool owned{};
    bool toolWindow{};
    std::int32_t clientWidth{};
    std::int32_t clientHeight{};
};

enum class NativeModuleDisposition {
    ReleaseFailOpen,
    RetainArmed,
};

enum class RenderHookEdition {
    Unsupported,
    Legacy,
    Enhanced,
};

RenderHookEdition DetectRenderHookEdition(
    const std::filesystem::path& executablePath) noexcept;

bool IsEnhancedGameExecutable(
    const std::filesystem::path& executablePath) noexcept;

bool IsLegacyGameExecutable(
    const std::filesystem::path& executablePath) noexcept;

bool IsStoryModdingPolicySatisfied(std::wstring_view commandLine);

RenderHookPaths ResolveRenderHookPaths(
    const std::filesystem::path& executablePath);

NativeModuleDisposition ResolveNativeModuleDisposition(
    bool exportAvailable,
    std::int32_t armResult) noexcept;

bool IsEligibleEnhancedRenderTarget(
    const RenderTargetWindowCandidate& candidate) noexcept;

bool IsEligibleLegacyRenderTarget(
    const RenderTargetWindowCandidate& candidate) noexcept;

} // namespace reactorv::renderhook
