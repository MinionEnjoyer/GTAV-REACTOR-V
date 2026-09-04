#include "SharedGpuFrameProducer.h"

#include <Windows.h>
#include <array>
#include <chrono>
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
        if (closeOnDestroy && value != nullptr &&
            value != INVALID_HANDLE_VALUE) {
            CloseHandle(value);
        }
    }
    HANDLE value{};
    bool closeOnDestroy{};
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

bool CreateTransientTexture(
    ID3D11Device* const device,
    ID3D11DeviceContext* const context,
    const std::uint8_t seed,
    Microsoft::WRL::ComPtr<ID3D11Texture2D>& texture,
    UniqueHandle& handle) {
    D3D11_TEXTURE2D_DESC description{};
    description.Width = 2;
    description.Height = 2;
    description.MipLevels = 1;
    description.ArraySize = 1;
    description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.Usage = D3D11_USAGE_DEFAULT;
    description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    // CEF's accelerated-paint source is explicitly non-keyed. The legacy
    // shared-handle form gives this deterministic WARP fixture the same
    // ownership semantics and also covers the producer's OpenSharedResource
    // fallback after OpenSharedResource1 rejects a non-NT handle.
    description.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
    if (FAILED(device->CreateTexture2D(&description, nullptr, &texture))) {
        return false;
    }
    const std::array<std::uint8_t, 16> pixels{
        seed, 2, 3, 255, seed, 5, 6, 255,
        seed, 8, 9, 255, seed, 11, 12, 255,
    };
    context->UpdateSubresource(
        texture.Get(), 0, nullptr, pixels.data(), 8, 0);
    context->Flush();

    Microsoft::WRL::ComPtr<IDXGIResource> resource;
    return SUCCEEDED(texture.As(&resource)) &&
        SUCCEEDED(resource->GetSharedHandle(&handle.value));
}

bool ReadFirstBlue(
    ID3D11Device* const device,
    ID3D11DeviceContext* const context,
    ID3D11Texture2D* const source,
    std::uint8_t& value) {
    D3D11_TEXTURE2D_DESC description{};
    source->GetDesc(&description);
    description.Usage = D3D11_USAGE_STAGING;
    description.BindFlags = 0;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    description.MiscFlags = 0;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> staging;
    if (FAILED(device->CreateTexture2D(
            &description, nullptr, &staging))) return false;
    context->CopyResource(staging.Get(), source);
    context->Flush();
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(
            staging.Get(), 0, D3D11_MAP_READ, 0, &mapped))) return false;
    value = static_cast<const std::uint8_t*>(mapped.pData)[0];
    context->Unmap(staging.Get(), 0);
    return true;
}

} // namespace

