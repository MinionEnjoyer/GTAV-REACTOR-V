#include "SharedGpuFrameConsumer.h"

#include <Windows.h>
#include <array>
#include <d3d11_4.h>
#include <utility>

namespace rwui::transport {
namespace {

constexpr std::size_t MaximumWindowsPathCharacters = 32768;

struct PathSlices final {
    std::size_t directoryLength{};
    std::size_t fileOffset{};
};

PathSlices SplitPath(
    const wchar_t* const path,
    const std::size_t length) noexcept {
    for (auto index = length; index != 0; --index) {
        const auto character = path[index - 1];
        if (character == L'\\' || character == L'/') {
            return {index - 1, index};
        }
    }
    return {0, 0};
}

bool ExpectedPreloaderImage(const std::uint32_t producerProcessId) noexcept {
    HANDLE process = OpenProcess(
        PROCESS_QUERY_LIMITED_INFORMATION, FALSE, producerProcessId);
    if (process == nullptr) return false;
    std::array<wchar_t, MaximumWindowsPathCharacters> producerPath{};
    DWORD producerLength = static_cast<DWORD>(producerPath.size());
    const bool queried = QueryFullProcessImageNameW(
        process, 0, producerPath.data(), &producerLength) != FALSE;
    CloseHandle(process);
    if (!queried || producerLength == 0 ||
        producerLength >= producerPath.size()) return false;

    HMODULE module{};
    if (GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&ExpectedPreloaderImage),
            &module) == FALSE) return false;
    std::array<wchar_t, MaximumWindowsPathCharacters> modulePath{};
    const auto moduleLength = GetModuleFileNameW(
        module, modulePath.data(), static_cast<DWORD>(modulePath.size()));
    if (moduleLength == 0 || moduleLength >= modulePath.size()) return false;

    const auto producerSlices = SplitPath(producerPath.data(), producerLength);
    const auto moduleSlices = SplitPath(modulePath.data(), moduleLength);
    constexpr wchar_t ExpectedFileName[] = L"ReactorV.Preloader.exe";
    constexpr auto ExpectedFileNameLength = std::size(ExpectedFileName) - 1;
    const auto producerFileLength =
        producerLength - producerSlices.fileOffset;
    return producerSlices.fileOffset != 0 && moduleSlices.fileOffset != 0 &&
        producerFileLength == ExpectedFileNameLength &&
        _wcsnicmp(
            producerPath.data() + producerSlices.fileOffset,
            ExpectedFileName,
            ExpectedFileNameLength) == 0 &&
        producerSlices.directoryLength == moduleSlices.directoryLength &&
        _wcsnicmp(
            producerPath.data(), modulePath.data(),
            producerSlices.directoryLength) == 0;
}

bool SameComIdentity(IUnknown* first, IUnknown* second) noexcept {
    if (first == nullptr || second == nullptr) return false;
    Microsoft::WRL::ComPtr<IUnknown> firstIdentity;
    Microsoft::WRL::ComPtr<IUnknown> secondIdentity;
    return SUCCEEDED(first->QueryInterface(IID_PPV_ARGS(&firstIdentity))) &&
        SUCCEEDED(second->QueryInterface(IID_PPV_ARGS(&secondIdentity))) &&
        firstIdentity.Get() == secondIdentity.Get();
}

bool DeviceAdapterLuid(
    ID3D11Device* const device,
    LUID& adapterLuid) noexcept {
    adapterLuid = {};
    if (device == nullptr) return false;
    Microsoft::WRL::ComPtr<IDXGIDevice> dxgiDevice;
    Microsoft::WRL::ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC description{};
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice))) ||
        FAILED(dxgiDevice->GetAdapter(&adapter)) ||
        FAILED(adapter->GetDesc(&description))) return false;
    adapterLuid = description.AdapterLuid;
    return true;
}

bool SameLuid(const LUID& first, const LUID& second) noexcept {
    return first.HighPart == second.HighPart &&
        first.LowPart == second.LowPart;
}

} // namespace

