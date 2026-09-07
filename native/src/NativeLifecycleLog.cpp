#include "NativeLifecycleLog.h"
#include "NativeBuildIdentity.h"
#include <windows.h>
#include <array>
#include <cstdio>
#include <filesystem>
#include <mutex>

namespace rwui {
void WriteNativeLifecycle(const char* event, const char* operation,
                          int result, const void* target) noexcept {
    try {
        static std::mutex mutex;
        std::scoped_lock lock(mutex);
        std::array<wchar_t, 32768> executable{};
        auto length = GetModuleFileNameW(nullptr, executable.data(),
                                         static_cast<DWORD>(executable.size()));
        if (length == 0 || length >= executable.size()) return;
        const auto directory = std::filesystem::path(executable.data()).parent_path() /
            L"scripts" / L"ReactorV";
        std::error_code error;
        std::filesystem::create_directories(directory, error);
        if (error) return;
        const auto path = directory / L"ReactorV.NativeLifecycle.log";
        // At most one 1-MiB current log and one rotated log; rotation failure
        // drops diagnostics rather than growing without a bound.
        const auto bytes = std::filesystem::file_size(path, error);
        if (!error && bytes >= 1024 * 1024 - 1024) {
            auto previous = path; previous += L".1";
            if (!MoveFileExW(path.c_str(), previous.c_str(), MOVEFILE_REPLACE_EXISTING)) return;
        }
        HMODULE module{};
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                              GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                          reinterpret_cast<LPCWSTR>(&WriteNativeLifecycle), &module);
        SYSTEMTIME now{};
        GetSystemTime(&now);
        std::array<char, 1024> line{};
        const int count = std::snprintf(line.data(), line.size(),
            "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ pid=%lu tid=%lu "
            "build=%s configuration=%s compiled=%s module=%p event=%s operation=%s result=%d target=%p\r\n",
            now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond,
            now.wMilliseconds, GetCurrentProcessId(), GetCurrentThreadId(),
            REACTORV_NATIVE_BUILD_ID, REACTORV_NATIVE_CONFIGURATION,
            __DATE__ "T" __TIME__, static_cast<void*>(module), event, operation, result, target);
        if (count <= 0 || static_cast<size_t>(count) >= line.size()) return;
        const HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
            OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
        DWORD written{};
        WriteFile(file, line.data(), static_cast<DWORD>(count), &written, nullptr);
        CloseHandle(file);
    } catch (...) {
        // A diagnostics failure must not affect GTA or native cleanup.
    }
}
}
