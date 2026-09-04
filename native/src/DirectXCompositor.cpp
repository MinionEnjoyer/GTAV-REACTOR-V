#include "DirectXCompositor.h"
#include "D3D11DeviceProbe.h"
#include "DxgiHookPolicy.h"

namespace rwui {

namespace {

class WrappedResourceLease final {
public:
    WrappedResourceLease(
        ID3D11On12Device* const device,
        ID3D11DeviceContext* const context,
        ID3D11Resource* const resource) noexcept
        : device_(device), context_(context), resource_(resource) {
        if (device_ != nullptr && resource_ != nullptr) {
            ID3D11Resource* resources[]{resource_};
            device_->AcquireWrappedResources(resources, 1);
            acquired_ = true;
        }
    }

    ~WrappedResourceLease() noexcept {
        if (!acquired_) return;
        ID3D11Resource* resources[]{resource_};
        device_->ReleaseWrappedResources(resources, 1);
        // Flush while the device/context and wrapper are guaranteed alive.
        // This also runs if a renderer exception unwinds the draw.
        if (context_ != nullptr) context_->Flush();
    }

    WrappedResourceLease(const WrappedResourceLease&) = delete;
    WrappedResourceLease& operator=(const WrappedResourceLease&) = delete;

private:
    ID3D11On12Device* device_{};
    ID3D11DeviceContext* context_{};
    ID3D11Resource* resource_{};
    bool acquired_{};
};

constexpr DWORD D3D12RetirementTimeoutMilliseconds = 2000;

bool SameComIdentity(IUnknown* const left, IUnknown* const right) noexcept {
    if (left == nullptr || right == nullptr) return false;
    Microsoft::WRL::ComPtr<IUnknown> leftIdentity;
    Microsoft::WRL::ComPtr<IUnknown> rightIdentity;
    return SUCCEEDED(left->QueryInterface(IID_PPV_ARGS(&leftIdentity))) &&
        SUCCEEDED(right->QueryInterface(IID_PPV_ARGS(&rightIdentity))) &&
        leftIdentity.Get() == rightIdentity.Get();
}

} // namespace

DirectXCompositor::DirectXCompositor(FrameMailbox& mailbox) : mailbox_(mailbox) {
}

DirectXCompositor::~DirectXCompositor() {
    ShutdownSharedFrameConsumer();
    Reset();
}

bool DirectXCompositor::ArmSharedFrameConsumer() noexcept {
    const bool preparationArmed = ArmPreparationWorker();
    const bool consumerArmed = sharedFrameConsumer_.Arm();
    return preparationArmed || consumerArmed;
}

void DirectXCompositor::ShutdownSharedFrameConsumer() noexcept {
    StopPreparationWorker();
    sharedFrameConsumer_.Stop();
}

bool DirectXCompositor::ArmPreparationWorker() noexcept {
    std::scoped_lock lock(preparationLifecycleMutex_);
    if (preparationArmed_.load(std::memory_order_acquire)) return true;
    preparationEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (preparationEvent_ == nullptr) return false;
    preparationStop_.store(false, std::memory_order_release);
    try {
        preparationWorker_ = std::thread(
            &DirectXCompositor::PreparationWorker, this);
    } catch (...) {
        CloseHandle(preparationEvent_);
        preparationEvent_ = nullptr;
        preparationStop_.store(true, std::memory_order_release);
        return false;
    }
    preparationArmed_.store(true, std::memory_order_release);
    return true;
}

void DirectXCompositor::StopPreparationWorker() noexcept {
    std::unique_lock lock(preparationLifecycleMutex_);
    preparationArmed_.store(false, std::memory_order_release);
    preparationStop_.store(true, std::memory_order_release);
    if (preparationEvent_ != nullptr) SetEvent(preparationEvent_);
    lock.unlock();
    if (preparationWorker_.joinable()) preparationWorker_.join();
    lock.lock();
    DrainPendingPreparationRequest();
    if (preparationEvent_ != nullptr) CloseHandle(preparationEvent_);
    preparationEvent_ = nullptr;
}

void DirectXCompositor::RequestPrepare(
    IDXGISwapChain* const swapChain,
    ID3D12CommandQueue* const directQueue) noexcept {
    if (swapChain == nullptr ||
        (preparedSwapChain_.load(std::memory_order_acquire) == swapChain &&
            (directQueue == nullptr ||
                preparedQueue_.load(std::memory_order_acquire) ==
                    directQueue)) ||
        !preparationArmed_.load(std::memory_order_acquire) ||
        preparationRequestGate_.test_and_set(std::memory_order_acquire)) {
        return;
    }
    // StopPreparationWorker first clears preparationArmed_, then takes this
    // same gate while draining and closing the event. Rechecking after the
    // nonblocking gate acquisition closes the stale-read race without ever
    // making Present wait for lifecycle teardown.
    if (!preparationArmed_.load(std::memory_order_acquire)) {
        preparationRequestGate_.clear(std::memory_order_release);
        return;
    }
    swapChain->AddRef();
    if (directQueue != nullptr) directQueue->AddRef();
    auto* const priorSwapChain = pendingPreparationSwapChain_.exchange(
        swapChain, std::memory_order_relaxed);
    auto* const priorQueue = pendingPreparationQueue_.exchange(
        directQueue, std::memory_order_relaxed);
    pendingPreparationEpoch_ = preparationEpoch_.load(
        std::memory_order_acquire);
    if (preparationEvent_ != nullptr) SetEvent(preparationEvent_);
    preparationRequestGate_.clear(std::memory_order_release);
    if (priorSwapChain != nullptr) priorSwapChain->Release();
    if (priorQueue != nullptr) priorQueue->Release();
}

void DirectXCompositor::DrainPendingPreparationRequest() noexcept {
    while (preparationRequestGate_.test_and_set(
        std::memory_order_acquire)) {
        SwitchToThread();
    }
    auto* const swapChain = pendingPreparationSwapChain_.exchange(
        nullptr, std::memory_order_relaxed);
    auto* const queue = pendingPreparationQueue_.exchange(
        nullptr, std::memory_order_relaxed);
    pendingPreparationEpoch_ = 0;
    preparationRequestGate_.clear(std::memory_order_release);
    if (swapChain != nullptr) swapChain->Release();
    if (queue != nullptr) queue->Release();
}

void DirectXCompositor::PreparationWorker() noexcept {
    while (!preparationStop_.load(std::memory_order_acquire)) {
        WaitForSingleObject(preparationEvent_, 100);
        if (preparationStop_.load(std::memory_order_acquire)) break;
        if (preparationRequestGate_.test_and_set(
                std::memory_order_acquire)) continue;
        auto* const swapChain = pendingPreparationSwapChain_.exchange(
            nullptr, std::memory_order_relaxed);
        auto* const queue = pendingPreparationQueue_.exchange(
            nullptr, std::memory_order_relaxed);
        const auto preparationEpoch = pendingPreparationEpoch_;
        pendingPreparationEpoch_ = 0;
        preparationRequestGate_.clear(std::memory_order_release);
        if (swapChain == nullptr) {
            if (queue != nullptr) queue->Release();
            continue;
        }
        try {
            Prepare(swapChain, queue, preparationEpoch);
        } catch (...) {
            // Native detours are fail-open. A malformed/removed device may not
            // propagate an exception into the game process.
        }
        swapChain->Release();
        if (queue != nullptr) queue->Release();
    }
}

bool DirectXCompositor::Prepare(
    IDXGISwapChain* const swapChain,
    ID3D12CommandQueue* const directQueue,
    const std::uint64_t expectedPreparationEpoch) noexcept {
    if (swapChain == nullptr) return false;
    try {
        std::scoped_lock lock(mutex_);
        if (retirementFailed_) return false;
        if (blockedPreparationSwapChain_ == swapChain) return false;
        if (blockedPreparationSwapChain_ != nullptr &&
            blockedPreparationSwapChain_ != swapChain) {
            blockedPreparationSwapChain_ = nullptr;
        }
        if (expectedPreparationEpoch != 0 &&
            expectedPreparationEpoch != preparationEpoch_.load(
                std::memory_order_acquire)) return false;
        if (resizingSwapChain_ == swapChain) return false;
        const bool deviceInvalidated = deviceInvalidated_.exchange(
            false, std::memory_order_acq_rel);
        if (deviceInvalidated && activeSwapChain_ == swapChain) {
            if (!ResetUnlocked()) return false;
        }
        if (activeSwapChain_ != swapChain) {
            if (!ResetUnlocked()) return false;
            activeSwapChain_ = swapChain;
        }
        if (api_ == RwuiRenderApi::None) {
            Microsoft::WRL::ComPtr<ID3D11Device> d3d11;
            const bool initialized =
                SUCCEEDED(swapChain->GetDevice(IID_PPV_ARGS(&d3d11)))
                ? InitializeD3D11(swapChain)
                : directQueue != nullptr &&
                    InitializeD3D12(swapChain, directQueue);
            if (!initialized) {
                ResetUnlocked();
                return false;
            }
        }
        if (api_ == RwuiRenderApi::Direct3D11 &&
            d3d11BackBuffers_.empty()) {
            Microsoft::WRL::ComPtr<ID3D11Device> swapChainDevice;
            const bool sameDevice =
                SUCCEEDED(swapChain->GetDevice(
                    IID_PPV_ARGS(&swapChainDevice))) &&
                SameComIdentity(swapChainDevice.Get(), d3d11Device_.Get());
            if (!sameDevice || d3d11Context_ == nullptr ||
                renderer_ == nullptr) {
                if (!ResetUnlocked()) return false;
                activeSwapChain_ = swapChain;
                if (!InitializeD3D11(swapChain)) {
                    ResetUnlocked();
                    return false;
                }
            } else if (!InitializeD3D11BackBuffers(swapChain)) {
                ResetUnlocked();
                return false;
            }
        }
        if (api_ == RwuiRenderApi::Direct3D12 &&
            directQueue != nullptr && directQueue != d3d12Queue_.Get()) {
            if (!ResetUnlocked()) return false;
            activeSwapChain_ = swapChain;
            if (!InitializeD3D12(swapChain, directQueue)) {
                ResetUnlocked();
                return false;
            }
        }
        if (api_ == RwuiRenderApi::Direct3D12 &&
            (wrappedBackBuffers_.empty() || wrappedTextures_.empty())) {
            // ResizeBuffers1 may temporarily retire the route for a mixed
            // queue set. Never rebuild against a missing/stale queue; a later
            // uniform capture supplies the exact DIRECT queue and retries.
            if (directQueue == nullptr || directQueue != d3d12Queue_.Get()) {
                return false;
            }
            if (!InitializeD3D12BackBuffers(swapChain)) {
                ResetUnlocked();
                return false;
            }
        }
        // A missing mailbox frame is not a preparation failure. A later
        // RWUI_SubmitFrame stages it on the submitting thread.
        renderer_->StageLatestCpuFrame();
        PrepareStartupStatusUnlocked();
        preparedQueue_.store(
            api_ == RwuiRenderApi::Direct3D12 ? d3d12Queue_.Get() : nullptr,
            std::memory_order_release);
        preparedSwapChain_.store(swapChain, std::memory_order_release);
        return true;
    } catch (...) {
        Reset();
        return false;
    }
}

bool DirectXCompositor::StageLatestCpuFrame() noexcept {
    try {
        // CEF/managed submit callbacks must never queue behind swap-chain
        // preparation or a concurrent Present. The mailbox retains the latest
        // immutable frame, so a missed staging attempt is safely retried by a
        // later submit or preparation pass.
        std::unique_lock lock(mutex_, std::try_to_lock);
        if (!lock.owns_lock()) return false;
        return renderer_ != nullptr && renderer_->StageLatestCpuFrame();
    } catch (...) {
        return false;
    }
}

void DirectXCompositor::PrepareStartupStatusUnlocked() {
    if (!startupStatusActive_.load() || api_ != RwuiRenderApi::Direct3D11 || d3d11BackBuffers_.empty()) return;
    if (!startupStatusRenderer_) {
        startupStatusRenderer_ = std::make_unique<D3D11OverlayRenderer>(d3d11Device_.Get(), d3d11Context_.Get(), startupStatusMailbox_);
    }
    for (const auto& buffer : d3d11BackBuffers_) startupStatusRenderer_->PrepareBackBuffer(buffer.Get());
    startupStatusRenderer_->StageLatestCpuFrame();
}

bool DirectXCompositor::SubmitStartupStatus(const void* pixels, int width, int height, int stride, std::uint64_t generation) noexcept {
    if (!pixels) { startupStatusActive_.store(false); return true; }
    if (!ValidStartupStatusFrame(width, height, stride)) return false;
    try {
        if (!startupStatusMailbox_.Submit(pixels, width, height, stride, generation)) return false;
        startupStatusActive_.store(true);
        // Called only by the bootstrap maintenance worker, never Present.
        std::unique_lock lock(mutex_, std::try_to_lock);
        if (!lock.owns_lock()) return false;
        PrepareStartupStatusUnlocked();
        return startupStatusRenderer_ && startupStatusRenderer_->CpuFramesStaged() > 0;
    } catch (...) { return false; }
}

bool DirectXCompositor::RenderStartupStatus(IDXGISwapChain* swapChain) noexcept {
    try {
        if (!startupStatusActive_.load()) return false;
        std::unique_lock lock(mutex_, std::try_to_lock);
        if (!lock.owns_lock() || !ShouldRenderStartupStatus(startupStatusActive_.load(),
                sharedFrameConsumer_.ExternalPresentationVisible(), api_ == RwuiRenderApi::Direct3D11) ||
            activeSwapChain_ != swapChain || d3d11BackBuffers_.empty() || !startupStatusRenderer_) return false;
        auto sharedContext = sharedFrameConsumer_.TryAcquireLatestForPresent();
        if (!sharedContext.OwnsContext()) return false;
        const auto bounds = StartupStatusPlacement(width_, height_);
        if (bounds.width <= 0 || bounds.height <= 0) return false;
        const D3D11_VIEWPORT viewport{bounds.x, bounds.y, bounds.width, bounds.height, 0.0f, 1.0f};
        return startupStatusRenderer_->Render(d3d11BackBuffers_.front().Get(), false, &viewport);
    } catch (...) { return false; }
}

bool DirectXCompositor::Render(
    IDXGISwapChain* swapChain,
    ID3D12CommandQueue* directQueue,
    const bool clearBeforeOverlay,
    const bool allowExternalFrame) noexcept {
    if (swapChain == nullptr) return false;
    try {
        std::unique_lock lock(mutex_, std::try_to_lock);
        if (!lock.owns_lock()) return false;
        const bool wrongQueue = api_ == RwuiRenderApi::Direct3D12 &&
            directQueue != nullptr && directQueue != d3d12Queue_.Get();
        const bool missingD3D11BackBuffer =
            api_ == RwuiRenderApi::Direct3D11 &&
            d3d11BackBuffers_.empty();
        if (deviceInvalidated_.load(std::memory_order_acquire) ||
            activeSwapChain_ != swapChain ||
            api_ == RwuiRenderApi::None || renderer_ == nullptr || wrongQueue ||
            missingD3D11BackBuffer) {
            lock.unlock();
            RequestPrepare(swapChain, directQueue);
            return false;
        }
        return api_ == RwuiRenderApi::Direct3D11
            ? RenderD3D11(
                swapChain, clearBeforeOverlay, allowExternalFrame)
            : RenderD3D12(
                swapChain, clearBeforeOverlay, allowExternalFrame);
    } catch (...) {
        return false;
    }
}

bool DirectXCompositor::BeforeResize(IDXGISwapChain* swapChain) noexcept {
    try {
        std::scoped_lock lock(mutex_);
        preparationEpoch_.fetch_add(1, std::memory_order_acq_rel);
        resizingSwapChain_ = swapChain;
        const bool retired = activeSwapChain_ != swapChain ||
            (api_ == RwuiRenderApi::Direct3D12
                ? ReleaseD3D12BackBuffersUnlocked(true)
                : api_ == RwuiRenderApi::Direct3D11
                    ? ReleaseD3D11BackBuffersUnlocked()
                    : ResetUnlocked(true));
        blockedPreparationSwapChain_ = retired ? nullptr : swapChain;
        return retired;
    } catch (...) {
        blockedPreparationSwapChain_ = swapChain;
        return false;
    }
}

void DirectXCompositor::AfterResize(IDXGISwapChain* swapChain) noexcept {
    try {
        std::scoped_lock lock(mutex_);
        if (resizingSwapChain_ == swapChain) {
            resizingSwapChain_ = nullptr;
            preparationEpoch_.fetch_add(1, std::memory_order_acq_rel);
        }
    } catch (...) {
    }
}

void DirectXCompositor::NotifyDeviceFailure(
    IDXGISwapChain* const swapChain,
    ID3D12CommandQueue* const directQueue) noexcept {
    if (swapChain == nullptr) return;
    deviceInvalidated_.store(true, std::memory_order_release);
    preparedSwapChain_.store(nullptr, std::memory_order_release);
    preparedQueue_.store(nullptr, std::memory_order_release);
    RequestPrepare(swapChain, directQueue);
}

void DirectXCompositor::Reset() noexcept {
    try {
        std::scoped_lock lock(mutex_);
        ResetUnlocked();
        if (!retirementFailed_) blockedPreparationSwapChain_ = nullptr;
    } catch (...) {
    }
}

bool DirectXCompositor::WaitForD3D12IdleUnlocked() noexcept {
    if (api_ != RwuiRenderApi::Direct3D12) return true;
    if (d3d12Queue_ == nullptr || d3d12RetirementFence_ == nullptr ||
        d3d12RetirementEvent_ == nullptr) return false;
    const auto value = ++d3d12RetirementValue_;
    if (FAILED(d3d12Queue_->Signal(d3d12RetirementFence_.Get(), value))) {
        return false;
    }
    if (d3d12RetirementFence_->GetCompletedValue() >= value) return true;
    ResetEvent(d3d12RetirementEvent_);
    if (FAILED(d3d12RetirementFence_->SetEventOnCompletion(
            value, d3d12RetirementEvent_))) return false;
    return WaitForSingleObject(
        d3d12RetirementEvent_, D3D12RetirementTimeoutMilliseconds) ==
        WAIT_OBJECT_0;
}

bool DirectXCompositor::ResetUnlocked(const bool waitForD3D12Idle) noexcept {
    visibilityProbe_.Stop();
    textureProbe_.Stop();
    probeRenderer_.reset();
    preparedSwapChain_.store(nullptr, std::memory_order_release);
    preparedQueue_.store(nullptr, std::memory_order_release);
    deviceInvalidated_.store(false, std::memory_order_release);
    // DXGI ResizeBuffers requires every direct and indirect reference to the
    // old back buffers to be released. D3D11On12 can retain RTV/SRV bindings
    // in the immediate context even after the wrappers and renderer are
    // dropped, so explicitly retire those bindings before releasing them.
    if (d3d11Context_ != nullptr) {
        d3d11Context_->ClearState();
    }
    sharedFrameConsumer_.UnbindDevice();
    renderer_.reset();
    startupStatusRenderer_.reset();
    wrappedTextures_.clear();
    wrappedBackBuffers_.clear();
    d3d11BackBuffers_.clear();
    // D3D11 defers COM destruction. Flush only after every renderer view and
    // wrapped back-buffer reference has been released, otherwise the old DXGI
    // buffers can remain live long enough for ResizeBuffers to fail.
    if (d3d11Context_ != nullptr) d3d11Context_->Flush();
    const bool retired = !waitForD3D12Idle || WaitForD3D12IdleUnlocked();
    if (!retired) retirementFailed_ = true;
    swapChain3_.Reset();
    d3d11On12Device_.Reset();
    d3d12RetirementFence_.Reset();
    if (d3d12RetirementEvent_ != nullptr) {
        CloseHandle(d3d12RetirementEvent_);
        d3d12RetirementEvent_ = nullptr;
    }
    d3d12RetirementValue_ = 0;
    d3d12Queue_.Reset();
    d3d12Device_.Reset();
    d3d11Context_.Reset();
    d3d11Device_.Reset();
    api_ = RwuiRenderApi::None;
    activeSwapChain_ = nullptr;
    width_ = 0;
    height_ = 0;
    return retired;
}

bool DirectXCompositor::ReleaseD3D12BackBuffersUnlocked(
    const bool waitForD3D12Idle) noexcept {
    if (api_ != RwuiRenderApi::Direct3D12) return true;
    preparedSwapChain_.store(nullptr, std::memory_order_release);
    preparedQueue_.store(nullptr, std::memory_order_release);
    deviceInvalidated_.store(false, std::memory_order_release);
    // Resize invalidates only swap-chain-sized resources. Retain the exact
    // D3D12 device/queue and D3D11On12 device/context so repeated resizes do
    // not churn the translation layer or its NVIDIA driver state.
    if (d3d11Context_ != nullptr) d3d11Context_->ClearState();
    if (renderer_ != nullptr) renderer_->InvalidateBackBuffer();
    wrappedTextures_.clear();
    wrappedBackBuffers_.clear();
    if (d3d11Context_ != nullptr) d3d11Context_->Flush();
    const bool retired = !waitForD3D12Idle || WaitForD3D12IdleUnlocked();
    if (!retired) retirementFailed_ = true;
    width_ = 0;
    height_ = 0;
    return retired;
}

bool DirectXCompositor::ReleaseD3D11BackBuffersUnlocked() noexcept {
    if (api_ != RwuiRenderApi::Direct3D11) return true;
    preparedSwapChain_.store(nullptr, std::memory_order_release);
    preparedQueue_.store(nullptr, std::memory_order_release);
    deviceInvalidated_.store(false, std::memory_order_release);
    // A native D3D11 ResizeBuffers invalidates only swap-chain-sized views.
    // Keep the device, compiled pipeline, staged CPU texture, and external
    // shared-frame consumer bound when the resized chain retains its device.
    if (d3d11Context_ != nullptr) d3d11Context_->ClearState();
    if (renderer_ != nullptr) renderer_->InvalidateBackBuffer();
    if (probeRenderer_ != nullptr) probeRenderer_->InvalidateBackBuffer();
    if (startupStatusRenderer_ != nullptr) startupStatusRenderer_->InvalidateBackBuffer();
    visibilityProbe_.Invalidate();
    d3d11BackBuffers_.clear();
    if (d3d11Context_ != nullptr) d3d11Context_->Flush();
    width_ = 0;
    height_ = 0;
    return true;
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
    swapChain->QueryInterface(IID_PPV_ARGS(&swapChain3_));
    DXGI_SWAP_CHAIN_DESC description{};
    if (FAILED(swapChain->GetDesc(&description)) ||
        description.BufferCount == 0 || description.BufferCount > 16) {
        return false;
    }
    renderer_ = std::make_unique<D3D11OverlayRenderer>(
        d3d11Device_.Get(), d3d11Context_.Get(), mailbox_);
    if (textureProbeConfigured_ && !(d3d11Device_->GetCreationFlags() & D3D11_CREATE_DEVICE_SINGLETHREADED))
        probeRenderer_ = std::make_unique<D3D11OverlayRenderer>(d3d11Device_.Get(), d3d11Context_.Get(), probeMailbox_);
    api_ = RwuiRenderApi::Direct3D11;
    if (textureProbeConfigured_ && !visibilityProbe_.Enabled())
        visibilityProbe_.Configure(visibilityProbeLog_);
    if (!InitializeD3D11BackBuffers(swapChain)) return false;
    // The shared-GPU receiver is an optional acceleration path. A device that
    // cannot expose the cross-thread D3D11 protection contract must still keep
    // the established CPU mailbox compositor alive and fail open to it.
    sharedFrameConsumer_.BindDevice(d3d11Device_.Get(), d3d11Context_.Get(), true);
    d3d11DeviceDiagnostics_ = ProbeD3D11Device(d3d11Device_.Get(), diagnosticProbesEnabled_.load());
    DescribeD3D11SwapChain(swapChain, d3d11DeviceDiagnostics_);
    d3d11CompositorGeneration_.fetch_add(1, std::memory_order_acq_rel);
    if (textureProbeConfigured_) textureProbe_.Start(d3d11Device_.Get());
    return true;
}

bool DirectXCompositor::InitializeD3D11BackBuffers(
    IDXGISwapChain* const swapChain) {
    if (swapChain == nullptr || api_ != RwuiRenderApi::Direct3D11 ||
        d3d11Device_ == nullptr || d3d11Context_ == nullptr ||
        renderer_ == nullptr || !d3d11BackBuffers_.empty()) {
        return false;
    }
    Microsoft::WRL::ComPtr<ID3D11Device> swapChainDevice;
    if (FAILED(swapChain->GetDevice(IID_PPV_ARGS(&swapChainDevice))) ||
        !SameComIdentity(swapChainDevice.Get(), d3d11Device_.Get())) {
        return false;
    }
    DXGI_SWAP_CHAIN_DESC description{};
    if (FAILED(swapChain->GetDesc(&description)) ||
        description.BufferCount == 0 || description.BufferCount > 16) {
        return false;
    }
    // D3D11 deliberately renders through buffer zero for every swap effect.
    // In flip-model D3D11 the runtime rotates the identity represented by that
    // buffer across Present calls; unlike D3D12, the application must not keep
    // a parallel array indexed by IDXGISwapChain3::GetCurrentBackBufferIndex.
    Microsoft::WRL::ComPtr<ID3D11Texture2D> backBuffer;
    if (FAILED(swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer))) ||
        !renderer_->PrepareBackBuffer(backBuffer.Get())) return false;
    if (probeRenderer_ && !probeRenderer_->PrepareBackBuffer(backBuffer.Get())) return false;
    // Failure of the diagnostic must never block the production compositor.
    if (textureProbeConfigured_)
        visibilityProbe_.Prepare(swapChain, backBuffer.Get(), d3d11Device_.Get(), d3d11Context_.Get());
    d3d11BackBuffers_.push_back(std::move(backBuffer));
    D3D11_TEXTURE2D_DESC backBufferDescription{};
    d3d11BackBuffers_.front()->GetDesc(&backBufferDescription);
    width_ = static_cast<std::int32_t>(backBufferDescription.Width);
    height_ = static_cast<std::int32_t>(backBufferDescription.Height);
    DescribeD3D11SwapChain(swapChain, d3d11DeviceDiagnostics_);
    d3d11BackBufferGeneration_.fetch_add(1, std::memory_order_acq_rel);
    return true;
}