SharedGpuFrameConsumer::PresentLease::PresentLease(
    std::unique_lock<std::mutex>&& lock,
    ID3D11ShaderResourceView* const view,
    const std::uint64_t generation) noexcept
    : lock_(std::move(lock)), view_(view), generation_(generation) {
}

SharedGpuFrameConsumer::PresentLease::~PresentLease() {
    Release();
}

SharedGpuFrameConsumer::PresentLease::PresentLease(
    PresentLease&& other) noexcept
    : lock_(std::move(other.lock_)),
      view_(other.view_),
      generation_(other.generation_) {
    other.view_ = nullptr;
    other.generation_ = 0;
}

SharedGpuFrameConsumer::PresentLease&
SharedGpuFrameConsumer::PresentLease::operator=(PresentLease&& other) noexcept {
    if (this == &other) return *this;
    Release();
    lock_ = std::move(other.lock_);
    view_ = other.view_;
    generation_ = other.generation_;
    other.view_ = nullptr;
    other.generation_ = 0;
    return *this;
}

void SharedGpuFrameConsumer::PresentLease::Release() noexcept {
    view_ = nullptr;
    generation_ = 0;
    if (lock_.owns_lock()) lock_.unlock();
}

SharedGpuFrameConsumer::~SharedGpuFrameConsumer() {
    Stop();
}

bool SharedGpuFrameConsumer::Arm() noexcept {
    std::scoped_lock lock(lifecycleMutex_);
    if (armed_.load(std::memory_order_acquire)) return true;
    stage_.store(SharedGpuFrameConsumerStage::Idle, std::memory_order_release);
    lastReceiveError_.store(0, std::memory_order_relaxed);
    lastImportError_.store(0, std::memory_order_relaxed);
    lastImportHresult_.store(0, std::memory_order_relaxed);
    discoveryMisses_.store(0, std::memory_order_relaxed);
    producerImageRejects_.store(0, std::memory_order_relaxed);
    connectFailures_.store(0, std::memory_order_relaxed);
    receiveFailures_.store(0, std::memory_order_relaxed);
    copyFailures_.store(0, std::memory_order_relaxed);
    acknowledgementFailures_.store(0, std::memory_order_relaxed);
    receivedFrames_.store(0, std::memory_order_relaxed);
    publishedFrames_.store(0, std::memory_order_relaxed);
    acknowledgementsAccepted_.store(0, std::memory_order_relaxed);
    acknowledgementsRejected_.store(0, std::memory_order_relaxed);
    lastReceivedGeneration_.store(0, std::memory_order_relaxed);
    lastPublishedGeneration_.store(0, std::memory_order_relaxed);
    presentationUpdates_.store(0, std::memory_order_relaxed);
    ClearExternalPresentation();
    stop_.store(false, std::memory_order_release);
    stopEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (stopEvent_ == nullptr) {
        stop_.store(true, std::memory_order_release);
        return false;
    }
    try {
        worker_ = std::thread(&SharedGpuFrameConsumer::Worker, this);
    } catch (...) {
        stop_.store(true, std::memory_order_release);
        CloseHandle(stopEvent_);
        stopEvent_ = nullptr;
        return false;
    }
    armed_.store(true, std::memory_order_release);
    return true;
}

bool SharedGpuFrameConsumer::BindDevice(
    ID3D11Device* const consumerDevice,
    ID3D11DeviceContext* const immediateContext,
    const bool allowLegacyBridge) noexcept {
    LUID adapterLuid{};
    return DeviceAdapterLuid(consumerDevice, adapterLuid) &&
        BindDevice(consumerDevice, immediateContext, adapterLuid, allowLegacyBridge);
}

