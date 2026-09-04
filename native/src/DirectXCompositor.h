#pragma once

#include "D3D11OverlayRenderer.h"
#include "FrameMailbox.h"
#include "RageWebUI.Native.h"
#include "SharedGpuFrameConsumer.h"
#include "LegacyTextureProbe.h"
#include "LegacyVisibilityProbe.h"
#include "StartupStatusPolicy.h"

#include <Windows.h>
#include <d3d11on12.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <atomic>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>
#include <wrl/client.h>

namespace rwui {

class DirectXCompositor final {
public:
    explicit DirectXCompositor(FrameMailbox& mailbox);
    ~DirectXCompositor();
    bool ArmSharedFrameConsumer() noexcept;
    void ShutdownSharedFrameConsumer() noexcept;
    // All allocation, shader compilation, backbuffer wrapping, RTV creation,
    // and CPU upload happen here, off Present (factory/target-bind/worker).
    bool Prepare(
        IDXGISwapChain* swapChain,
        ID3D12CommandQueue* directQueue,
        std::uint64_t expectedPreparationEpoch = 0) noexcept;
    bool StageLatestCpuFrame() noexcept;
    bool SubmitStartupStatus(const void* pixels, int width, int height, int stride, std::uint64_t generation) noexcept;
    bool RenderStartupStatus(IDXGISwapChain* swapChain) noexcept;
    void RequestPrepare(
        IDXGISwapChain* swapChain,
        ID3D12CommandQueue* directQueue) noexcept;
    // Hot Present path: try-lock + cached draw only. If the swapchain was not
    // captured early enough, it publishes a latest-only worker request.
    bool Render(
        IDXGISwapChain* swapChain,
        ID3D12CommandQueue* directQueue,
        bool clearBeforeOverlay = false,
        bool allowExternalFrame = true) noexcept;
    bool BeforeResize(IDXGISwapChain* swapChain) noexcept;
    void AfterResize(IDXGISwapChain* swapChain) noexcept;
    // Present failure notification is allocation-free/nonblocking. It only
    // invalidates the committed identity and publishes worker recovery.
    void NotifyDeviceFailure(
        IDXGISwapChain* swapChain,
        ID3D12CommandQueue* directQueue) noexcept;
    void Reset() noexcept;
    RwuiRenderStats Stats() const;
    RwuiD3D11DeviceDiagnostics D3D11DeviceDiagnostics() const noexcept;
    RwuiD3D11CompatibilityDiagnostics D3D11CompatibilityDiagnostics() const noexcept {
        return {sizeof(RwuiD3D11CompatibilityDiagnostics), 1,
            sharedFrameConsumer_.LegacyBridgeEnabled() ? 1u : 0u,
            sharedFrameConsumer_.LegacyBridgeActive() ? 1u : 0u,
            sharedFrameConsumer_.LegacyBridgeStage(),
            sharedFrameConsumer_.LegacyBridgeHresult(),
            sharedFrameConsumer_.LegacyDirectFailure(), 0,
            sharedFrameConsumer_.LegacyBridgedFrames()};
    }
    void EnableD3D11DiagnosticProbes(bool enabled) noexcept { diagnosticProbesEnabled_.store(enabled); }
    void ConfigureLegacyTextureProbe(const wchar_t* helper, const wchar_t* log);
    void RenderLegacyTextureProbe(IDXGISwapChain* swapChain) noexcept;
    bool RenderLegacyVisibilityProbe(IDXGISwapChain* swapChain) noexcept;
    void RecordLegacyProbePresent(HRESULT result) noexcept { visibilityProbe_.Presented(result); }
    bool ExternalPresentationVisible() const noexcept {
        return sharedFrameConsumer_.ExternalPresentationVisible();
    }
    bool ExternalProducerConnected() const noexcept {
        return sharedFrameConsumer_.Connected();
    }
    std::uint64_t ExternalPresentationEpoch() const noexcept {
        return sharedFrameConsumer_.ExternalPresentationEpoch();
    }
    std::uint64_t D3D12InteropGeneration() const noexcept {
        return d3d12InteropGeneration_.load(std::memory_order_acquire);
    }
    std::uint64_t D3D12BackBufferGeneration() const noexcept {
        return d3d12BackBufferGeneration_.load(std::memory_order_acquire);
    }
    std::uint64_t D3D11CompositorGeneration() const noexcept {
        return d3d11CompositorGeneration_.load(std::memory_order_acquire);
    }
    std::uint64_t D3D11BackBufferGeneration() const noexcept {
        return d3d11BackBufferGeneration_.load(std::memory_order_acquire);
    }
    transport::SharedGpuFrameConsumerDiagnostics SharedFrameDiagnostics()
        const noexcept {
        return sharedFrameConsumer_.Diagnostics();
    }

private:
    bool ArmPreparationWorker() noexcept;
    void StopPreparationWorker() noexcept;
    void PreparationWorker() noexcept;
    void DrainPendingPreparationRequest() noexcept;
    // Every teardown/rebind of an active D3D11On12 compositor must retire its
    // submitted work before releasing wrappers or the 11On12 device. This is
    // not resize-specific: test-surface switches, target rebinds, device
    // invalidation, and shutdown all cross the same GPU ownership boundary.
    bool ResetUnlocked(bool waitForD3D12Idle = true) noexcept;
    bool ReleaseD3D11BackBuffersUnlocked() noexcept;
    bool ReleaseD3D12BackBuffersUnlocked(
        bool waitForD3D12Idle = true) noexcept;
    bool WaitForD3D12IdleUnlocked() noexcept;
    bool InitializeD3D11(IDXGISwapChain* swapChain);
    bool InitializeD3D11BackBuffers(IDXGISwapChain* swapChain);
    bool InitializeD3D12(IDXGISwapChain* swapChain, ID3D12CommandQueue* directQueue);
    bool InitializeD3D12BackBuffers(IDXGISwapChain* swapChain);
    bool RenderD3D11(
        IDXGISwapChain* swapChain,
        bool clearBeforeOverlay,
        bool allowExternalFrame);
    bool RenderD3D12(
        IDXGISwapChain* swapChain,
        bool clearBeforeOverlay,
        bool allowExternalFrame);
    static bool SameDevice(ID3D12Device* device, ID3D12CommandQueue* queue);

