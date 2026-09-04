#include "RageWebUI.Native.h"

#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <iomanip>
#include <iostream>
#include <string>
#include <thread>
#include <vector>
#include <windows.h>
#include <wrl/client.h>

namespace {

using Microsoft::WRL::ComPtr;

bool EnvironmentFlagEnabled(const wchar_t* const name) {
    wchar_t value[8]{};
    return GetEnvironmentVariableW(
               name, value,
               static_cast<DWORD>(std::size(value))) != 0 &&
        value[0] != L'0';
}

bool D3D12DiagnosticsEnabled() {
    return EnvironmentFlagEnabled(L"REACTORV_D3D12_DEBUG");
}

void EnableD3D12Diagnostics() {
    if (!D3D12DiagnosticsEnabled()) return;
    ComPtr<ID3D12Debug> debug;
    if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug)))) {
        debug->EnableDebugLayer();
        ComPtr<ID3D12Debug1> debug1;
        if (SUCCEEDED(debug.As(&debug1))) {
            debug1->SetEnableGPUBasedValidation(TRUE);
            debug1->SetEnableSynchronizedCommandQueueValidation(TRUE);
        }
    }
    ComPtr<ID3D12DeviceRemovedExtendedDataSettings> dred;
    if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&dred)))) {
        dred->SetAutoBreadcrumbsEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
        dred->SetPageFaultEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
        dred->SetWatsonDumpEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
    }
    std::cerr << "DIAG: D3D12 debug layer, GPU validation, and DRED enabled\n";
}

void DumpD3D12Diagnostics(ID3D12Device* const device) {
    if (!D3D12DiagnosticsEnabled() || device == nullptr) return;
    ComPtr<ID3D12InfoQueue> infoQueue;
    if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&infoQueue)))) {
        const auto count = infoQueue->GetNumStoredMessagesAllowedByRetrievalFilter();
        const auto first = count > 32 ? count - 32 : 0;
        for (UINT64 index = first; index < count; ++index) {
            SIZE_T bytes{};
            if (FAILED(infoQueue->GetMessage(index, nullptr, &bytes)) ||
                bytes == 0) continue;
            std::vector<std::uint8_t> storage(bytes);
            auto* const message = reinterpret_cast<D3D12_MESSAGE*>(storage.data());
            if (SUCCEEDED(infoQueue->GetMessage(index, message, &bytes))) {
                std::cerr << "D3D12: severity=" << message->Severity
                          << " id=" << message->ID << " "
                          << (message->pDescription == nullptr
                                  ? "<no description>"
                                  : message->pDescription)
                          << '\n';
            }
        }
    }
    ComPtr<ID3D12DeviceRemovedExtendedData> dred;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dred)))) return;
    D3D12_DRED_AUTO_BREADCRUMBS_OUTPUT breadcrumbs{};
    if (SUCCEEDED(dred->GetAutoBreadcrumbsOutput(&breadcrumbs))) {
        for (auto* node = breadcrumbs.pHeadAutoBreadcrumbNode;
             node != nullptr; node = node->pNext) {
            std::cerr << "DRED breadcrumb: queue="
                      << (node->pCommandQueueDebugNameA == nullptr
                              ? "<unnamed>"
                              : node->pCommandQueueDebugNameA)
                      << " list="
                      << (node->pCommandListDebugNameA == nullptr
                              ? "<unnamed>"
                              : node->pCommandListDebugNameA)
                      << " completed="
                      << (node->pLastBreadcrumbValue == nullptr
                              ? 0
                              : *node->pLastBreadcrumbValue)
                      << '/' << node->BreadcrumbCount << '\n';
        }
    }
    D3D12_DRED_PAGE_FAULT_OUTPUT pageFault{};
    if (SUCCEEDED(dred->GetPageFaultAllocationOutput(&pageFault))) {
        std::cerr << "DRED page fault VA=0x" << std::hex
                  << pageFault.PageFaultVA << std::dec << '\n';
    }
}

using ArmEnhancedHookFunction = std::int32_t(RWUI_CALL*)();
using ArmLegacyHookFunction = std::int32_t(RWUI_CALL*)();
using BindEnhancedTargetFunction = std::int32_t(RWUI_CALL*)(void*);
using BindLegacyTargetFunction = std::int32_t(RWUI_CALL*)(void*);
using GetEnhancedDiagnosticsFunction = std::int32_t(RWUI_CALL*)(
    RwuiEnhancedHookDiagnostics*);
using GetLegacyDiagnosticsFunction = std::int32_t(RWUI_CALL*)(
    RwuiLegacyHookDiagnostics*);
using InitializeFunction = std::int32_t(RWUI_CALL*)(void*);
using ShutdownFunction = void(RWUI_CALL*)();
using SetVisibleFunction = void(RWUI_CALL*)(std::int32_t);
using SubmitFrameFunction = std::int32_t(RWUI_CALL*)(
    const void*, std::int32_t, std::int32_t, std::int32_t, std::uint64_t);
using GetStatsFunction = std::int32_t(RWUI_CALL*)(RwuiRenderStats*);
using PollInputFunction = std::int32_t(RWUI_CALL*)(RwuiInputEvent*);
using TestStartFunction = std::int32_t(RWUI_CALL*)(
    RwuiRenderApi, std::int32_t, std::int32_t, const wchar_t*);
using TestStopFunction = void(RWUI_CALL*)();
using TestIsRunningFunction = std::int32_t(RWUI_CALL*)();
using QueryAdapterFunction = std::int32_t(RWUI_CALL*)(
    std::uint32_t, std::int32_t*, std::uint32_t*);

struct NativeApi final {
    HMODULE module{};
    ArmEnhancedHookFunction arm{};
    ArmLegacyHookFunction armLegacy{};
    BindEnhancedTargetFunction bindEnhanced{};
    BindLegacyTargetFunction bindLegacy{};
    GetEnhancedDiagnosticsFunction getEnhancedDiagnostics{};
    GetLegacyDiagnosticsFunction getLegacyDiagnostics{};
    InitializeFunction initialize{};
    ShutdownFunction shutdown{};
    SetVisibleFunction setVisible{};
    SubmitFrameFunction submitFrame{};
    GetStatsFunction getStats{};
    PollInputFunction pollInput{};
    TestStartFunction testStart{};
    TestStopFunction testStop{};
    TestIsRunningFunction testIsRunning{};
};

class GpuProducerFixture final {
public:
    ~GpuProducerFixture() {
        if (process_ != nullptr &&
            WaitForSingleObject(process_, 0) == WAIT_TIMEOUT) {
            if (done_ != nullptr) SetEvent(done_);
            if (WaitForSingleObject(process_, 1000) != WAIT_OBJECT_0) {
                TerminateProcess(process_, 90);
            }
        }
        if (thread_ != nullptr) CloseHandle(thread_);
        if (process_ != nullptr) CloseHandle(process_);
        if (ready_ != nullptr) CloseHandle(ready_);
        if (acknowledged_ != nullptr) CloseHandle(acknowledged_);
        if (done_ != nullptr) CloseHandle(done_);
    }

    bool Start(
        const wchar_t* helperPath,
        const LUID adapter,
        const std::uint64_t generation) {
        const auto processId = GetCurrentProcessId();
        const auto prefix = L"Local\\ReactorV.SharedGpuFrame.RuntimeTest." +
            std::to_wstring(processId) + L".";
        ready_ = CreateEventW(
            nullptr, TRUE, FALSE, (prefix + L"Ready").c_str());
        acknowledged_ = CreateEventW(
            nullptr, TRUE, FALSE, (prefix + L"Acknowledged").c_str());
        done_ = CreateEventW(
            nullptr, TRUE, FALSE, (prefix + L"Done").c_str());
        if (ready_ == nullptr || acknowledged_ == nullptr ||
            done_ == nullptr) return false;

        std::wstring command = L"\"" + std::wstring(helperPath) +
            L"\" --producer " + std::to_wstring(processId) + L" " +
            std::to_wstring(generation) + L" " +
            std::to_wstring(adapter.HighPart) + L" " +
            std::to_wstring(adapter.LowPart);
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION child{};
        if (CreateProcessW(
                helperPath, command.data(), nullptr, nullptr, FALSE,
                CREATE_NO_WINDOW, nullptr, nullptr, &startup, &child) ==
            FALSE) return false;
        process_ = child.hProcess;
        thread_ = child.hThread;
        return WaitForSingleObject(ready_, 5000) == WAIT_OBJECT_0;
    }

    DWORD Stop() {
        if (done_ != nullptr) SetEvent(done_);
        if (process_ == nullptr ||
            WaitForSingleObject(process_, 5000) != WAIT_OBJECT_0) {
            return STILL_ACTIVE;
        }
        DWORD exitCode{};
        return GetExitCodeProcess(process_, &exitCode) ? exitCode : 91;
    }

