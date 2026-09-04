#include "LegacyD3D11FrameBridge.h"
#include "ReactorV.SharedGpuFrame.h"
#include <Windows.h>

namespace rwui::transport {
namespace {
using Microsoft::WRL::ComPtr;

// All retries run on the receiver worker, outside the game-context/Present
// gate. WAIT_TIMEOUT/WAIT_ABANDONED are positive: SUCCEEDED is NOT sufficient.
HRESULT AcquireBounded(IDXGIKeyedMutex* mutex, UINT64 key, HANDLE stop) noexcept {
    for (unsigned attempt = 0; attempt < 25; ++attempt) {
        if (stop && WaitForSingleObject(stop, 0) == WAIT_OBJECT_0)
            return HRESULT_FROM_WIN32(ERROR_CANCELLED);
        const HRESULT hr = mutex->AcquireSync(key, 0);
        if (hr == S_OK) return S_OK;
        if (hr != WAIT_TIMEOUT) return hr == WAIT_ABANDONED
            ? HRESULT_FROM_WIN32(WAIT_ABANDONED) : hr;
        if (stop) {
            if (WaitForSingleObject(stop, 2) == WAIT_OBJECT_0)
                return HRESULT_FROM_WIN32(ERROR_CANCELLED);
        } else Sleep(2);
    }
    return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
}
}

HRESULT LegacyD3D11FrameBridge::Record(
    LegacyD3D11BridgeStage stage, HRESULT result) noexcept {
    stage_.store(static_cast<std::uint32_t>(stage));
    lastHresult_.store(static_cast<std::uint32_t>(result));
    return result;
}

void LegacyD3D11FrameBridge::ResetTexture() noexcept {
    if (gameAcquired_ && gameMutex_) gameMutex_->ReleaseSync(0);
    gameAcquired_ = false;
    peerMutex_.Reset();
    gameMutex_.Reset();
    peerTexture_.Reset();
    gameTexture_.Reset();
}

void LegacyD3D11FrameBridge::Reset() noexcept {
    ResetTexture();
    peerContext_.Reset();
    peerDevice_.Reset();
    gameDevice_.Reset();
    Record(LegacyD3D11BridgeStage::Idle, S_OK);
}

HRESULT LegacyD3D11FrameBridge::Initialize(ID3D11Device* gameDevice) noexcept {
    Reset();
    if (!gameDevice || (gameDevice->GetCreationFlags() & D3D11_CREATE_DEVICE_SINGLETHREADED))
        return Record(LegacyD3D11BridgeStage::PeerDevice, E_INVALIDARG);
    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    HRESULT hr = gameDevice->QueryInterface(IID_PPV_ARGS(&dxgi));
    if (SUCCEEDED(hr)) hr = dxgi->GetAdapter(&adapter);
    if (FAILED(hr)) return Record(LegacyD3D11BridgeStage::PeerDevice, hr);
    // No default-adapter selection: use exactly the game's adapter.
    hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
        &peerDevice_, nullptr, &peerContext_);
    if (FAILED(hr)) return Record(LegacyD3D11BridgeStage::PeerDevice, hr);
    gameDevice_ = gameDevice;
    return Record(LegacyD3D11BridgeStage::Ready, S_OK);
}

