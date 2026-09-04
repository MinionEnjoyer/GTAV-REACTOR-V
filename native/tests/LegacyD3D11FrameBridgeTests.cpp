#include "LegacyD3D11FrameBridge.h"
#include "D3D11OverlayRenderer.h"
#include <Windows.h>
#include <array>
#include <chrono>
#include <iostream>
#include <vector>
#include <string_view>

using Microsoft::WRL::ComPtr;
using namespace rwui::transport;
namespace {
struct FullscreenDisplay {
    HWND window{};
    ComPtr<IDXGISwapChain> swap;
    ~FullscreenDisplay() {
        if (swap) { swap->SetFullscreenState(FALSE, nullptr); swap.Reset(); }
        if (window) DestroyWindow(window);
    }
    bool Open(ID3D11Device* device) {
        WNDCLASSW wc{};
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"ReactorV.LegacyBridge.Qualification";
        RegisterClassW(&wc);
        window = CreateWindowW(wc.lpszClassName, L"Reactor V Legacy GPU bridge test",
            WS_OVERLAPPEDWINDOW, 0, 0, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);
        if (!window) return false;
        ShowWindow(window, SW_SHOW);
        SetForegroundWindow(window);
        for (unsigned i = 0; i < 25; ++i) {
            MSG msg{};
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                TranslateMessage(&msg); DispatchMessageW(&msg);
            }
            Sleep(10);
        }
        ComPtr<IDXGIDevice> dxgi;
        ComPtr<IDXGIAdapter> adapter;
        ComPtr<IDXGIFactory> factory;
        if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgi))) ||
            FAILED(dxgi->GetAdapter(&adapter)) ||
            FAILED(adapter->GetParent(IID_PPV_ARGS(&factory)))) return false;
        DXGI_SWAP_CHAIN_DESC desc{};
        desc.BufferDesc.Width = 1280; desc.BufferDesc.Height = 720;
        desc.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 1; desc.OutputWindow = window;
        desc.Windowed = TRUE; desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
        desc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;
        if (FAILED(factory->CreateSwapChain(device, &desc, &swap))) return false;
        const HRESULT set = swap->SetFullscreenState(TRUE, nullptr);
        const HRESULT resize = SUCCEEDED(set) ? swap->ResizeBuffers(0,1280,720,
            DXGI_FORMAT_UNKNOWN, DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH) : set;
        BOOL fullscreen{};
        const HRESULT query = swap->GetFullscreenState(&fullscreen, nullptr);
        std::cout << "fullscreen=" << fullscreen << " set_hr=0x" << std::hex << set
            << " resize_hr=0x" << resize << std::dec << '\n';
        return SUCCEEDED(set) && SUCCEEDED(resize) && SUCCEEDED(query) && fullscreen;
    }
};
int failures{};
void Check(bool ok, const char* label) {
    if (!ok) { ++failures; std::cerr << "FAIL: " << label << '\n'; }
}
bool PixelMatches(ID3D11Device* device, ID3D11DeviceContext* context,
    ID3D11Texture2D* texture, std::array<int, 4> expected, int tolerance = 0) {
    D3D11_TEXTURE2D_DESC desc{};
    texture->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = desc.MiscFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> readback;
    if (FAILED(device->CreateTexture2D(&desc, nullptr, &readback))) return false;
    context->CopyResource(readback.Get(), texture);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(readback.Get(), 0, D3D11_MAP_READ, 0, &mapped))) return false;
    bool matches = true;
    for (UINT y : {0u, desc.Height - 1}) for (UINT x : {0u, desc.Width - 1}) {
        const auto* pixel = static_cast<const unsigned char*>(mapped.pData) +
            static_cast<size_t>(y) * mapped.RowPitch + x * 4;
        for (unsigned channel = 0; channel < 4; ++channel)
            matches &= std::abs(static_cast<int>(pixel[channel]) - expected[channel]) <= tolerance;
    }
    context->Unmap(readback.Get(), 0);
    return matches;
}
}