    bool Acknowledged() const {
        return acknowledged_ != nullptr &&
            WaitForSingleObject(acknowledged_, 2000) == WAIT_OBJECT_0;
    }

private:
    HANDLE process_{};
    HANDLE thread_{};
    HANDLE ready_{};
    HANDLE acknowledged_{};
    HANDLE done_{};
};

int failures = 0;
constexpr UINT BlockingWindowMessage = WM_APP + 73;
constexpr UINT ChainedWindowMessage = WM_APP + 74;
HANDLE blockingWindowEntered{};
HANDLE blockingWindowRelease{};
std::atomic_bool blockingWindowCompleted{};
std::atomic_uint32_t laterSubclassCalls{};
std::atomic_uint32_t originalWindowCalls{};
WNDPROC laterPreviousProcedure{};
bool legacyExternalHardwareQualified{};

void Check(const bool condition, const char* message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        ++failures;
    }
}

bool SameComIdentity(IUnknown* const left, IUnknown* const right) {
    if (left == nullptr || right == nullptr) return false;
    ComPtr<IUnknown> leftIdentity;
    ComPtr<IUnknown> rightIdentity;
    return SUCCEEDED(left->QueryInterface(IID_PPV_ARGS(&leftIdentity))) &&
        SUCCEEDED(right->QueryInterface(IID_PPV_ARGS(&rightIdentity))) &&
        leftIdentity.Get() == rightIdentity.Get();
}

void CheckSucceeded(
    const HRESULT result,
    const char* message,
    ID3D12Device* const device = nullptr) {
    if (FAILED(result)) {
        std::cerr << "FAIL: " << message << " (HRESULT=0x"
                  << std::hex << std::uppercase
                  << static_cast<std::uint32_t>(result);
        if (device != nullptr) {
            std::cerr << ", deviceRemovedReason=0x"
                      << static_cast<std::uint32_t>(
                          device->GetDeviceRemovedReason());
        }
        std::cerr << std::nouppercase << std::dec << ")\n";
        DumpD3D12Diagnostics(device);
        ++failures;
    }
}

template<typename T>
T Export(const HMODULE module, const char* name) {
    return reinterpret_cast<T>(GetProcAddress(module, name));
}

bool LoadNative(const wchar_t* path, NativeApi& api) {
    api.module = LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (api.module == nullptr) return false;
    api.arm = Export<ArmEnhancedHookFunction>(api.module, "RWUI_ArmEnhancedHook");
    api.armLegacy = Export<ArmLegacyHookFunction>(
        api.module, "RWUI_ArmLegacyHook");
    api.bindEnhanced = Export<BindEnhancedTargetFunction>(
        api.module, "RWUI_BindEnhancedTarget");
    api.bindLegacy = Export<BindLegacyTargetFunction>(
        api.module, "RWUI_BindLegacyTarget");
    api.getEnhancedDiagnostics = Export<GetEnhancedDiagnosticsFunction>(
        api.module, "RWUI_GetEnhancedHookDiagnostics");
    api.getLegacyDiagnostics = Export<GetLegacyDiagnosticsFunction>(
        api.module, "RWUI_GetLegacyHookDiagnostics");
    api.initialize = Export<InitializeFunction>(api.module, "RWUI_Initialize");
    api.shutdown = Export<ShutdownFunction>(api.module, "RWUI_Shutdown");
    api.setVisible = Export<SetVisibleFunction>(api.module, "RWUI_SetVisible");
    api.submitFrame = Export<SubmitFrameFunction>(api.module, "RWUI_SubmitFrame");
    api.getStats = Export<GetStatsFunction>(api.module, "RWUI_GetStats");
    api.pollInput = Export<PollInputFunction>(api.module, "RWUI_PollInput");
    api.testStart = Export<TestStartFunction>(api.module, "RWUI_TestStart");
    api.testStop = Export<TestStopFunction>(api.module, "RWUI_TestStop");
    api.testIsRunning = Export<TestIsRunningFunction>(
        api.module, "RWUI_TestIsRunning");
    return api.arm != nullptr && api.armLegacy != nullptr &&
        api.bindEnhanced != nullptr && api.bindLegacy != nullptr &&
        api.getEnhancedDiagnostics != nullptr &&
        api.getLegacyDiagnostics != nullptr && api.initialize != nullptr &&
        api.shutdown != nullptr && api.setVisible != nullptr &&
        api.submitFrame != nullptr && api.getStats != nullptr &&
        api.pollInput != nullptr && api.testStart != nullptr &&
        api.testStop != nullptr && api.testIsRunning != nullptr;
}

bool RunNativeReloadStress(const wchar_t* nativePath) {
    // The DXGI discovery probe must not leave any process-owned window class
    // pointing into an unloaded copy of RageWebUI.Native. Repeating this in one
    // process catches stale WNDPROC registrations on the very next load.
    constexpr std::size_t ReloadCycles = 12;
    for (std::size_t cycle = 0; cycle < ReloadCycles; ++cycle) {
        NativeApi api;
        if (!LoadNative(nativePath, api)) {
            if (api.module != nullptr) FreeLibrary(api.module);
            return false;
        }
        if (api.arm() != 1) {
            api.shutdown();
            FreeLibrary(api.module);
            return false;
        }
        api.shutdown();
        if (FreeLibrary(api.module) == FALSE) return false;
    }
    return true;
}

