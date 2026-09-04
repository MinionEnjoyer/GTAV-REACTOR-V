#include "RageWebUI.Native.h"

#include <cstdint>
#include <d3d11.h>
#include <dxgi.h>
#include <iostream>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>
#include <windows.h>
#include <wrl/client.h>

namespace {

using Microsoft::WRL::ComPtr;
using ArmFunction = std::int32_t(RWUI_CALL*)();
using BindFunction = std::int32_t(RWUI_CALL*)(void*);
using DiagnosticsFunction = std::int32_t(RWUI_CALL*)(
    RwuiLegacyHookDiagnostics*);
using ShutdownFunction = void(RWUI_CALL*)();

int failures{};

void Check(const bool condition, const char* const message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        ++failures;
    }
}

template<typename T>
T Export(const HMODULE module, const char* const name) {
    return reinterpret_cast<T>(GetProcAddress(module, name));
}

LRESULT CALLBACK WindowProcedure(
    const HWND window,
    const UINT message,
    const WPARAM wParam,
    const LPARAM lParam) {
    return DefWindowProcW(window, message, wParam, lParam);
}

HWND CreateLegacyWindow() {
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"grcWindow";
    RegisterClassW(&windowClass);
    return CreateWindowExW(
        0,
        windowClass.lpszClassName,
        L"Grand Theft Auto V",
        WS_OVERLAPPEDWINDOW,
        0,
        0,
        640,
        360,
        nullptr,
        nullptr,
        windowClass.hInstance,
        nullptr);
}

bool CreateLegacySwapChain(
    const HWND window,
    ID3D11Device** const deviceResult,
    ID3D11DeviceContext** const contextResult,
    IDXGISwapChain** const swapChainResult) {
    DXGI_SWAP_CHAIN_DESC description{};
    description.BufferDesc.Width = 640;
    description.BufferDesc.Height = 360;
    description.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = 2;
    description.OutputWindow = window;
    description.Windowed = TRUE;
    description.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    D3D_FEATURE_LEVEL featureLevel{};
    auto result = D3D11CreateDeviceAndSwapChain(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &description,
        swapChainResult,
        deviceResult,
        &featureLevel,
        contextResult);
    if (FAILED(result)) {
        result = D3D11CreateDeviceAndSwapChain(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &description,
            swapChainResult,
            deviceResult,
            &featureLevel,
            contextResult);
    }
    return SUCCEEDED(result);
}

} // namespace