int main(int argc, char** argv) {
    ComPtr<ID3D11Device> game;
    ComPtr<ID3D11DeviceContext> gameContext;
    const D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_0};
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
        0, levels, 1, D3D11_SDK_VERSION, &game, nullptr, &gameContext))) return 125;
    FullscreenDisplay display;
    if (argc == 2 && std::string_view(argv[1]) == "--fullscreen" && !display.Open(game.Get())) {
        std::cerr << "FAIL: fullscreen unavailable; no windowed substitute\n";
        return 1;
    }
    LegacyD3D11FrameBridge bridge;
    Check(FAILED(bridge.Initialize(nullptr)), "null game rejected");
    Check(bridge.Initialize(game.Get()) == S_OK, "peer initialized on exact game adapter");
    if (!bridge.ImportDevice()) return 1;
    rwui::FrameMailbox mailbox;
    rwui::D3D11OverlayRenderer renderer(game.Get(), gameContext.Get(), mailbox);
    struct Event { HANDLE value{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
        ~Event() { if (value) CloseHandle(value); } } stop;
    Check(stop.value != nullptr, "cancellation event available");
    for (auto size : {std::array<UINT,2>{2,2}, {1280,720}, {2560,1440}, {640,360}}) {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = size[0]; desc.Height = size[1];
        desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        std::vector<UINT> pixels(static_cast<size_t>(desc.Width) * desc.Height, 0x80604020);
        D3D11_SUBRESOURCE_DATA initial{pixels.data(), desc.Width * 4, 0};
        ComPtr<ID3D11Texture2D> source;
        Check(SUCCEEDED(bridge.ImportDevice()->CreateTexture2D(&desc, &initial, &source)),
            "create peer source");
        if (!source) return 1;
        Check(bridge.StageSource(source.Get(), stop.value) == S_OK, "bridge resized/source staged");
        if (!bridge.GameTexture()) {
            std::cerr << "bridge_stage=" << bridge.Stage() << " hr=0x" << std::hex << bridge.LastHresult();
            return 1;
        }
        Check(PixelMatches(game.Get(), gameContext.Get(), bridge.GameTexture(), {32,64,96,128}),
            "GPU copy preserves BGRA and partial alpha at all sizes");
        ComPtr<ID3D11ShaderResourceView> view;
        Check(SUCCEEDED(game->CreateShaderResourceView(bridge.GameTexture(), nullptr, &view)), "game SRV");
        desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> backBuffer;
        Check(display.swap ? SUCCEEDED(display.swap->GetBuffer(0, IID_PPV_ARGS(&backBuffer))) :
            SUCCEEDED(game->CreateTexture2D(&desc, nullptr, &backBuffer)), "render target");
        Check(renderer.PrepareBackBuffer(backBuffer.Get()) &&
            renderer.RenderShared(backBuffer.Get(), view.Get(), 1, true), "real overlay renderer draws bridge");
        Check(PixelMatches(game.Get(), gameContext.Get(), backBuffer.Get(), {36,67,98,255}, 1),
            "premultiplied-alpha blending preserves visible color");
        if (display.swap) Check(SUCCEEDED(display.swap->Present(1, 0)), "fullscreen Present");
        gameContext->Flush();
        Check(bridge.ReleaseGame() == S_OK && bridge.GameTexture() == nullptr, "release game lease");
        Check(FAILED(bridge.ReleaseGame()), "double release rejected");
        renderer.InvalidateBackBuffer();
        // Warm up lazy driver submission objects, then measure steady-state
        // handle growth over a separate, longer batch. First-use driver handles
        // are not per-frame resource leaks (the initial batch is logged too).
        DWORD before{}, after{};
        GetProcessHandleCount(GetCurrentProcess(), &before);
        ID3D11Texture2D* stable{};
        for (unsigned frame = 0; frame < 40; ++frame) {
            Check(bridge.StageSource(source.Get(), stop.value) == S_OK, "repeat stage");
            if (!frame) stable = bridge.GameTexture();
            Check(stable == bridge.GameTexture(), "steady-state intermediate reused");
            Check(bridge.ReleaseGame() == S_OK, "repeat release");
        }
        GetProcessHandleCount(GetCurrentProcess(), &after);
        std::cout << "handles size=" << desc.Width << "x" << desc.Height
            << " cold=" << before << " warm=" << after;
        before = after;
        for (unsigned frame = 0; frame < 160; ++frame) {
            Check(bridge.StageSource(source.Get(), stop.value) == S_OK, "steady-state stage");
            Check(stable == bridge.GameTexture(), "steady-state intermediate identity");
            Check(bridge.ReleaseGame() == S_OK, "steady-state release");
        }
        GetProcessHandleCount(GetCurrentProcess(), &after);
        std::cout << " steady=" << after << '\n';
        Check(after <= before + 2, "no per-frame handle leak");
        SetEvent(stop.value);
        const auto start = std::chrono::steady_clock::now();
        Check(bridge.StageSource(source.Get(), stop.value) == HRESULT_FROM_WIN32(ERROR_CANCELLED),
            "shutdown cancels worker acquisition");
        Check(std::chrono::steady_clock::now() - start < std::chrono::milliseconds(200), "bounded cancellation");
        Check(bridge.GameTexture() == nullptr, "cancelled frame cannot be published");
        ResetEvent(stop.value);
        Check(bridge.StageSource(source.Get(), stop.value) == S_OK, "recovers after cancellation");
        Check(bridge.ReleaseGame() == S_OK, "release recovered frame");
        Check(FAILED(bridge.StageSource(backBuffer.Get(), stop.value)), "wrong-device source rejected");
    }
    bridge.Reset();
    Check(bridge.ImportDevice() == nullptr && bridge.GameTexture() == nullptr, "retirement releases resources");
    Check(FAILED(bridge.StageSource(nullptr, stop.value)), "uninitialized bridge fails closed");
    std::cout << (failures ? "FAIL" : "PASS") << ": Legacy GPU bridge resize, pixels, alpha, draw, reuse and cancellation\n";
    return failures ? 1 : 0;
}
