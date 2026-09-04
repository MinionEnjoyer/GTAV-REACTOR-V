#include <Windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <array>
#include <cstdint>
#include <cstdlib>
#include <iomanip>
#include <iostream>

using Microsoft::WRL::ComPtr;

namespace {

LRESULT CALLBACK WindowProcedure(
    HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    return DefWindowProcW(window, message, wParam, lParam);
}

HWND CreateTestWindow() {
    static constexpr wchar_t ClassName[] = L"ReactorV.D3D12.Resize.Control";
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = ClassName;
    RegisterClassW(&windowClass);
    return CreateWindowExW(
        0, ClassName, L"ReactorV bare D3D12 resize control",
        WS_OVERLAPPEDWINDOW, 0, 0, 640, 360, nullptr, nullptr,
        windowClass.hInstance, nullptr);
}

void EnableDiagnostics() {
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
}

void DumpDiagnostics(ID3D12Device* device) {
    if (device == nullptr) return;
    std::cerr << " removedReason=0x" << std::hex
              << static_cast<std::uint32_t>(device->GetDeviceRemovedReason())
              << std::dec << '\n';
    ComPtr<ID3D12InfoQueue> infoQueue;
    if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&infoQueue)))) {
        const auto count = infoQueue->GetNumStoredMessagesAllowedByRetrievalFilter();
        const auto first = count > 32 ? count - 32 : 0;
        for (UINT64 index = first; index < count; ++index) {
            SIZE_T bytes{};
            infoQueue->GetMessage(index, nullptr, &bytes);
            if (bytes == 0) continue;
            auto* storage = new std::uint8_t[bytes];
            auto* message = reinterpret_cast<D3D12_MESSAGE*>(storage);
            if (SUCCEEDED(infoQueue->GetMessage(index, message, &bytes))) {
                std::cerr << "D3D12 id=" << message->ID << " severity="
                          << message->Severity << " "
                          << (message->pDescription == nullptr
                                  ? "<no description>"
                                  : message->pDescription)
                          << '\n';
            }
            delete[] storage;
        }
    }
    ComPtr<ID3D12DeviceRemovedExtendedData> dred;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dred)))) return;
    D3D12_DRED_AUTO_BREADCRUMBS_OUTPUT breadcrumbs{};
    if (SUCCEEDED(dred->GetAutoBreadcrumbsOutput(&breadcrumbs))) {
        for (auto* node = breadcrumbs.pHeadAutoBreadcrumbNode;
             node != nullptr; node = node->pNext) {
            std::cerr << "DRED queue="
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
}

bool CreateDeviceAndQueues(
    IDXGIFactory6* factory,
    bool useWarp,
    ID3D12Device** deviceResult,
    ID3D12CommandQueue** firstQueueResult,
    ID3D12CommandQueue** secondQueueResult) {
    if (useWarp) {
        ComPtr<IDXGIAdapter> warp;
        if (FAILED(factory->EnumWarpAdapter(IID_PPV_ARGS(&warp))) ||
            FAILED(D3D12CreateDevice(
                warp.Get(), D3D_FEATURE_LEVEL_11_0,
                IID_PPV_ARGS(deviceResult)))) return false;
    } else {
        ComPtr<IDXGIAdapter1> adapter;
        for (UINT index = 0;
             factory->EnumAdapterByGpuPreference(
                 index, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                 IID_PPV_ARGS(&adapter)) != DXGI_ERROR_NOT_FOUND;
             ++index) {
            DXGI_ADAPTER_DESC1 description{};
            adapter->GetDesc1(&description);
            if ((description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0 &&
                SUCCEEDED(D3D12CreateDevice(
                    adapter.Get(), D3D_FEATURE_LEVEL_11_0,
                    IID_PPV_ARGS(deviceResult)))) break;
            adapter.Reset();
        }
    }
    if (*deviceResult == nullptr) return false;
    D3D12_COMMAND_QUEUE_DESC description{};
    description.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    return SUCCEEDED((*deviceResult)->CreateCommandQueue(
               &description, IID_PPV_ARGS(firstQueueResult))) &&
        SUCCEEDED((*deviceResult)->CreateCommandQueue(
               &description, IID_PPV_ARGS(secondQueueResult)));
}

bool CreateSwapChain(
    IDXGIFactory6* factory,
    ID3D12CommandQueue* queue,
    HWND window,
    IDXGISwapChain3** result) {
    DXGI_SWAP_CHAIN_DESC1 description{};
    description.Width = 640;
    description.Height = 360;
    description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = 2;
    description.SampleDesc.Count = 1;
    description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    ComPtr<IDXGISwapChain1> swapChain;
    return SUCCEEDED(factory->CreateSwapChainForHwnd(
               queue, window, &description, nullptr, nullptr, &swapChain)) &&
        SUCCEEDED(swapChain->QueryInterface(IID_PPV_ARGS(result)));
}

bool Check(
    HRESULT result,
    const char* operation,
    UINT cycle,
    ID3D12Device* device) {
    if (SUCCEEDED(result)) return true;
    std::cerr << "FAIL cycle=" << cycle << " operation=" << operation
              << " HRESULT=0x" << std::hex
              << static_cast<std::uint32_t>(result) << std::dec;
    DumpDiagnostics(device);
    return false;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    UINT cycles = 100;
    bool diagnostics{};
    bool useWarp{};
    for (int index = 1; index < argc; ++index) {
        if (wcscmp(argv[index], L"--debug") == 0) diagnostics = true;
        else if (wcscmp(argv[index], L"--warp") == 0) useWarp = true;
        else cycles = static_cast<UINT>(_wtoi(argv[index]));
    }
    if (diagnostics) EnableDiagnostics();

    const HWND window = CreateTestWindow();
    ComPtr<IDXGIFactory6> factory;
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> firstQueue;
    ComPtr<ID3D12CommandQueue> secondQueue;
    ComPtr<IDXGISwapChain3> swapChain;
    if (window == nullptr ||
        FAILED(CreateDXGIFactory2(
            diagnostics ? DXGI_CREATE_FACTORY_DEBUG : 0,
            IID_PPV_ARGS(&factory))) ||
        !CreateDeviceAndQueues(
            factory.Get(), useWarp, &device, &firstQueue, &secondQueue) ||
        !CreateSwapChain(
            factory.Get(), firstQueue.Get(), window, &swapChain)) {
        std::cerr << "FAIL setup\n";
        if (window != nullptr) DestroyWindow(window);
        return 2;
    }

    constexpr UINT BufferCount = 2;
    const UINT nodeMasks[BufferCount]{};
    IUnknown* uniformQueues[BufferCount]{
        firstQueue.Get(), firstQueue.Get()};
    IUnknown* mixedQueues[BufferCount]{
        firstQueue.Get(), secondQueue.Get()};
    DXGI_PRESENT_PARAMETERS parameters{};

    for (UINT cycle = 0; cycle < cycles; ++cycle) {
        const UINT offset = cycle % 16;
        if (!Check(swapChain->ResizeBuffers1(
                BufferCount, 800 + offset, 450 + offset,
                DXGI_FORMAT_R8G8B8A8_UNORM, 0, nodeMasks, uniformQueues),
                "uniform ResizeBuffers1", cycle, device.Get()) ||
            !Check(swapChain->Present(0, 0),
                "Present after uniform", cycle, device.Get()) ||
            !Check(swapChain->ResizeBuffers1(
                BufferCount, 820 + offset, 460 + offset,
                DXGI_FORMAT_R8G8B8A8_UNORM, 0, nodeMasks, mixedQueues),
                "mixed ResizeBuffers1", cycle, device.Get()) ||
            !Check(swapChain->Present(0, 0),
                "Present after mixed", cycle, device.Get()) ||
            !Check(swapChain->ResizeBuffers1(
                BufferCount, 840 + offset, 472 + offset,
                DXGI_FORMAT_R8G8B8A8_UNORM, 0, nodeMasks, uniformQueues),
                "uniform recovery ResizeBuffers1", cycle, device.Get()) ||
            !Check(swapChain->Present1(0, 0, &parameters),
                "Present1 after uniform recovery", cycle, device.Get()) ||
            !Check(swapChain->ResizeBuffers(
                BufferCount, 960 + offset, 540 + offset,
                DXGI_FORMAT_R8G8B8A8_UNORM, 0),
                "ordinary ResizeBuffers", cycle, device.Get()) ||
            !Check(swapChain->Present1(0, 0, &parameters),
                "Present1 after ordinary", cycle, device.Get())) {
            DestroyWindow(window);
            return 1;
        }
    }

    std::cout << "PASS bare D3D12 resize control cycles=" << cycles
              << " diagnostics=" << diagnostics
              << " warp=" << useWarp << '\n';
    swapChain.Reset();
    secondQueue.Reset();
    firstQueue.Reset();
    device.Reset();
    factory.Reset();
    DestroyWindow(window);
    return 0;
}
