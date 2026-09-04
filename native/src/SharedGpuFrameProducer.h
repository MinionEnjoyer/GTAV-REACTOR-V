#pragma once

#include "SharedGpuFrameD3D11.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <mutex>
#include <wrl/client.h>

namespace rwui::transport {

enum class SharedGpuProducerSubmitResult : std::uint8_t {
    Submitted = 0,
    NotInitialized,
    InvalidArguments,
    UnsupportedFormat,
    TransientHandleOpenFailed,
    TransientTextureMismatch,
    TransientSynchronizationUnavailable,
    ProducerBusy,
    PoolBusy,
    PoolTextureCreationFailed,
    CopyFailed,
    CopyCompletionTimedOut,
    DeviceRemoved,
};

const char* SharedGpuProducerSubmitResultName(
    SharedGpuProducerSubmitResult result) noexcept;

// Producer-side copy pool used by the external ReactorV Preloader process.
//
// CEF's accelerated-paint handle is transient and may change on every
// callback. SubmitTransientTexture opens it synchronously, then copies its
// contents into one of two Reactor-owned keyed-mutex NT shared textures before
// returning. The resulting descriptor remains valid after the CEF callback.
// All mutex acquisition uses timeout zero and the CPU lock is try-only, so a
// busy pool drops the frame instead of stalling CEF or GTA.
class D3D11SharedFrameProducer final {
public:
    static constexpr std::uint32_t SlotCount = 2;

    ~D3D11SharedFrameProducer();

    // device must be selected on the adapter that can open CEF's transient
    // texture. Production discovery must try hardware adapters against the
    // first handle and cache the successful LUID; WARP is test-only. Until
    // that discovery layer is connected, capability flags remain disabled.
    bool Initialize(
        ID3D11Device* device,
        ID3D11DeviceContext* context,
        std::uint32_t targetConsumerProcessId,
        std::uint64_t sessionIdHigh,
        std::uint64_t sessionIdLow) noexcept;
    // Lifecycle-only. This method takes the producer mutex; an overlapping
    // callback fails open with ProducerBusy rather than observing teardown.
    void Reset() noexcept;

    SharedGpuProducerSubmitResult SubmitTransientTexture(
        HANDLE transientSharedHandle,
        std::uint32_t width,
        std::uint32_t height,
        SharedGpuPixelFormat format,
        std::uint64_t generation,
        SharedGpuFrameDescriptorV1& descriptor) noexcept;

    // If the control worker replaces a not-yet-sent descriptor, it must
    // recycle that slot locally. Sent descriptors are recycled by the target
    // consumer after rendering or intentionally discarding them.
    bool TryRecycleUnsent(
        const SharedGpuFrameDescriptorV1& descriptor) noexcept;
    // Retire, never recycle, a CPU-readback source whose GPU completion was
    // not proven. Runtime holds submitMutex_ until this finishes.
    bool RetireUnsent(const SharedGpuFrameDescriptorV1& descriptor) noexcept;

    bool Initialized() const noexcept {
        return initialized_.load(std::memory_order_acquire);
    }
    std::uint64_t SubmittedFrames() const noexcept;
    std::uint64_t DroppedFrames() const noexcept;
    // Deterministic non-game fault seam for bounded-pool recovery coverage.
    void FailNextPoolReleaseForTesting() noexcept {
        failNextPoolReleaseForTesting_.store(true, std::memory_order_release);
    }
    void FailNextCopyCompletionForTesting() noexcept {
        failNextCopyCompletionForTesting_.store(
            true, std::memory_order_release);
    }

private:
    struct Slot final {
        Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
        Microsoft::WRL::ComPtr<IDXGIKeyedMutex> mutex;
        HANDLE sharedHandle{};
        std::uint64_t resourceEpoch{};
        std::uint64_t nextProducerAcquireKey{};
        std::uint64_t lastGeneration{};
        std::uint32_t width{};
        std::uint32_t height{};
        SharedGpuPixelFormat format{SharedGpuPixelFormat::Unknown};
    };

    bool CreateSlotTexture(
        Slot& slot,
        std::uint32_t width,
        std::uint32_t height,
        SharedGpuPixelFormat format) noexcept;
    bool OpenTransientTexture(
        HANDLE handle,
        Microsoft::WRL::ComPtr<ID3D11Texture2D>& texture) noexcept;
    bool RecreateCopyCompletionQuery() noexcept;
    void ResetUnlocked() noexcept;
    void CloseSlot(Slot& slot) noexcept;

    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11Device1> device1_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
    Microsoft::WRL::ComPtr<ID3D11Query> copyCompletionQuery_;
    std::array<Slot, SlotCount> slots_{};
    WindowsProcessIdentity producerIdentity_{};
    WindowsProcessIdentity consumerIdentity_{};
    std::uint32_t consumerProcessId_{};
    std::uint64_t sessionIdHigh_{};
    std::uint64_t sessionIdLow_{};
    std::uint64_t nextResourceEpoch_{1};
    std::uint32_t nextSlot_{};
    std::atomic_bool initialized_{};
    mutable std::mutex mutex_;
    std::atomic<std::uint64_t> submittedFrames_{};
    std::atomic<std::uint64_t> droppedFrames_{};
    std::atomic_bool failNextPoolReleaseForTesting_{};
    std::atomic_bool failNextCopyCompletionForTesting_{};
};

} // namespace rwui::transport