bool SharedGpuFrameConsumer::BindDevice(
    ID3D11Device* const consumerDevice,
    ID3D11DeviceContext* const immediateContext,
    const LUID& authoritativeAdapterLuid,
    const bool allowLegacyBridge) noexcept {
    if (consumerDevice == nullptr || immediateContext == nullptr) return false;
    // Binding runs on compositor preparation/resize lifecycle paths, never on
    // Present. It must commit reliably: a one-shot try_lock can race discovery
    // cleanup and permanently leave the receiver without a device.
    std::scoped_lock lock(frameMutex_, contextMutex_);
    if (SameComIdentity(device_.Get(), consumerDevice) &&
        SameComIdentity(context_.Get(), immediateContext) &&
        legacyBridgeAllowed_.load() == allowLegacyBridge) {
        return multithreadProtectionReady_;
    }
    LUID deviceAdapterLuid{};
    if (!DeviceAdapterLuid(consumerDevice, deviceAdapterLuid) ||
        !SameLuid(deviceAdapterLuid, authoritativeAdapterLuid)) return false;
    // The receiver copies on its control worker while Present renders on the
    // game thread. Ask the D3D runtime to serialize immediate-context access
    // in addition to our fail-open gate; game-owned D3D11 devices do not
    // guarantee this protection is enabled by default.
    // ID3D11Multithread is the authoritative D3D11 immediate-context gate.
    // Do not silently fall back to the older D3D10 interface: failure means
    // off-thread copy/upload is unavailable and the compositor stays hidden.
    Microsoft::WRL::ComPtr<ID3D11Multithread> multithread;
    const bool protectedContext = SUCCEEDED(immediateContext->QueryInterface(
            IID_PPV_ARGS(&multithread))) &&
        (multithread->SetMultithreadProtected(TRUE),
            multithread->GetMultithreadProtected() != FALSE);
    adapterLuidPublisher_.Clear();
    ++deviceEpoch_;
    ClearLatestLocked();
    ClearImportedSlotsLocked();
    ResetLegacyBridgeLocked();
    context_.Reset();
    device_.Reset();
    multithreadProtectionReady_ = false;
    if (!protectedContext) return false;
    device_ = consumerDevice;
    context_ = immediateContext;
    if (!adapterLuidPublisher_.Publish(authoritativeAdapterLuid)) {
        context_.Reset();
        device_.Reset();
        return false;
    }
    multithreadProtectionReady_ = true;
    legacyBridgeAllowed_.store(allowLegacyBridge);
    cpuBridgeAllowed_ = cpuBridgeForTesting_ || (allowLegacyBridge && LegacyCpuFramesEnabled(GetCurrentProcessId()));
    cpuTrace_.SetPath(CpuFrameLogPath(GetCurrentProcessId(), L"Consumer"));
    return true;
}

void SharedGpuFrameConsumer::UnbindDevice() noexcept {
    // Like BindDevice, retirement is lifecycle work and must not be dropped.
    // The context gate also lets an in-flight Present finish before resources
    // and the bound immediate context are retired.
    std::scoped_lock lock(frameMutex_, contextMutex_);
    adapterLuidPublisher_.Clear();
    ++deviceEpoch_;
    ClearLatestLocked();
    ClearImportedSlotsLocked();
    ResetLegacyBridgeLocked();
    context_.Reset();
    device_.Reset();
    multithreadProtectionReady_ = false;
}

void SharedGpuFrameConsumer::Stop() noexcept {
    std::unique_lock lock(lifecycleMutex_);
    stop_.store(true, std::memory_order_release);
    if (stopEvent_ != nullptr) SetEvent(stopEvent_);
    // TryReceive is a PeekNamedPipe path and every idle wait is cancelable, so
    // joining does not inherit discovery backoff or channel write timeouts.
    lock.unlock();
    if (worker_.joinable()) worker_.join();
    lock.lock();
    client_.Close();
    if (stopEvent_ != nullptr) CloseHandle(stopEvent_);
    stopEvent_ = nullptr;
    connected_.store(false, std::memory_order_release);
    ClearExternalPresentation();
    endpoint_ = {};
    armed_.store(false, std::memory_order_release);
    lock.unlock();
    std::scoped_lock frameLock(frameMutex_, contextMutex_);
    ++deviceEpoch_;
    ClearLatestLocked();
    ClearImportedSlotsLocked();
    ResetLegacyBridgeLocked();
    context_.Reset();
    device_.Reset();
    multithreadProtectionReady_ = false;
    adapterLuidPublisher_.Close();
}

