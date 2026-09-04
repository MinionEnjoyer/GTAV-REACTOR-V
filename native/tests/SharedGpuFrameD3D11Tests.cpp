#include "SharedGpuFrameD3D11.h"

#include <Windows.h>
#include <array>
#include <cstdint>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <iostream>
#include <wrl/client.h>

namespace {

int failures = 0;

void Check(const bool condition, const char* const message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        ++failures;
    }
}

class UniqueHandle final {
public:
    ~UniqueHandle() {
        if (value != nullptr && value != INVALID_HANDLE_VALUE) {
            CloseHandle(value);
        }
    }
    HANDLE value{};
};

bool CreateWarpDevice(
    Microsoft::WRL::ComPtr<ID3D11Device>& device,
    Microsoft::WRL::ComPtr<ID3D11DeviceContext>& context) {
    D3D_FEATURE_LEVEL featureLevel{};
    return SUCCEEDED(D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_WARP,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &device,
        &featureLevel,
        &context));
}

} // namespace

int main() {
    using namespace rwui::transport;

    WindowsProcessIdentity processIdentity{};
    Check(QueryWindowsProcessIdentity(GetCurrentProcessId(), processIdentity),
        "current process identity includes a nonzero creation time");
    if (processIdentity.creationTime == 0) return 1;

    Microsoft::WRL::ComPtr<ID3D11Device> producerDevice;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> producerContext;
    Microsoft::WRL::ComPtr<ID3D11Device> consumerDevice;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> consumerContext;
    if (!CreateWarpDevice(producerDevice, producerContext) ||
        !CreateWarpDevice(consumerDevice, consumerContext)) {
        std::cout << "SKIP: D3D11 WARP is unavailable\n";
        return 125;
    }

    constexpr UINT width = 2;
    constexpr UINT height = 2;
    D3D11_TEXTURE2D_DESC textureDescription{};
    textureDescription.Width = width;
    textureDescription.Height = height;
    textureDescription.MipLevels = 1;
    textureDescription.ArraySize = 1;
    textureDescription.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    textureDescription.SampleDesc.Count = 1;
    textureDescription.Usage = D3D11_USAGE_DEFAULT;
    textureDescription.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    textureDescription.MiscFlags =
        D3D11_RESOURCE_MISC_SHARED_NTHANDLE |
        D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;

    Microsoft::WRL::ComPtr<ID3D11Texture2D> producerTexture;
    Check(SUCCEEDED(producerDevice->CreateTexture2D(
        &textureDescription, nullptr, &producerTexture)),
        "producer creates a bounded NT-handle keyed-mutex texture");
    if (producerTexture == nullptr) return 1;

    Microsoft::WRL::ComPtr<IDXGIResource1> sharedResource;
    Microsoft::WRL::ComPtr<IDXGIKeyedMutex> producerMutex;
    Check(SUCCEEDED(producerTexture.As(&sharedResource)) &&
        SUCCEEDED(producerTexture.As(&producerMutex)),
        "producer texture exposes resource sharing and keyed synchronization");
    if (sharedResource == nullptr || producerMutex == nullptr) return 1;

    UniqueHandle sharedTextureHandle;
    Check(SUCCEEDED(sharedResource->CreateSharedHandle(
        nullptr,
        DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
        nullptr,
        &sharedTextureHandle.value)),
        "producer creates an unnamed producer-local NT handle");
    if (sharedTextureHandle.value == nullptr) return 1;

    SharedGpuFrameValidationContext validation{
        processIdentity.processId,
        processIdentity.processId,
        processIdentity.creationTime,
        processIdentity.creationTime,
        0x0123456789abcdefull,
        0xfedcba9876543210ull,
        4096,
        4096,
    };
    SharedGpuFrameDescriptorV1 descriptor{};
    descriptor.producerProcessId = validation.expectedProducerProcessId;
    descriptor.consumerProcessId = validation.expectedConsumerProcessId;
    descriptor.producerCreationTime =
        validation.expectedProducerCreationTime;
    descriptor.consumerCreationTime =
        validation.expectedConsumerCreationTime;
    descriptor.sessionIdHigh = validation.expectedSessionIdHigh;
    descriptor.sessionIdLow = validation.expectedSessionIdLow;
    descriptor.generation = 1;
    descriptor.resourceEpoch = 1;
    descriptor.slotCount = 2;
    descriptor.slotIndex = 0;
    descriptor.width = width;
    descriptor.height = height;
    descriptor.pixelFormat = SharedGpuPixelFormat::Bgra8Unorm;
    descriptor.synchronization =
        SharedGpuSynchronization::D3d11KeyedMutex;
    descriptor.sharedTextureHandle = reinterpret_cast<std::uintptr_t>(
        sharedTextureHandle.value);
    descriptor.acquireValue = 1;
    descriptor.releaseValue = 2;

    constexpr std::array<std::uint8_t, width * height * 4> pixels{
        11, 22, 33, 255, 44, 55, 66, 255,
        77, 88, 99, 255, 12, 23, 34, 255,
    };
    Check(producerMutex->AcquireSync(0, 0) == S_OK,
        "producer acquires the initial texture key without waiting");
    producerContext->UpdateSubresource(
        producerTexture.Get(), 0, nullptr, pixels.data(), width * 4, 0);
    producerContext->Flush();
    Check(producerMutex->ReleaseSync(descriptor.acquireValue) == S_OK,
        "producer publishes the texture using the descriptor acquisition key");

    ImportedD3D11SharedFrame imported;
    Check(ImportD3D11SharedFrame(
        consumerDevice.Get(), descriptor, validation, imported) ==
        SharedGpuD3D11ImportError::None,
        "consumer duplicates the handle from the verified producer and opens it");
    Check(imported.Texture() != nullptr && imported.View() != nullptr,
        "import validates the resource and creates a persistent SRV");
    Check(imported.TryAcquireForPresent(),
        "Present acquisition succeeds with a zero-millisecond timeout");

    D3D11_TEXTURE2D_DESC stagingDescription = textureDescription;
    stagingDescription.Usage = D3D11_USAGE_STAGING;
    stagingDescription.BindFlags = 0;
    stagingDescription.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    stagingDescription.MiscFlags = 0;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> staging;
    Check(SUCCEEDED(consumerDevice->CreateTexture2D(
        &stagingDescription, nullptr, &staging)),
        "test creates a CPU-readable copy target");
    if (staging != nullptr && imported.Texture() != nullptr) {
        consumerContext->CopyResource(staging.Get(), imported.Texture());
        consumerContext->Flush();
        D3D11_MAPPED_SUBRESOURCE mapped{};
        Check(SUCCEEDED(consumerContext->Map(
            staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)),
            "consumer can read pixels through the duplicated shared resource");
        if (mapped.pData != nullptr) {
            const auto* data = static_cast<const std::uint8_t*>(mapped.pData);
            Check(data[0] == pixels[0] && data[1] == pixels[1] &&
                data[2] == pixels[2] && data[3] == pixels[3],
                "shared GPU texture preserves BGRA pixels");
            consumerContext->Unmap(staging.Get(), 0);
        }
    }
    Check(imported.ReleaseAfterPresent(),
        "consumer releases ownership to the producer's recycle key");
    Check(!imported.TryAcquireForPresent(),
        "an unavailable key fails immediately rather than blocking Present");

    auto wrongIdentityDescriptor = descriptor;
    auto wrongIdentityValidation = validation;
    ++wrongIdentityDescriptor.producerCreationTime;
    ++wrongIdentityValidation.expectedProducerCreationTime;
    ImportedD3D11SharedFrame rejected;
    Check(ImportD3D11SharedFrame(
        consumerDevice.Get(),
        wrongIdentityDescriptor,
        wrongIdentityValidation,
        rejected) == SharedGpuD3D11ImportError::ProducerIdentityChanged,
        "actual process creation time prevents PID-reuse handle import");

    auto wrongConsumerDescriptor = descriptor;
    auto wrongConsumerValidation = validation;
    ++wrongConsumerDescriptor.consumerProcessId;
    ++wrongConsumerValidation.expectedConsumerProcessId;
    Check(ImportD3D11SharedFrame(
        consumerDevice.Get(),
        wrongConsumerDescriptor,
        wrongConsumerValidation,
        rejected) == SharedGpuD3D11ImportError::WrongConsumerProcess,
        "a resource intended for another GTA process is rejected");

    UniqueHandle eventHandle;
    eventHandle.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    auto nonTexture = descriptor;
    nonTexture.sharedTextureHandle =
        reinterpret_cast<std::uintptr_t>(eventHandle.value);
    HRESULT importHresult = S_OK;
    Check(ImportD3D11SharedFrame(
        consumerDevice.Get(), nonTexture, validation, rejected, &importHresult) ==
        SharedGpuD3D11ImportError::SharedTextureOpenFailed,
        "a valid producer-owned handle is still rejected unless it is a D3D texture");
    Check(FAILED(importHresult),
        "a rejected GPU handle retains the actual graphics HRESULT");

    auto wrongDimensions = descriptor;
    ++wrongDimensions.width;
    Check(ImportD3D11SharedFrame(
        consumerDevice.Get(), wrongDimensions, validation, rejected, &importHresult) ==
        SharedGpuD3D11ImportError::TextureDescriptionMismatch,
        "opened GPU resource dimensions must match authenticated metadata");
    Check(importHresult == S_OK,
        "metadata rejection clears a stale graphics HRESULT");
    Check(ImportD3D11SharedFrame(
        consumerDevice.Get(), descriptor, validation, rejected, &importHresult) ==
        SharedGpuD3D11ImportError::None && importHresult == S_OK,
        "successful import reports no stale driver error");

    if (failures == 0) {
        std::cout << "PASS: shared GPU D3D11 import tests\n";
    }
    return failures == 0 ? 0 : 1;
}
