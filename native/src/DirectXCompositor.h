#pragma once

#include "D3D11OverlayRenderer.h"
#include "FrameMailbox.h"
#include "RageWebUI.Native.h"

#include <d3d11on12.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <memory>
#include <mutex>
#include <vector>
#include <wrl/client.h>

namespace rwui {

class DirectXCompositor final {
public:
    explicit DirectXCompositor(FrameMailbox& mailbox);
    bool Render(IDXGISwapChain* swapChain, ID3D12CommandQueue* directQueue, bool clearBeforeOverlay = false);
    void BeforeResize(IDXGISwapChain* swapChain);
    void Reset();
    RwuiRenderStats Stats() const;

private:
    bool InitializeD3D11(IDXGISwapChain* swapChain);
    bool InitializeD3D12(IDXGISwapChain* swapChain, ID3D12CommandQueue* directQueue);
    bool RenderD3D11(IDXGISwapChain* swapChain, bool clearBeforeOverlay);
    bool RenderD3D12(IDXGISwapChain* swapChain, bool clearBeforeOverlay);
    static bool SameDevice(ID3D12Device* device, ID3D12CommandQueue* queue);

    FrameMailbox& mailbox_;
    mutable std::mutex mutex_;
    RwuiRenderApi api_{RwuiRenderApi::None};
    IDXGISwapChain* activeSwapChain_{};
    Microsoft::WRL::ComPtr<ID3D11Device> d3d11Device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> d3d11Context_;
    Microsoft::WRL::ComPtr<ID3D12Device> d3d12Device_;
    Microsoft::WRL::ComPtr<ID3D12CommandQueue> d3d12Queue_;
    Microsoft::WRL::ComPtr<ID3D11On12Device> d3d11On12Device_;
    Microsoft::WRL::ComPtr<IDXGISwapChain3> swapChain3_;
    std::vector<Microsoft::WRL::ComPtr<ID3D11Resource>> wrappedBackBuffers_;
    std::unique_ptr<D3D11OverlayRenderer> renderer_;
    std::int32_t width_{};
    std::int32_t height_{};
};

} // namespace rwui