SharedGpuFrameConsumer::PresentLease
SharedGpuFrameConsumer::TryAcquireLatestForPresent() noexcept {
    // CopyAndPublish does not take this gate until after discovery, import, and
    // all keyed-mutex retries have completed. Waiting here is therefore bounded
    // to the local CopyResource/Flush sequence and prevents a visible native
    // plane from disappearing for one or more GTA Presents during every CEF
    // paint.
    std::unique_lock lock(contextMutex_);
    return PresentLease(
        std::move(lock), latestView_.Get(), latestGeneration_);
}

bool SharedGpuFrameConsumer::Connected() const noexcept {
    return connected_.load(std::memory_order_acquire);
}

SharedGpuFrameConsumerDiagnostics
SharedGpuFrameConsumer::Diagnostics() const noexcept {
    return {
        stage_.load(std::memory_order_acquire),
        lastReceiveError_.load(std::memory_order_relaxed),
        lastImportError_.load(std::memory_order_relaxed),
        lastImportHresult_.load(std::memory_order_relaxed),
        discoveryMisses_.load(std::memory_order_relaxed),
        producerImageRejects_.load(std::memory_order_relaxed),
        connectFailures_.load(std::memory_order_relaxed),
        receiveFailures_.load(std::memory_order_relaxed),
        copyFailures_.load(std::memory_order_relaxed),
        acknowledgementFailures_.load(std::memory_order_relaxed),
        receivedFrames_.load(std::memory_order_relaxed),
        publishedFrames_.load(std::memory_order_relaxed),
        acknowledgementsAccepted_.load(std::memory_order_relaxed),
        acknowledgementsRejected_.load(std::memory_order_relaxed),
        importedResourceCount_.load(std::memory_order_relaxed),
        lastReceivedGeneration_.load(std::memory_order_relaxed),
        lastPublishedGeneration_.load(std::memory_order_relaxed),
        presentationUpdates_.load(std::memory_order_relaxed),
        externalPresentationEpoch_.load(std::memory_order_acquire),
        externalPresentationVisible_.load(std::memory_order_acquire),
    };
}

void SharedGpuFrameConsumer::ClearExternalPresentation() noexcept {
    externalPresentationVisible_.store(false, std::memory_order_release);
    externalPresentationEpoch_.store(0, std::memory_order_release);
}

void SharedGpuFrameConsumer::ClearLatestLocked() noexcept {
    cpuSpareView_.Reset(); cpuSpareTexture_.Reset();
    latestView_.Reset();
    latestTexture_.Reset();
    latestGeneration_ = 0;
    latestSessionHigh_ = 0;
    latestSessionLow_ = 0;
    latestDeviceEpoch_ = 0;
}

void SharedGpuFrameConsumer::ClearImportedSlotsLocked() noexcept {
    for (auto& imported : importedSlots_) imported = {};
}

void SharedGpuFrameConsumer::ResetLegacyBridgeLocked() noexcept {
    cpuBridgeAllowed_ = false;
    legacyBridge_.Reset();
    legacyBridgeAllowed_.store(false);
    legacyBridgeActive_.store(false);
    legacyBridgeAttempted_ = false;
    legacyDirectFailure_.store(0);
    legacyBridgedFrames_.store(0);
}

bool SharedGpuFrameConsumer::WaitForStop(
    const std::uint32_t milliseconds) const noexcept {
    if (stop_.load(std::memory_order_acquire)) return true;
    return stopEvent_ != nullptr &&
        WaitForSingleObject(stopEvent_, milliseconds) == WAIT_OBJECT_0;
}

