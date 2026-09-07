#include <windows.h>
#include <iostream>

namespace {
WNDPROC reactorProcedure{};
int forwarded{};
constexpr UINT ProbeMessage = WM_APP + 42;
LRESULT CALLBACK Original(HWND window, UINT message, WPARAM w, LPARAM l) {
    if (message == ProbeMessage) { ++forwarded; return 42; }
    return DefWindowProcW(window, message, w, l);
}
LRESULT CALLBACK OtherMod(HWND window, UINT message, WPARAM w, LPARAM l) {
    return CallWindowProcW(reactorProcedure, window, message, w, l);
}
}
int main(int argc, char** argv) {
    if (argc != 2) return 1;
    const HMODULE module = LoadLibraryA(argv[1]);
    if (!module) return 2;
    const auto attach = reinterpret_cast<BOOL(*)(HWND)>(GetProcAddress(module, "ProbeAttach"));
    const auto detach = reinterpret_cast<void(*)()>(GetProcAddress(module, "ProbeDetach"));
    if (!attach || !detach) return 3;
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = Original;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"ReactorCallbackLifetimeTest";
    if (!RegisterClassW(&windowClass)) return 4;
    HWND window = CreateWindowW(windowClass.lpszClassName, L"", 0, 0, 0, 0, 0,
                                HWND_MESSAGE, nullptr, windowClass.hInstance, nullptr);
    if (!window || !attach(window)) return 5;
    reactorProcedure = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(
        window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&OtherMod)));
    if (!reactorProcedure) return 6;
    detach();
    if (!FreeLibrary(module)) return 7;
    HMODULE retained{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(reactorProcedure), &retained) ||
        retained != module) return 8;
    // Actual indirect call through the other mod's retained Reactor callback,
    // after its owner detached and the explicit library reference was freed.
    if (SendMessageW(window, ProbeMessage, 0, 0) != 42 || forwarded != 1) return 9;
    DestroyWindow(window);
    UnregisterClassW(windowClass.lpszClassName, windowClass.hInstance);
    std::cout << "PASS: callback chain remains valid after detach + FreeLibrary\n";
    return 0;
}