HRESULT LegacyD3D11FrameBridge::PrepareTexture(ID3D11Texture2D* source) noexcept {
    if (!source || !gameDevice_ || !peerDevice_ || gameAcquired_)
        return Record(LegacyD3D11BridgeStage::InvalidSource, E_INVALIDARG);
    ComPtr<ID3D11Device> sourceDevice;
    source->GetDevice(&sourceDevice);
    ComPtr<IUnknown> sourceIdentity, peerIdentity;
    if (!sourceDevice || FAILED(sourceDevice.As(&sourceIdentity)) ||
        FAILED(peerDevice_.As(&peerIdentity)) || sourceIdentity.Get() != peerIdentity.Get())
        return Record(LegacyD3D11BridgeStage::InvalidSource, E_INVALIDARG);
    D3D11_TEXTURE2D_DESC desc{}, cached{};
    source->GetDesc(&desc);
    if (!desc.Width || !desc.Height || desc.Width > SharedGpuFrameMaximumDimension ||
        desc.Height > SharedGpuFrameMaximumDimension || desc.MipLevels != 1 ||
        desc.ArraySize != 1 || desc.SampleDesc.Count != 1 ||
        (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM &&
         desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB))
        return Record(LegacyD3D11BridgeStage::InvalidSource, E_INVALIDARG);
    if (gameTexture_) gameTexture_->GetDesc(&cached);
    if (gameTexture_ && cached.Width == desc.Width && cached.Height == desc.Height &&
        cached.Format == desc.Format) return S_OK;
    ResetTexture();
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    desc.CPUAccessFlags = 0;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX; // deliberately NOT NTHANDLE
    HRESULT hr = gameDevice_->CreateTexture2D(&desc, nullptr, &gameTexture_);
    if (FAILED(hr)) return Record(LegacyD3D11BridgeStage::GameTexture, hr);
    ComPtr<IDXGIResource> resource;
    hr = gameTexture_.As(&resource);
    HANDLE handle{};
    if (SUCCEEDED(hr)) hr = resource->GetSharedHandle(&handle);
    if (FAILED(hr)) { ResetTexture(); return Record(LegacyD3D11BridgeStage::SharedHandle, hr); }
    // KMT handle lifetime is owned by gameTexture_: never CloseHandle or
    // DuplicateHandle it, never reinterpret the incoming NT handle as KMT.
    hr = peerDevice_->OpenSharedResource(handle, IID_PPV_ARGS(&peerTexture_));
    if (FAILED(hr)) { ResetTexture(); return Record(LegacyD3D11BridgeStage::PeerOpen, hr); }
    hr = gameTexture_.As(&gameMutex_);
    if (SUCCEEDED(hr)) hr = peerTexture_.As(&peerMutex_);
    if (FAILED(hr)) { ResetTexture(); return Record(LegacyD3D11BridgeStage::KeyedMutex, hr); }
    return Record(LegacyD3D11BridgeStage::Ready, S_OK);
}

HRESULT LegacyD3D11FrameBridge::StageSource(ID3D11Texture2D* source, HANDLE stop) noexcept {
    HRESULT hr = PrepareTexture(source);
    if (FAILED(hr)) return hr;
    hr = AcquireBounded(peerMutex_.Get(), 0, stop);
    if (hr != S_OK) { ResetTexture(); return Record(LegacyD3D11BridgeStage::PeerAcquire, hr); }
    peerContext_->CopyResource(peerTexture_.Get(), source);
    peerContext_->Flush();
    hr = peerMutex_->ReleaseSync(1);
    if (hr != S_OK) { ResetTexture(); return Record(LegacyD3D11BridgeStage::PeerRelease, hr); }
    hr = peerDevice_->GetDeviceRemovedReason();
    if (FAILED(hr)) { ResetTexture(); return Record(LegacyD3D11BridgeStage::DeviceRemoved, hr); }
    hr = AcquireBounded(gameMutex_.Get(), 1, stop);
    if (hr != S_OK) { ResetTexture(); return Record(LegacyD3D11BridgeStage::GameAcquire, hr); }
    gameAcquired_ = true;
    return Record(LegacyD3D11BridgeStage::Ready, S_OK);
}

HRESULT LegacyD3D11FrameBridge::ReleaseGame() noexcept {
    if (!gameAcquired_ || !gameMutex_)
        return Record(LegacyD3D11BridgeStage::GameRelease, E_UNEXPECTED);
    HRESULT hr = gameMutex_->ReleaseSync(0);
    gameAcquired_ = false;
    if (hr != S_OK) ResetTexture();
    return Record(LegacyD3D11BridgeStage::GameRelease, hr);
}
} // namespace rwui::transport
