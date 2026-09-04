#include "SharedGpuFrameRuntime.h"

#include <Windows.h>
#include <bcrypt.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <algorithm>

namespace rwui::transport {
namespace {

bool RandomSession(std::uint64_t& high, std::uint64_t& low) noexcept {
    std::uint64_t values[2]{};
    if (BCryptGenRandom(
            nullptr,
            reinterpret_cast<PUCHAR>(values),
            sizeof(values),
            BCRYPT_USE_SYSTEM_PREFERRED_RNG) < 0) return false;
    high = values[0];
    low = values[1];
    return high != 0 || low != 0;
}

bool SupportedSourceTexture(ID3D11Texture2D* texture) noexcept {
    if (texture == nullptr) return false;
    D3D11_TEXTURE2D_DESC description{};
    texture->GetDesc(&description);
    const bool supportedFormat =
        description.Format == DXGI_FORMAT_B8G8R8A8_UNORM ||
        description.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
    return description.Width != 0 && description.Height != 0 &&
        description.Width <= SharedGpuFrameMaximumDimension &&
        description.Height <= SharedGpuFrameMaximumDimension &&
        static_cast<std::uint64_t>(description.Width) *
            description.Height * 4ull <= SharedGpuFrameMaximumBytes &&
        supportedFormat &&
        description.MipLevels == 1 && description.ArraySize == 1 &&
        description.SampleDesc.Count == 1;
}

} // namespace

SharedGpuFrameProducerRuntime::~SharedGpuFrameProducerRuntime() {
    Stop();
}

bool SharedGpuFrameProducerRuntime::Start(
    const std::uint32_t targetGtaProcessId) noexcept {
    Stop();
    ResetDiagnostics();
    std::scoped_lock lock(lifecycleMutex_);
    cpuBridgeEnabled_ = cpuBridgeForTesting_ || LegacyCpuFramesEnabled(targetGtaProcessId);
    cpuTrace_ = {}; cpuTrace_.SetPath(CpuFrameLogPath(targetGtaProcessId, L"Producer"));
    lastCpuSubmitTick_ = 0;
    cpuRetryAfterTick_ = 0; cpuRecoveryBudget_ = {}; cpuRecoveries_.store(0);
    WindowsProcessIdentity producerIdentity{};
    WindowsProcessIdentity consumerIdentity{};
    std::uint64_t sessionHigh{};
    std::uint64_t sessionLow{};
    if (targetGtaProcessId == 0 ||
        !QueryWindowsProcessIdentity(GetCurrentProcessId(), producerIdentity) ||
        !QueryWindowsProcessIdentity(targetGtaProcessId, consumerIdentity) ||
        !RandomSession(sessionHigh, sessionLow)) return false;
    endpoint_ = {
        producerIdentity.processId,
        producerIdentity.creationTime,
        consumerIdentity.processId,
        consumerIdentity.creationTime,
        sessionHigh,
        sessionLow,
    };
    if (server_.Create(endpoint_) != SharedGpuFrameChannelError::None ||
        !discovery_.Publish(endpoint_)) {
        server_.Close();
        endpoint_ = {};
        return false;
    }
    wakeEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (wakeEvent_ == nullptr) {
        discovery_.Close();
        server_.Close();
        endpoint_ = {};
        return false;
    }
    stop_.store(false, std::memory_order_release);
    presentationVisible_.store(false, std::memory_order_release);
    presentationEpoch_.store(1, std::memory_order_release);
    {
        std::scoped_lock outbox(outboxMutex_);
        hasPendingPresentation_ = true;
    }
    lastAcknowledgedGeneration_.store(0, std::memory_order_release);
    bound_.store(true, std::memory_order_release);
    try {
        worker_ = std::thread(&SharedGpuFrameProducerRuntime::Worker, this);
        SetEvent(wakeEvent_);
    } catch (...) {
        bound_.store(false, std::memory_order_release);
        stop_.store(true, std::memory_order_release);
        CloseHandle(wakeEvent_);
        wakeEvent_ = nullptr;
        discovery_.Close();
        server_.Close();
        endpoint_ = {};
        return false;
    }
    return true;
}

void SharedGpuFrameProducerRuntime::Stop() noexcept {
    std::unique_lock lock(lifecycleMutex_);
    bound_.store(false, std::memory_order_release);
    connected_.store(false, std::memory_order_release);
    consumerValidated_.store(false, std::memory_order_release);
    adapterReady_.store(false, std::memory_order_release);
    lastAcknowledgedGeneration_.store(0, std::memory_order_release);
    presentationVisible_.store(false, std::memory_order_release);
    presentationEpoch_.store(0, std::memory_order_release);
    stop_.store(true, std::memory_order_release);
    if (wakeEvent_ != nullptr) SetEvent(wakeEvent_);
    lock.unlock();
    if (worker_.joinable()) worker_.join();
    lock.lock();
    // Drain an in-flight accelerated-paint callback before destroying the
    // producer pool or its wake event. New callbacks observe bound=false and
    // fail open without entering this critical section.
    std::scoped_lock submitLock(submitMutex_);
    {
        std::scoped_lock outbox(outboxMutex_);
        if (hasPending_) producer_.TryRecycleUnsent(pending_);
        pending_ = {};
        hasPending_ = false;
        hasPendingPresentation_ = false;
    }
    producer_.Reset();
    cpuBridge_.Reset();
    discovery_.Close();
    server_.Close();
    if (wakeEvent_ != nullptr) CloseHandle(wakeEvent_);
    wakeEvent_ = nullptr;
    endpoint_ = {};
    adapterLuid_ = {};
    adapterVendorId_ = 0;
    adapterDeviceId_ = 0;
    adapterDescription_.fill(L'\0');
}

bool SharedGpuFrameProducerRuntime::SubmitTransient(
    HANDLE const handle,
    const std::uint32_t width,
    const std::uint32_t height,
    const SharedGpuPixelFormat format,
    const std::uint64_t generation) noexcept {
    return SubmitTransientStatus(
        handle, width, height, format, generation) ==
        RwuiSharedTextureSubmitStatus::Submitted;
}

RwuiSharedTextureSubmitStatus
SharedGpuFrameProducerRuntime::SubmitTransientStatus(
    HANDLE const handle,
    const std::uint32_t width,
    const std::uint32_t height,
    const SharedGpuPixelFormat format,
    const std::uint64_t generation,
    const bool bootstrapProbe) noexcept {
    (bootstrapProbe ? probeAttempts_ : submitAttempts_).fetch_add(
        1, std::memory_order_relaxed);
    lastAttemptedGeneration_.store(generation, std::memory_order_relaxed);
    if (!bound_.load(std::memory_order_acquire)) {
        return CompleteAttempt(
            RwuiSharedTextureSubmitStatus::ProducerStopped, generation);
    }
    std::unique_lock submitLock(submitMutex_, std::try_to_lock);
    if (!submitLock.owns_lock()) {
        return CompleteAttempt(
            RwuiSharedTextureSubmitStatus::Backpressure, generation);
    }
    if (!bound_.load(std::memory_order_acquire)) {
        return CompleteAttempt(
            RwuiSharedTextureSubmitStatus::ProducerStopped, generation);
    }
    if (cpuBridgeEnabled_ && (UINT64(width) * height * 4 > CpuFrameMaximumBytes))
        return CompleteAttempt(RwuiSharedTextureSubmitStatus::InvalidFrame, generation);
    if (cpuBridgeEnabled_ && GetTickCount64() < cpuRetryAfterTick_)
        return CompleteAttempt(RwuiSharedTextureSubmitStatus::Backpressure, generation);
    if (cpuBridgeEnabled_ && !cpuBridgeForTesting_ && lastCpuSubmitTick_ && GetTickCount64() - lastCpuSubmitTick_ < 67)
        return CompleteAttempt(RwuiSharedTextureSubmitStatus::Backpressure, generation);
    if (cpuBridgeEnabled_) lastCpuSubmitTick_ = GetTickCount64();
    if (!adapterReady_.load(std::memory_order_acquire) &&
        !SelectHardwareAdapter(handle)) {
        return CompleteAttempt(
            RwuiSharedTextureSubmitStatus::AdapterOrResourceInvalid,
            generation);
    }
    const bool routeWasReady = AcceleratedReady();
    SharedGpuFrameDescriptorV1 descriptor{};
    const auto result = producer_.SubmitTransientTexture(
        handle, width, height, format, generation, descriptor);
    // Never tear down and re-probe the persistent pool while descriptors may
    // be in flight. A handle that cannot open on the cached CEF adapter is a
    // dropped frame; managed policy may fall back to CPU without invalidating
    // resources already imported by GTA.
    if (result != SharedGpuProducerSubmitResult::Submitted) {
        RwuiSharedTextureSubmitStatus status{};
        switch (result) {
        case SharedGpuProducerSubmitResult::ProducerBusy:
        case SharedGpuProducerSubmitResult::PoolBusy:
        case SharedGpuProducerSubmitResult::TransientSynchronizationUnavailable:
            status = RwuiSharedTextureSubmitStatus::Backpressure;
            break;
        case SharedGpuProducerSubmitResult::NotInitialized:
            status = RwuiSharedTextureSubmitStatus::SessionInvalid;
            break;
        case SharedGpuProducerSubmitResult::InvalidArguments:
        case SharedGpuProducerSubmitResult::UnsupportedFormat:
            status = RwuiSharedTextureSubmitStatus::InvalidFrame;
            break;
        case SharedGpuProducerSubmitResult::TransientHandleOpenFailed:
        case SharedGpuProducerSubmitResult::TransientTextureMismatch:
            status = RwuiSharedTextureSubmitStatus::AdapterOrResourceInvalid;
            break;
        case SharedGpuProducerSubmitResult::PoolTextureCreationFailed:
        case SharedGpuProducerSubmitResult::CopyFailed:
        case SharedGpuProducerSubmitResult::CopyCompletionTimedOut:
        case SharedGpuProducerSubmitResult::DeviceRemoved:
            status = RwuiSharedTextureSubmitStatus::DeviceOrCopyFailure;
            break;
        case SharedGpuProducerSubmitResult::Submitted:
        default:
            status = RwuiSharedTextureSubmitStatus::UnknownFailure;
            break;
        }
        const bool routeInvalid =
            status == RwuiSharedTextureSubmitStatus::SessionInvalid ||
            status == RwuiSharedTextureSubmitStatus::
                AdapterOrResourceInvalid ||
            status == RwuiSharedTextureSubmitStatus::ProducerStopped;
        if (routeWasReady && routeInvalid) {
            DemoteAcceleratedRoute();
        }
        return CompleteAttempt(status, generation);
    }

    std::unique_lock outbox(outboxMutex_, std::try_to_lock);
    if (!outbox.owns_lock()) {
        if (!producer_.TryRecycleUnsent(descriptor)) {
            DemoteAcceleratedRoute();
            return CompleteAttempt(
                RwuiSharedTextureSubmitStatus::SessionInvalid, generation);
        }
        return CompleteAttempt(
            RwuiSharedTextureSubmitStatus::Backpressure, generation);
    }
    if (hasPending_ && !producer_.TryRecycleUnsent(pending_)) {
        producer_.TryRecycleUnsent(descriptor);
        DemoteAcceleratedRoute();
        return CompleteAttempt(
            RwuiSharedTextureSubmitStatus::SessionInvalid, generation);
    }
    pending_ = descriptor;
    hasPending_ = true;
    if (SetEvent(wakeEvent_) == FALSE) {
        pending_ = {};
        hasPending_ = false;
        producer_.TryRecycleUnsent(descriptor);
        DemoteAcceleratedRoute();
        return CompleteAttempt(
            RwuiSharedTextureSubmitStatus::SessionInvalid, generation);
    }
    return CompleteAttempt(
        RwuiSharedTextureSubmitStatus::Submitted, generation);
}

RwuiSharedTextureSubmitStatus SharedGpuFrameProducerRuntime::CompleteAttempt(
    const RwuiSharedTextureSubmitStatus status,
    const std::uint64_t generation) noexcept {
    lastStatus_.store(status, std::memory_order_relaxed);
    switch (status) {
    case RwuiSharedTextureSubmitStatus::Submitted:
        submitted_.fetch_add(1, std::memory_order_relaxed);
        lastSubmittedGeneration_.store(generation, std::memory_order_relaxed);
        break;
    case RwuiSharedTextureSubmitStatus::Backpressure:
        backpressure_.fetch_add(1, std::memory_order_relaxed);
        break;
    case RwuiSharedTextureSubmitStatus::SessionInvalid:
        sessionInvalid_.fetch_add(1, std::memory_order_relaxed);
        break;
    case RwuiSharedTextureSubmitStatus::AdapterOrResourceInvalid:
        adapterOrResourceInvalid_.fetch_add(1, std::memory_order_relaxed);
        break;
    case RwuiSharedTextureSubmitStatus::DeviceOrCopyFailure:
        deviceOrCopyFailure_.fetch_add(1, std::memory_order_relaxed);
        break;
    case RwuiSharedTextureSubmitStatus::ProducerStopped:
        producerStopped_.fetch_add(1, std::memory_order_relaxed);
        break;
    case RwuiSharedTextureSubmitStatus::InvalidFrame:
        invalidFrame_.fetch_add(1, std::memory_order_relaxed);
        break;
    case RwuiSharedTextureSubmitStatus::UnknownFailure:
    default:
        unknownFailure_.fetch_add(1, std::memory_order_relaxed);
        break;
    }
    return status;
}

RwuiSharedTextureSubmitStatus
SharedGpuFrameProducerRuntime::RecordRejectedAttempt(
    const RwuiSharedTextureSubmitStatus status,
    const std::uint64_t generation,
    const bool bootstrapProbe) noexcept {
    (bootstrapProbe ? probeAttempts_ : submitAttempts_).fetch_add(
        1, std::memory_order_relaxed);
    lastAttemptedGeneration_.store(generation, std::memory_order_relaxed);
    return CompleteAttempt(status, generation);
}

void SharedGpuFrameProducerRuntime::ResetDiagnostics() noexcept {
    lastStatus_.store(
        RwuiSharedTextureSubmitStatus::UnknownFailure,
        std::memory_order_relaxed);
    probeAttempts_.store(0, std::memory_order_relaxed);
    submitAttempts_.store(0, std::memory_order_relaxed);
    submitted_.store(0, std::memory_order_relaxed);
    backpressure_.store(0, std::memory_order_relaxed);
    sessionInvalid_.store(0, std::memory_order_relaxed);
    adapterOrResourceInvalid_.store(0, std::memory_order_relaxed);
    deviceOrCopyFailure_.store(0, std::memory_order_relaxed);
    producerStopped_.store(0, std::memory_order_relaxed);
    invalidFrame_.store(0, std::memory_order_relaxed);
    unknownFailure_.store(0, std::memory_order_relaxed);
    acknowledgementsAccepted_.store(0, std::memory_order_relaxed);
    acknowledgementsRejected_.store(0, std::memory_order_relaxed);
    acknowledgementFailures_.store(0, std::memory_order_relaxed);
    lastAttemptedGeneration_.store(0, std::memory_order_relaxed);
    lastSubmittedGeneration_.store(0, std::memory_order_relaxed);
}

RwuiSharedTextureProducerDiagnostics
SharedGpuFrameProducerRuntime::Diagnostics() const noexcept {
    std::uint32_t flags{};
    if (Bound()) flags |= RWUI_SHARED_TEXTURE_PRODUCER_BOUND;
    if (ConsumerConnected()) flags |= RWUI_SHARED_TEXTURE_PRODUCER_CONNECTED;
    if (consumerValidated_.load(std::memory_order_acquire)) {
        flags |= RWUI_SHARED_TEXTURE_PRODUCER_CONSUMER_VALIDATED;
    }
    if (adapterReady_.load(std::memory_order_acquire)) {
        flags |= RWUI_SHARED_TEXTURE_PRODUCER_ADAPTER_READY;
    }
    if (AcceleratedReady()) {
        flags |= RWUI_SHARED_TEXTURE_PRODUCER_ACCELERATED_READY;
    }
    RwuiSharedTextureProducerDiagnostics diagnostics{
        sizeof(RwuiSharedTextureProducerDiagnostics), 1, 0,
        lastStatus_.load(std::memory_order_relaxed), flags,
        probeAttempts_.load(std::memory_order_relaxed),
        submitAttempts_.load(std::memory_order_relaxed),
        submitted_.load(std::memory_order_relaxed),
        backpressure_.load(std::memory_order_relaxed),
        sessionInvalid_.load(std::memory_order_relaxed),
        adapterOrResourceInvalid_.load(std::memory_order_relaxed),
        deviceOrCopyFailure_.load(std::memory_order_relaxed),
        producerStopped_.load(std::memory_order_relaxed),
        invalidFrame_.load(std::memory_order_relaxed),
        unknownFailure_.load(std::memory_order_relaxed),
        acknowledgementsAccepted_.load(std::memory_order_relaxed),
        acknowledgementsRejected_.load(std::memory_order_relaxed),
        acknowledgementFailures_.load(std::memory_order_relaxed),
        lastAttemptedGeneration_.load(std::memory_order_relaxed),
        lastSubmittedGeneration_.load(std::memory_order_relaxed),
        lastAcknowledgedGeneration_.load(std::memory_order_acquire),
        0, 0, 0, 0, {},
    };
    std::unique_lock adapterLock(submitMutex_, std::try_to_lock);
    if (adapterLock.owns_lock()) {
        diagnostics.adapterLuidHigh = adapterLuid_.HighPart;
        diagnostics.adapterLuidLow = adapterLuid_.LowPart;
        diagnostics.adapterVendorId = adapterVendorId_;
        diagnostics.adapterDeviceId = adapterDeviceId_;
        std::copy(
            adapterDescription_.begin(),
            adapterDescription_.end(),
            std::begin(diagnostics.adapterDescription));
    }
    return diagnostics;
}

bool SharedGpuFrameProducerRuntime::Bound() const noexcept {
    return bound_.load(std::memory_order_acquire);
}

bool SharedGpuFrameProducerRuntime::ConsumerConnected() const noexcept {
    return connected_.load(std::memory_order_acquire);
}

bool SharedGpuFrameProducerRuntime::AcceleratedReady() const noexcept {
    return Bound() && ConsumerConnected() &&
        consumerValidated_.load(std::memory_order_acquire) &&
        adapterReady_.load(std::memory_order_acquire);
}

bool SharedGpuFrameProducerRuntime::SetPresentationVisible(
    const bool visible) noexcept {
    if (!bound_.load(std::memory_order_acquire)) return false;
    const bool prior = presentationVisible_.exchange(
        visible, std::memory_order_acq_rel);
    if (prior == visible) return true;
    presentationEpoch_.fetch_add(1, std::memory_order_acq_rel);
    {
        std::scoped_lock outbox(outboxMutex_);
        hasPendingPresentation_ = true;
    }
    return wakeEvent_ != nullptr && SetEvent(wakeEvent_) != FALSE;
}

bool SharedGpuFrameProducerRuntime::SelectHardwareAdapter(
    HANDLE const transientHandle) noexcept {
    Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return false;
    for (UINT index = 0;; ++index) {
        Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
        if (factory->EnumAdapters1(index, &adapter) == DXGI_ERROR_NOT_FOUND) break;
        DXGI_ADAPTER_DESC1 adapterDescription{};
        if (FAILED(adapter->GetDesc1(&adapterDescription)) ||
            (adapterDescription.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
        Microsoft::WRL::ComPtr<ID3D11Device> device;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
        D3D_FEATURE_LEVEL level{};
        if (FAILED(D3D11CreateDevice(
                adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
                D3D11_SDK_VERSION, &device, &level, &context))) continue;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> probe;
        Microsoft::WRL::ComPtr<ID3D11Device1> device1;
        if (SUCCEEDED(device.As(&device1))) {
            device1->OpenSharedResource1(
                transientHandle, IID_PPV_ARGS(&probe));
        }
        if (probe == nullptr) {
            device->OpenSharedResource(
                transientHandle, IID_PPV_ARGS(&probe));
        }
        if (!SupportedSourceTexture(probe.Get())) continue;
        if (!producer_.Initialize(
                device.Get(), context.Get(), endpoint_.targetConsumerProcessId,
                endpoint_.sessionIdHigh, endpoint_.sessionIdLow)) continue;
        adapterLuid_ = adapterDescription.AdapterLuid;
        adapterVendorId_ = adapterDescription.VendorId;
        adapterDeviceId_ = adapterDescription.DeviceId;
        std::copy_n(
            adapterDescription.Description,
            adapterDescription_.size() - 1,
            adapterDescription_.begin());
        adapterDescription_.back() = L'\0';
        adapterReady_.store(true, std::memory_order_release);
        return true;
    }
    return false;
}

void SharedGpuFrameProducerRuntime::ResetProducerPoolAfterRecycleFailure()
    noexcept {
    // This path runs on the control worker, never Present. Exclude accelerated
    // paint callbacks, discard descriptors that name the retiring pool, and
    // force the next probe to rediscover CEF's hardware adapter.
    std::scoped_lock submitLock(submitMutex_);
    {
        std::scoped_lock outboxLock(outboxMutex_);
        pending_ = {};
        hasPending_ = false;
    }
    producer_.Reset();
    adapterLuid_ = {};
    adapterVendorId_ = 0;
    adapterDeviceId_ = 0;
    adapterDescription_.fill(L'\0');
    adapterReady_.store(false, std::memory_order_release);
}

void SharedGpuFrameProducerRuntime::DemoteAcceleratedRoute() noexcept {
    // A hard failure after ACK means the cached adapter/resource/session can
    // no longer be trusted. Stop publishing capabilities immediately and ask
    // the worker to quiesce; managed policy performs lifecycle-safe Stop/Start
    // before probing a new CEF device or adapter.
    consumerValidated_.store(false, std::memory_order_release);
    adapterReady_.store(false, std::memory_order_release);
    bound_.store(false, std::memory_order_release);
    stop_.store(true, std::memory_order_release);
    if (wakeEvent_ != nullptr) SetEvent(wakeEvent_);
}

void SharedGpuFrameProducerRuntime::Worker() noexcept {
    while (!stop_.load(std::memory_order_acquire) &&
        !connected_.load(std::memory_order_acquire)) {
        const auto result = server_.WaitForConsumer(250);
        if (result == SharedGpuFrameChannelError::None) {
            connected_.store(true, std::memory_order_release);
            break;
        }
        if (result != SharedGpuFrameChannelError::ConnectionTimedOut) {
            bound_.store(false, std::memory_order_release);
            consumerValidated_.store(false, std::memory_order_release);
            return;
        }
    }
    while (!stop_.load(std::memory_order_acquire) &&
        connected_.load(std::memory_order_acquire)) {
        WaitForSingleObject(wakeEvent_, 100);
        SharedGpuPresentationControl presentation{};
        bool sendPresentation{};
        SharedGpuFrameDescriptorV1 descriptor{};
        bool sendDescriptor{};
        {
            std::unique_lock outbox(outboxMutex_, std::try_to_lock);
            if (!outbox.owns_lock()) continue;
            if (hasPendingPresentation_) {
                presentation = {
                    presentationEpoch_.load(std::memory_order_acquire),
                    presentationVisible_.load(std::memory_order_acquire),
                };
                hasPendingPresentation_ = false;
                sendPresentation = true;
            }
            if (hasPending_) {
                descriptor = pending_;
                pending_ = {};
                hasPending_ = false;
                sendDescriptor = true;
            }
        }
        if (sendPresentation &&
            server_.SendPresentationControl(presentation) !=
                SharedGpuFrameChannelError::None) {
            if (sendDescriptor && !producer_.TryRecycleUnsent(descriptor)) {
                ResetProducerPoolAfterRecycleFailure();
            }
            bound_.store(false, std::memory_order_release);
            connected_.store(false, std::memory_order_release);
            consumerValidated_.store(false, std::memory_order_release);
            return;
        }
        if (!sendDescriptor) continue;
        bool cpuTransferred{};
        if (cpuBridgeEnabled_) {
            const auto started = CpuFrameTimestampUs();
            SharedGpuFrameDescriptorV1 cpu{};
            HRESULT hr{};
            bool recovered{};
            {
                // Paint uses try_lock: it drops rather than waiting here.
                // Keep source slots unavailable to paint until a failed copy
                // has been retired, including its potentially in-flight query.
                std::scoped_lock submitLock(submitMutex_);
                hr = cpuBridge_.Convert(descriptor, adapterLuid_, stop_, cpu);
                if (hr != S_OK && !stop_.load() && cpuRecoveryBudget_.TryRecover(hr, GetTickCount64())) {
                    recovered = producer_.RetireUnsent(descriptor);
                    if (recovered) {
                        cpuBridge_.Reset();
                        cpuRetryAfterTick_ = GetTickCount64() + 100;
                        cpuRecoveries_.fetch_add(1);
                    }
                }
            }
            cpuTrace_.Record(hr, descriptor.generation, UINT64(descriptor.width) * descriptor.height * 4,
                CpuFrameTimestampUs() - started, backpressure_.load(), cpuBridge_.Stage(), recovered);
            if (hr != S_OK) {
                // No ACK for the failed generation. The already uploaded local
                // game texture remains valid while a new producer frame retries.
                if (recovered) continue;
                // Permanent failures or exhausted recovery budget fail closed.
                bound_.store(false); connected_.store(false); consumerValidated_.store(false);
                return;
            }
            descriptor = cpu; cpuTransferred = true; // GPU source slot was released by Convert.
        }
        if (server_.Send(descriptor) != SharedGpuFrameChannelError::None) {
            if (!cpuTransferred && !producer_.TryRecycleUnsent(descriptor)) {
                ResetProducerPoolAfterRecycleFailure();
            }
            bound_.store(false, std::memory_order_release);
            connected_.store(false, std::memory_order_release);
            consumerValidated_.store(false, std::memory_order_release);
            return;
        }
        SharedGpuFrameAcknowledgement acknowledgement{};
        const auto acknowledged = server_.ReceiveAcknowledgement(
            descriptor, acknowledgement);
        if (acknowledged != SharedGpuFrameChannelError::None ||
            acknowledgement != SharedGpuFrameAcknowledgement::Accepted) {
            if (acknowledged != SharedGpuFrameChannelError::None) {
                acknowledgementFailures_.fetch_add(1, std::memory_order_relaxed);
            } else {
                acknowledgementsRejected_.fetch_add(1, std::memory_order_relaxed);
            }
            const bool recycled = cpuTransferred || producer_.TryRecycleUnsent(descriptor);
            consumerValidated_.store(false, std::memory_order_release);
            if (!recycled) ResetProducerPoolAfterRecycleFailure();
            if (acknowledged != SharedGpuFrameChannelError::None ||
                !recycled) {
                // A failed recycle can mean the consumer already advanced the
                // key, but it can also mean the slot is stranded. Disable the
                // accelerated route rather than guessing and exhausting the
                // bounded pool. Stop performs lifecycle-safe cleanup.
                if (acknowledged != SharedGpuFrameChannelError::None) {
                    bound_.store(false, std::memory_order_release);
                    connected_.store(false, std::memory_order_release);
                    return;
                }
                // A healthy control channel can stay bound after NACK. The
                // next bootstrap probe rebuilds the pool on a verified adapter.
                continue;
            }
            continue;
        }
        acknowledgementsAccepted_.fetch_add(1, std::memory_order_relaxed);
        consumerValidated_.store(true, std::memory_order_release);
        lastAcknowledgedGeneration_.store(
            descriptor.generation, std::memory_order_release);
    }
}

SharedGpuFrameProducerRuntime& GlobalSharedGpuFrameProducerRuntime() noexcept {
    static SharedGpuFrameProducerRuntime runtime;
    return runtime;
}

} // namespace rwui::transport
