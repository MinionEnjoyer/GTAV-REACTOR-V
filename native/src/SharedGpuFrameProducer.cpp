#include "SharedGpuFrameProducer.h"

#include <Windows.h>
#include <d3d10.h>
#include <utility>

namespace rwui::transport {
namespace {

DXGI_FORMAT DxgiFormat(const SharedGpuPixelFormat format) noexcept {
    return static_cast<DXGI_FORMAT>(static_cast<std::uint32_t>(format));
}

bool SupportedFormat(const SharedGpuPixelFormat format) noexcept {
    return format == SharedGpuPixelFormat::Bgra8Unorm ||
        format == SharedGpuPixelFormat::Bgra8UnormSrgb;
}

bool TrySharedFormat(
    const DXGI_FORMAT format,
    SharedGpuPixelFormat& sharedFormat) noexcept {
    switch (format) {
    case DXGI_FORMAT_B8G8R8A8_UNORM:
        sharedFormat = SharedGpuPixelFormat::Bgra8Unorm;
        return true;
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        sharedFormat = SharedGpuPixelFormat::Bgra8UnormSrgb;
        return true;
    default:
        sharedFormat = SharedGpuPixelFormat::Unknown;
        return false;
    }
}

constexpr std::uint64_t CopyCompletionTimeoutMilliseconds = 100;

} // namespace

const char* SharedGpuProducerSubmitResultName(
    const SharedGpuProducerSubmitResult result) noexcept {
    switch (result) {
    case SharedGpuProducerSubmitResult::Submitted: return "submitted";
    case SharedGpuProducerSubmitResult::NotInitialized:
        return "not_initialized";
    case SharedGpuProducerSubmitResult::InvalidArguments:
        return "invalid_arguments";
    case SharedGpuProducerSubmitResult::UnsupportedFormat:
        return "unsupported_format";
    case SharedGpuProducerSubmitResult::TransientHandleOpenFailed:
        return "transient_handle_open_failed";
    case SharedGpuProducerSubmitResult::TransientTextureMismatch:
        return "transient_texture_mismatch";
    case SharedGpuProducerSubmitResult::TransientSynchronizationUnavailable:
        return "transient_synchronization_unavailable";
    case SharedGpuProducerSubmitResult::ProducerBusy:
        return "producer_busy";
    case SharedGpuProducerSubmitResult::PoolBusy: return "pool_busy";
    case SharedGpuProducerSubmitResult::PoolTextureCreationFailed:
        return "pool_texture_creation_failed";
    case SharedGpuProducerSubmitResult::CopyFailed: return "copy_failed";
    case SharedGpuProducerSubmitResult::CopyCompletionTimedOut:
        return "copy_completion_timed_out";
    case SharedGpuProducerSubmitResult::DeviceRemoved:
        return "device_removed";
    default: return "unknown";
    }
}

D3D11SharedFrameProducer::~D3D11SharedFrameProducer() {
    Reset();
}

bool D3D11SharedFrameProducer::Initialize(
    ID3D11Device* const device,
    ID3D11DeviceContext* const context,
    const std::uint32_t targetConsumerProcessId,
    const std::uint64_t sessionIdHigh,
    const std::uint64_t sessionIdLow) noexcept {
    std::unique_lock lock(mutex_, std::try_to_lock);
    if (!lock.owns_lock()) return false;
    ResetUnlocked();
    if (device == nullptr || context == nullptr ||
        targetConsumerProcessId == 0 ||
        (sessionIdHigh == 0 && sessionIdLow == 0) ||
        FAILED(device->QueryInterface(IID_PPV_ARGS(&device1_))) ||
        !QueryWindowsProcessIdentity(
            GetCurrentProcessId(), producerIdentity_) ||
        !QueryWindowsProcessIdentity(
            targetConsumerProcessId, consumerIdentity_)) {
        ResetUnlocked();
        return false;
    }
    D3D11_QUERY_DESC completionDescription{};
    completionDescription.Query = D3D11_QUERY_EVENT;
    if (FAILED(device->CreateQuery(
            &completionDescription, &copyCompletionQuery_))) {
        ResetUnlocked();
        return false;
    }
    device_ = device;
    context_ = context;
    consumerProcessId_ = targetConsumerProcessId;
    sessionIdHigh_ = sessionIdHigh;
    sessionIdLow_ = sessionIdLow;
    // Keep epochs monotonic for this producer object's lifetime. Runtime may
    // rebuild the pool after a rejected frame without rotating the authenticated
    // session, and Windows may recycle an NT HANDLE value. Reusing epoch 1 would
    // create an ABA collision in the consumer's slot/resource cache.
    nextSlot_ = 0;
    submittedFrames_.store(0, std::memory_order_relaxed);
    droppedFrames_.store(0, std::memory_order_relaxed);

    // The CEF callback and channel worker are not necessarily the device's
    // creation thread. Protect immediate-context access at the D3D runtime
    // boundary in addition to this class's fail-fast CPU mutex.
    Microsoft::WRL::ComPtr<ID3D10Multithread> multithread;
    if (SUCCEEDED(context_->QueryInterface(IID_PPV_ARGS(&multithread)))) {
        multithread->SetMultithreadProtected(TRUE);
    }
    initialized_.store(true, std::memory_order_release);
    return true;
}

void D3D11SharedFrameProducer::Reset() noexcept {
    std::scoped_lock lock(mutex_);
    ResetUnlocked();
}

void D3D11SharedFrameProducer::ResetUnlocked() noexcept {
    for (auto& slot : slots_) CloseSlot(slot);
    context_.Reset();
    copyCompletionQuery_.Reset();
    device1_.Reset();
    device_.Reset();
    producerIdentity_ = {};
    consumerIdentity_ = {};
    consumerProcessId_ = 0;
    sessionIdHigh_ = 0;
    sessionIdLow_ = 0;
    initialized_.store(false, std::memory_order_release);
    failNextPoolReleaseForTesting_.store(false, std::memory_order_release);
    failNextCopyCompletionForTesting_.store(false, std::memory_order_release);
}

SharedGpuProducerSubmitResult
D3D11SharedFrameProducer::SubmitTransientTexture(
    HANDLE const transientSharedHandle,
    const std::uint32_t width,
    const std::uint32_t height,
    const SharedGpuPixelFormat format,
    const std::uint64_t generation,
    SharedGpuFrameDescriptorV1& descriptor) noexcept {
    descriptor = {};
    if (transientSharedHandle == nullptr ||
        transientSharedHandle == INVALID_HANDLE_VALUE ||
        width == 0 || height == 0 ||
        width > SharedGpuFrameMaximumDimension ||
        height > SharedGpuFrameMaximumDimension ||
        static_cast<std::uint64_t>(width) * height * 4ull >
            SharedGpuFrameMaximumBytes ||
        generation == 0) {
        return SharedGpuProducerSubmitResult::InvalidArguments;
    }
    if (!SupportedFormat(format)) {
        return SharedGpuProducerSubmitResult::UnsupportedFormat;
    }

    std::unique_lock lock(mutex_, std::try_to_lock);
    if (!lock.owns_lock()) {
        droppedFrames_.fetch_add(1, std::memory_order_relaxed);
        return SharedGpuProducerSubmitResult::ProducerBusy;
    }
    if (!initialized_.load(std::memory_order_acquire)) {
        return SharedGpuProducerSubmitResult::NotInitialized;
    }

    Microsoft::WRL::ComPtr<ID3D11Texture2D> transientTexture;
    if (!OpenTransientTexture(transientSharedHandle, transientTexture)) {
        droppedFrames_.fetch_add(1, std::memory_order_relaxed);
        return SharedGpuProducerSubmitResult::TransientHandleOpenFailed;
    }
    D3D11_TEXTURE2D_DESC transientDescription{};
    transientTexture->GetDesc(&transientDescription);
    SharedGpuPixelFormat actualFormat{};
    const auto actualWidth = transientDescription.Width;
    const auto actualHeight = transientDescription.Height;
    if (actualWidth == 0 || actualHeight == 0 ||
        actualWidth > SharedGpuFrameMaximumDimension ||
        actualHeight > SharedGpuFrameMaximumDimension ||
        static_cast<std::uint64_t>(actualWidth) * actualHeight * 4ull >
            SharedGpuFrameMaximumBytes ||
        !TrySharedFormat(transientDescription.Format, actualFormat) ||
        transientDescription.MipLevels != 1 ||
        transientDescription.ArraySize != 1 ||
        transientDescription.SampleDesc.Count != 1) {
        droppedFrames_.fetch_add(1, std::memory_order_relaxed);
        return SharedGpuProducerSubmitResult::TransientTextureMismatch;
    }

    // CEF explicitly creates its accelerated-paint texture without a keyed
    // mutex. The callback itself is the ownership boundary: open and finish
    // copying the source before returning. Do not infer or participate in a
    // private keyed-mutex protocol when a non-CEF fixture happens to expose
    // IDXGIKeyedMutex.

    Slot* selected{};
    std::uint32_t selectedIndex{};
    bool acquiredExisting{};
    for (std::uint32_t offset = 0; offset < SlotCount; ++offset) {
        const auto index = (nextSlot_ + offset) % SlotCount;
        auto& slot = slots_[index];
        if (slot.texture == nullptr) {
            selected = &slot;
            selectedIndex = index;
            break;
        }
        if (slot.mutex != nullptr &&
            slot.mutex->AcquireSync(
                slot.nextProducerAcquireKey, 0) == S_OK) {
            selected = &slot;
            selectedIndex = index;
            acquiredExisting = true;
            break;
        }
    }
    if (selected == nullptr) {
        droppedFrames_.fetch_add(1, std::memory_order_relaxed);
        return SharedGpuProducerSubmitResult::PoolBusy;
    }

    // The callback size is useful as an expectation, but CEF's accelerated
    // paint contract makes the opened texture description authoritative.
    // Browser view/device-scale transitions can briefly make the callback
    // size differ from the resource. Copy and publish exactly what was opened
    // instead of rejecting a valid CEF frame or creating a mismatched pool.
    const bool matchingTexture =
        selected->texture != nullptr && selected->width == actualWidth &&
        selected->height == actualHeight && selected->format == actualFormat;
    if (!matchingTexture) {
        // Acquiring nextProducerAcquireKey proves the consumer has finished
        // with the old resource. It is safe to retire it without another key
        // transition and create a replacement at initial key zero.
        CloseSlot(*selected);
        acquiredExisting = false;
        if (!CreateSlotTexture(
                *selected, actualWidth, actualHeight, actualFormat) ||
            selected->mutex->AcquireSync(0, 0) != S_OK) {
            CloseSlot(*selected);
            droppedFrames_.fetch_add(1, std::memory_order_relaxed);
            return SharedGpuProducerSubmitResult::PoolTextureCreationFailed;
        }
    }
    if (selected->texture == nullptr || selected->mutex == nullptr ||
        selected->sharedHandle == nullptr) {
        if (acquiredExisting) {
            selected->mutex->ReleaseSync(selected->nextProducerAcquireKey);
        }
        droppedFrames_.fetch_add(1, std::memory_order_relaxed);
        return SharedGpuProducerSubmitResult::CopyFailed;
    }

    const auto readyKey = selected->nextProducerAcquireKey + 1;
    const auto recycleKey = selected->nextProducerAcquireKey + 2;
    context_->CopyResource(selected->texture.Get(), transientTexture.Get());
    context_->End(copyCompletionQuery_.Get());
    context_->Flush();

    // CopyResource is asynchronous. CEF releases its pooled source as soon as
    // OnAcceleratedPaint returns, so Flush alone does not prove that the GPU
    // has stopped reading it. Match CefSharp's reference implementation by
    // waiting on an EVENT query, but keep the callback fail-open and bounded.
    // A timeout retires the destination slot and demotes this frame; it never
    // publishes partially copied pixels to GTA.
    const auto copyDeadline = GetTickCount64() +
        CopyCompletionTimeoutMilliseconds;
    HRESULT completion = S_FALSE;
    const bool forceTimeout = failNextCopyCompletionForTesting_.exchange(
        false, std::memory_order_acq_rel);
    do {
        completion = forceTimeout ? S_FALSE : context_->GetData(
            copyCompletionQuery_.Get(), nullptr, 0,
            D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (completion == S_OK) break;
        if (FAILED(completion)) {
            CloseSlot(*selected);
            copyCompletionQuery_.Reset();
            initialized_.store(false, std::memory_order_release);
            droppedFrames_.fetch_add(1, std::memory_order_relaxed);
            return SharedGpuProducerSubmitResult::DeviceRemoved;
        }
        SwitchToThread();
    } while (GetTickCount64() < copyDeadline);
    if (completion != S_OK) {
        CloseSlot(*selected);
        // An EVENT query must not be re-issued while its prior End is still
        // outstanding. Retire it with the timed-out copy and use a fresh query
        // for a later frame; mutex_ serializes all immediate-context use.
        if (!RecreateCopyCompletionQuery()) {
            initialized_.store(false, std::memory_order_release);
            droppedFrames_.fetch_add(1, std::memory_order_relaxed);
            return SharedGpuProducerSubmitResult::DeviceRemoved;
        }
        droppedFrames_.fetch_add(1, std::memory_order_relaxed);
        return SharedGpuProducerSubmitResult::CopyCompletionTimedOut;
    }

    auto releaseResult = selected->mutex->ReleaseSync(readyKey);
    if (failNextPoolReleaseForTesting_.exchange(
            false, std::memory_order_acq_rel)) {
        releaseResult = E_FAIL;
    }
    if (releaseResult != S_OK) {
        // The keyed state is indeterminate after a failed release. Retire the
        // resource immediately so one bad transition cannot permanently
        // consume a slot in the bounded pool.
        CloseSlot(*selected);
        droppedFrames_.fetch_add(1, std::memory_order_relaxed);
        return SharedGpuProducerSubmitResult::CopyFailed;
    }

    selected->nextProducerAcquireKey = recycleKey;
    selected->lastGeneration = generation;
    nextSlot_ = (selectedIndex + 1) % SlotCount;

    descriptor.producerProcessId = producerIdentity_.processId;
    descriptor.consumerProcessId = consumerProcessId_;
    descriptor.producerCreationTime = producerIdentity_.creationTime;
    descriptor.consumerCreationTime = consumerIdentity_.creationTime;
    descriptor.sessionIdHigh = sessionIdHigh_;
    descriptor.sessionIdLow = sessionIdLow_;
    descriptor.generation = generation;
    descriptor.resourceEpoch = selected->resourceEpoch;
    descriptor.slotIndex = selectedIndex;
    descriptor.slotCount = SlotCount;
    descriptor.width = actualWidth;
    descriptor.height = actualHeight;
    descriptor.pixelFormat = actualFormat;
    descriptor.synchronization =
        SharedGpuSynchronization::D3d11KeyedMutex;
    descriptor.sharedTextureHandle = reinterpret_cast<std::uintptr_t>(
        selected->sharedHandle);
    descriptor.acquireValue = readyKey;
    descriptor.releaseValue = recycleKey;

    submittedFrames_.fetch_add(1, std::memory_order_relaxed);
    return SharedGpuProducerSubmitResult::Submitted;
}

bool D3D11SharedFrameProducer::TryRecycleUnsent(
    const SharedGpuFrameDescriptorV1& descriptor) noexcept {
    std::unique_lock lock(mutex_, std::try_to_lock);
    if (!lock.owns_lock() ||
        !initialized_.load(std::memory_order_acquire) ||
        descriptor.producerProcessId != producerIdentity_.processId ||
        descriptor.producerCreationTime != producerIdentity_.creationTime ||
        descriptor.consumerCreationTime != consumerIdentity_.creationTime ||
        descriptor.consumerProcessId != consumerProcessId_ ||
        descriptor.sessionIdHigh != sessionIdHigh_ ||
        descriptor.sessionIdLow != sessionIdLow_ ||
        descriptor.slotCount != SlotCount ||
        descriptor.slotIndex >= SlotCount) {
        return false;
    }
    auto& slot = slots_[descriptor.slotIndex];
    if (slot.mutex == nullptr ||
        slot.resourceEpoch != descriptor.resourceEpoch ||
        slot.lastGeneration != descriptor.generation ||
        slot.nextProducerAcquireKey != descriptor.releaseValue ||
        slot.mutex->AcquireSync(descriptor.acquireValue, 0) != S_OK) {
        return false;
    }
    return slot.mutex->ReleaseSync(descriptor.releaseValue) == S_OK;
}

bool D3D11SharedFrameProducer::RetireUnsent(const SharedGpuFrameDescriptorV1& descriptor) noexcept {
    std::scoped_lock lock(mutex_);
    if (!initialized_.load() || descriptor.producerProcessId != producerIdentity_.processId ||
        descriptor.producerCreationTime != producerIdentity_.creationTime ||
        descriptor.consumerProcessId != consumerProcessId_ || descriptor.consumerCreationTime != consumerIdentity_.creationTime ||
        descriptor.sessionIdHigh != sessionIdHigh_ || descriptor.sessionIdLow != sessionIdLow_ ||
        descriptor.slotCount != SlotCount || descriptor.slotIndex >= SlotCount) return false;
    auto& slot = slots_[descriptor.slotIndex];
    if (!slot.texture || slot.resourceEpoch != descriptor.resourceEpoch ||
        slot.lastGeneration != descriptor.generation || slot.nextProducerAcquireKey != descriptor.releaseValue) return false;
    CloseSlot(slot); droppedFrames_.fetch_add(1); return true;
}

std::uint64_t D3D11SharedFrameProducer::SubmittedFrames() const noexcept {
    return submittedFrames_.load(std::memory_order_relaxed);
}

std::uint64_t D3D11SharedFrameProducer::DroppedFrames() const noexcept {
    return droppedFrames_.load(std::memory_order_relaxed);
}

bool D3D11SharedFrameProducer::CreateSlotTexture(
    Slot& slot,
    const std::uint32_t width,
    const std::uint32_t height,
    const SharedGpuPixelFormat format) noexcept {
    D3D11_TEXTURE2D_DESC description{};
    description.Width = width;
    description.Height = height;
    description.MipLevels = 1;
    description.ArraySize = 1;
    description.Format = DxgiFormat(format);
    description.SampleDesc.Count = 1;
    description.Usage = D3D11_USAGE_DEFAULT;
    description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    description.MiscFlags =
        D3D11_RESOURCE_MISC_SHARED_NTHANDLE |
        D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    if (FAILED(device_->CreateTexture2D(
            &description, nullptr, &slot.texture)) ||
        FAILED(slot.texture.As(&slot.mutex))) {
        CloseSlot(slot);
        return false;
    }
    Microsoft::WRL::ComPtr<IDXGIResource1> resource;
    if (FAILED(slot.texture.As(&resource)) ||
        FAILED(resource->CreateSharedHandle(
            nullptr,
            DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
            nullptr,
            &slot.sharedHandle))) {
        CloseSlot(slot);
        return false;
    }
    slot.resourceEpoch = nextResourceEpoch_++;
    slot.nextProducerAcquireKey = 0;
    slot.width = width;
    slot.height = height;
    slot.format = format;
    return true;
}

bool D3D11SharedFrameProducer::OpenTransientTexture(
    HANDLE const handle,
    Microsoft::WRL::ComPtr<ID3D11Texture2D>& texture) noexcept {
    texture.Reset();
    if (device1_ != nullptr && SUCCEEDED(device1_->OpenSharedResource1(
        handle, IID_PPV_ARGS(&texture)))) {
        return true;
    }
    texture.Reset();
    return device_ != nullptr && SUCCEEDED(device_->OpenSharedResource(
        handle, IID_PPV_ARGS(&texture)));
}

bool D3D11SharedFrameProducer::RecreateCopyCompletionQuery() noexcept {
    copyCompletionQuery_.Reset();
    if (device_ == nullptr) return false;
    D3D11_QUERY_DESC description{};
    description.Query = D3D11_QUERY_EVENT;
    return SUCCEEDED(device_->CreateQuery(
        &description, &copyCompletionQuery_));
}

void D3D11SharedFrameProducer::CloseSlot(Slot& slot) noexcept {
    if (slot.sharedHandle != nullptr &&
        slot.sharedHandle != INVALID_HANDLE_VALUE) {
        CloseHandle(slot.sharedHandle);
    }
    slot = {};
}

} // namespace rwui::transport
