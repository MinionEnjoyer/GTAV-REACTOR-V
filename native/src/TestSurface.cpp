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

constexpr wchar_t TestWindowClassName[] = L"RageWebUI.DirectXHarness";

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

class TestWindowClassRegistration final {
public:
    ~TestWindowClassRegistration() {
        Reset();
    }

    TestWindowClassRegistration(const TestWindowClassRegistration&) = delete;
    TestWindowClassRegistration& operator=(
        const TestWindowClassRegistration&) = delete;

    TestWindowClassRegistration() = default;

    bool Register() noexcept {
        Reset();
        HMODULE module{};
        if (GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                    GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(&TestWindowProcedure),
                &module) == FALSE || module == nullptr) {
            return false;
        }

        instance_ = module;
        WNDCLASSW windowClass{};
        windowClass.lpfnWndProc = TestWindowProcedure;
        windowClass.hInstance = instance_;
        windowClass.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
        windowClass.hbrBackground = reinterpret_cast<HBRUSH>(
            GetStockObject(BLACK_BRUSH));
        windowClass.lpszClassName = TestWindowClassName;
        if (RegisterClassW(&windowClass) != 0) {
            active_ = true;
            return true;
        }
        if (GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
            instance_ = nullptr;
            return false;
        }

        // A prior load may have left the same local class behind if a caller
        // unloaded without completing RWUI_TestStop. Adopt it only when its
        // module identity and procedure both resolve to this exact DLL load;
        // never create a window through an arbitrary/stale callback.
        WNDCLASSW existing{};
        if (GetClassInfoW(instance_, TestWindowClassName, &existing) == FALSE ||
            existing.hInstance != instance_ ||
            existing.lpfnWndProc != TestWindowProcedure) {
            instance_ = nullptr;
            return false;
        }
        active_ = true;
        return true;
    }

    HINSTANCE Instance() const noexcept {
        return instance_;
    }

private:
    void Reset() noexcept {
        if (active_ && instance_ != nullptr) {
            UnregisterClassW(TestWindowClassName, instance_);
        }
        active_ = false;
        instance_ = nullptr;
    }

    HINSTANCE instance_{};
    bool active_{};
};

HWND CreateTestWindow(
    const HINSTANCE instance,
    const std::int32_t width,
    const std::int32_t height,
    const wchar_t* title) {
    if (instance == nullptr) return nullptr;
    RECT rectangle{0, 0, width, height};
    AdjustWindowRect(&rectangle, WS_OVERLAPPEDWINDOW, FALSE);
    return CreateWindowExW(
        0,
        TestWindowClassName,
        title == nullptr ? L"RageWebUI DirectX Harness" : title,
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        rectangle.right - rectangle.left,
        rectangle.bottom - rectangle.top,
        nullptr,
        nullptr,
        instance,
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
    TestWindowClassRegistration windowClass;
    if (!windowClass.Register()) {
        testRunning.store(false, std::memory_order_relaxed);
        CoUninitialize();
        return;
    }
    testWindow = CreateTestWindow(
        windowClass.Instance(), width, height, title.c_str());
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
    if (!created ||
        !g_compositor.Prepare(swapChain.Get(), queue.Get()) ||
        !g_inputQueue.Attach(testWindow)) {
        g_compositor.Reset();
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
    try {
        return rwui::StartTestSurface(api, width, height, title) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

RWUI_API void RWUI_CALL RWUI_TestStop() {
    try {
        rwui::StopTestSurface();
    } catch (...) {
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_TestIsRunning() {
    try {
        return rwui::IsTestSurfaceRunning() ? 1 : 0;
    } catch (...) {
        return 0;
    }
}
