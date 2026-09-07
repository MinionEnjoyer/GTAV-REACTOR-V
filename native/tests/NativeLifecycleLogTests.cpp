#include "NativeLifecycleLog.h"
#include "NativeBuildIdentity.h"
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>

std::string Read(const std::filesystem::path& path) {
    std::ifstream input(path);
    return {std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
}
int main() {
    wchar_t executable[32768]{};
    if (!GetModuleFileNameW(nullptr, executable, 32768)) return 1;
    const auto directory = std::filesystem::path(executable).parent_path() / L"scripts" / L"ReactorV";
    const auto path = directory / L"ReactorV.NativeLifecycle.log";
    const auto rotated = directory / L"ReactorV.NativeLifecycle.log.1";
    std::filesystem::create_directories(directory);
    { std::ofstream reset(path, std::ios::trunc); }
    rwui::WriteNativeLifecycle("test_event", "test_operation", 7);
    const auto line = Read(path);
    if (line.find("event=test_event operation=test_operation result=7") == std::string::npos ||
        line.find(REACTORV_NATIVE_BUILD_ID) == std::string::npos ||
        line.find("pid=" + std::to_string(GetCurrentProcessId())) == std::string::npos ||
        line.find("tid=") == std::string::npos || line.find("configuration=") == std::string::npos ||
        line.find("module=") == std::string::npos) return 2;
    { std::ofstream full(path, std::ios::binary | std::ios::trunc);
      full << std::string(1024 * 1024, 'x'); }
    rwui::WriteNativeLifecycle("after_rotation", "test");
    if (std::filesystem::file_size(rotated) != 1024 * 1024 ||
        std::filesystem::file_size(path) >= 1024 ||
        Read(path).find("after_rotation") == std::string::npos) return 3;
    // An unwritable log destination must fail open, without throwing.
    std::filesystem::remove(path);
    std::filesystem::create_directory(path);
    rwui::WriteNativeLifecycle("unwritable", "test");
    if (!std::filesystem::is_directory(path)) return 4;
    std::filesystem::remove(path);
    std::filesystem::remove(rotated);
    std::cout << "PASS: identity, event fields, bounded rotation, and failed I/O\n";
    return 0;
}
