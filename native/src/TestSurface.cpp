#include "RuntimeState.h"

#include <atomic>
#include <chrono>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <mutex>
#include <string>
#include <thread>
#include <wrl/client.h>

namespace rwui {

namespace {

std::mutex testMutex;
std::thread testThread;
std::atomic_bool testRunning{false};
std::atomic_bool testStopRequested{false};
HWND testWindow{};

LRESULT CALLBACK TestWindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    if (message == WM_CLOSE) {
        testStopRequested.store(true, std::memory_order_relaxed);
        DestroyWindow(window);
        return 0;
    }
    if (message == WM_DESTROY) {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

HWND CreateTestWindow(const std::int32_t width, const std::int32_t height, const wchar_t* title) {
    static constexpr wchar_t ClassName[] = L"RageWebUI.DirectXHarness";
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = TestWindowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    windowClass.hbrBackground = reinterpret_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    windowClass.lpszClassName = ClassName;
    RegisterClassW(&windowClass);

    RECT rectangle{0, 0, width, height};
    AdjustWindowRect(&rectangle, WS_OVERLAPPEDWINDOW, FALSE);
    return CreateWindowExW(
        0,
        ClassName,
        title == nullptr ? L"RageWebUI DirectX Harness" : title,
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        rectangle.right - rectangle.left,
        rectangle.bottom - rectangle.top,
        nullptr,
        nullptr,
        windowClass.hInstance,
        nullptr);
}

bool CreateD3D11SwapChain(HWND window, const std::int32_t width, const std::int32_t height, IDXGISwapChain** result) {
    DXGI_SWAP_CHAIN_DESC description{};
    description.BufferCount = 2;
    description.BufferDesc.Width = width;
    description.BufferDesc.Height = height;
    description.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.OutputWindow = window;
    description.SampleDesc.Count = 1;
    description.Windowed = TRUE;
    description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    Microsoft::WRL::ComPtr<ID3D11Device> device;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
    D3D_FEATURE_LEVEL level{};
    return SUCCEEDED(D3D11CreateDeviceAndSwapChain(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &description,
        result,
        &device,
        &level,
        &context));
}

bool CreateD3D12SwapChain(
    HWND window,
    const std::int32_t width,
    const std::int32_t height,
    IDXGISwapChain** result,
    ID3D12CommandQueue** queueResult) {
    Microsoft::WRL::ComPtr<ID3D12Device> device;
    if (FAILED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)))) return false;
    D3D12_COMMAND_QUEUE_DESC queueDescription{};
    queueDescription.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;
    if (FAILED(device->CreateCommandQueue(&queueDescription, IID_PPV_ARGS(&queue)))) return false;
    Microsoft::WRL::ComPtr<IDXGIFactory4> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return false;
    DXGI_SWAP_CHAIN_DESC1 description{};
    description.Width = width;
    description.Height = height;
    description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = 2;
    description.SampleDesc.Count = 1;
    description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    Microsoft::WRL::ComPtr<IDXGISwapChain1> swapChain;
    if (FAILED(factory->CreateSwapChainForHwnd(queue.Get(), window, &description, nullptr, nullptr, &swapChain)) ||
        FAILED(swapChain->QueryInterface(IID_PPV_ARGS(result)))) {
        return false;
    }
    *queueResult = queue.Detach();
    return true;
}

void RunTestSurface(
    const RwuiRenderApi api,
    const std::int32_t width,
    const std::int32_t height,
    std::wstring title) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    testWindow = CreateTestWindow(width, height, title.c_str());
    if (testWindow == nullptr) {
        testRunning.store(false, std::memory_order_relaxed);
        CoUninitialize();
        return;
    }

    Microsoft::WRL::ComPtr<IDXGISwapChain> swapChain;
    Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;
    const bool created = api == RwuiRenderApi::Direct3D11
        ? CreateD3D11SwapChain(testWindow, width, height, &swapChain)
        : CreateD3D12SwapChain(testWindow, width, height, &swapChain, &queue);
    if (!created || !g_inputQueue.Attach(testWindow)) {
        DestroyWindow(testWindow);
        testWindow = nullptr;
        testRunning.store(false, std::memory_order_relaxed);
        CoUninitialize();
        return;
    }

    g_visible.store(true, std::memory_order_relaxed);
    g_inputQueue.SetCapture(true);
    ShowWindow(testWindow, SW_SHOW);
    UpdateWindow(testWindow);

    MSG message{};
    while (!testStopRequested.load(std::memory_order_relaxed)) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        g_compositor.Render(swapChain.Get(), queue.Get(), true);
        swapChain->Present(1, 0);
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }

    g_inputQueue.SetCapture(false);
    g_inputQueue.Detach();
    g_visible.store(false, std::memory_order_relaxed);
    g_compositor.Reset();
    queue.Reset();
    swapChain.Reset();
    if (testWindow != nullptr && IsWindow(testWindow)) DestroyWindow(testWindow);
    testWindow = nullptr;
    testRunning.store(false, std::memory_order_relaxed);
    CoUninitialize();
}

} // namespace

bool StartTestSurface(
    const RwuiRenderApi api,
    const std::int32_t width,
    const std::int32_t height,
    const wchar_t* title) {
    std::scoped_lock lock(testMutex);
    if (testRunning.load(std::memory_order_relaxed) ||
        (api != RwuiRenderApi::Direct3D11 && api != RwuiRenderApi::Direct3D12) ||
        width < 320 || height < 240 || width > 8192 || height > 8192) {
        return false;
    }
    testStopRequested.store(false, std::memory_order_relaxed);
    testRunning.store(true, std::memory_order_relaxed);
    testThread = std::thread(RunTestSurface, api, width, height, title == nullptr ? L"" : title);
    return true;
}

void StopTestSurface() {
    std::scoped_lock lock(testMutex);
    testStopRequested.store(true, std::memory_order_relaxed);
    if (testWindow != nullptr) PostMessageW(testWindow, WM_CLOSE, 0, 0);
    if (testThread.joinable()) testThread.join();
}

bool IsTestSurfaceRunning() {
    return testRunning.load(std::memory_order_relaxed);
}

} // namespace rwui

RWUI_API std::int32_t RWUI_CALL RWUI_TestStart(
    const RwuiRenderApi api,
    const std::int32_t width,
    const std::int32_t height,
    const wchar_t* title) {
    return rwui::StartTestSurface(api, width, height, title) ? 1 : 0;
}

RWUI_API void RWUI_CALL RWUI_TestStop() {
    rwui::StopTestSurface();
}

RWUI_API std::int32_t RWUI_CALL RWUI_TestIsRunning() {
    return rwui::IsTestSurfaceRunning() ? 1 : 0;
}