LRESULT CALLBACK WindowProcedure(
    const HWND window,
    const UINT message,
    const WPARAM wParam,
    const LPARAM lParam) {
    if (message == BlockingWindowMessage &&
        blockingWindowEntered != nullptr &&
        blockingWindowRelease != nullptr) {
        SetEvent(blockingWindowEntered);
        WaitForSingleObject(blockingWindowRelease, 5000);
        blockingWindowCompleted.store(true, std::memory_order_release);
        return 73;
    }
    if (message == ChainedWindowMessage) {
        originalWindowCalls.fetch_add(1, std::memory_order_relaxed);
        return 74;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

LRESULT CALLBACK LaterWindowProcedure(
    const HWND window,
    const UINT message,
    const WPARAM wParam,
    const LPARAM lParam) {
    if (message == ChainedWindowMessage) {
        laterSubclassCalls.fetch_add(1, std::memory_order_relaxed);
    }
    return laterPreviousProcedure != nullptr
        ? CallWindowProcW(
            laterPreviousProcedure, window, message, wParam, lParam)
        : DefWindowProcW(window, message, wParam, lParam);
}

HWND CreateTestWindow() {
    static constexpr wchar_t ClassName[] =
        L"ReactorV.EnhancedHook.Integration.Tests";
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = ClassName;
    RegisterClassW(&windowClass);
    return CreateWindowExW(
        0,
        ClassName,
        L"ReactorV Enhanced hook integration test",
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

HWND CreateStrictEnhancedWindow() {
    static constexpr wchar_t ClassName[] = L"sgaWindow";
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = ClassName;
    RegisterClassW(&windowClass);
    return CreateWindowExW(
        0, ClassName, L"Grand_Theft_Auto_V", WS_OVERLAPPEDWINDOW,
        0, 0, 640, 360, nullptr, nullptr, windowClass.hInstance, nullptr);
}

HWND CreateStrictLegacyWindow() {
    static constexpr wchar_t ClassName[] = L"grcWindow";
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = ClassName;
    RegisterClassW(&windowClass);
    return CreateWindowExW(
        0, ClassName, L"Grand Theft Auto V", WS_OVERLAPPEDWINDOW,
        0, 0, 640, 360, nullptr, nullptr, windowClass.hInstance, nullptr);
}

bool CreateDeviceAndQueue(
    IDXGIFactory6* factory,
    ID3D12Device** deviceResult,
    ID3D12CommandQueue** queueResult,
    bool& hardwareDevice) {
    hardwareDevice = false;
    ComPtr<IDXGIAdapter1> adapter;
    if (!EnvironmentFlagEnabled(L"REACTORV_FORCE_WARP")) {
        for (UINT index = 0;
             factory->EnumAdapterByGpuPreference(
                 index,
                 DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                 IID_PPV_ARGS(&adapter)) != DXGI_ERROR_NOT_FOUND;
             ++index) {
            DXGI_ADAPTER_DESC1 description{};
            adapter->GetDesc1(&description);
            if ((description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0 &&
                SUCCEEDED(D3D12CreateDevice(
                    adapter.Get(),
                    D3D_FEATURE_LEVEL_11_0,
                    IID_PPV_ARGS(deviceResult)))) {
                hardwareDevice = true;
                break;
            }
            adapter.Reset();
        }
    }

    if (*deviceResult == nullptr) {
        ComPtr<IDXGIAdapter> warp;
        if (FAILED(factory->EnumWarpAdapter(IID_PPV_ARGS(&warp))) ||
            FAILED(D3D12CreateDevice(
                warp.Get(),
                D3D_FEATURE_LEVEL_11_0,
                IID_PPV_ARGS(deviceResult)))) {
            return false;
        }
    }

    D3D12_COMMAND_QUEUE_DESC queueDescription{};
    queueDescription.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    return SUCCEEDED((*deviceResult)->CreateCommandQueue(
        &queueDescription,
        IID_PPV_ARGS(queueResult)));
}

bool CreateSwapChain(
    IDXGIFactory6* factory,
    ID3D12CommandQueue* queue,
    const HWND window,
    const UINT width,
    const UINT height,
    IDXGISwapChain3** result) {
    DXGI_SWAP_CHAIN_DESC1 description{};
    description.Width = width;
    description.Height = height;
    description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = 2;
    description.SampleDesc.Count = 1;
    description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;

    ComPtr<IDXGISwapChain1> swapChain;
    return SUCCEEDED(factory->CreateSwapChainForHwnd(
        queue,
        window,
        &description,
        nullptr,
        nullptr,
        &swapChain)) &&
        SUCCEEDED(swapChain->QueryInterface(IID_PPV_ARGS(result)));
}

std::array<std::uint8_t, 16> Frame(const std::uint8_t blue) {
    return {
        blue, 0x30, 0xe0, 0xff,
        blue, 0x30, 0xe0, 0xff,
        blue, 0x30, 0xe0, 0xff,
        blue, 0x30, 0xe0, 0xff,
    };
}

bool SubmitAndRead(
    const NativeApi& api,
    const std::uint64_t generation,
    RwuiRenderStats& stats) {
    const auto frame = Frame(static_cast<std::uint8_t>(generation));
    return api.submitFrame(
               frame.data(), 2, 2, 8, generation) == 1 &&
        api.getStats(&stats) == 1;
}

void DrainInput(const NativeApi& api) {
    RwuiInputEvent input{};
    while (api.pollInput(&input) == 1) {
    }
}

bool MouseMoveCaptured(
    const NativeApi& api,
    const HWND window,
    const std::int32_t coordinate) {
    DrainInput(api);
    SendMessageW(
        window,
        WM_MOUSEMOVE,
        0,
        MAKELPARAM(coordinate, coordinate));
    RwuiInputEvent input{};
    while (api.pollInput(&input) == 1) {
        if (input.type == RwuiInputType::MouseMove &&
            input.x == coordinate && input.y == coordinate) return true;
    }
    return false;
}

void CheckRendered(
    const RwuiRenderStats& stats,
    const std::int32_t width,
    const std::int32_t height,
    const std::uint64_t generation,
    const char* phase) {
    if (stats.renderedFrames == 0 ||
        stats.lastFrameGeneration != generation) {
        std::cerr << "DIAG: " << phase << " rendered=" <<
            stats.renderedFrames << " generation=" <<
            stats.lastFrameGeneration << " expected=" << generation << '\n';
    }
    Check(stats.api == RwuiRenderApi::Direct3D12, phase);
    Check(stats.width == width && stats.height == height,
        "hooked compositor tracks the resized D3D12 back buffer");
    Check(stats.renderedFrames > 0,
        "a hooked Present entry point rendered the submitted frame");
    Check(stats.lastFrameGeneration == generation,
        "hooked compositor consumed the newest frame generation");
}

bool RunStrictExternalCompositor(
    const wchar_t* nativePath,
    const wchar_t* helperPath) {
    NativeApi api;
    if (!LoadNative(nativePath, api)) {
        Check(false, "strict external fixture loads the native API");
        if (api.module != nullptr) FreeLibrary(api.module);
        return false;
    }
    Check(api.arm() == 1, "strict external fixture arms before swap creation");
    const HWND window = CreateStrictEnhancedWindow();
    ComPtr<IDXGIFactory6> factory;
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<IDXGISwapChain3> swapChain;
    bool hardwareDevice{};
    const bool ready = window != nullptr &&
        SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory))) &&
        CreateDeviceAndQueue(
            factory.Get(), &device, &queue, hardwareDevice) &&
        CreateSwapChain(
            factory.Get(), queue.Get(), window, 640, 360, &swapChain);
    if (!ready || !hardwareDevice) {
        std::cout <<
            "SKIP: strict external compositor needs a D3D12 hardware adapter\n";
        api.shutdown();
        if (window != nullptr) DestroyWindow(window);
        FreeLibrary(api.module);
        return true;
    }

    Check(api.bindEnhanced(window) == static_cast<std::int32_t>(
            RwuiEnhancedTargetBindStatus::Bound),
        "strict bind accepts only a captured D3D12 DIRECT route");
    RwuiEnhancedHookDiagnostics diagnostics{};
    diagnostics.byteSize = sizeof(diagnostics);
    Check(api.getEnhancedDiagnostics(&diagnostics) == 1 &&
            (diagnostics.flags &
                (RWUI_ENHANCED_DIAGNOSTIC_TARGET_BOUND |
                 RWUI_ENHANCED_DIAGNOSTIC_D3D12_READY |
                 RWUI_ENHANCED_DIAGNOSTIC_DIRECT_QUEUE)) ==
                (RWUI_ENHANCED_DIAGNOSTIC_TARGET_BOUND |
                 RWUI_ENHANCED_DIAGNOSTIC_D3D12_READY |
                 RWUI_ENHANCED_DIAGNOSTIC_DIRECT_QUEUE) &&
            (diagnostics.flags &
                (RWUI_ENHANCED_DIAGNOSTIC_LOCAL_OWNER |
                 RWUI_ENHANCED_DIAGNOSTIC_INPUT_ATTACHED)) == 0 &&
            diagnostics.targetWindowProcessId == GetCurrentProcessId() &&
            diagnostics.targetWindowClass ==
                RwuiEnhancedTargetWindowClass::SgaWindow &&
            diagnostics.queueBindingSource == 2,
        "strict diagnostics identify sgaWindow and factory DIRECT binding");

    constexpr std::uint64_t SharedGeneration = 701;
    GpuProducerFixture producer;
    Check(producer.Start(
            helperPath, device->GetAdapterLuid(), SharedGeneration),
        "strict external producer starts on the compositor adapter");
    RwuiRenderStats stats{};
    bool rendered{};
    const auto deadline = GetTickCount64() + 5000;
    while (GetTickCount64() < deadline) {
        swapChain->Present(0, 0);
        if (api.getStats(&stats) == 1 &&
            stats.lastFrameGeneration == SharedGeneration) {
            rendered = true;
            break;
        }
        Sleep(2);
    }
    Check(rendered,
        "authenticated producer visibility drives compositor-only rendering");
    Check(producer.Acknowledged(),
        "strict consumer acknowledges the external GPU frame");
    DrainInput(api);
    SendMessageW(window, WM_MOUSEMOVE, 0, MAKELPARAM(33, 33));
    RwuiInputEvent input{};
    Check(api.pollInput(&input) == 0,
        "compositor-only binding never subclasses or captures GTA input");

    Check(producer.Stop() == 0, "strict external producer exits cleanly");
    bool cleared{};
    const auto clearDeadline = GetTickCount64() + 2000;
    while (GetTickCount64() < clearDeadline) {
        diagnostics = {};
        diagnostics.byteSize = sizeof(diagnostics);
        if (api.getEnhancedDiagnostics(&diagnostics) == 1 &&
            (diagnostics.flags &
                RWUI_ENHANCED_DIAGNOSTIC_EXTERNAL_VISIBLE) == 0 &&
            diagnostics.presentationEpoch == 0) {
            cleared = true;
            break;
        }
        Sleep(2);
    }
    Check(cleared,
        "producer disconnect clears external visibility and its epoch");

    const HWND replacementWindow = CreateStrictEnhancedWindow();
    ComPtr<IDXGISwapChain3> replacementSwapChain;
    Check(replacementWindow != nullptr && CreateSwapChain(
            factory.Get(), queue.Get(), replacementWindow,
            640, 360, &replacementSwapChain) &&
            api.bindEnhanced(replacementWindow) ==
                static_cast<std::int32_t>(
                    RwuiEnhancedTargetBindStatus::Bound),
        "strict compositor rebinds after an sgaWindow recreation");
    diagnostics = {};
    diagnostics.byteSize = sizeof(diagnostics);
    Check(api.getEnhancedDiagnostics(&diagnostics) == 1 &&
            diagnostics.targetWindowProcessId == GetCurrentProcessId() &&
            diagnostics.targetWindowClass ==
                RwuiEnhancedTargetWindowClass::SgaWindow &&
            (diagnostics.flags &
                RWUI_ENHANCED_DIAGNOSTIC_INPUT_ATTACHED) == 0,
        "recreated target remains compositor-only and diagnosed");

    api.shutdown();
    replacementSwapChain.Reset();
    swapChain.Reset();
    queue.Reset();
    device.Reset();
    factory.Reset();
    if (replacementWindow != nullptr) DestroyWindow(replacementWindow);
    DestroyWindow(window);
    FreeLibrary(api.module);
    return true;
}

bool RunLegacyDiscardSwapChain(const wchar_t* nativePath) {
    NativeApi api;
    if (!LoadNative(nativePath, api)) {
        Check(false, "Legacy fixture loads the native API");
        if (api.module != nullptr) FreeLibrary(api.module);
        return false;
    }
    Check(api.arm() == 1, "Legacy fixture arms the DXGI hook");

    const HWND window = CreateTestWindow();
    Check(api.bindEnhanced(window) == static_cast<std::int32_t>(
            RwuiEnhancedTargetBindStatus::Invalid),
        "strict Enhanced binding rejects an unrelated top-level window class");
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    D3D_FEATURE_LEVEL featureLevel{};
    auto result = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &device,
        &featureLevel,
        &context);
    if (FAILED(result)) {
        result = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &device,
            &featureLevel,
            &context);
    }

    ComPtr<IDXGIDevice> dxgiDevice;
    ComPtr<IDXGIAdapter> adapter;
    ComPtr<IDXGIFactory> factory;
    ComPtr<IDXGISwapChain> swapChain;
    DXGI_SWAP_CHAIN_DESC description{};
    description.BufferDesc.Width = 640;
    description.BufferDesc.Height = 360;
    description.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    // A Legacy blt/discard chain may report two buffers while GetBuffer only
    // exposes index zero. This is the compatibility shape GTA Legacy uses.
    description.BufferCount = 2;
    description.OutputWindow = window;
    description.Windowed = TRUE;
    description.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    const bool ready = window != nullptr && SUCCEEDED(result) &&
        SUCCEEDED(device.As(&dxgiDevice)) &&
        SUCCEEDED(dxgiDevice->GetAdapter(&adapter)) &&
        SUCCEEDED(adapter->GetParent(IID_PPV_ARGS(&factory))) &&
        SUCCEEDED(factory->CreateSwapChain(
            device.Get(), &description, &swapChain));
    if (!ready) {
        std::cout << "SKIP: no usable D3D11 discard swap chain\n";
        api.shutdown();
        if (window != nullptr) DestroyWindow(window);
        FreeLibrary(api.module);
        return true;
    }

    ComPtr<ID3D11Texture2D> secondBuffer;
    const bool exposesOnlyBufferZero = FAILED(swapChain->GetBuffer(
        1, IID_PPV_ARGS(&secondBuffer)));
    Check(exposesOnlyBufferZero,
        "Legacy discard fixture exposes only back buffer zero");
    std::atomic_int crossThreadInitializeResult{};
    std::thread crossThreadInitialize([&] {
        crossThreadInitializeResult.store(
            api.initialize(window), std::memory_order_release);
    });
    crossThreadInitialize.join();
    Check(crossThreadInitializeResult.load(std::memory_order_acquire) == 1,
        "Legacy cross-thread target binding accepts the buffer-zero-only chain");
    SetLastError(0);
    laterPreviousProcedure = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(
        window, GWLP_WNDPROC,
        reinterpret_cast<LONG_PTR>(&LaterWindowProcedure)));
    Check(laterPreviousProcedure != nullptr || GetLastError() == 0,
        "a later mod can subclass the Reactor-bound game window");
    api.setVisible(1);
    RwuiRenderStats stats{};
    Check(SubmitAndRead(api, 201, stats),
        "Legacy CPU frame submission succeeds");
    Check(SUCCEEDED(swapChain->Present(0, 0)),
        "Legacy discard Present succeeds through the production hook");
    Check(api.getStats(&stats) == 1,
        "Legacy compositor stats are readable");
    Check(stats.api == RwuiRenderApi::Direct3D11,
        "Legacy discard chain selects D3D11");
    Check(stats.width == 640 && stats.height == 360,
        "Legacy compositor tracks buffer zero dimensions");
    Check(stats.renderedFrames > 0 && stats.lastFrameGeneration == 201,
        "Legacy discard chain renders from its prepared buffer zero");

    ComPtr<ID3D11Device> initialSwapChainDevice;
    Check(SUCCEEDED(swapChain->GetDevice(
              IID_PPV_ARGS(&initialSwapChainDevice))) &&
              SameComIdentity(initialSwapChainDevice.Get(), device.Get()),
        "Legacy swap chain starts on the fixture D3D11 device identity");

    RwuiLegacyHookDiagnostics lifecycleBeforeResize{};
    lifecycleBeforeResize.byteSize = sizeof(lifecycleBeforeResize);
    Check(api.getLegacyDiagnostics(&lifecycleBeforeResize) == 1 &&
            lifecycleBeforeResize.reserved[0] != 0 &&
            lifecycleBeforeResize.reserved[1] != 0,
        "Legacy diagnostics expose initialized compositor and backbuffer generations");
    constexpr std::array<std::array<UINT, 2>, 6> resizeSequence{{
        {{800, 450}},
        {{1024, 576}},
        {{640, 360}},
        {{1280, 720}},
        {{960, 540}},
        {{1366, 768}},
    }};
    const auto compositorGeneration = lifecycleBeforeResize.reserved[0];
    auto backBufferGeneration = lifecycleBeforeResize.reserved[1];
    auto renderedFrames = stats.renderedFrames;
    for (std::size_t cycle = 0; cycle < resizeSequence.size(); ++cycle) {
        const auto width = resizeSequence[cycle][0];
        const auto height = resizeSequence[cycle][1];
        Check(SUCCEEDED(swapChain->ResizeBuffers(
                2, width, height, DXGI_FORMAT_R8G8B8A8_UNORM, 0)),
            "repeated Legacy discard ResizeBuffers succeeds through the hook");
        ComPtr<ID3D11Device> resizedSwapChainDevice;
        Check(SUCCEEDED(swapChain->GetDevice(
                  IID_PPV_ARGS(&resizedSwapChainDevice))) &&
                  SameComIdentity(
                      resizedSwapChainDevice.Get(),
                      initialSwapChainDevice.Get()),
            "repeated Legacy resize retains the exact D3D11 device identity");
        RwuiLegacyHookDiagnostics lifecycleAfterResize{};
        lifecycleAfterResize.byteSize = sizeof(lifecycleAfterResize);
        Check(api.getLegacyDiagnostics(&lifecycleAfterResize) == 1 &&
                lifecycleAfterResize.reserved[0] == compositorGeneration &&
                lifecycleAfterResize.reserved[1] > backBufferGeneration,
            "repeated Legacy resize preserves compositor and replaces backbuffer state");
        backBufferGeneration = lifecycleAfterResize.reserved[1];

        const auto generation = 202 + cycle;
        Check(SubmitAndRead(api, generation, stats),
            "post-resize Legacy CPU frame submission succeeds");
        Check(SUCCEEDED(swapChain->Present(0, 0)),
            "Legacy discard Present succeeds after repeated resize");
        Check(api.getStats(&stats) == 1 &&
                stats.api == RwuiRenderApi::Direct3D11 &&
                stats.width == static_cast<std::int32_t>(width) &&
                stats.height == static_cast<std::int32_t>(height) &&
                stats.renderedFrames > renderedFrames &&
                stats.lastFrameGeneration == generation,
            "each resized Legacy backbuffer reveals its fresh submitted frame");
        renderedFrames = stats.renderedFrames;
    }

    blockingWindowEntered = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    blockingWindowRelease = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    blockingWindowCompleted.store(false, std::memory_order_release);
    std::atomic_bool shutdownStarted{};
    std::atomic_bool shutdownCompleted{};
    std::thread concurrentShutdown([&] {
        if (blockingWindowEntered == nullptr ||
            WaitForSingleObject(blockingWindowEntered, 2000) !=
                WAIT_OBJECT_0) {
            if (blockingWindowRelease != nullptr) {
                SetEvent(blockingWindowRelease);
            }
            return;
        }
        shutdownStarted.store(true, std::memory_order_release);
        api.shutdown();
        shutdownCompleted.store(true, std::memory_order_release);
        if (blockingWindowRelease != nullptr) SetEvent(blockingWindowRelease);
    });
    // Send on the window's owning thread so Windows dispatches the subclassed
    // procedure deterministically. The lifecycle thread detaches while the
    // immutable callback binding is retained by this invocation.
    SendMessageW(window, BlockingWindowMessage, 0, 0);
    const bool callbackEntered = blockingWindowEntered != nullptr &&
        WaitForSingleObject(blockingWindowEntered, 0) == WAIT_OBJECT_0;
    Check(callbackEntered,
        "the input callback is in flight before lifecycle detach");
    concurrentShutdown.join();
    Check(shutdownStarted.load(std::memory_order_acquire) &&
        shutdownCompleted.load(std::memory_order_acquire),
        "lifecycle detach completes while an old callback lease is in flight");
    Check(blockingWindowCompleted.load(std::memory_order_acquire),
        "an in-flight input callback retains its immutable forwarding target");
    laterSubclassCalls.store(0, std::memory_order_release);
    originalWindowCalls.store(0, std::memory_order_release);
    Check(SendMessageW(window, ChainedWindowMessage, 0, 0) == 74 &&
            laterSubclassCalls.load(std::memory_order_acquire) == 1 &&
            originalWindowCalls.load(std::memory_order_acquire) == 1,
        "detached Reactor tombstone forwards a later mod to the original proc");
    SetWindowLongPtrW(
        window, GWLP_WNDPROC,
        reinterpret_cast<LONG_PTR>(laterPreviousProcedure));
    laterPreviousProcedure = nullptr;
    if (blockingWindowEntered != nullptr) CloseHandle(blockingWindowEntered);
    if (blockingWindowRelease != nullptr) CloseHandle(blockingWindowRelease);
    blockingWindowEntered = nullptr;
    blockingWindowRelease = nullptr;
    swapChain.Reset();
    factory.Reset();
    adapter.Reset();
    dxgiDevice.Reset();
    context.Reset();
    device.Reset();
    DestroyWindow(window);
    FreeLibrary(api.module);
    return true;
}

