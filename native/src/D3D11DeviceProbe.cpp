#include "D3D11DeviceProbe.h"
#include <d3d11_1.h>
#include <wrl/client.h>

namespace rwui {
namespace {
using Microsoft::WRL::ComPtr;

HRESULT OpenKnownTexture(ID3D11Device* producer, ID3D11Device1* consumer,
    DXGI_FORMAT format, UINT bindFlags) noexcept {
    if (!producer || !consumer) return E_NOINTERFACE;
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = desc.Height = 16;
    desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
    desc.Format = format;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = bindFlags;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE |
        D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    ComPtr<ID3D11Texture2D> texture;
    auto hr = producer->CreateTexture2D(&desc, nullptr, &texture);
    if (FAILED(hr)) return hr;
    ComPtr<IDXGIResource1> resource;
    hr = texture.As(&resource);
    if (FAILED(hr)) return hr;
    HANDLE shared{};
    hr = resource->CreateSharedHandle(nullptr,
        DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &shared);
    if (FAILED(hr)) return hr;
    // Use the same NT handle contract as the production importer. The known
    // resource stays alive through import; no CEF or transient handles involved.
    ComPtr<ID3D11Texture2D> imported;
    hr = consumer->OpenSharedResource1(shared, IID_PPV_ARGS(&imported));
    CloseHandle(shared);
    return hr;
}
}

RwuiD3D11DeviceDiagnostics ProbeD3D11Device(ID3D11Device* device, bool allocate) noexcept {
    RwuiD3D11DeviceDiagnostics result{};
    result.byteSize = sizeof(result);
    result.majorVersion = 1;
    result.device1Hresult = result.peerDeviceHresult = result.localBgraHresult =
        result.sharedBgraHresult = result.sharedRgbaHresult =
        result.sharedBgraRenderTargetHresult = static_cast<UINT>(E_PENDING);
    result.fullscreenHresult = static_cast<UINT>(E_PENDING);
    if (!device) return result;
    result.featureLevel = device->GetFeatureLevel();
    result.creationFlags = device->GetCreationFlags();
    device->CheckFormatSupport(DXGI_FORMAT_B8G8R8A8_UNORM, &result.bgraSupport);
    device->CheckFormatSupport(DXGI_FORMAT_R8G8B8A8_UNORM, &result.rgbaSupport);
    ComPtr<ID3D11Device1> device1;
    result.device1Hresult = static_cast<UINT>(device->QueryInterface(IID_PPV_ARGS(&device1)));
    ComPtr<IDXGIDevice> dxgiDevice;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC desc{};
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice))) ||
        FAILED(dxgiDevice->GetAdapter(&adapter)) || FAILED(adapter->GetDesc(&desc))) return result;
    result.adapterHigh = static_cast<UINT>(desc.AdapterLuid.HighPart);
    result.adapterLow = desc.AdapterLuid.LowPart;
    result.vendorId = desc.VendorId;
    result.deviceId = desc.DeviceId;
    // A diagnostic worker must not allocate on a SINGLETHREADED game device.
    // Keep the identity evidence and explicitly report the probe as not run.
    if (!allocate || (result.creationFlags & D3D11_CREATE_DEVICE_SINGLETHREADED) != 0) return result;
    D3D11_TEXTURE2D_DESC local{};
    local.Width = local.Height = 16;
    local.MipLevels = local.ArraySize = local.SampleDesc.Count = 1;
    local.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    local.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> localTexture;
    result.localBgraHresult = static_cast<UINT>(device->CreateTexture2D(&local, nullptr, &localTexture));
    ComPtr<ID3D11Device> peer;
    D3D_FEATURE_LEVEL peerLevel{};
    result.peerDeviceHresult = static_cast<UINT>(D3D11CreateDevice(adapter.Get(),
        D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, &peer, &peerLevel, nullptr));
    result.peerFeatureLevel = peerLevel;
    if (FAILED(static_cast<HRESULT>(result.peerDeviceHresult))) return result;
    result.sharedBgraHresult = static_cast<UINT>(OpenKnownTexture(peer.Get(), device1.Get(),
        DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_BIND_SHADER_RESOURCE));
    result.sharedRgbaHresult = static_cast<UINT>(OpenKnownTexture(peer.Get(), device1.Get(),
        DXGI_FORMAT_R8G8B8A8_UNORM, D3D11_BIND_SHADER_RESOURCE));
    result.sharedBgraRenderTargetHresult = static_cast<UINT>(OpenKnownTexture(peer.Get(), device1.Get(),
        DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET));
    result.probeComplete = 1;
    return result;
}

void DescribeD3D11SwapChain(IDXGISwapChain* swapChain,
    RwuiD3D11DeviceDiagnostics& result) noexcept {
    if (!swapChain) return;
    DXGI_SWAP_CHAIN_DESC desc{};
    if (SUCCEEDED(swapChain->GetDesc(&desc))) {
        result.swapEffect = desc.SwapEffect;
        result.swapFlags = desc.Flags;
        result.backBufferFormat = desc.BufferDesc.Format;
        result.width = desc.BufferDesc.Width;
        result.height = desc.BufferDesc.Height;
        result.sampleCount = desc.SampleDesc.Count;
    }
    BOOL fullscreen{};
    result.fullscreenHresult = static_cast<UINT>(swapChain->GetFullscreenState(&fullscreen, nullptr));
    result.fullscreen = fullscreen ? 1u : 0u;
}
}
