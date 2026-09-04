#include "IncrementalLogSignals.h"

#include <windows.h>

#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>

namespace {

using reactorv::bootstrap::IncrementalLogSignals;
using reactorv::bootstrap::InitialLogContentPolicy;

void Require(const bool condition, const char* message) {
    if (!condition) {
        std::cerr << message << '\n';
        std::exit(1);
    }
}

std::uint64_t FileTimeValue(const FILETIME& value) {
    ULARGE_INTEGER result{};
    result.LowPart = value.dwLowDateTime;
    result.HighPart = value.dwHighDateTime;
    return result.QuadPart;
}

FILETIME ToFileTime(const std::uint64_t value) {
    ULARGE_INTEGER source{};
    source.QuadPart = value;
    FILETIME result{};
    result.dwLowDateTime = source.LowPart;
    result.dwHighDateTime = source.HighPart;
    return result;
}

std::uint64_t NowFileTime() {
    FILETIME now{};
    GetSystemTimeAsFileTime(&now);
    return FileTimeValue(now);
}

void SetLastWrite(const std::filesystem::path& path, const std::uint64_t value) {
    const HANDLE handle = CreateFileW(
        path.c_str(),
        FILE_WRITE_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    Require(handle != INVALID_HANDLE_VALUE, "The test log should open for timestamp updates.");
    const auto timestamp = ToFileTime(value);
    const bool updated = SetFileTime(handle, nullptr, nullptr, &timestamp) != FALSE;
    CloseHandle(handle);
    Require(updated, "The test log timestamp should be updated.");
}

void Write(const std::filesystem::path& path, const std::string& value, const bool append = false) {
    std::ofstream output(
        path,
        std::ios::binary | (append ? std::ios::app : std::ios::trunc));
    Require(static_cast<bool>(output), "The test log should open for writing.");
    output.write(value.data(), static_cast<std::streamsize>(value.size()));
    output.flush();
    Require(static_cast<bool>(output), "The test log write should complete.");
}

class TemporaryDirectory final {
public:
    TemporaryDirectory() {
        wchar_t root[MAX_PATH]{};
        Require(GetTempPathW(MAX_PATH, root) > 0, "A temporary path is required.");
        _path = std::filesystem::path(root) /
            (L"ReactorV-IncrementalLogSignals-" +
             std::to_wstring(GetCurrentProcessId()) + L"-" +
             std::to_wstring(GetTickCount64()));
        std::filesystem::create_directories(_path);
    }
    ~TemporaryDirectory() {
        std::error_code ignored;
        std::filesystem::remove_all(_path, ignored);
    }
    const std::filesystem::path& Path() const noexcept { return _path; }

private:
    std::filesystem::path _path;
};

} // namespace

int main() {
    TemporaryDirectory temporary;
    const auto processStart = NowFileTime();
    const auto freshWrite = processStart + 10000000ULL;

    const auto unchangedPath = temporary.Path() / L"unchanged.log";
    Write(unchangedPath, std::string(300000, 'x') + " INIT: Success");
    SetLastWrite(unchangedPath, freshWrite);
    IncrementalLogSignals unchanged(
        unchangedPath,
        processStart,
        {"INIT: Success"});
    Require(unchanged.Refresh(), "A fresh marker in the bounded tail should be detected.");
    for (int tick = 0; tick < 240; ++tick) {
        Require(!unchanged.Refresh(), "An unchanged log must not rediscover a marker.");
    }
    Require(unchanged.Counters().metadataChecks == 241,
        "Every maintenance tick should perform one bounded metadata check.");
    Require(unchanged.Counters().contentReads == 1,
        "An unchanged log should be read only once across a simulated minute.");
    Require(unchanged.Counters().bytesRead <= IncrementalLogSignals::DefaultMaximumReadBytes,
        "The initial tail read must remain bounded.");

    const auto splitPath = temporary.Path() / L"split.log";
    Write(splitPath, "prefix MANAGED_");
    SetLastWrite(splitPath, freshWrite);
    IncrementalLogSignals split(
        splitPath,
        processStart,
        {"MANAGED_RUNTIME_READY"});
    Require(!split.Refresh(), "An incomplete marker must not be reported.");
    Write(splitPath, "RUNTIME_READY suffix", true);
    SetLastWrite(splitPath, freshWrite + 1);
    Require(split.Refresh(), "A marker split across appended writes should be detected.");
    Require(split.HasSignal("MANAGED_RUNTIME_READY"), "Detected markers should remain sticky.");
    Require(split.Counters().contentReads == 2, "Only initial and appended bytes should be read.");
    Require(split.Counters().bytesRead == std::string("prefix MANAGED_RUNTIME_READY suffix").size(),
        "Incremental reads should consume the exact appended byte total.");

    const auto stalePath = temporary.Path() / L"stale.log";
    Write(stalePath, "stage=story_mode_ready");
    SetLastWrite(stalePath, processStart - 1);
    IncrementalLogSignals stale(
        stalePath,
        processStart,
        {"story_mode_ready"});
    Require(!stale.Refresh() && !stale.HasSignal("story_mode_ready"),
        "Even a one-tick pre-process write must be rejected without tolerance.");
    SetLastWrite(stalePath, processStart - 49000000ULL);
    Require(!stale.Refresh() && !stale.HasSignal("story_mode_ready"),
        "A rapidly restarted session's terminal marker must stay rejected.");

    const auto aggregatePath = temporary.Path() / L"aggregate.log";
    Write(aggregatePath, "old session stage=story_mode_ready\n");
    SetLastWrite(aggregatePath, freshWrite);
    IncrementalLogSignals aggregate(
        aggregatePath,
        processStart,
        {"story_mode_ready"},
        InitialLogContentPolicy::IgnoreExistingContent);
    Require(!aggregate.Refresh() && !aggregate.HasSignal("story_mode_ready"),
        "An aggregate log must ignore existing prior-session records.");
    Write(aggregatePath, "current session stage=starting\n", true);
    SetLastWrite(aggregatePath, freshWrite + 2);
    Require(!aggregate.Refresh() && !aggregate.HasSignal("story_mode_ready"),
        "New nonterminal aggregate records must not expose old terminal content.");
    Write(aggregatePath, "current session stage=story_mode_ready\n", true);
    SetLastWrite(aggregatePath, freshWrite + 3);
    Require(aggregate.Refresh() && aggregate.HasSignal("story_mode_ready"),
        "A terminal marker appended after the aggregate boundary should be detected.");

    const auto createdAfterBaselinePath = temporary.Path() / L"created after baseline.log";
    IncrementalLogSignals createdAfterBaseline(
        createdAfterBaselinePath,
        processStart,
        {"stage=story_mode_ready"},
        InitialLogContentPolicy::IgnoreExistingContent);
    Require(!createdAfterBaseline.Refresh(),
        "An absent aggregate path should establish an empty baseline.");
    Write(createdAfterBaselinePath, "current stage=story_mode_ready\n");
    SetLastWrite(createdAfterBaselinePath, freshWrite + 4);
    Require(createdAfterBaseline.Refresh() &&
            createdAfterBaseline.HasSignal("stage=story_mode_ready"),
        "A log created after the baseline belongs to the current session and must be scanned.");

    const auto replacedPath = temporary.Path() / L"replace.log";
    Write(replacedPath, "initial no marker");
    SetLastWrite(replacedPath, freshWrite);
    IncrementalLogSignals replaced(
        replacedPath,
        processStart,
        {"CORE: Creating threads"});
    Require(!replaced.Refresh(), "The initial replacement fixture has no marker.");
    std::filesystem::remove(replacedPath);
    Write(replacedPath, "CORE: Creating threads");
    SetLastWrite(replacedPath, freshWrite + 5);
    Require(replaced.Refresh(), "A replacement file should reset the cursor and be inspected.");

    const auto truncatedPath = temporary.Path() / L"truncate.log";
    Write(truncatedPath, "padding without signal");
    SetLastWrite(truncatedPath, freshWrite);
    IncrementalLogSignals truncated(
        truncatedPath,
        processStart,
        {"Loading scripts from"});
    Require(!truncated.Refresh(), "The initial truncation fixture has no marker.");
    Write(truncatedPath, "Loading scripts from");
    SetLastWrite(truncatedPath, freshWrite + 6);
    Require(truncated.Refresh(), "A truncated file should restart at byte zero.");
    Write(truncatedPath, "no marker after a second truncation");
    SetLastWrite(truncatedPath, freshWrite + 7);
    Require(!truncated.Refresh() && truncated.HasSignal("Loading scripts from"),
        "A detected stage must remain sticky after later truncation removes its marker.");

    const auto temporarilyLockedPath = temporary.Path() / L"temporarily locked.log";
    Write(temporarilyLockedPath, "INIT: Success");
    SetLastWrite(temporarilyLockedPath, freshWrite);
    const HANDLE exclusive = CreateFileW(
        temporarilyLockedPath.c_str(),
        GENERIC_READ,
        0,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    Require(exclusive != INVALID_HANDLE_VALUE, "The test should acquire an exclusive log handle.");
    IncrementalLogSignals temporarilyLocked(
        temporarilyLockedPath,
        processStart,
        {"INIT: Success"});
    Require(!temporarilyLocked.Refresh() && !temporarilyLocked.HasSignal("INIT: Success"),
        "A temporary sharing violation must fail closed without throwing.");
    CloseHandle(exclusive);
    Require(temporarilyLocked.Refresh() && temporarilyLocked.HasSignal("INIT: Success"),
        "A log should recover when a temporary sharing violation clears.");

    return 0;
}