int main() {
    using namespace rwui::transport;

    Microsoft::WRL::ComPtr<ID3D11Device> producerDevice;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> producerContext;
    Microsoft::WRL::ComPtr<ID3D11Device> consumerDevice;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> consumerContext;
    if (!CreateWarpDevice(producerDevice, producerContext) ||
        !CreateWarpDevice(consumerDevice, consumerContext)) {
        std::cout << "SKIP: D3D11 WARP is unavailable\n";
        return 125;
    }

    WindowsProcessIdentity identity{};
    Check(QueryWindowsProcessIdentity(GetCurrentProcessId(), identity),
        "producer identity is available");
    constexpr std::uint64_t sessionHigh = 0x1111222233334444ull;
    constexpr std::uint64_t sessionLow = 0xaaaabbbbccccddddull;
    D3D11SharedFrameProducer producer;
    Check(producer.Initialize(
        producerDevice.Get(),
        producerContext.Get(),
        GetCurrentProcessId(),
        sessionHigh,
        sessionLow),
        "external producer initializes a target-PID-scoped two-slot pool");

    SharedGpuFrameValidationContext validation{
        identity.processId,
        identity.processId,
        identity.creationTime,
        identity.creationTime,
        sessionHigh,
        sessionLow,
        4096,
        4096,
    };

    Microsoft::WRL::ComPtr<ID3D11Texture2D> transient1;
    UniqueHandle transientHandle1;
    const bool transientCreated = CreateTransientTexture(
        producerDevice.Get(), producerContext.Get(), 41,
        transient1, transientHandle1);
    Check(transientCreated,
        "fixture creates a CEF-like transient shared texture");
    if (!transientCreated) return failures == 0 ? 125 : 1;

    SharedGpuFrameDescriptorV1 frame1{};
    Check(producer.SubmitTransientTexture(
        transientHandle1.value,
        640,
        360,
        SharedGpuPixelFormat::Bgra8Unorm,
        1,
        frame1) == SharedGpuProducerSubmitResult::Submitted,
        "opened CEF texture remains authoritative when callback size is stale");
    Check(frame1.width == 2 && frame1.height == 2 &&
        frame1.pixelFormat == SharedGpuPixelFormat::Bgra8Unorm,
        "descriptor and persistent pool use the opened texture description");
    Check(frame1.slotCount == 2 && frame1.slotIndex == 0 &&
        frame1.sharedTextureHandle !=
            reinterpret_cast<std::uintptr_t>(transientHandle1.value),
        "published handle belongs to Reactor's persistent bounded pool");

    // Destroying both the transient HANDLE and COM object models returning
    // from OnAcceleratedPaint. The persistent pool frame must remain valid.
    // A legacy DXGI shared handle is not an NT handle and must not be passed
    // to CloseHandle. Releasing the last source COM object ends its lifetime.
    transientHandle1.value = nullptr;
    transient1.Reset();

    ImportedD3D11SharedFrame imported1;
    Check(ImportD3D11SharedFrame(
        consumerDevice.Get(), frame1, validation, imported1) ==
        SharedGpuD3D11ImportError::None,
        "consumer imports the persistent frame after the transient handle closes");
    Check(imported1.TryAcquireForPresent(),
        "consumer acquires persistent frame one without waiting");
    std::uint8_t blue{};
    Check(ReadFirstBlue(
        consumerDevice.Get(), consumerContext.Get(),
        imported1.Texture(), blue) && blue == 41,
        "persistent pool copy preserves transient frame pixels");
    Check(imported1.ReleaseAfterPresent(),
        "consumer returns pool slot zero to the producer");

    Microsoft::WRL::ComPtr<ID3D11Texture2D> transient2;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> transient3;
    UniqueHandle transientHandle2;
    UniqueHandle transientHandle3;
    Check(CreateTransientTexture(
        producerDevice.Get(), producerContext.Get(), 52,
        transient2, transientHandle2) &&
        CreateTransientTexture(
            producerDevice.Get(), producerContext.Get(), 63,
            transient3, transientHandle3),
        "fixture creates later transient frames");

    SharedGpuFrameDescriptorV1 frame2{};
    SharedGpuFrameDescriptorV1 frame3{};
    Check(producer.SubmitTransientTexture(
        transientHandle2.value, 2, 2,
        SharedGpuPixelFormat::Bgra8Unorm, 2, frame2) ==
        SharedGpuProducerSubmitResult::Submitted &&
        frame2.slotIndex == 1,
        "second frame occupies the other bounded pool slot");
    Check(producer.SubmitTransientTexture(
        transientHandle3.value, 2, 2,
        SharedGpuPixelFormat::Bgra8Unorm, 3, frame3) ==
        SharedGpuProducerSubmitResult::Submitted &&
        frame3.slotIndex == 0,
        "a consumer-recycled slot is reused with a newer generation");

    // Neither frame two nor three has been consumed. A third in-flight frame
    // must be dropped immediately rather than growing the pool or waiting.
    SharedGpuFrameDescriptorV1 dropped{};
    const auto started = std::chrono::steady_clock::now();
    const auto fullResult = producer.SubmitTransientTexture(
        transientHandle3.value, 2, 2,
        SharedGpuPixelFormat::Bgra8Unorm, 4, dropped);
    const auto elapsed = std::chrono::steady_clock::now() - started;
    Check(fullResult == SharedGpuProducerSubmitResult::PoolBusy,
        "two outstanding textures bound producer memory and frame retention");
    Check(elapsed < std::chrono::milliseconds(50),
        "pool exhaustion fails open without a callback stall");

    // A descriptor replaced before the control worker sends it is recycled by
    // the producer, allowing the slot to be used again without the consumer.
    Check(producer.TryRecycleUnsent(frame2),
        "control plane can recycle a latest-wins frame dropped before send");
    SharedGpuFrameDescriptorV1 frame4{};
    Check(producer.SubmitTransientTexture(
        transientHandle3.value, 2, 2,
        SharedGpuPixelFormat::Bgra8Unorm, 4, frame4) ==
        SharedGpuProducerSubmitResult::Submitted &&
        frame4.slotIndex == frame2.slotIndex,
        "locally recycled unsent slot returns to the pool");

    Check(producer.SubmittedFrames() == 4 && producer.DroppedFrames() == 1,
        "producer reports bounded submissions and fail-open drops");

    // Rebuilding a pool in the same authenticated session must not recreate
    // the same cache identity. Windows may recycle the numeric HANDLE value,
    // so resourceEpoch is the ABA guard.
    const auto originalEpoch = frame1.resourceEpoch;
    producer.Reset();
    Check(producer.Initialize(
        producerDevice.Get(), producerContext.Get(), GetCurrentProcessId(),
        sessionHigh, sessionLow),
        "same-session producer pool can be rebuilt after a transport fault");
    Microsoft::WRL::ComPtr<ID3D11Texture2D> rebuiltTransient;
    UniqueHandle rebuiltTransientHandle;
    Check(CreateTransientTexture(
        producerDevice.Get(), producerContext.Get(), 84,
        rebuiltTransient, rebuiltTransientHandle),
        "fixture creates a post-rebuild transient frame");
    SharedGpuFrameDescriptorV1 rebuiltFrame{};
    Check(producer.SubmitTransientTexture(
        rebuiltTransientHandle.value, 2, 2,
        SharedGpuPixelFormat::Bgra8Unorm, 5, rebuiltFrame) ==
        SharedGpuProducerSubmitResult::Submitted,
        "rebuilt pool publishes a new slot resource");
    Check(rebuiltFrame.resourceEpoch > originalEpoch &&
        !imported1.RebindDescriptor(rebuiltFrame, validation),
        "same-session pool rebuild advances epoch and invalidates old cache");
    ImportedD3D11SharedFrame rebuiltImport;
    Check(ImportD3D11SharedFrame(
        consumerDevice.Get(), rebuiltFrame, validation, rebuiltImport) ==
        SharedGpuD3D11ImportError::None &&
        rebuiltImport.TryAcquireForPresent(),
        "consumer reimports the rebuilt slot instead of retaining old resource");
    blue = 0;
    Check(ReadFirstBlue(
        consumerDevice.Get(), consumerContext.Get(),
        rebuiltImport.Texture(), blue) && blue == 84,
        "reimported same-session slot exposes the new resource pixels");
    Check(rebuiltImport.ReleaseAfterPresent(),
        "consumer releases rebuilt slot after ABA regression check");

    producer.FailNextCopyCompletionForTesting();
    SharedGpuFrameDescriptorV1 timedOutFrame{};
    Check(producer.SubmitTransientTexture(
        rebuiltTransientHandle.value, 2, 2,
        SharedGpuPixelFormat::Bgra8Unorm, 6, timedOutFrame) ==
            SharedGpuProducerSubmitResult::CopyCompletionTimedOut,
        "a GPU copy that misses the callback deadline is never published");
    SharedGpuFrameDescriptorV1 postTimeoutFrame{};
    Check(producer.SubmitTransientTexture(
        rebuiltTransientHandle.value, 2, 2,
        SharedGpuPixelFormat::Bgra8Unorm, 7, postTimeoutFrame) ==
            SharedGpuProducerSubmitResult::Submitted,
        "the destination slot is recreated after a bounded copy timeout");
    ImportedD3D11SharedFrame postTimeoutImport;
    Check(ImportD3D11SharedFrame(
        consumerDevice.Get(), postTimeoutFrame, validation,
        postTimeoutImport) == SharedGpuD3D11ImportError::None &&
        postTimeoutImport.TryAcquireForPresent() &&
        postTimeoutImport.ReleaseAfterPresent(),
        "a frame after copy-timeout recovery remains consumable");

    producer.FailNextPoolReleaseForTesting();
    SharedGpuFrameDescriptorV1 failedReleaseFrame{};
    Check(producer.SubmitTransientTexture(
        rebuiltTransientHandle.value, 2, 2,
        SharedGpuPixelFormat::Bgra8Unorm, 8, failedReleaseFrame) ==
        SharedGpuProducerSubmitResult::CopyFailed,
        "a failed producer keyed release is reported as a copy failure");
    SharedGpuFrameDescriptorV1 recoveredReleaseFrame{};
    Check(producer.SubmitTransientTexture(
        rebuiltTransientHandle.value, 2, 2,
        SharedGpuPixelFormat::Bgra8Unorm, 9, recoveredReleaseFrame) ==
            SharedGpuProducerSubmitResult::Submitted &&
        recoveredReleaseFrame.resourceEpoch > rebuiltFrame.resourceEpoch,
        "the failed keyed slot is retired and recreated on the next frame");

    if (failures == 0) {
        std::cout << "PASS: shared GPU producer pool roundtrip tests\n";
    }
    return failures == 0 ? 0 : 1;
}