bool SharedGpuFrameConsumer::CopyAndPublish(
    const SharedGpuFrameDescriptorV1& descriptor,
    const SharedGpuFrameChannelEndpoint& endpoint) noexcept {
    lastImportError_.store(0, std::memory_order_relaxed);
    lastImportHresult_.store(0, std::memory_order_relaxed);
    const SharedGpuFrameValidationContext validation{
        endpoint.producerProcessId,
        endpoint.targetConsumerProcessId,
        endpoint.producerCreationTime,
        endpoint.targetConsumerCreationTime,
        endpoint.sessionIdHigh,
        endpoint.sessionIdLow,
        SharedGpuFrameMaximumDimension,
        SharedGpuFrameMaximumDimension,
    };
    if (descriptor.slotIndex >= importedSlots_.size()) {
        lastImportError_.store(UINT32_MAX, std::memory_order_relaxed);
        return false;
    }

    // Import and keyed-mutex acquisition run only on the receiver worker.
    // frameMutex keeps imported slots/device retirement safe, but Present does
    // not take it: a producer scheduler race may require up to 25 x 2 ms here
    // and must not blank the already-published overlay during that interval.
    std::scoped_lock lock(frameMutex_);
    if (device_ == nullptr || context_ == nullptr) {
        lastImportError_.store(UINT32_MAX - 1, std::memory_order_relaxed);
        return false;
    }
    const auto deviceEpoch = deviceEpoch_;
    if (descriptor.synchronization == SharedGpuSynchronization::CpuBgraMapping)
        return UploadCpuFrame(descriptor, validation);
    auto& imported = importedSlots_[descriptor.slotIndex];
    if (!imported.RebindDescriptor(descriptor, validation)) {
        imported = {};
        HRESULT importHresult = S_OK;
        auto importError = importer_(legacyBridgeActive_.load()
                ? legacyBridge_.ImportDevice() : device_.Get(),
            descriptor, validation, imported, &importHresult);
        if (!legacyBridgeActive_.load() && !legacyBridgeAttempted_ &&
            ShouldBridge(legacyBridgeAllowed_.load(), importError, importHresult)) {
            legacyDirectFailure_.store(static_cast<std::uint32_t>(importHresult));
            legacyBridgeAttempted_ = true; // no device-creation retry storm
            ClearImportedSlotsLocked();
            const HRESULT bridgeResult = legacyBridge_.Initialize(device_.Get());
            if (SUCCEEDED(bridgeResult)) {
                legacyBridgeActive_.store(true);
                // Run the SAME descriptor/process/identity validation again;
                // only the D3D11 import device changes.
                importError = importer_(legacyBridge_.ImportDevice(), descriptor,
                    validation, imported, &importHresult);
            } else importHresult = bridgeResult;
        }
        lastImportHresult_.store(
            static_cast<std::uint32_t>(importHresult), std::memory_order_relaxed);
        lastImportError_.store(
            static_cast<std::uint32_t>(importError),
            std::memory_order_relaxed);
        if (importError != SharedGpuD3D11ImportError::None) return false;
        importedResourceCount_.fetch_add(1, std::memory_order_relaxed);
    }

    // This is a worker wait, not a Present wait. A descriptor is published only
    // after the producer releases the consumer key; retry briefly so a scheduler
    // race cannot strand a bounded producer slot.
    bool acquired{};
    for (unsigned attempt = 0; attempt != 25 &&
         !stop_.load(std::memory_order_acquire); ++attempt) {
        if (imported.TryAcquireForPresent()) {
            acquired = true;
            break;
        }
        if (WaitForStop(2)) break;
    }
    if (!acquired) {
        imported = {};
        return false;
    }

    ID3D11Texture2D* copySource = imported.Texture();
    const bool bridged = legacyBridgeActive_.load();
    if (bridged) {
        // This copy and both bridge mutex acquisitions happen before the
        // Present/context gate. GPU synchronization never stalls that gate.
        const HRESULT bridgeResult = legacyBridge_.StageSource(copySource, stopEvent_);
        const bool released = imported.ReleaseAfterPresent();
        if (bridgeResult != S_OK || !released) {
            if (bridgeResult == S_OK) legacyBridge_.ReleaseGame();
            return false;
        }
        copySource = legacyBridge_.GameTexture();
    }
    const auto releaseSource = [&]() noexcept {
        return bridged ? legacyBridge_.ReleaseGame() == S_OK : imported.ReleaseAfterPresent();
    };

    // From this point through publication we touch the immediate context and
    // the latest local texture. Present waits only for this short local GPU
    // submission, then immediately reuses either the prior or newly published
    // frame. No producer wait or IPC occurs while this gate is held.
    std::scoped_lock contextLock(contextMutex_);

    const bool sameSession =
        latestSessionHigh_ == endpoint.sessionIdHigh &&
        latestSessionLow_ == endpoint.sessionIdLow;
    if (sameSession && latestDeviceEpoch_ == deviceEpoch &&
        latestGeneration_ >= descriptor.generation) {
        return releaseSource();
    }

    D3D11_TEXTURE2D_DESC sourceDescription{};
    copySource->GetDesc(&sourceDescription);
    D3D11_TEXTURE2D_DESC localDescription{};
    if (latestTexture_ != nullptr) latestTexture_->GetDesc(&localDescription);
    if (latestTexture_ == nullptr ||
        localDescription.Width != sourceDescription.Width ||
        localDescription.Height != sourceDescription.Height ||
        localDescription.Format != sourceDescription.Format ||
        latestDeviceEpoch_ != deviceEpoch) {
        ClearLatestLocked();
        sourceDescription.Usage = D3D11_USAGE_DEFAULT;
        sourceDescription.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        sourceDescription.CPUAccessFlags = 0;
        sourceDescription.MiscFlags = 0;
        if (FAILED(device_->CreateTexture2D(
                &sourceDescription, nullptr, &latestTexture_)) ||
            FAILED(device_->CreateShaderResourceView(
                latestTexture_.Get(), nullptr, &latestView_))) {
            ClearLatestLocked();
            releaseSource();
            return false;
        }
    }

    context_->CopyResource(latestTexture_.Get(), copySource);
    // The producer may overwrite this keyed slot immediately after release.
    // Flush guarantees the copy that consumes it has reached the driver first.
    context_->Flush();
    if (!releaseSource()) {
        imported = {};
        ClearLatestLocked();
        return false;
    }
    latestGeneration_ = descriptor.generation;
    latestSessionHigh_ = endpoint.sessionIdHigh;
    latestSessionLow_ = endpoint.sessionIdLow;
    latestDeviceEpoch_ = deviceEpoch;
    if (bridged) legacyBridgedFrames_.fetch_add(1);
    return true;
}

