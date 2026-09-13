#include <windows.h>
#include <dxgi.h>
#include <d3d11.h>
#include <d3d12.h>

// A genuine static DXGI import, like the production native compositor. The
// launcher deliberately has no graphics imports, so ordering is controlled.
extern "C" __declspec(dllexport) HRESULT InvokeImportedDxgi() {
    IDXGIFactory* factory{};
    const auto result = CreateDXGIFactory(__uuidof(IDXGIFactory), reinterpret_cast<void**>(&factory));
    if (factory) factory->Release();
    return result;
}
extern "C" __declspec(dllexport) FARPROC ImportedD3D11() {
    return reinterpret_cast<FARPROC>(&D3D11CreateDevice);
}
extern "C" __declspec(dllexport) FARPROC ImportedD3D12() {
    return reinterpret_cast<FARPROC>(&D3D12CreateDevice);
}