bool RunStrictLegacyExternalResize(
    const wchar_t* nativePath,
    const wchar_t* helperPath,
    const bool flipModel = false,
    const bool fullscreen = false) {
    NativeApi api;
    if (!LoadNative(nativePath, api)) {
        Check(false, "strict Legacy external fixture loads the native API");
        if (api.module != nullptr) FreeLibrary(api.module);
        return false;
    }
    using EnableProbes = void(RWUI_CALL*)(std::int32_t);
    const auto enableProbes = Export<EnableProbes>(api.module, "RWUI_EnableD3D11DiagnosticProbes");
    Check(enableProbes != nullptr, "explicit device-probe opt-in export exists");
    if (enableProbes) enableProbes(1);
    Check(api.armLegacy() == 1,
        "strict Legacy external fixture arms before swap creation");

    const HWND window = CreateStrictLegacyWindow();
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    D3D_FEATURE_LEVEL featureLevel{};
    const auto deviceResult = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        0, // GTA owns device creation; do not require our optional BGRA flag.
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &device,
        &featureLevel,
        &context);
    ComPtr<IDXGIDevice> dxgiDevice;
    ComPtr<IDXGIAdapter> adapter;
    ComPtr<IDXGIFactory> factory;
    ComPtr<IDXGISwapChain> swapChain;
    DXGI_SWAP_CHAIN_DESC description{};
    description.BufferDesc.Width = 640;
    description.BufferDesc.Height = 360;
    description.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = 2;
    description.OutputWindow = window;
    description.Windowed = TRUE;
    description.SwapEffect = flipModel
        ? DXGI_SWAP_EFFECT_FLIP_DISCARD : DXGI_SWAP_EFFECT_DISCARD;
    const bool ready = window != nullptr && SUCCEEDED(deviceResult) &&
        SUCCEEDED(device.As(&dxgiDevice)) &&
        SUCCEEDED(dxgiDevice->GetAdapter(&adapter)) &&
        SUCCEEDED(adapter->GetParent(IID_PPV_ARGS(&factory))) &&
        SUCCEEDED(factory->CreateSwapChain(
            device.Get(), &description, &swapChain));
    if (!ready) {
        std::cout <<
            "SKIP: strict Legacy external resize needs hardware D3D11\n";
        api.shutdown();
        if (window != nullptr) DestroyWindow(window);
        FreeLibrary(api.module);
        return true;
    }

    if (fullscreen) {
        ShowWindow(window, SW_SHOW);
        SetForegroundWindow(window);
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        BOOL actualFullscreen{};
        const auto enter = swapChain->SetFullscreenState(TRUE, nullptr);
        const auto resized = SUCCEEDED(enter) ? swapChain->ResizeBuffers(
            2, 640, 360, DXGI_FORMAT_R8G8B8A8_UNORM, 0) : enter;
        const bool entered = SUCCEEDED(enter) && SUCCEEDED(resized) &&
            SUCCEEDED(swapChain->GetFullscreenState(&actualFullscreen, nullptr)) && actualFullscreen;
        std::cout << "CHECKPOINT fullscreen requested=1 actual=" << actualFullscreen
            << " foreground=" << (GetForegroundWindow() == window)
            << " remote_session=" << GetSystemMetrics(SM_REMOTESESSION)
            << " set_hr=0x" << std::hex << enter << " resize_hr=0x" << resized << std::dec << '\n';
        Check(entered, "explicit fullscreen qualification never falls back to a windowed pass");
        if (!entered) {
            swapChain->SetFullscreenState(FALSE, nullptr);
            api.shutdown();
            swapChain.Reset();
            DestroyWindow(window);
            FreeLibrary(api.module);
            return false;
        }
    }

    bool bound{};
    RwuiLegacyHookDiagnostics diagnostics{};
    const auto bindDeadline = GetTickCount64() + 3000;
    while (GetTickCount64() < bindDeadline) {
        swapChain->Present(0, 0);
        const auto bindResult = api.bindLegacy(window);
        diagnostics = {};
        diagnostics.byteSize = sizeof(diagnostics);
        if (api.getLegacyDiagnostics(&diagnostics) == 1 &&
            bindResult == static_cast<std::int32_t>(
                RwuiLegacyTargetBindStatus::Bound) &&
            (diagnostics.flags & RWUI_LEGACY_DIAGNOSTIC_TARGET_BOUND) != 0) {
            bound = true;
            break;
        }
        Sleep(2);
    }
    Check(bound,
        "strict Legacy grcWindow binds through the compositor-only ABI");
    Check((diagnostics.flags &
            (RWUI_LEGACY_DIAGNOSTIC_D3D11_READY |
             RWUI_LEGACY_DIAGNOSTIC_TARGET_BOUND)) ==
            (RWUI_LEGACY_DIAGNOSTIC_D3D11_READY |
             RWUI_LEGACY_DIAGNOSTIC_TARGET_BOUND) &&
            (diagnostics.flags &
                (RWUI_LEGACY_DIAGNOSTIC_LOCAL_OWNER |
                 RWUI_LEGACY_DIAGNOSTIC_INPUT_ATTACHED)) == 0 &&
            diagnostics.targetWindowClass ==
                RwuiLegacyTargetWindowClass::GrcWindow,
        "strict Legacy binding remains external-owner and input-detached");
    const auto compositorGeneration = diagnostics.reserved[0];
    const auto backBufferGeneration = diagnostics.reserved[1];
    Check(compositorGeneration != 0 && backBufferGeneration != 0,
        "strict Legacy binding publishes lifecycle generations");

    using ReadDevice = std::int32_t(RWUI_CALL*)(RwuiD3D11DeviceDiagnostics*);
    const auto readDevice = Export<ReadDevice>(api.module, "RWUI_GetD3D11DeviceDiagnostics");
    RwuiD3D11DeviceDiagnostics deviceEvidence{};
    deviceEvidence.byteSize = sizeof(deviceEvidence);
    Check(readDevice && readDevice(&deviceEvidence) == 1 &&
        deviceEvidence.probeComplete == 1 && deviceEvidence.featureLevel == static_cast<UINT>(featureLevel) &&
        deviceEvidence.creationFlags == device->GetCreationFlags(),
        "device probe reports the actual unmodified game-style device");
    Check(deviceEvidence.localBgraHresult == S_OK &&
        deviceEvidence.sharedBgraHresult == S_OK && deviceEvidence.sharedRgbaHresult == S_OK &&
        deviceEvidence.sharedBgraRenderTargetHresult == S_OK,
        "known BGRA/RGBA shared resources import with and without render-target binding");
    std::cout << "CHECKPOINT known_texture bgra_hr=0x" << std::hex << deviceEvidence.sharedBgraHresult
        << " rgba_hr=0x" << deviceEvidence.sharedRgbaHresult
        << " bgra_rt_hr=0x" << deviceEvidence.sharedBgraRenderTargetHresult << std::dec << '\n';
    if (fullscreen) Check(deviceEvidence.fullscreen == 1,
        "production device diagnostics witness the fullscreen swap chain");

    ComPtr<ID3D11Device> initialSwapChainDevice;
    Check(SUCCEEDED(swapChain->GetDevice(
              IID_PPV_ARGS(&initialSwapChainDevice))) &&
              SameComIdentity(initialSwapChainDevice.Get(), device.Get()),
        "strict Legacy swap chain uses the producer-selected hardware device");
    DXGI_ADAPTER_DESC adapterDescription{};
    const bool adapterReady = SUCCEEDED(adapter->GetDesc(&adapterDescription));
    Check(adapterReady,
        "strict Legacy external fixture resolves its adapter LUID");
    const auto queryAdapter = Export<QueryAdapterFunction>(
        api.module, "RWUI_QueryTargetAdapterLuid");
    std::int32_t publishedHigh{};
    std::uint32_t publishedLow{};
    Check(queryAdapter != nullptr &&
        queryAdapter(GetCurrentProcessId(), &publishedHigh, &publishedLow) == 1 &&
        publishedHigh == adapterDescription.AdapterLuid.HighPart &&
        publishedLow == adapterDescription.AdapterLuid.LowPart,
        "Legacy publishes the exact game adapter through the browser discovery ABI");
    const LUID publishedAdapter{publishedLow, publishedHigh};

    constexpr std::uint64_t SharedGeneration = 701;
    GpuProducerFixture producer;
    const bool producerStarted = adapterReady && producer.Start(
        helperPath, publishedAdapter, SharedGeneration);
    Check(producerStarted,
        "strict Legacy external producer starts on the compositor adapter");
    RwuiRenderStats stats{};
    bool externalRendered{};
    if (producerStarted) {
        const auto renderDeadline = GetTickCount64() + 5000;
        while (GetTickCount64() < renderDeadline) {
            swapChain->Present(0, 0);
            if (api.getStats(&stats) == 1 &&
                stats.lastFrameGeneration == SharedGeneration) {
                externalRendered = true;
                break;
            }
            Sleep(2);
        }
    }
    Check(externalRendered,
        "strict Legacy compositor renders the authenticated external GPU frame");
    const bool acknowledged = producerStarted && producer.Acknowledged();
    Check(acknowledged,
        "strict Legacy consumer acknowledges the external GPU frame");

    // DXGI_PRESENT_TEST occurs around occlusion/mode changes. It must not
    // consume a frame or advance the native presentation receipt.
    api.getStats(&stats);
    const auto beforeTest = stats.renderedFrames;
    for (int index = 0; index != 4; ++index) swapChain->Present(0, DXGI_PRESENT_TEST);
    Check(api.getStats(&stats) == 1 && stats.renderedFrames == beforeTest,
        "Legacy test-only presents do not draw or acknowledge a presentation");

    const auto renderedBeforeResize = stats.renderedFrames;
    Check(SUCCEEDED(swapChain->ResizeBuffers(
            2, 1600, 900, DXGI_FORMAT_R8G8B8A8_UNORM, 0)),
        "strict Legacy external route resizes through the production hook");
    ComPtr<ID3D11Device> resizedSwapChainDevice;
    Check(SUCCEEDED(swapChain->GetDevice(
              IID_PPV_ARGS(&resizedSwapChainDevice))) &&
              SameComIdentity(
                  resizedSwapChainDevice.Get(),
                  initialSwapChainDevice.Get()),
        "strict Legacy external resize retains D3D11 device identity");
    diagnostics = {};
    diagnostics.byteSize = sizeof(diagnostics);
    Check(api.getLegacyDiagnostics(&diagnostics) == 1 &&
            diagnostics.reserved[0] == compositorGeneration &&
            diagnostics.reserved[1] > backBufferGeneration,
        "strict Legacy external resize preserves compositor and consumer state");

    bool externalContinued{};
    if (producerStarted) {
        const auto continuityDeadline = GetTickCount64() + 3000;
        while (GetTickCount64() < continuityDeadline) {
            swapChain->Present(0, 0);
            if (api.getStats(&stats) == 1 &&
                stats.width == 1600 && stats.height == 900 &&
                stats.renderedFrames > renderedBeforeResize &&
                stats.lastFrameGeneration == SharedGeneration) {
                externalContinued = true;
                break;
            }
            Sleep(2);
        }
    }
    Check(externalContinued,
        "cached external GPU frame remains renderable after Legacy resize");
    // Exercise retirement with the last acknowledged frame, not a newly
    // generated browser frame masking a broken recovery path.
    for (const auto size : {std::pair<UINT, UINT>{1280, 720}, {640, 360}}) {
        const auto before = stats.renderedFrames;
        Check(SUCCEEDED(swapChain->ResizeBuffers(
            2, size.first, size.second, DXGI_FORMAT_R8G8B8A8_UNORM, 0)),
            "Legacy repeated backbuffer retirement succeeds");
        bool continued = false;
        const auto deadline = GetTickCount64() + 3000;
        while (GetTickCount64() < deadline) {
            swapChain->Present(0, 0);
            if (api.getStats(&stats) == 1 && stats.renderedFrames > before &&
                stats.lastFrameGeneration == SharedGeneration &&
                stats.width == static_cast<std::int32_t>(size.first) &&
                stats.height == static_cast<std::int32_t>(size.second)) {
                continued = true;
                break;
            }
            Sleep(2);
        }
        Check(continued, "Legacy retained GPU frame survives every resize");
        diagnostics = {};
        diagnostics.byteSize = sizeof(diagnostics);
        Check(api.getLegacyDiagnostics(&diagnostics) == 1 &&
            diagnostics.reserved[0] == compositorGeneration,
            "Legacy resize never rebuilds the device/consumer unnecessarily");
    }
    const bool producerStopped = producerStarted && producer.Stop() == 0;
    Check(producerStopped,
        "strict Legacy external producer exits cleanly after resize");
    legacyExternalHardwareQualified = externalRendered && acknowledged &&
        externalContinued && producerStopped;

    if (fullscreen) {
        Check(SUCCEEDED(swapChain->SetFullscreenState(FALSE, nullptr)),
            "fullscreen harness releases the display before shutdown");
    }
    api.shutdown();
    swapChain.Reset();
    factory.Reset();
    adapter.Reset();
    dxgiDevice.Reset();
    context.Reset();
    device.Reset();
    DestroyWindow(window);
    FreeLibrary(api.module);
    return true;
}

} // namespace