RwuiD3D11DeviceDiagnostics DirectXCompositor::D3D11DeviceDiagnostics() const noexcept {
    std::scoped_lock lock(mutex_);
    return api_ == RwuiRenderApi::Direct3D11 ? d3d11DeviceDiagnostics_ : RwuiD3D11DeviceDiagnostics{};
}

void DirectXCompositor::ConfigureLegacyTextureProbe(const wchar_t* helper, const wchar_t* log) {
    std::scoped_lock lock(mutex_);
    // Must be armed before a device is prepared; never rebuild live pipelines.
    if (api_ != RwuiRenderApi::None || !helper || !log) return;
    const std::filesystem::path executable(helper), output(log);
    if (!executable.is_absolute() || !output.is_absolute() ||
        executable.filename() != L"ReactorV.TextureProbe.Partner.exe" ||
        !std::filesystem::is_regular_file(executable)) return;
    textureProbe_.Configure(executable, output);
    visibilityProbeLog_ = output.parent_path() / (output.stem().wstring() + L".visibility.log");
    textureProbeConfigured_ = true;
}

bool DirectXCompositor::RenderLegacyVisibilityProbe(IDXGISwapChain* swapChain) noexcept {
    if (!visibilityProbe_.Enabled()) return false;
    try {
        std::unique_lock lock(mutex_, std::try_to_lock);
        if (!lock.owns_lock() || api_ != RwuiRenderApi::Direct3D11 ||
            activeSwapChain_ != swapChain || deviceInvalidated_.load()) return false;
        auto lease = sharedFrameConsumer_.TryAcquireLatestForPresent();
        if (!lease.OwnsContext()) return false;
        return visibilityProbe_.Draw(swapChain);
    } catch (...) { return false; }
}

