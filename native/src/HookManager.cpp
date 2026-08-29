#include "RuntimeState.h"
#include "RageWebUI.Native.h"

#include <MinHook.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <atomic>
#include <mutex>
#include <wrl/client.h>

namespace rwui {

FrameMailbox g_frameMailbox;
DirectXCompositor g_compositor(g_frameMailbox);
InputQueue g_inputQueue;
std::atomic_bool g_visible{false};

namespace {

using PresentFunction = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using ResizeBuffersFunction = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
using ExecuteCommandListsFunction = void(STDMETHODCALLTYPE*)(ID3D12CommandQueue*, UINT, ID3D12CommandList* const*);

PresentFunction originalPresent{};
ResizeBuffersFunction originalResizeBuffers{};
ExecuteCommandListsFunction originalExecuteCommandLists{};
void* presentAddress{};
void* resizeAddress{};
void* executeAddress{};
HWND targetWindow{};
bool minHookOwned{};
bool hooksInstalled{};
std::mutex queueMutex;
Microsoft::WRL::ComPtr<ID3D12CommandQueue> directQueue;

LRESULT CALLBACK DummyWindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    return DefWindowProcW(window, message, wParam, lParam);
}

HWND CreateDummyWindow() {
    static constexpr wchar_t ClassName[] = L"RageWebUI.Native.Dummy";
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DummyWindowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = ClassName;
    RegisterClassW(&windowClass);
    return CreateWindowExW(
        0,
        ClassName,
        L"",
        WS_OVERLAPPED,
        0,
        0,
        16,
        16,
        nullptr,
        nullptr,
        windowClass.hInstance,
        nullptr);
}

bool ResolveDxgiMethods() {
    const HWND window = CreateDummyWindow();
    if (window == nullptr) return false;

    DXGI_SWAP_CHAIN_DESC swapChainDescription{};
    swapChainDescription.BufferCount = 2;
    swapChainDescription.BufferDesc.Width = 16;
    swapChainDescription.BufferDesc.Height = 16;
    swapChainDescription.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    swapChainDescription.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swapChainDescription.OutputWindow = window;
    swapChainDescription.SampleDesc.Count = 1;
    swapChainDescription.Windowed = TRUE;
    swapChainDescription.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    Microsoft::WRL::ComPtr<ID3D11Device> device;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
    Microsoft::WRL::ComPtr<IDXGISwapChain> swapChain;
    D3D_FEATURE_LEVEL featureLevel{};
    const auto result = D3D11CreateDeviceAndSwapChain(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        0,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &swapChainDescription,
        &swapChain,
        &device,
        &featureLevel,
        &context);
    if (SUCCEEDED(result)) {
        auto** virtualTable = *reinterpret_cast<void***>(swapChain.Get());
        presentAddress = virtualTable[8];
        resizeAddress = virtualTable[13];
    }

    DestroyWindow(window);
    return presentAddress != nullptr && resizeAddress != nullptr;
}

bool ResolveD3D12Methods() {
    Microsoft::WRL::ComPtr<ID3D12Device> device;
    if (FAILED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)))) {
        return false;
    }
    D3D12_COMMAND_QUEUE_DESC description{};
    description.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;
    if (FAILED(device->CreateCommandQueue(&description, IID_PPV_ARGS(&queue)))) {
        return false;
    }
    auto** virtualTable = *reinterpret_cast<void***>(queue.Get());
    executeAddress = virtualTable[10];
    return executeAddress != nullptr;
}

bool IsTargetSwapChain(IDXGISwapChain* swapChain) {
    if (targetWindow == nullptr) return false;
    DXGI_SWAP_CHAIN_DESC description{};
    return SUCCEEDED(swapChain->GetDesc(&description)) && description.OutputWindow == targetWindow;
}

HRESULT STDMETHODCALLTYPE PresentHook(IDXGISwapChain* swapChain, const UINT syncInterval, const UINT flags) {
    if (g_visible.load(std::memory_order_relaxed) && IsTargetSwapChain(swapChain)) {
        Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;
        {
            std::scoped_lock lock(queueMutex);
            queue = directQueue;
        }
        g_compositor.Render(swapChain, queue.Get());
    }
    return originalPresent(swapChain, syncInterval, flags);
}

HRESULT STDMETHODCALLTYPE ResizeBuffersHook(
    IDXGISwapChain* swapChain,
    const UINT bufferCount,
    const UINT width,
    const UINT height,
    const DXGI_FORMAT format,
    const UINT flags) {
    if (IsTargetSwapChain(swapChain)) {
        g_compositor.BeforeResize(swapChain);
    }
    return originalResizeBuffers(swapChain, bufferCount, width, height, format, flags);
}

