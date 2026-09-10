#pragma once

#include <windows.h>
#include <filesystem>
#include <string>

namespace reactorv::renderhook {

struct AppLocalDxgiResult final {
    bool allowNativeLoad{};
    DWORD error{};
    std::wstring status;
    std::wstring productVersion;
    std::filesystem::path candidate;
    std::filesystem::path loadedPath;
    std::filesystem::path firstDxgiBefore;
    std::filesystem::path firstDxgiAfter;
};

// Worker-thread only, after the Story/edition gate. Never call from DllMain.
// Recognizes an already installed ReShade proxy; does not install or configure it.
// Successful executable module references are intentionally retained for process
// lifetime, even if subsequent native loading/arming fails (published hooks).
AppLocalDxgiResult PrepareAppLocalDxgi(const std::filesystem::path& executablePath);

} // namespace reactorv::renderhook
