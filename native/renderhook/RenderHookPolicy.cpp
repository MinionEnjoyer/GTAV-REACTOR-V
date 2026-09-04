#include "RenderHookPolicy.h"

#include <cwchar>
#include <string>
#include <vector>

namespace reactorv::renderhook {

namespace {

wchar_t FoldAsciiCase(const wchar_t value) noexcept {
    return value >= L'A' && value <= L'Z'
        ? static_cast<wchar_t>(value + (L'a' - L'A'))
        : value;
}

bool EqualsAsciiCaseInsensitive(
    const std::wstring_view left,
    const std::wstring_view right) noexcept {
    if (left.size() != right.size()) return false;
    for (std::size_t index = 0; index < left.size(); ++index) {
        if (FoldAsciiCase(left[index]) != FoldAsciiCase(right[index])) {
            return false;
        }
    }
    return true;
}

std::vector<std::wstring> ParseWindowsCommandLine(
    const std::wstring_view commandLine) {
    std::vector<std::wstring> arguments;
    std::size_t cursor = 0;
    while (cursor < commandLine.size()) {
        while (cursor < commandLine.size() &&
            (commandLine[cursor] == L' ' || commandLine[cursor] == L'\t')) {
            ++cursor;
        }
        if (cursor >= commandLine.size()) break;

        std::wstring argument;
        bool quoted = false;
        while (cursor < commandLine.size()) {
            std::size_t backslashes = 0;
            while (cursor < commandLine.size() &&
                commandLine[cursor] == L'\\') {
                ++backslashes;
                ++cursor;
            }

            if (cursor < commandLine.size() && commandLine[cursor] == L'"') {
                argument.append(backslashes / 2, L'\\');
                if ((backslashes % 2) != 0) {
                    argument.push_back(L'"');
                } else {
                    quoted = !quoted;
                }
                ++cursor;
                continue;
            }

            argument.append(backslashes, L'\\');
            if (cursor >= commandLine.size()) break;
            if (!quoted &&
                (commandLine[cursor] == L' ' ||
                 commandLine[cursor] == L'\t')) {
                break;
            }
            argument.push_back(commandLine[cursor]);
            ++cursor;
        }
        arguments.push_back(std::move(argument));
    }
    return arguments;
}

} // namespace

bool IsEnhancedGameExecutable(
    const std::filesystem::path& executablePath) noexcept {
    const auto fileName = executablePath.filename().wstring();
    return _wcsicmp(fileName.c_str(), EnhancedExecutableName) == 0;
}

bool IsLegacyGameExecutable(
    const std::filesystem::path& executablePath) noexcept {
    const auto fileName = executablePath.filename().wstring();
    return _wcsicmp(fileName.c_str(), LegacyExecutableName) == 0;
}

RenderHookEdition DetectRenderHookEdition(
    const std::filesystem::path& executablePath) noexcept {
    if (IsEnhancedGameExecutable(executablePath)) {
        return RenderHookEdition::Enhanced;
    }
    if (IsLegacyGameExecutable(executablePath)) {
        return RenderHookEdition::Legacy;
    }
    return RenderHookEdition::Unsupported;
}

bool IsStoryModdingPolicySatisfied(const std::wstring_view commandLine) {
    const auto arguments = ParseWindowsCommandLine(commandLine);
    // Argument zero is the executable image. An executable path or filename
    // must never accidentally satisfy the explicit offline launch policy.
    for (std::size_t index = 1; index < arguments.size(); ++index) {
        if (EqualsAsciiCaseInsensitive(arguments[index], L"-nobattleye")) {
            return true;
        }
    }
    return false;
}

RenderHookPaths ResolveRenderHookPaths(
    const std::filesystem::path& executablePath) {
    const auto gameRoot = executablePath.parent_path();
    const auto diagnosticsDirectory =
        gameRoot / std::filesystem::path(DiagnosticsRelativeDirectory);
    return {
        gameRoot,
        gameRoot / std::filesystem::path(NativeRelativePath),
        diagnosticsDirectory,
        diagnosticsDirectory / DiagnosticsFileName,
    };
}

NativeModuleDisposition ResolveNativeModuleDisposition(
    const bool exportAvailable,
    const std::int32_t armResult) noexcept {
    return exportAvailable && armResult > 0
        ? NativeModuleDisposition::RetainArmed
        : NativeModuleDisposition::ReleaseFailOpen;
}

bool IsEligibleEnhancedRenderTarget(
    const RenderTargetWindowCandidate& candidate) noexcept {
    const bool knownClass =
        EqualsAsciiCaseInsensitive(candidate.className, L"sgaWindow") ||
        EqualsAsciiCaseInsensitive(candidate.className, L"grcWindow");
    return candidate.processId != 0 &&
        candidate.processId == candidate.expectedProcessId && knownClass &&
        candidate.topLevel && !candidate.owned && !candidate.toolWindow &&
        candidate.clientWidth > 0 && candidate.clientHeight > 0;
}

bool IsEligibleLegacyRenderTarget(
    const RenderTargetWindowCandidate& candidate) noexcept {
    return candidate.processId != 0 &&
        candidate.processId == candidate.expectedProcessId &&
        EqualsAsciiCaseInsensitive(candidate.className, L"grcWindow") &&
        candidate.topLevel && !candidate.owned && !candidate.toolWindow &&
        candidate.clientWidth > 0 && candidate.clientHeight > 0;
}

} // namespace reactorv::renderhook