void STDMETHODCALLTYPE ExecuteCommandListsHook(
    ID3D12CommandQueue* queue,
    const UINT count,
    ID3D12CommandList* const* lists) {
    const auto description = queue->GetDesc();
    if (description.Type == D3D12_COMMAND_LIST_TYPE_DIRECT) {
        std::scoped_lock lock(queueMutex);
        directQueue = queue;
    }
    originalExecuteCommandLists(queue, count, lists);
}

} // namespace

bool InstallHooks(HWND window) {
    if (hooksInstalled || window == nullptr || !IsWindow(window)) return false;
    if (!ResolveDxgiMethods()) return false;
    // A D3D11-only machine can still use the Legacy renderer. The command
    // queue hook is required only when D3D12 is available in this process.
    const bool d3d12Available = ResolveD3D12Methods();

    const auto initializeResult = MH_Initialize();
    if (initializeResult != MH_OK && initializeResult != MH_ERROR_ALREADY_INITIALIZED) return false;
    minHookOwned = initializeResult == MH_OK;

    const bool dxgiHooksCreated =
        MH_CreateHook(presentAddress, reinterpret_cast<void*>(&PresentHook), reinterpret_cast<void**>(&originalPresent)) == MH_OK &&
        MH_CreateHook(resizeAddress, reinterpret_cast<void*>(&ResizeBuffersHook), reinterpret_cast<void**>(&originalResizeBuffers)) == MH_OK;
    const bool d3d12HookCreated = !d3d12Available ||
        MH_CreateHook(executeAddress, reinterpret_cast<void*>(&ExecuteCommandListsHook), reinterpret_cast<void**>(&originalExecuteCommandLists)) == MH_OK;
    const bool hooksEnabled = dxgiHooksCreated && d3d12HookCreated &&
        MH_EnableHook(presentAddress) == MH_OK &&
        MH_EnableHook(resizeAddress) == MH_OK &&
        (!d3d12Available || MH_EnableHook(executeAddress) == MH_OK);
    if (!hooksEnabled) {
        RemoveHooks();
        return false;
    }

    targetWindow = window;
    if (!g_inputQueue.Attach(window)) {
        RemoveHooks();
        return false;
    }
    hooksInstalled = true;
    return true;
}

void RemoveHooks() {
    g_visible.store(false, std::memory_order_relaxed);
    g_inputQueue.SetCapture(false);
    g_inputQueue.Detach();
    if (presentAddress != nullptr) {
        MH_DisableHook(presentAddress);
        MH_RemoveHook(presentAddress);
    }
    if (resizeAddress != nullptr) {
        MH_DisableHook(resizeAddress);
        MH_RemoveHook(resizeAddress);
    }
    if (executeAddress != nullptr) {
        MH_DisableHook(executeAddress);
        MH_RemoveHook(executeAddress);
    }
    if (minHookOwned) MH_Uninitialize();

    {
        std::scoped_lock lock(queueMutex);
        directQueue.Reset();
    }
    g_compositor.Reset();
    targetWindow = nullptr;
    hooksInstalled = false;
    minHookOwned = false;
}

} // namespace rwui

RWUI_API std::int32_t RWUI_CALL RWUI_Initialize(void* targetWindow) {
    return rwui::InstallHooks(static_cast<HWND>(targetWindow)) ? 1 : 0;
}

RWUI_API void RWUI_CALL RWUI_Shutdown() {
    RWUI_TestStop();
    rwui::RemoveHooks();
    rwui::g_frameMailbox.Clear();
}

RWUI_API void RWUI_CALL RWUI_SetVisible(const std::int32_t visible) {
    const bool active = visible != 0;
    rwui::g_visible.store(active, std::memory_order_relaxed);
    rwui::g_inputQueue.SetCapture(active);
}

RWUI_API std::int32_t RWUI_CALL RWUI_SubmitFrame(
    const void* bgraPixels,
    const std::int32_t width,
    const std::int32_t height,
    const std::int32_t stride,
    const std::uint64_t generation) {
    return rwui::g_frameMailbox.Submit(bgraPixels, width, height, stride, generation) ? 1 : 0;
}

RWUI_API std::int32_t RWUI_CALL RWUI_PollInput(RwuiInputEvent* inputEvent) {
    return inputEvent != nullptr && rwui::g_inputQueue.Poll(*inputEvent) ? 1 : 0;
}

RWUI_API std::int32_t RWUI_CALL RWUI_GetStats(RwuiRenderStats* stats) {
    if (stats == nullptr) return 0;
    *stats = rwui::g_compositor.Stats();
    return 1;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
