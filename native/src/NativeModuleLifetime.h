#pragma once

#include <windows.h>

namespace rwui {

// A callback can be retained by Windows, another ASI, or a thread paused
// before entering our callback counter. Never unload its code mid-process.
// Pin before publishing the first hook/subclass, not only after arming succeeds.
inline bool RetainCallbackModuleForProcessLifetime() noexcept {
    HMODULE module{};
    return GetModuleHandleExW(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&RetainCallbackModuleForProcessLifetime),
        &module) != FALSE;
}

} // namespace rwui