bool SharedGpuFrameConsumer::UploadCpuFrame(const SharedGpuFrameDescriptorV1& descriptor,
    const SharedGpuFrameValidationContext& validation) noexcept {
    const auto started = CpuFrameTimestampUs();
    const auto finish = [&](HRESULT hr) {
        lastImportHresult_.store(static_cast<ULONG>(hr));
        if (hr != S_OK) lastImportError_.store(UINT32_MAX - 2);
        cpuTrace_.Record(hr, descriptor.generation, UINT64(descriptor.width) * descriptor.height * 4,
            CpuFrameTimestampUs() - started);
        return hr == S_OK;
    };
    if (!cpuBridgeAllowed_ || !multithreadProtectionReady_) return finish(E_ACCESSDENIED);
    CpuFrameMapping mapping;
    auto hr = mapping.Open(descriptor, validation);
    if (hr != S_OK) return finish(hr);
    // Only this worker holds frameMutex_. Allocate/validate away from the
    // immediate-context gate; Present can keep drawing the previous texture.
    D3D11_TEXTURE2D_DESC d{};
    if (cpuSpareTexture_) cpuSpareTexture_->GetDesc(&d);
    if (!cpuSpareTexture_ || d.Width != descriptor.width || d.Height != descriptor.height ||
        d.Format != static_cast<DXGI_FORMAT>(descriptor.pixelFormat)) {
        cpuSpareView_.Reset(); cpuSpareTexture_.Reset(); d = {};
        d.Width = descriptor.width; d.Height = descriptor.height;
        d.MipLevels = d.ArraySize = d.SampleDesc.Count = 1;
        d.Format = static_cast<DXGI_FORMAT>(descriptor.pixelFormat); d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        hr = device_->CreateTexture2D(&d, nullptr, &cpuSpareTexture_);
        if (hr != S_OK) return finish(hr);
        hr = device_->CreateShaderResourceView(cpuSpareTexture_.Get(), nullptr, &cpuSpareView_);
        if (hr != S_OK) { cpuSpareTexture_.Reset(); return finish(hr); }
    }
    {
        std::scoped_lock contextLock(contextMutex_);
        if (latestSessionHigh_ == descriptor.sessionIdHigh && latestSessionLow_ == descriptor.sessionIdLow &&
            latestDeviceEpoch_ == deviceEpoch_ && latestGeneration_ >= descriptor.generation) return false;
        // Source bytes may be reused by the producer only after this call has
        // copied them and the receiver sends its existing authenticated ACK.
        context_->UpdateSubresource(cpuSpareTexture_.Get(), 0, nullptr, mapping.Pixels(), descriptor.width * 4, 0);
        hr = device_->GetDeviceRemovedReason();
        if (hr == S_OK) {
            std::swap(latestTexture_, cpuSpareTexture_); std::swap(latestView_, cpuSpareView_);
            latestGeneration_ = descriptor.generation; latestDeviceEpoch_ = deviceEpoch_;
            latestSessionHigh_ = descriptor.sessionIdHigh; latestSessionLow_ = descriptor.sessionIdLow;
        }
    }
    // No file logging or IPC while holding the Present/context gate.
    return finish(hr);
}