int wmain(const int argc, wchar_t** argv) {
    const bool legacyResizeExternalOnly = argc == 4 &&
        std::wstring(argv[3]) == L"--legacy-resize-external";
    const bool legacyFlipExternalOnly = argc == 4 &&
        std::wstring(argv[3]) == L"--legacy-flip-external";
    const bool legacyFullscreenOnly = argc == 4 &&
        std::wstring(argv[3]) == L"--legacy-fullscreen-external";
    if (argc != 3 && !legacyResizeExternalOnly && !legacyFlipExternalOnly && !legacyFullscreenOnly) {
        std::cerr <<
            "usage: EnhancedHookIntegrationTests <RageWebUI.Native.dll> "
            "<ReactorV.Preloader.exe> [--legacy-resize-external|--legacy-flip-external|--legacy-fullscreen-external]\n";
        return 2;
    }

    const auto comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninitializeCom = SUCCEEDED(comResult);
    if (legacyResizeExternalOnly || legacyFlipExternalOnly || legacyFullscreenOnly) {
        if (legacyResizeExternalOnly || legacyFullscreenOnly) {
            const auto before = failures;
            RunLegacyDiscardSwapChain(argv[1]);
            std::cout << "CHECKPOINT local_colored_frame windowed=1 passed=" << (before == failures) << '\n';
        }
        RunStrictLegacyExternalResize(argv[1], argv[2], legacyFlipExternalOnly, legacyFullscreenOnly);
        if (uninitializeCom) CoUninitialize();
        if (failures == 0 && !legacyExternalHardwareQualified) {
            std::cout <<
                "SKIP: Legacy external resize qualification requires hardware D3D11\n";
            return 125;
        }
        if (failures == 0) {
            std::cout <<
                "PASS: Legacy repeated resize and external-frame lifecycle\n";
        }
        return failures == 0 ? 0 : 1;
    }
    EnableD3D12Diagnostics();
    Check(RunNativeReloadStress(argv[1]),
        "native hook survives repeated arm/shutdown/FreeLibrary reloads");
    if (failures != 0) {
        if (uninitializeCom) CoUninitialize();
        return 1;
    }
    NativeApi api;
    if (!LoadNative(argv[1], api)) {
        std::cerr << "FAIL: could not load the native API and required exports\n";
        if (api.module != nullptr) FreeLibrary(api.module);
        if (uninitializeCom) CoUninitialize();
        return 1;
    }

    // Production arms from the root ASI before the game creates its swap
    // chain. The HWND/input binding arrives later from the managed host.
    Check(api.arm() == 1, "the Enhanced render hooks arm without an HWND");
    Check(api.arm() == 1, "early arming is idempotent");

    const HWND window = CreateTestWindow();
    ComPtr<IDXGIFactory6> factory;
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<IDXGISwapChain3> swapChain;
    bool hardwareDevice{};
    const bool d3d12Ready = window != nullptr &&
        SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory))) &&
        CreateDeviceAndQueue(
            factory.Get(), &device, &queue, hardwareDevice) &&
        CreateSwapChain(
            factory.Get(), queue.Get(), window, 640, 360, &swapChain);
    if (!d3d12Ready) {
        std::cerr << "SKIP: no usable D3D12 hardware or WARP device\n";
        api.shutdown();
        if (window != nullptr) DestroyWindow(window);
        FreeLibrary(api.module);
        if (uninitializeCom) CoUninitialize();
        return 125;
    }

    // This late bind must recover the exact queue captured by the factory
    // hook. No command lists have executed, so a heuristic queue fallback
    // cannot make the first hooked Present1 render succeed.
    Check(api.initialize(window) == 1,
        "late HWND binding selects the pre-captured factory queue");
    Check(api.initialize(window) == 1,
        "late HWND/input binding is idempotent");
    api.setVisible(1);

    RwuiRenderStats stats{};
    Check(SubmitAndRead(api, 101, stats), "initial frame submission succeeds");
    DXGI_PRESENT_PARAMETERS parameters{};
    const auto firstPresentStarted = std::chrono::steady_clock::now();
    Check(SUCCEEDED(swapChain->Present1(0, 0, &parameters)),
        "Present1 succeeds through the production hook");
    const auto firstPresentElapsed =
        std::chrono::steady_clock::now() - firstPresentStarted;
    Check(firstPresentElapsed < std::chrono::seconds(2),
        "one-time D3D11On12 compositor setup is explicitly bounded");
    Check(api.getStats(&stats) == 1, "stats are readable after Present1");
    CheckRendered(stats, 640, 360, 101, "Present1 reports D3D12");
    RwuiEnhancedHookDiagnostics lifecycleBeforeReset{};
    lifecycleBeforeReset.byteSize = sizeof(lifecycleBeforeReset);
    Check(api.getEnhancedDiagnostics(&lifecycleBeforeReset) == 1 &&
            lifecycleBeforeReset.reserved[0] != 0 &&
            lifecycleBeforeReset.reserved[1] != 0,
        "lifecycle diagnostics identify the initial D3D11On12 device and buffers");

    // Capture is a commit property of each rendered frame. Reset the shared
    // compositor through the existing standalone harness, then prove the next
    // visible Present fails open and releases capture before async preparation
    // recovers both drawing and input.
    Check(MouseMoveCaptured(api, window, 17),
        "a successful visible draw commits input capture");
    Check(api.testStart(
        RwuiRenderApi::Direct3D11, 320, 240,
        L"ReactorV compositor reset fixture") == 1,
        "the reset fixture starts");
    const auto resetDeadline = GetTickCount64() + 5000;
    while (api.testIsRunning() == 1 && GetTickCount64() < resetDeadline) {
        Sleep(2);
    }
    Check(api.testIsRunning() == 0,
        "the reset fixture completes within its bound");
    api.testStop();
    Check(SUCCEEDED(swapChain->Present1(0, 0, &parameters)),
        "the forced unprepared Present remains fail-open");
    Check(!MouseMoveCaptured(api, window, 18),
        "a failed visible draw immediately releases input capture");

    bool recoveredCapture{};
    const auto recoveryDeadline = GetTickCount64() + 5000;
    while (GetTickCount64() < recoveryDeadline) {
        swapChain->Present1(0, 0, &parameters);
        if (MouseMoveCaptured(api, window, 19)) {
            recoveredCapture = true;
            break;
        }
        Sleep(2);
    }
    Check(recoveredCapture,
        "a recovered visible draw recommits input capture");
    Check(api.getStats(&stats) == 1 && stats.renderedFrames > 0 &&
            stats.lastFrameGeneration == 101,
        "recovery reuses the retained latest CPU frame");
    RwuiEnhancedHookDiagnostics lifecycleBeforeResize{};
    lifecycleBeforeResize.byteSize = sizeof(lifecycleBeforeResize);
    Check(api.getEnhancedDiagnostics(&lifecycleBeforeResize) == 1 &&
            lifecycleBeforeResize.reserved[0] >
                lifecycleBeforeReset.reserved[0],
        "a full TestSurface rebind creates a new D3D11On12 device generation");

    const auto renderedBeforeTestPresent = stats.renderedFrames;
    Check(SUCCEEDED(swapChain->Present1(
            0, DXGI_PRESENT_TEST, &parameters)),
        "DXGI test Present passes through the production hook");
    Check(api.getStats(&stats) == 1 &&
            stats.renderedFrames == renderedBeforeTestPresent,
        "DXGI test Present performs no overlay draw");
    Check(!MouseMoveCaptured(api, window, 20),
        "DXGI test Present cannot retain input capture");
    Check(SUCCEEDED(swapChain->Present1(0, 0, &parameters)) &&
            MouseMoveCaptured(api, window, 21),
        "a later committed Present restores drawing and capture");

    if (hardwareDevice) {
        constexpr std::uint64_t SharedGeneration = 501;
        GpuProducerFixture producer;
        const bool producerReady = producer.Start(
            argv[2], device->GetAdapterLuid(), SharedGeneration);
        Check(producerReady,
            "external GPU producer probes the compositor adapter");
        if (producerReady) {
            bool sharedAcknowledged{};
            const auto deadline = GetTickCount64() + 5000;
            while (GetTickCount64() < deadline) {
                swapChain->Present1(0, 0, &parameters);
                if (producer.Acknowledged()) {
                    sharedAcknowledged = true;
                    break;
                }
                Sleep(5);
            }
            Check(sharedAcknowledged,
                "producer capability promotion waits for consumer ACK");
            const auto renderedBeforeRepeat = stats.renderedFrames;
            Check(SUCCEEDED(swapChain->Present(0, 0)),
                "a repeated Present succeeds without another producer frame");
            Check(api.getStats(&stats) == 1 &&
                    stats.lastFrameGeneration == 101 &&
                    stats.renderedFrames > renderedBeforeRepeat,
                "local provider ownership supersedes the external shared frame");
            Check(producer.Stop() == 0,
                "external GPU producer exits cleanly");
            Check(SubmitAndRead(api, 102, stats),
                "CPU mailbox accepts a generation below the shared route");
            bool cpuFallbackRendered{};
            const auto fallbackDeadline = GetTickCount64() + 5000;
            while (GetTickCount64() < fallbackDeadline) {
                swapChain->Present(0, 0);
                if (api.getStats(&stats) == 1 &&
                    stats.lastFrameGeneration == 102) {
                    cpuFallbackRendered = true;
                    break;
                }
                Sleep(5);
            }
            Check(cpuFallbackRendered,
                "producer disconnect clears shared latest and renders lower CPU generation");
        }
    } else {
        std::cout <<
            "SKIP: external GPU D3D12 persistence needs a hardware adapter\n";
    }

    constexpr UINT BufferCount = 2;
    const UINT nodeMasks[BufferCount]{};
    IUnknown* presentQueues[BufferCount]{queue.Get(), queue.Get()};
    CheckSucceeded(swapChain->ResizeBuffers1(
        BufferCount,
        800,
        450,
        DXGI_FORMAT_R8G8B8A8_UNORM,
        0,
        nodeMasks,
        presentQueues),
        "ResizeBuffers1 succeeds through the production hook", device.Get());
    Check(SubmitAndRead(api, 103, stats),
        "post-ResizeBuffers1 frame submission succeeds");
    Check(SUCCEEDED(swapChain->Present(0, 0)),
        "Present succeeds after ResizeBuffers1 queue refresh");
    Check(api.getStats(&stats) == 1,
        "stats are readable after ResizeBuffers1 and Present");
    CheckRendered(stats, 800, 450, 103,
        "Present after ResizeBuffers1 reports D3D12");
    RwuiEnhancedHookDiagnostics lifecycleAfterResize1{};
    lifecycleAfterResize1.byteSize = sizeof(lifecycleAfterResize1);
    Check(api.getEnhancedDiagnostics(&lifecycleAfterResize1) == 1 &&
            lifecycleAfterResize1.reserved[0] ==
                lifecycleBeforeResize.reserved[0] &&
            lifecycleAfterResize1.reserved[1] >
                lifecycleBeforeResize.reserved[1],
        "ResizeBuffers1 rebuilds wrappers without recreating D3D11On12");

    D3D12_COMMAND_QUEUE_DESC alternateQueueDescription{};
    alternateQueueDescription.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    ComPtr<ID3D12CommandQueue> alternateQueue;
    Check(SUCCEEDED(device->CreateCommandQueue(
            &alternateQueueDescription,
            IID_PPV_ARGS(&alternateQueue))),
        "mixed-queue fixture creates a second direct queue");
    IUnknown* mixedPresentQueues[BufferCount]{
        queue.Get(), alternateQueue.Get()};
    if (alternateQueue == nullptr) {
        Check(false, "game ResizeBuffers1 has a mixed direct queue fixture");
    } else {
        CheckSucceeded(swapChain->ResizeBuffers1(
            BufferCount,
            820,
            460,
            DXGI_FORMAT_R8G8B8A8_UNORM,
            0,
            nodeMasks,
            mixedPresentQueues),
            "game ResizeBuffers1 remains successful for mixed direct queues",
            device.Get());
    }

    // A successful mixed-queue resize is unsupported by the one-queue
    // compositor. Switching the target away and back must not resurrect the
    // stale queue captured when the factory first created this swap chain.
    const HWND alternateWindow = CreateTestWindow();
    Check(alternateWindow != nullptr && api.initialize(alternateWindow) == 1,
        "mixed-queue fixture switches to an unrelated target window");
    Check(api.initialize(window) == 1,
        "mixed-queue fixture rebinds the original target window");
    Check(SubmitAndRead(api, 104, stats),
        "unsupported mixed route retains the CPU mailbox frame");
    Check(SUCCEEDED(swapChain->Present(0, 0)),
        "mixed-queue game Present remains fail-open");
    Check(api.getStats(&stats) == 1 &&
            stats.lastFrameGeneration != 104 &&
            !MouseMoveCaptured(api, window, 104),
        "target rebind cannot resurrect the retired factory queue");

    IUnknown* recoveredPresentQueues[BufferCount]{queue.Get(), queue.Get()};
    CheckSucceeded(swapChain->ResizeBuffers1(
        BufferCount,
        840,
        472,
        DXGI_FORMAT_R8G8B8A8_UNORM,
        0,
        nodeMasks,
        recoveredPresentQueues),
        "a later uniform ResizeBuffers1 restores a supported route", device.Get());
    Check(SubmitAndRead(api, 105, stats),
        "uniform queue recovery accepts the newest CPU frame");
    bool uniformQueueRecovered{};
    const auto uniformRecoveryDeadline = GetTickCount64() + 5000;
    while (GetTickCount64() < uniformRecoveryDeadline) {
        swapChain->Present(0, 0);
        if (api.getStats(&stats) == 1 &&
            stats.lastFrameGeneration == 105) {
            uniformQueueRecovered = true;
            break;
        }
        Sleep(2);
    }
    Check(uniformQueueRecovered,
        "uniform ResizeBuffers1 replaces the retired mixed-queue record");
    if (alternateWindow != nullptr) DestroyWindow(alternateWindow);

    CheckSucceeded(swapChain->ResizeBuffers(
        BufferCount,
        960,
        540,
        DXGI_FORMAT_R8G8B8A8_UNORM,
        0),
        "ResizeBuffers succeeds through the production hook", device.Get());
    Check(SubmitAndRead(api, 106, stats),
        "post-ResizeBuffers frame submission succeeds");
    Check(SUCCEEDED(swapChain->Present1(0, 0, &parameters)),
        "Present1 succeeds after ordinary ResizeBuffers");
    bool asynchronouslyPrepared{};
    const auto prepareDeadline = GetTickCount64() + 5000;
    while (GetTickCount64() < prepareDeadline) {
        if (api.getStats(&stats) == 1 &&
            stats.lastFrameGeneration == 106) {
            asynchronouslyPrepared = true;
            break;
        }
        swapChain->Present1(0, 0, &parameters);
        Sleep(2);
    }
    Check(asynchronouslyPrepared,
        "visible Present fails open until the off-thread resize prepare completes");
    Check(api.getStats(&stats) == 1,
        "stats are readable after asynchronous ResizeBuffers preparation");
    CheckRendered(stats, 960, 540, 106,
        "Present1 after ResizeBuffers reports D3D12");
    RwuiEnhancedHookDiagnostics lifecycleAfterResize{};
    lifecycleAfterResize.byteSize = sizeof(lifecycleAfterResize);
    Check(api.getEnhancedDiagnostics(&lifecycleAfterResize) == 1 &&
            lifecycleAfterResize.reserved[0] ==
                lifecycleBeforeResize.reserved[0] &&
            lifecycleAfterResize.reserved[1] >
                lifecycleAfterResize1.reserved[1],
        "ordinary ResizeBuffers preserves D3D11On12 and replaces only buffers");

    // Stress the lifecycle that previously failed intermittently: retire a
    // mixed queue, recover a uniform ResizeBuffers1 route, then perform an
    // ordinary ResizeBuffers. ClearState/Flush must release every indirect
    // D3D11On12 back-buffer reference on every cycle.
    for (UINT cycle = 0; cycle != 4; ++cycle) {
        const UINT uniformWidth = 1000 + cycle * 8;
        const UINT uniformHeight = 560 + cycle * 4;
        CheckSucceeded(swapChain->ResizeBuffers1(
                BufferCount, uniformWidth, uniformHeight,
                DXGI_FORMAT_R8G8B8A8_UNORM, 0, nodeMasks,
                recoveredPresentQueues),
            "stress uniform ResizeBuffers1 succeeds", device.Get());
        const std::uint64_t uniformGeneration = 200 + cycle * 2;
        Check(SubmitAndRead(api, uniformGeneration, stats),
            "stress uniform frame submission succeeds");
        bool uniformRendered{};
        const auto uniformDeadline = GetTickCount64() + 3000;
        while (GetTickCount64() < uniformDeadline) {
            swapChain->Present(0, 0);
            if (api.getStats(&stats) == 1 &&
                stats.lastFrameGeneration == uniformGeneration) {
                uniformRendered = true;
                break;
            }
            Sleep(2);
        }
        Check(uniformRendered, "stress uniform route renders");

        const UINT ordinaryWidth = uniformWidth + 4;
        const UINT ordinaryHeight = uniformHeight + 2;
        CheckSucceeded(swapChain->ResizeBuffers(
                BufferCount, ordinaryWidth, ordinaryHeight,
                DXGI_FORMAT_R8G8B8A8_UNORM, 0),
            "stress ordinary ResizeBuffers releases all D3D11On12 references",
            device.Get());
        const std::uint64_t ordinaryGeneration = uniformGeneration + 1;
        Check(SubmitAndRead(api, ordinaryGeneration, stats),
            "stress ordinary frame submission succeeds");
        bool ordinaryRendered{};
        const auto ordinaryDeadline = GetTickCount64() + 3000;
        while (GetTickCount64() < ordinaryDeadline) {
            swapChain->Present1(0, 0, &parameters);
            if (api.getStats(&stats) == 1 &&
                stats.lastFrameGeneration == ordinaryGeneration) {
                ordinaryRendered = true;
                break;
            }
            Sleep(2);
        }
        Check(ordinaryRendered, "stress ordinary resize route recovers");
    }
    RwuiEnhancedHookDiagnostics lifecycleAfterStress{};
    lifecycleAfterStress.byteSize = sizeof(lifecycleAfterStress);
    Check(api.getEnhancedDiagnostics(&lifecycleAfterStress) == 1 &&
            lifecycleAfterStress.reserved[0] ==
                lifecycleBeforeResize.reserved[0] &&
            lifecycleAfterStress.reserved[1] >=
                lifecycleAfterResize.reserved[1] + 8,
        "alternating resize stress retains one D3D11On12 device generation");

    api.setVisible(0);
    const auto renderedBeforeHiddenPresent = stats.renderedFrames;
    Check(SubmitAndRead(api, 300, stats), "hidden frame submission succeeds");
    Check(SUCCEEDED(swapChain->Present(0, 0)),
        "game Present remains fail-open while the overlay is hidden");
    Check(api.getStats(&stats) == 1,
        "stats are readable after a hidden Present");
    Check(stats.renderedFrames == renderedBeforeHiddenPresent,
        "hidden overlay does not inject a render pass");

    Check(api.testStart(
        RwuiRenderApi::Direct3D11, 320, 240,
        L"ReactorV shutdown ordering fixture") == 1,
        "shutdown fixture resets the active compositor");
    const auto shutdownFixtureDeadline = GetTickCount64() + 5000;
    while (api.testIsRunning() == 1 &&
        GetTickCount64() < shutdownFixtureDeadline) {
        Sleep(2);
    }
    api.testStop();
    Check(SUCCEEDED(swapChain->Present(0, 0)),
        "hidden unprepared Present publishes a pending preparation request");
    api.shutdown();
    Check(api.getStats(&stats) == 1 &&
            stats.api == RwuiRenderApi::None &&
            stats.width == 0 && stats.height == 0,
        "shutdown joins pending preparation before the final resource reset");
    swapChain.Reset();
    queue.Reset();
    device.Reset();
    factory.Reset();
    DestroyWindow(window);
    FreeLibrary(api.module);
    RunStrictExternalCompositor(argv[1], argv[2]);
    if (uninitializeCom) CoUninitialize();

    if (failures == 0) {
        std::cout <<
            "PASS: Enhanced and Legacy DXGI hook integration\n";
    }
    return failures == 0 ? 0 : 1;
}