int wmain(const int argc, wchar_t** argv) {
    const bool textureProbe = argc == 3 && std::wstring_view(argv[2]) == L"--texture-probe";
    if (argc != 2 && !textureProbe) {
        std::cerr << "usage: LegacyHookIntegrationTests <native-dll>\n";
        return 2;
    }
    const HMODULE module = LoadLibraryExW(
        argv[1], nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr) {
        std::cerr << "SKIP: native DLL could not be loaded\n";
        return 125;
    }
    const auto arm = Export<ArmFunction>(module, "RWUI_ArmLegacyHook");
    const auto bind = Export<BindFunction>(module, "RWUI_BindLegacyTarget");
    const auto diagnostics = Export<DiagnosticsFunction>(
        module, "RWUI_GetLegacyHookDiagnostics");
    const auto shutdown = Export<ShutdownFunction>(module, "RWUI_Shutdown");
    if (arm == nullptr || bind == nullptr || diagnostics == nullptr ||
        shutdown == nullptr) {
        std::cerr << "FAIL: Legacy compositor-only ABI is complete\n";
        FreeLibrary(module);
        return 1;
    }
    std::filesystem::path probeLog;
    if (textureProbe) {
        using Configure = void(RWUI_CALL*)(const wchar_t*, const wchar_t*);
        const auto configure = Export<Configure>(module, "RWUI_ConfigureLegacyTextureProbe");
        if (!configure) { FreeLibrary(module); return 1; }
        wchar_t temporary[MAX_PATH]{}; GetTempPathW(MAX_PATH, temporary);
        probeLog = std::filesystem::path(temporary) / (L"ReactorV-HookProbe-" +
            std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()) + L".log");
        const auto helper = std::filesystem::absolute(argv[1]).parent_path() / L"ReactorV.TextureProbe.Partner.exe";
        configure(helper.c_str(), probeLog.c_str());
    }

    // Create the swap chain before arming. ScriptHook commonly loads an ASI
    // after GTA's D3D11 device exists, so the production route must discover
    // the already-live chain from Present rather than depend on factory hooks.
    const HWND window = CreateLegacyWindow();
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGISwapChain> swapChain;
    if (window == nullptr || !CreateLegacySwapChain(
            window, &device, &context, &swapChain)) {
        std::cout << "SKIP: no usable D3D11 Legacy swap chain\n";
        shutdown();
        if (window != nullptr) DestroyWindow(window);
        FreeLibrary(module);
        return 125;
    }

    Check(arm() == 1, "Legacy DXGI hooks arm after swap-chain creation");
    Check(bind(window) == static_cast<std::int32_t>(
            RwuiLegacyTargetBindStatus::PendingCapture),
        "Legacy late binding waits for the first matching Present");
    RwuiLegacyHookDiagnostics state{};
    bool bound{};
    const auto deadline = GetTickCount64() + 3000;
    while (GetTickCount64() < deadline) {
        swapChain->Present(0, 0);
        const auto bindResult = bind(window);
        state = {};
        state.byteSize = sizeof(state);
        if (diagnostics(&state) == 1 &&
            bindResult == static_cast<std::int32_t>(
                RwuiLegacyTargetBindStatus::Bound) &&
            (state.flags & RWUI_LEGACY_DIAGNOSTIC_TARGET_BOUND) != 0) {
            bound = true;
            break;
        }
        Sleep(2);
    }
    Check(bound,
        "Legacy grcWindow late-binds to its live D3D11 Present route");
    using SubmitStatus = std::int32_t(RWUI_CALL*)(const void*, std::int32_t, std::int32_t, std::int32_t, std::uint64_t);
    const auto submitStatus = Export<SubmitStatus>(module, "RWUI_SubmitStartupStatusFrame");
    Check(submitStatus != nullptr, "Legacy exposes passive startup status ABI");
    if (submitStatus && !textureProbe) {
        std::vector<std::uint8_t> statusPixels(560 * 68 * 4, 0xff);
        Check(submitStatus(statusPixels.data(), 560, 68, 2240, 1) == 1,
            "passive HUD stages on the already prepared live device");
        Check(submitStatus(statusPixels.data(), 561, 68, 2240, 2) == 0,
            "passive HUD rejects invalid dimensions");
        Check(SUCCEEDED(swapChain->Present(0, 0)), "HUD does not obstruct Present");
        Check(submitStatus(nullptr, 0, 0, 0, 0) == 1, "bootstrap handoff can hide HUD without a menu event");
    }
    if (textureProbe) {
        const auto probeDeadline = GetTickCount64() + 18000;
        const auto resizeAt = GetTickCount64() + 1500;
        bool resized{}, complete{};
        while (GetTickCount64() < probeDeadline) {
            Check(SUCCEEDED(swapChain->Present(0, 0)), "probe leaves host Present operational");
            if (!resized && GetTickCount64() >= resizeAt) {
                Check(SUCCEEDED(swapChain->ResizeBuffers(2, 640, 360, DXGI_FORMAT_R8G8B8A8_UNORM, 0)),
                    "opt-in probe releases its extra RTVs during resize");
                bind(window); resized = true;
            }
            std::ifstream in(probeLog); std::string report{std::istreambuf_iterator<char>(in), {}};
            if (report.find("step=complete_not_menu_acceptance") != std::string::npos) {
                complete = true;
                for (const char* mode : {"local", "outward_nt", "outward_kmt"})
                    Check(report.find(std::string("mode=") + mode + " step=game_draw_submitted hr=0x0 result=PASS") != std::string::npos,
                        "hidden-menu Present draws the independent diagnostic texture");
                break;
            }
            Sleep(10);
        }
        Check(complete, "hook-driven probe completes all three paths");
        diagnostics(&state);
        Check(state.renderedFrames == 0 && state.lastFrameGeneration == 0 && state.presentationEpoch == 0,
            "probe draw submissions do not qualify a browser menu frame");
    }
    Check(diagnostics(&state) == 1,
        "Legacy D3D11 diagnostics are readable");
    Check((state.flags &
            (RWUI_LEGACY_DIAGNOSTIC_HOOKS_ARMED |
             RWUI_LEGACY_DIAGNOSTIC_TARGET_BOUND |
             RWUI_LEGACY_DIAGNOSTIC_D3D11_READY)) ==
            (RWUI_LEGACY_DIAGNOSTIC_HOOKS_ARMED |
             RWUI_LEGACY_DIAGNOSTIC_TARGET_BOUND |
             RWUI_LEGACY_DIAGNOSTIC_D3D11_READY),
        "Legacy diagnostics report an armed, bound D3D11 route");
    Check((state.flags &
            (RWUI_LEGACY_DIAGNOSTIC_LOCAL_OWNER |
             RWUI_LEGACY_DIAGNOSTIC_INPUT_ATTACHED)) == 0,
        "Legacy root hook remains compositor-only with input detached");
    Check(state.renderApi == RwuiRenderApi::Direct3D11 &&
            state.targetWindowProcessId == GetCurrentProcessId() &&
            state.targetWindowClass ==
                RwuiLegacyTargetWindowClass::GrcWindow,
        "Legacy diagnostics identify the D3D11 current-process grcWindow");
    const auto compositorGenerationBeforeResize = state.reserved[0];
    const auto backBufferGenerationBeforeResize = state.reserved[1];
    Check(compositorGenerationBeforeResize != 0 &&
            backBufferGenerationBeforeResize != 0,
        "Legacy diagnostics expose initialized compositor and backbuffer generations");

    Check(SUCCEEDED(swapChain->Present(0, 0)),
        "hidden Legacy compositor leaves game Present fail-open");
    Check(SUCCEEDED(swapChain->ResizeBuffers(
            2, 800, 450, DXGI_FORMAT_R8G8B8A8_UNORM, 0)),
        "Legacy compositor survives a fullscreen-style buffer resize");
    Check(bind(window) == static_cast<std::int32_t>(
            RwuiLegacyTargetBindStatus::Bound),
        "Legacy compositor rebinds after resize without RWUI_Initialize");
    state = {};
    state.byteSize = sizeof(state);
    Check(diagnostics(&state) == 1 &&
            state.renderApi == RwuiRenderApi::Direct3D11 &&
            (state.flags & RWUI_LEGACY_DIAGNOSTIC_TARGET_BOUND) != 0 &&
            state.reserved[0] == compositorGenerationBeforeResize &&
            state.reserved[1] > backBufferGenerationBeforeResize,
        "Legacy resize preserves the compositor while replacing its backbuffer");

    shutdown();
    swapChain.Reset();
    context.Reset();
    device.Reset();
    DestroyWindow(window);

    const HWND reboundWindow = CreateLegacyWindow();
    ComPtr<ID3D11Device> reboundDevice;
    ComPtr<ID3D11DeviceContext> reboundContext;
    ComPtr<IDXGISwapChain> reboundSwapChain;
    const bool reboundSurfaceReady = reboundWindow != nullptr &&
        CreateLegacySwapChain(
            reboundWindow,
            &reboundDevice,
            &reboundContext,
            &reboundSwapChain);
    Check(reboundSurfaceReady,
        "Legacy teardown fixture creates a replacement D3D11 target");
    if (reboundSurfaceReady) {
        Check(arm() == 1,
            "Legacy hooks re-arm after complete compositor teardown");
        Check(bind(reboundWindow) == static_cast<std::int32_t>(
                RwuiLegacyTargetBindStatus::PendingCapture),
            "replacement Legacy target waits for its first captured Present");
        bool rebound{};
        const auto reboundDeadline = GetTickCount64() + 3000;
        while (GetTickCount64() < reboundDeadline) {
            reboundSwapChain->Present(0, 0);
            const auto bindResult = bind(reboundWindow);
            state = {};
            state.byteSize = sizeof(state);
            if (diagnostics(&state) == 1 &&
                bindResult == static_cast<std::int32_t>(
                    RwuiLegacyTargetBindStatus::Bound) &&
                (state.flags & RWUI_LEGACY_DIAGNOSTIC_TARGET_BOUND) != 0) {
                rebound = true;
                break;
            }
            Sleep(2);
        }
        Check(rebound &&
                state.renderApi == RwuiRenderApi::Direct3D11 &&
                state.reserved[0] > compositorGenerationBeforeResize &&
                state.reserved[1] > backBufferGenerationBeforeResize,
            "Legacy teardown/rebind creates one fresh compositor and backbuffer generation");
        shutdown();
    }
    reboundSwapChain.Reset();
    reboundContext.Reset();
    reboundDevice.Reset();
    if (reboundWindow != nullptr) DestroyWindow(reboundWindow);
    FreeLibrary(module);
    if (!probeLog.empty()) {
        const auto visibilityLog = probeLog.parent_path() / (probeLog.stem().wstring() + L".visibility.log");
        std::ifstream in(visibilityLog); const std::string report{std::istreambuf_iterator<char>(in), {}};
        Check(report.find("probe=visibility_v3") != std::string::npos,
            "real hook prepares the separate visibility diagnostic");
        Check(report.find("onscreen_visibility=USER_VERIFICATION_REQUIRED") != std::string::npos,
            "hidden hook harness cannot claim actual screen visibility");
        in.close();
        std::error_code error; std::filesystem::remove(probeLog, error);
        std::filesystem::remove(visibilityLog, error);
    }
    if (failures == 0) {
        std::cout << "PASS: Legacy D3D11 compositor-only hook integration\n";
    }
    return failures == 0 ? 0 : 1;
}
