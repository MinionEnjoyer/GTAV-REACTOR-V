#include "DirectXCompositor.h"

namespace rwui {

DirectXCompositor::DirectXCompositor(FrameMailbox& mailbox) : mailbox_(mailbox) {
}

bool DirectXCompositor::Render(
    IDXGISwapChain* swapChain,
    ID3D12CommandQueue* directQueue,
    const bool clearBeforeOverlay) {
    if (swapChain == nullptr) return false;
    std::scoped_lock lock(mutex_);

    if (activeSwapChain_ != swapChain) {
        Reset();
        activeSwapChain_ = swapChain;
    }

    if (api_ == RwuiRenderApi::None) {
        Microsoft::WRL::ComPtr<ID3D11Device> d3d11;
        if (SUCCEEDED(swapChain->GetDevice(IID_PPV_ARGS(&d3d11)))) {
            if (!InitializeD3D11(swapChain)) return false;
        } else {
            if (directQueue == nullptr || !InitializeD3D12(swapChain, directQueue)) return false;
        }
    }

    return api_ == RwuiRenderApi::Direct3D11
        ? RenderD3D11(swapChain, clearBeforeOverlay)
        : RenderD3D12(swapChain, clearBeforeOverlay);
}

void DirectXCompositor::BeforeResize(IDXGISwapChain* swapChain) {
    std::scoped_lock lock(mutex_);
    if (activeSwapChain_ == swapChain) Reset();
}

void DirectXCompositor::Reset() {
    renderer_.reset();
    wrappedBackBuffers_.clear();
    swapChain3_.Reset();
    d3d11On12Device_.Reset();
    d3d12Queue_.Reset();
    d3d12Device_.Reset();
    d3d11Context_.Reset();
    d3d11Device_.Reset();
    api_ = RwuiRenderApi::None;
    activeSwapChain_ = nullptr;
    width_ = 0;
    height_ = 0;
}

RwuiRenderStats DirectXCompositor::Stats() const {
    std::scoped_lock lock(mutex_);
    return RwuiRenderStats{
        api_,
        width_,
        height_,
        mailbox_.SubmittedFrames(),
        renderer_ == nullptr ? 0u : renderer_->RenderedFrames(),
        mailbox_.DroppedFrames(),
        renderer_ == nullptr ? 0u : renderer_->LastGeneration(),
    };
}

bool DirectXCompositor::InitializeD3D11(IDXGISwapChain* swapChain) {
    if (FAILED(swapChain->GetDevice(IID_PPV_ARGS(&d3d11Device_)))) return false;
    d3d11Device_->GetImmediateContext(&d3d11Context_);
    if (d3d11Context_ == nullptr) return false;
    renderer_ = std::make_unique<D3D11OverlayRenderer>(d3d11Device_.Get(), d3d11Context_.Get(), mailbox_);
    api_ = RwuiRenderApi::Direct3D11;
    return true;
}

bool DirectXCompositor::InitializeD3D12(
    IDXGISwapChain* swapChain,
    ID3D12CommandQueue* directQueue) {
    if (FAILED(swapChain->GetDevice(IID_PPV_ARGS(&d3d12Device_))) ||
        !SameDevice(d3d12Device_.Get(), directQueue) ||
        FAILED(swapChain->QueryInterface(IID_PPV_ARGS(&swapChain3_)))) {
        return false;
    }

    d3d12Queue_ = directQueue;
    IUnknown* queues[]{directQueue};
    D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_0};
    if (FAILED(D3D11On12CreateDevice(
            d3d12Device_.Get(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels,
            1,
            queues,
            1,
            0,
            &d3d11Device_,
            &d3d11Context_,
            nullptr)) ||
        FAILED(d3d11Device_.As(&d3d11On12Device_))) {
        return false;
    }

    DXGI_SWAP_CHAIN_DESC description{};
    if (FAILED(swapChain->GetDesc(&description))) return false;
    wrappedBackBuffers_.resize(description.BufferCount);
    D3D11_RESOURCE_FLAGS flags{};
    flags.BindFlags = D3D11_BIND_RENDER_TARGET;
    for (UINT index = 0; index < description.BufferCount; ++index) {
        Microsoft::WRL::ComPtr<ID3D12Resource> backBuffer;
        if (FAILED(swapChain->GetBuffer(index, IID_PPV_ARGS(&backBuffer))) ||
            FAILED(d3d11On12Device_->CreateWrappedResource(
                backBuffer.Get(),
                &flags,
                D3D12_RESOURCE_STATE_PRESENT,
                D3D12_RESOURCE_STATE_PRESENT,
                IID_PPV_ARGS(&wrappedBackBuffers_[index])))) {
            wrappedBackBuffers_.clear();
            return false;
        }
    }

    renderer_ = std::make_unique<D3D11OverlayRenderer>(d3d11Device_.Get(), d3d11Context_.Get(), mailbox_);
    api_ = RwuiRenderApi::Direct3D12;
    return true;
}

bool DirectXCompositor::RenderD3D11(IDXGISwapChain* swapChain, const bool clearBeforeOverlay) {
    Microsoft::WRL::ComPtr<ID3D11Texture2D> backBuffer;
    if (FAILED(swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer)))) return false;
    D3D11_TEXTURE2D_DESC description{};
    backBuffer->GetDesc(&description);
    width_ = static_cast<std::int32_t>(description.Width);
    height_ = static_cast<std::int32_t>(description.Height);
    return renderer_->Render(backBuffer.Get(), clearBeforeOverlay);
}

bool DirectXCompositor::RenderD3D12(IDXGISwapChain*, const bool clearBeforeOverlay) {
    const auto index = swapChain3_->GetCurrentBackBufferIndex();
    if (index >= wrappedBackBuffers_.size()) return false;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> backBuffer;
    if (FAILED(wrappedBackBuffers_[index].As(&backBuffer))) return false;
    D3D11_TEXTURE2D_DESC description{};
    backBuffer->GetDesc(&description);
    width_ = static_cast<std::int32_t>(description.Width);
    height_ = static_cast<std::int32_t>(description.Height);

    ID3D11Resource* resources[]{wrappedBackBuffers_[index].Get()};
    d3d11On12Device_->AcquireWrappedResources(resources, 1);
    const bool rendered = renderer_->Render(backBuffer.Get(), clearBeforeOverlay);
    d3d11On12Device_->ReleaseWrappedResources(resources, 1);
    d3d11Context_->Flush();
    return rendered;
}

bool DirectXCompositor::SameDevice(ID3D12Device* device, ID3D12CommandQueue* queue) {
    if (device == nullptr || queue == nullptr) return false;
    Microsoft::WRL::ComPtr<ID3D12Device> queueDevice;
    if (FAILED(queue->GetDevice(IID_PPV_ARGS(&queueDevice)))) return false;
    Microsoft::WRL::ComPtr<IUnknown> first;
    Microsoft::WRL::ComPtr<IUnknown> second;
    device->QueryInterface(IID_PPV_ARGS(&first));
    queueDevice->QueryInterface(IID_PPV_ARGS(&second));
    return first.Get() == second.Get();
}

} // namespace rwui

