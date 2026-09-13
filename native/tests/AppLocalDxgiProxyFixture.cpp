// Synthetic loader fixture. Not a renderer or a distributable DXGI replacement.
#include <windows.h>

extern "C" __declspec(dllexport) HRESULT WINAPI CreateDXGIFactory(REFIID, void**) {
    return E_NOTIMPL;
}
#ifndef FIXTURE_MISSING_EXPORTS
extern "C" __declspec(dllexport) HRESULT WINAPI CreateDXGIFactory1(REFIID, void**) {
    return E_NOTIMPL;
}
extern "C" __declspec(dllexport) HRESULT WINAPI CreateDXGIFactory2(UINT, REFIID, void**) {
    return E_NOTIMPL;
}
#endif
BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
#ifdef FIXTURE_FAIL_LOAD
    if (reason == DLL_PROCESS_ATTACH) return FALSE;
#endif
    return TRUE;
}
