#include "LegacyVisibilityProbe.h"
#include <fstream>
#include <iostream>
#include <string>

using Microsoft::WRL::ComPtr;
using rwui::probe::LegacyVisibilityProbe;
int failures{};
void Check(bool value, const char* label) {
    std::cout << (value ? "PASS: " : "FAIL: ") << label << '\n';
    if (!value) ++failures;
}
std::string Read(const std::filesystem::path& file) {
    std::ifstream in(file); return {std::istreambuf_iterator<char>(in), {}};
}
UINT64 LastCounter(const std::string& report, const char* name) {
    const std::string field = std::string(name) + '=';
    const auto pos = report.rfind(field);
    return pos == std::string::npos ? UINT64_MAX : std::stoull(report.substr(pos + field.size()));
}
int main() {
    wchar_t temp[32768]{}; GetTempPathW(32768, temp);
    const auto root = std::filesystem::path(temp) / (L"ReactorV-Visibility-Test-" +
        std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    if (!CreateDirectoryW(root.c_str(), nullptr)) return 1;
    struct Cleanup { std::filesystem::path root; ~Cleanup() {
        // Exclusively test-created path, never a source or game directory.
        std::error_code error; std::filesystem::remove_all(root, error);
    } } cleanup{root};
    const HWND window = CreateWindowExW(0, L"STATIC", L"ReactorV diagnostic test", WS_OVERLAPPEDWINDOW,
        0, 0, 640, 360, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!window) return 1;
    struct WindowCleanup { HWND window; ~WindowCleanup() { DestroyWindow(window); } } windowCleanup{window};
    for (auto format : {DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM}) {
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; ComPtr<IDXGISwapChain> chain;
        DXGI_SWAP_CHAIN_DESC desc{};
        desc.BufferDesc.Width = 640; desc.BufferDesc.Height = 360; desc.BufferDesc.Format = format;
        desc.SampleDesc.Count = 1; desc.BufferCount = 1; desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.OutputWindow = window; desc.Windowed = TRUE; desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
        const auto hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &desc, &chain, &device, nullptr, &context);
        if (FAILED(hr)) return 125;
        ComPtr<ID3D11Texture2D> buffer;
        Check(chain->GetBuffer(0, IID_PPV_ARGS(&buffer)) == S_OK, "actual swap-chain buffer acquired");
        LegacyVisibilityProbe disabled;
        Check(!disabled.Prepare(chain.Get(), buffer.Get(), device.Get(), context.Get()) && !disabled.Draw(chain.Get()),
            "not configured means no preparation or draw");
        const auto log = root / (std::to_wstring(format) + L".log");
        LegacyVisibilityProbe probe;
        probe.SetTestMode(200); probe.Configure(log);
        Check(probe.Prepare(chain.Get(), buffer.Get(), device.Get(), context.Get()), "independent ClearView probe prepared");
        Check(!probe.Draw(nullptr), "wrong intercepted chain rejected");
        ComPtr<ID3D11RenderTargetView> hostTarget;
        device->CreateRenderTargetView(buffer.Get(), nullptr, &hostTarget);
        ID3D11RenderTargetView* host = hostTarget.Get(); context->OMSetRenderTargets(1, &host, nullptr);
        const D3D11_VIEWPORT viewport{10, 20, 100, 120, 0, 1}; context->RSSetViewports(1, &viewport);
        // A tiny host viewport cannot clip the diagnostic rectangle.
        UINT painted{};
        const auto deadline = GetTickCount64() + 500;
        while (GetTickCount64() < deadline) {
            if (probe.Draw(chain.Get())) { ++painted; probe.Presented(S_OK); }
            context->Flush(); Sleep(2); // Harness-only; probe must never flush/wait for readback.
        }
        Check(painted > 0 && !probe.Draw(chain.Get()), "automatic foreground duration is bounded");
        ComPtr<ID3D11RenderTargetView> restored; context->OMGetRenderTargets(1, &restored, nullptr);
        D3D11_VIEWPORT actual{}; UINT count = 1; context->RSGetViewports(&count, &actual);
        Check(restored.Get() == hostTarget.Get() && actual.TopLeftX == 10 && actual.Width == 100,
            "diagnostic leaves host RTV and viewport untouched");
        restored.Reset();
        probe.Rearm(); Check(probe.Draw(chain.Get()), "diagnostic can be rearmed after startup");
        probe.Presented(DXGI_STATUS_OCCLUDED); probe.Presented(DXGI_ERROR_DEVICE_REMOVED);
        context->Flush();
        const auto readDeadline = GetTickCount64() + 150;
        while (GetTickCount64() < readDeadline) { probe.Draw(chain.Get()); context->Flush(); Sleep(2); }
        probe.Invalidate();
        Check(!probe.Draw(chain.Get()), "resize invalidation prevents stale buffer drawing");
        context->OMSetRenderTargets(0, nullptr, nullptr); hostTarget.Reset(); buffer.Reset(); context->Flush();
        Check(chain->ResizeBuffers(1, 800, 450, format, 0) == S_OK, "all probe backbuffer references released for resize");
        chain->GetBuffer(0, IID_PPV_ARGS(&buffer));
        Check(probe.Prepare(chain.Get(), buffer.Get(), device.Get(), context.Get()) && probe.Draw(chain.Get()),
            "resized actual buffer can be prepared and drawn");
        probe.Stop();
        Check(!probe.Draw(chain.Get()), "stop removes diagnostic authority immediately");
        const auto report = Read(log);
        Check(LastCounter(report, "sample_checks") > 0 && LastCounter(report, "sample_checks") != UINT64_MAX &&
            LastCounter(report, "pixel_matches") == LastCounter(report, "sample_checks"), "backbuffer pixels verified asynchronously");
        Check(LastCounter(report, "pixel_mismatches") == 0 && LastCounter(report, "sample_timeouts") == 0,
            "RGB channel mapping and white border agree with the buffer format");
        Check(LastCounter(report, "texture_checks") > 0 && LastCounter(report, "texture_checks") != UINT64_MAX &&
            LastCounter(report, "texture_matches") == LastCounter(report, "texture_checks") &&
            LastCounter(report, "texture_mismatches") == 0 && LastCounter(report, "texture_draw_failures") == 0,
            "production shader renderer reproduces local RGB/checkerboard/premultiplied-alpha pixels");
        const std::string expectedPattern = format == DXGI_FORMAT_B8G8R8A8_UNORM
            ? "texture_pixels=ffff0000,ff00ff00,ff0000ff,ffffffff,ff000000,ff800000,ff008000,ff808080,ff000000"
            : "texture_pixels=ff0000ff,ff00ff00,ffff0000,ffffffff,ff000000,ff000080,ff008000,ff808080,ff000000";
        Check(report.find(expectedPattern) != std::string::npos,
            "known texture pixel bytes independently verify channels, checker cells and alpha over black");
        Check(report.find("present_occluded=1 present_other=1") != std::string::npos,
            "occluded and failed Presents cannot masquerade as success");
        Check(report.find("onscreen_visibility=USER_VERIFICATION_REQUIRED menu_ready=UNMODIFIED") != std::string::npos,
            "readback does not claim screen visibility or browser readiness");
        LegacyVisibilityProbe hidden;
        hidden.Configure(root / (std::to_wstring(format) + L"-hidden.log"));
        Check(hidden.Prepare(chain.Get(), buffer.Get(), device.Get(), context.Get()) && !hidden.Draw(chain.Get()),
            "real probe does not consume the display interval on a hidden/background window");
        hidden.Stop();
        LegacyVisibilityProbe mismatch;
        mismatch.SetTestMode(); mismatch.Configure(root / (std::to_wstring(format) + L"-mismatch.log"));
        D3D11_TEXTURE2D_DESC otherDesc{}; buffer->GetDesc(&otherDesc);
        ComPtr<ID3D11Texture2D> other; device->CreateTexture2D(&otherDesc, nullptr, &other);
        Check(other && mismatch.Prepare(chain.Get(), other.Get(), device.Get(), context.Get()) && !mismatch.Draw(chain.Get()),
            "foreign buffer identity cannot pass the actual Present buffer check");
        mismatch.Stop();
        Check(LastCounter(Read(root / (std::to_wstring(format) + L"-mismatch.log")), "buffer_mismatch") == 1,
            "wrong-buffer rejection is recorded explicitly");
        LegacyVisibilityProbe unsupported;
        unsupported.SetTestMode(); unsupported.Configure(root / (std::to_wstring(format) + L"-unsupported.log"));
        other.Reset(); otherDesc.Width = 32;
        device->CreateTexture2D(&otherDesc, nullptr, &other);
        Check(other && !unsupported.Prepare(chain.Get(), other.Get(), device.Get(), context.Get()) && !unsupported.Draw(chain.Get()),
            "unsupported target description fails closed without changing the game output");
        unsupported.Stop();
        LegacyVisibilityProbe corrupt;
        const auto corruptLog = root / (std::to_wstring(format) + L"-corrupt.log");
        corrupt.SetTestMode(100, true); corrupt.Configure(corruptLog);
        Check(corrupt.Prepare(chain.Get(), buffer.Get(), device.Get(), context.Get()), "corrupt-image negative control prepared");
        const auto corruptDeadline = GetTickCount64() + 300;
        while (GetTickCount64() < corruptDeadline) {
            if (corrupt.Draw(chain.Get())) corrupt.Presented(S_OK);
            context->Flush(); Sleep(2);
        }
        corrupt.Stop();
        const auto corruptReport = Read(corruptLog);
        Check(LastCounter(corruptReport, "pixel_matches") > 0 && LastCounter(corruptReport, "pixel_mismatches") == 0 &&
            LastCounter(corruptReport, "texture_checks") > 0 && LastCounter(corruptReport, "texture_checks") != UINT64_MAX &&
            LastCounter(corruptReport, "texture_matches") == 0 &&
            LastCounter(corruptReport, "texture_mismatches") == LastCounter(corruptReport, "texture_checks"),
            "successful control draws and Presents cannot hide incorrect texture pixels");
        buffer.Reset(); context->ClearState(); context->Flush();
    }
    return failures ? 1 : 0;
}