void DirectXCompositor::RenderLegacyTextureProbe(IDXGISwapChain* swapChain) noexcept {
    if (!textureProbe_.Active()) return;
    try {
        std::unique_lock lock(mutex_, std::try_to_lock);
        if (!lock.owns_lock() || api_ != RwuiRenderApi::Direct3D11 ||
            activeSwapChain_ != swapChain || deviceInvalidated_.load() ||
            !probeRenderer_ || d3d11BackBuffers_.empty()) return;
        auto lease = sharedFrameConsumer_.TryAcquireLatestForPresent();
        if (!lease.OwnsContext()) return;
        BOOL fullscreen{};
        const bool exclusive = SUCCEEDED(swapChain->GetFullscreenState(&fullscreen, nullptr)) && fullscreen;
        textureProbe_.Draw(*probeRenderer_, d3d11BackBuffers_.front().Get(), d3d11Context_.Get(), exclusive);
    } catch (...) {}
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
    if (FAILED(d3d12Device_->CreateFence(
            0, D3D12_FENCE_FLAG_NONE,
            IID_PPV_ARGS(&d3d12RetirementFence_)))) return false;
    d3d12RetirementEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (d3d12RetirementEvent_ == nullptr) return false;
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

    d3d12InteropGeneration_.fetch_add(1, std::memory_order_acq_rel);
    if (!InitializeD3D12BackBuffers(swapChain)) return false;
    // D3D11On12 shared-frame import is optional for the same reason as the
    // native D3D11 path: failure demotes transport, not the whole overlay.
    sharedFrameConsumer_.BindDevice(
        d3d11Device_.Get(),
        d3d11Context_.Get(),
        d3d12Device_->GetAdapterLuid());
    api_ = RwuiRenderApi::Direct3D12;
    return true;
}