void SharedGpuFrameConsumer::Worker() noexcept {
    try {
        SharedGpuFrameChannelEndpoint activeEndpoint{};
        stage_.store(
            SharedGpuFrameConsumerStage::Discovering,
            std::memory_order_release);
        while (!stop_.load(std::memory_order_acquire)) {
        if (endpoint_.producerProcessId == 0) {
            if (!DiscoverSharedGpuFrameProducer(
                    GetCurrentProcessId(), endpoint_)) {
                const auto priorMisses = discoveryMisses_.fetch_add(
                    1, std::memory_order_relaxed);
                WaitForStop(SharedGpuDiscoveryPollDelayMs(priorMisses));
                continue;
            }
            stage_.store(
                SharedGpuFrameConsumerStage::ProducerDiscovered,
                std::memory_order_release);
            if (!ExpectedPreloaderImage(endpoint_.producerProcessId)) {
                producerImageRejects_.fetch_add(1, std::memory_order_relaxed);
                stage_.store(
                    SharedGpuFrameConsumerStage::ProducerRejected,
                    std::memory_order_release);
                endpoint_ = {};
                WaitForStop(100);
                continue;
            }
            activeEndpoint = endpoint_;
            ClearExternalPresentation();
            std::scoped_lock frameLock(frameMutex_, contextMutex_);
            ClearLatestLocked();
            ClearImportedSlotsLocked();
        }
        if (!client_.Connected()) {
            stage_.store(
                SharedGpuFrameConsumerStage::Connecting,
                std::memory_order_release);
            const auto result = client_.Connect(endpoint_, 250);
            if (result != SharedGpuFrameChannelError::None) {
                connectFailures_.fetch_add(1, std::memory_order_relaxed);
                endpoint_ = {};
                stage_.store(
                    SharedGpuFrameConsumerStage::Discovering,
                    std::memory_order_release);
                WaitForStop(50);
                continue;
            }
            connected_.store(true, std::memory_order_release);
            stage_.store(
                SharedGpuFrameConsumerStage::Connected,
                std::memory_order_release);
        }

        SharedGpuFrameChannelMessage message{};
        stage_.store(
            SharedGpuFrameConsumerStage::Receiving,
            std::memory_order_release);
        const auto receive = client_.TryReceiveMessage(message);
        if (receive == SharedGpuFrameChannelError::ConnectionTimedOut) {
            WaitForStop(2);
            continue;
        }
        if (receive != SharedGpuFrameChannelError::None) {
            lastReceiveError_.store(
                static_cast<std::uint32_t>(receive),
                std::memory_order_relaxed);
            receiveFailures_.fetch_add(1, std::memory_order_relaxed);
            stage_.store(
                SharedGpuFrameConsumerStage::ReceiveFailed,
                std::memory_order_release);
            connected_.store(false, std::memory_order_release);
            ClearExternalPresentation();
            client_.Close();
            endpoint_ = {};
            std::scoped_lock frameLock(frameMutex_, contextMutex_);
            ClearLatestLocked();
            ClearImportedSlotsLocked();
            continue;
        }

        if (message.kind ==
            SharedGpuFrameChannelMessageKind::PresentationControl) {
            const auto priorEpoch = externalPresentationEpoch_.load(
                std::memory_order_acquire);
            if (message.presentation.epoch > priorEpoch) {
                externalPresentationVisible_.store(
                    message.presentation.visible, std::memory_order_release);
                externalPresentationEpoch_.store(
                    message.presentation.epoch, std::memory_order_release);
                presentationUpdates_.fetch_add(1, std::memory_order_relaxed);
                stage_.store(
                    SharedGpuFrameConsumerStage::PresentationUpdated,
                    std::memory_order_release);
            }
            continue;
        }
        const auto& descriptor = message.frame;
        receivedFrames_.fetch_add(1, std::memory_order_relaxed);
        lastReceivedGeneration_.store(
            descriptor.generation, std::memory_order_relaxed);

        // A frame can arrive before the compositor device is bound. Keep the
        // bounded control worker on this descriptor until a device exists, so
        // the producer slot is not silently leaked during startup.
        while (!stop_.load(std::memory_order_acquire)) {
            {
                std::scoped_lock frameLock(frameMutex_);
                if (device_ != nullptr && context_ != nullptr) break;
            }
            WaitForStop(10);
        }
        if (stop_.load(std::memory_order_acquire)) break;
        stage_.store(
            SharedGpuFrameConsumerStage::Copying,
            std::memory_order_release);
        const auto copied = CopyAndPublish(descriptor, activeEndpoint);
        if (!copied) {
            copyFailures_.fetch_add(1, std::memory_order_relaxed);
            stage_.store(
                SharedGpuFrameConsumerStage::CopyFailed,
                std::memory_order_release);
            // A rejected incoming paint is not a presentation boundary. Keep
            // the last consumer-owned texture available so one recoverable
            // producer/import failure cannot flash the visible overlay away.
            // Session disconnect and identity changes retire it explicitly in
            // the branches above and below.
        } else {
            publishedFrames_.fetch_add(1, std::memory_order_relaxed);
            lastPublishedGeneration_.store(
                descriptor.generation, std::memory_order_relaxed);
            stage_.store(
                SharedGpuFrameConsumerStage::Published,
                std::memory_order_release);
        }
        const auto acknowledgement = copied
            ? SharedGpuFrameAcknowledgement::Accepted
            : SharedGpuFrameAcknowledgement::Rejected;
        if (client_.Acknowledge(
                descriptor,
                acknowledgement) !=
            SharedGpuFrameChannelError::None) {
            acknowledgementFailures_.fetch_add(1, std::memory_order_relaxed);
            stage_.store(
                SharedGpuFrameConsumerStage::AcknowledgementFailed,
                std::memory_order_release);
            connected_.store(false, std::memory_order_release);
            ClearExternalPresentation();
            client_.Close();
            endpoint_ = {};
            std::scoped_lock frameLock(frameMutex_, contextMutex_);
            ClearLatestLocked();
            ClearImportedSlotsLocked();
        } else if (acknowledgement ==
            SharedGpuFrameAcknowledgement::Accepted) {
            acknowledgementsAccepted_.fetch_add(1, std::memory_order_relaxed);
        } else {
            acknowledgementsRejected_.fetch_add(1, std::memory_order_relaxed);
        }
        }
    } catch (...) {
        // This is an injected-DLL worker boundary. Any allocation, filesystem,
        // or synchronization failure is a disabled GPU route, never a process
        // termination. Cleanup below retires the last published frame.
    }
    connected_.store(false, std::memory_order_release);
    ClearExternalPresentation();
    stage_.store(
        SharedGpuFrameConsumerStage::Stopped,
        std::memory_order_release);
    client_.Close();
    endpoint_ = {};
    // A disconnected producer must never leave its final frame resident. The
    // compositor will immediately fall back to the CPU mailbox on its next
    // successful Present lease.
    try {
        std::scoped_lock frameLock(frameMutex_, contextMutex_);
        ClearLatestLocked();
        ClearImportedSlotsLocked();
    } catch (...) {
        // Best-effort shutdown only. All public state already reports stopped.
    }
}

} // namespace rwui::transport