    FrameMailbox& mailbox_;
    mutable std::mutex mutex_;
    RwuiRenderApi api_{RwuiRenderApi::None};
    IDXGISwapChain* activeSwapChain_{};
    Microsoft::WRL::ComPtr<ID3D11Device> d3d11Device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> d3d11Context_;
    Microsoft::WRL::ComPtr<ID3D12Device> d3d12Device_;
    Microsoft::WRL::ComPtr<ID3D12CommandQueue> d3d12Queue_;
    Microsoft::WRL::ComPtr<ID3D12Fence> d3d12RetirementFence_;
    HANDLE d3d12RetirementEvent_{};
    std::uint64_t d3d12RetirementValue_{};
    Microsoft::WRL::ComPtr<ID3D11On12Device> d3d11On12Device_;
    Microsoft::WRL::ComPtr<IDXGISwapChain3> swapChain3_;
    std::vector<Microsoft::WRL::ComPtr<ID3D11Texture2D>> d3d11BackBuffers_;
    std::vector<Microsoft::WRL::ComPtr<ID3D11Resource>> wrappedBackBuffers_;
    std::vector<Microsoft::WRL::ComPtr<ID3D11Texture2D>> wrappedTextures_;
    std::unique_ptr<D3D11OverlayRenderer> renderer_;
    FrameMailbox startupStatusMailbox_;
    std::unique_ptr<D3D11OverlayRenderer> startupStatusRenderer_;
    std::atomic_bool startupStatusActive_{};
    void PrepareStartupStatusUnlocked();
    transport::SharedGpuFrameConsumer sharedFrameConsumer_;

    std::mutex preparationLifecycleMutex_;
    std::thread preparationWorker_;
    HANDLE preparationEvent_{};
    std::atomic_bool preparationStop_{};
    std::atomic_bool preparationArmed_{};
    std::atomic_flag preparationRequestGate_ = ATOMIC_FLAG_INIT;
    std::atomic<IDXGISwapChain*> preparedSwapChain_{};
    std::atomic<ID3D12CommandQueue*> preparedQueue_{};
    std::atomic_bool deviceInvalidated_{};
    std::atomic_uint64_t d3d11CompositorGeneration_{};
    std::atomic_uint64_t d3d11BackBufferGeneration_{};
    std::atomic_uint64_t d3d12InteropGeneration_{};
    std::atomic_uint64_t d3d12BackBufferGeneration_{};
    // Guards the interval between the resize detour releasing our old
    // back-buffer references and DXGI completing the real resize. The
    // preparation worker must not recreate wrappers in that interval.
    IDXGISwapChain* resizingSwapChain_{};
    IDXGISwapChain* blockedPreparationSwapChain_{};
    // A failed queue Signal/fence wait means GPU ownership was not proven.
    // Never recreate wrappers in the same DLL lifetime after that boundary;
    // the host Present path remains fail-open with Reactor disabled.
    bool retirementFailed_{};
    std::atomic<IDXGISwapChain*> pendingPreparationSwapChain_{};
    std::atomic<ID3D12CommandQueue*> pendingPreparationQueue_{};
    // Epoch is protected by preparationRequestGate_ alongside the pending
    // COM pointers. Nonzero values are compared again under mutex_ by Prepare.
    std::uint64_t pendingPreparationEpoch_{};
    std::atomic_uint64_t preparationEpoch_{1};
    std::int32_t width_{};
    std::int32_t height_{};
    RwuiD3D11DeviceDiagnostics d3d11DeviceDiagnostics_{};
    std::atomic_bool diagnosticProbesEnabled_{};
    probe::LegacyTextureProbe textureProbe_;
    probe::LegacyVisibilityProbe visibilityProbe_;
    std::filesystem::path visibilityProbeLog_;
    FrameMailbox probeMailbox_;
    std::unique_ptr<D3D11OverlayRenderer> probeRenderer_;
    bool textureProbeConfigured_{};
};

} // namespace rwui