bool DirectXCompositor::InitializeD3D12BackBuffers(
    IDXGISwapChain* const swapChain) {
    if (swapChain == nullptr || d3d11On12Device_ == nullptr ||
        d3d11Device_ == nullptr || d3d11Context_ == nullptr ||
        FAILED(swapChain->QueryInterface(IID_PPV_ARGS(&swapChain3_)))) {
        return false;
    }
    DXGI_SWAP_CHAIN_DESC description{};
    if (FAILED(swapChain->GetDesc(&description)) ||
        description.BufferCount == 0 || description.BufferCount > 16) {
        return false;
    }
    wrappedBackBuffers_.resize(description.BufferCount);
    wrappedTextures_.resize(description.BufferCount);
    D3D11_RESOURCE_FLAGS flags{};
    flags.BindFlags = D3D11_BIND_RENDER_TARGET;
    for (UINT index = 0; index < description.BufferCount; ++index) {
        Microsoft::WRL::ComPtr<ID3D12Resource> backBuffer;
        if (FAILED(swapChain->GetBuffer(index, IID_PPV_ARGS(&backBuffer))) ||
            FAILED(d3d11On12Device_->CreateWrappedResource(
                backBuffer.Get(),
                &flags,
                // Reactor enters from the Present detour, after the host has
                // finished its D3D12 rendering and returned the buffer to
                // PRESENT. CreateWrappedResource's InState must describe that
                // last D3D12 use; claiming RENDER_TARGET eventually poisons
                // the device during repeated release/resize cycles.
                D3D12_RESOURCE_STATE_PRESENT,
                D3D12_RESOURCE_STATE_PRESENT,
                IID_PPV_ARGS(&wrappedBackBuffers_[index])))) {
            wrappedBackBuffers_.clear();
            wrappedTextures_.clear();
            return false;
        }
        if (FAILED(wrappedBackBuffers_[index].As(
                &wrappedTextures_[index]))) return false;
    }

    if (renderer_ == nullptr) {
        renderer_ = std::make_unique<D3D11OverlayRenderer>(
            d3d11Device_.Get(),
            d3d11Context_.Get(),
            mailbox_,
            false);
    }
    for (const auto& backBuffer : wrappedTextures_) {
        if (!renderer_->PrepareBackBuffer(backBuffer.Get())) return false;
    }
    D3D11_TEXTURE2D_DESC backBufferDescription{};
    wrappedTextures_.front()->GetDesc(&backBufferDescription);
    width_ = static_cast<std::int32_t>(backBufferDescription.Width);
    height_ = static_cast<std::int32_t>(backBufferDescription.Height);
    d3d12BackBufferGeneration_.fetch_add(1, std::memory_order_acq_rel);
    return true;
}

