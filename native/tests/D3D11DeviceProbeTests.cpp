#include "D3D11DeviceProbe.h"
#include <iostream>
#include <wrl/client.h>

int main() {
    int failures = 0;
    auto check = [&](bool good, const char* name) {
        std::cout << (good ? "PASS: " : "FAIL: ") << name << '\n';
        if (!good) ++failures;
    };
    const auto absent = rwui::ProbeD3D11Device(nullptr);
    check(!absent.probeComplete && absent.localBgraHresult == static_cast<UINT>(E_PENDING),
        "absent device cannot masquerade as a passing probe");
    for (const UINT flags : {0u, UINT(D3D11_CREATE_DEVICE_BGRA_SUPPORT), UINT(D3D11_CREATE_DEVICE_SINGLETHREADED)}) {
        Microsoft::WRL::ComPtr<ID3D11Device> device;
        const D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_0};
        const auto hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            flags, levels, 1, D3D11_SDK_VERSION, &device, nullptr, nullptr);
        if (FAILED(hr)) { std::cout << "SKIP: hardware D3D11 required\n"; return 125; }
        const auto result = rwui::ProbeD3D11Device(device.Get());
        const auto passive = rwui::ProbeD3D11Device(device.Get(), false);
        check(!passive.probeComplete && passive.peerDeviceHresult == static_cast<UINT>(E_PENDING),
            "production identity snapshot performs no opt-in allocation probe");
        check(result.featureLevel == D3D_FEATURE_LEVEL_11_0 && result.creationFlags == flags,
            "actual feature level and creation flags remain unchanged");
        if (flags == D3D11_CREATE_DEVICE_SINGLETHREADED) {
            check(!result.probeComplete && result.peerDeviceHresult == static_cast<UINT>(E_PENDING),
                "single-threaded game device rejects worker-side allocation probes");
        } else {
            check(result.probeComplete && result.localBgraHresult == S_OK &&
                result.sharedBgraHresult == S_OK && result.sharedRgbaHresult == S_OK &&
                result.sharedBgraRenderTargetHresult == S_OK,
                "known shared formats open on game-style device without optional BGRA flag");
        }
    }
    return failures ? 1 : 0;
}