bool DirectXCompositor::RenderD3D11(
    IDXGISwapChain*,
    const bool clearBeforeOverlay,
    const bool allowExternalFrame) {
    if (d3d11BackBuffers_.empty()) return false;
    auto* const backBuffer = d3d11BackBuffers_.front().Get();
    auto sharedFrame = sharedFrameConsumer_.TryAcquireLatestForPresent();
    if (!sharedFrame.OwnsContext()) return false;
    const bool sharedRendered = allowExternalFrame && sharedFrame
        ? renderer_->RenderShared(
            backBuffer, sharedFrame.View(),
            sharedFrame.Generation(), clearBeforeOverlay)
        : false;
    const bool rendered = sharedRendered ||
        renderer_->Render(backBuffer, clearBeforeOverlay);
    return rendered;
}

bool DirectXCompositor::RenderD3D12(
    IDXGISwapChain*,
    const bool clearBeforeOverlay,
    const bool allowExternalFrame) {
    const auto index = swapChain3_->GetCurrentBackBufferIndex();
    if (index >= wrappedBackBuffers_.size() ||
        index >= wrappedTextures_.size()) return false;
    auto* const backBuffer = wrappedTextures_[index].Get();

    auto sharedFrame = sharedFrameConsumer_.TryAcquireLatestForPresent();
    if (!sharedFrame.OwnsContext()) return false;
    WrappedResourceLease wrappedLease(
        d3d11On12Device_.Get(), d3d11Context_.Get(),
        wrappedBackBuffers_[index].Get());
    const bool sharedRendered = allowExternalFrame && sharedFrame
        ? renderer_->RenderShared(
            backBuffer, sharedFrame.View(),
            sharedFrame.Generation(), clearBeforeOverlay)
        : false;
    const bool rendered = sharedRendered ||
        renderer_->Render(backBuffer, clearBeforeOverlay);
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
