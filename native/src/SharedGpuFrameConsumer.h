#pragma once

#include "AdapterLuidDiscovery.h"
#include "SharedGpuFrameDiscovery.h"
#include "SharedGpuFrameD3D11.h"
#include "LegacyD3D11FrameBridge.h"
#include "LegacyCpuFrameBridge.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <d3d11.h>
#include <mutex>
#include <thread>
#include <wrl/client.h>

namespace rwui::transport {

constexpr std::uint32_t SharedGpuDiscoveryPollDelayMs(
    const std::uint64_t consecutiveMisses) noexcept {
    return consecutiveMisses < 40 ? 50u :
        consecutiveMisses < 80 ? 250u : 1000u;
}

enum class SharedGpuFrameConsumerStage : std::uint32_t {
    Idle,
    Discovering,
    ProducerDiscovered,
    ProducerRejected,
    Connecting,
    Connected,
    PresentationUpdated,
    Receiving,
    Copying,
    Published,
    ReceiveFailed,
    CopyFailed,
    AcknowledgementFailed,
    Stopped,
};

struct SharedGpuFrameConsumerDiagnostics final {
    SharedGpuFrameConsumerStage stage{};
    std::uint32_t lastReceiveError{};
    std::uint32_t lastImportError{};
    std::uint32_t lastImportHresult{};
    std::uint64_t discoveryMisses{};
    std::uint64_t producerImageRejects{};
    std::uint64_t connectFailures{};
    std::uint64_t receiveFailures{};
    std::uint64_t copyFailures{};
    std::uint64_t acknowledgementFailures{};
    std::uint64_t receivedFrames{};
    std::uint64_t publishedFrames{};
    std::uint64_t acknowledgementsAccepted{};
    std::uint64_t acknowledgementsRejected{};
    std::uint64_t importedResources{};
    std::uint64_t lastReceivedGeneration{};
    std::uint64_t lastPublishedGeneration{};
    std::uint64_t presentationUpdates{};
    std::uint64_t presentationEpoch{};
    bool presentationVisible{};
};

// Receives process-scoped shared frames on a control worker. The worker copies
// each producer-owned keyed texture into one consumer-owned local texture and
// releases the producer slot before publishing it. Present therefore never
// waits on IPC or a keyed mutex and can reuse the latest local SRV until a newer
// generation arrives.
class SharedGpuFrameConsumer final {
public:
    using ImportFunction = SharedGpuD3D11ImportError (*)(ID3D11Device*,
        const SharedGpuFrameDescriptorV1&, const SharedGpuFrameValidationContext&,
        ImportedD3D11SharedFrame&, HRESULT*) noexcept;
    // Native test seam: production always uses the validating importer.
    explicit SharedGpuFrameConsumer(ImportFunction importer = ImportD3D11SharedFrame)
        : importer_(importer ? importer : ImportD3D11SharedFrame) {}
    void EnableCpuBridgeForTesting() noexcept { cpuBridgeForTesting_ = true; }
    static constexpr bool ShouldBridge(bool enabled, SharedGpuD3D11ImportError error,
        HRESULT hr) noexcept {
        return enabled && error == SharedGpuD3D11ImportError::SharedTextureOpenFailed &&
            hr == E_INVALIDARG;
    }
    bool LegacyBridgeEnabled() const noexcept { return legacyBridgeAllowed_.load(); }
    bool LegacyBridgeActive() const noexcept { return legacyBridgeActive_.load(); }
    std::uint32_t LegacyBridgeStage() const noexcept { return legacyBridge_.Stage(); }
    std::uint32_t LegacyBridgeHresult() const noexcept { return legacyBridge_.LastHresult(); }
    std::uint32_t LegacyDirectFailure() const noexcept { return legacyDirectFailure_.load(); }
    std::uint64_t LegacyBridgedFrames() const noexcept { return legacyBridgedFrames_.load(); }
    class PresentLease final {
    public:
        PresentLease() = default;
        ~PresentLease();
        PresentLease(PresentLease&& other) noexcept;
        PresentLease& operator=(PresentLease&& other) noexcept;
        PresentLease(const PresentLease&) = delete;
        PresentLease& operator=(const PresentLease&) = delete;

        // A lease owns the immediate-context gate without necessarily
        // containing a GPU frame. In that case the compositor may safely use
        // its CPU mailbox.
        bool OwnsContext() const noexcept { return lock_.owns_lock(); }
        explicit operator bool() const noexcept { return view_ != nullptr; }
        ID3D11ShaderResourceView* View() const noexcept { return view_; }
        std::uint64_t Generation() const noexcept { return generation_; }

    private:
        friend class SharedGpuFrameConsumer;
        PresentLease(
            std::unique_lock<std::mutex>&& lock,
            ID3D11ShaderResourceView* view,
            std::uint64_t generation) noexcept;
        void Release() noexcept;

        std::unique_lock<std::mutex> lock_;
        ID3D11ShaderResourceView* view_{};
        std::uint64_t generation_{};
    };

    ~SharedGpuFrameConsumer();

    // Arm is intentionally independent of a graphics device so discovery and
    // channel setup can start from RWUI_ArmEnhancedHook, never first Present.
    bool Arm() noexcept;
    // Returns true only when the immediate context is safe for worker/submit
    // thread access. Callers must not stage or copy off Present otherwise.
    bool BindDevice(
        ID3D11Device* consumerDevice,
        ID3D11DeviceContext* immediateContext,
        bool allowLegacyBridge = false) noexcept;
    // D3D11On12 callers provide the captured D3D12 device's LUID explicitly;
    // the receiver verifies it against the interop D3D11 device before
    // publishing it to the external browser producer.
    bool BindDevice(
        ID3D11Device* consumerDevice,
        ID3D11DeviceContext* immediateContext,
        const LUID& authoritativeAdapterLuid,
        bool allowLegacyBridge = false) noexcept;
    void UnbindDevice() noexcept;
    void Stop() noexcept;

    // Waits only for the worker's short CopyResource/Flush critical section;
    // producer discovery, import, and keyed-mutex retries use a separate gate.
    // This preserves the last good frame instead of exposing a transparent GTA
    // Present whenever a new CEF paint is being received. A successful empty
    // lease gates the immediate context for CPU fallback.
    PresentLease TryAcquireLatestForPresent() noexcept;
    bool Connected() const noexcept;
    bool ExternalPresentationVisible() const noexcept {
        return externalPresentationVisible_.load(std::memory_order_acquire);
    }
    std::uint64_t ExternalPresentationEpoch() const noexcept {
        return externalPresentationEpoch_.load(std::memory_order_acquire);
    }
    std::uint64_t ImportedResourceCount() const noexcept {
        return importedResourceCount_.load(std::memory_order_relaxed);
    }
    SharedGpuFrameConsumerDiagnostics Diagnostics() const noexcept;

private:
    void Worker() noexcept;
    bool CopyAndPublish(
        const SharedGpuFrameDescriptorV1& descriptor,
        const SharedGpuFrameChannelEndpoint& endpoint) noexcept;
    bool UploadCpuFrame(const SharedGpuFrameDescriptorV1& descriptor,
        const SharedGpuFrameValidationContext& validation) noexcept;
    void ClearLatestLocked() noexcept;
    void ClearImportedSlotsLocked() noexcept;
    void ClearExternalPresentation() noexcept;
    bool WaitForStop(std::uint32_t milliseconds) const noexcept;
    void ResetLegacyBridgeLocked() noexcept;

    const ImportFunction importer_;
    LegacyD3D11FrameBridge legacyBridge_;
    std::atomic_bool legacyBridgeAllowed_{}, legacyBridgeActive_{};
    bool legacyBridgeAttempted_{};
    std::atomic<std::uint32_t> legacyDirectFailure_{};
    std::atomic<std::uint64_t> legacyBridgedFrames_{};
    SharedGpuFrameChannelClient client_;
    SharedGpuFrameChannelEndpoint endpoint_{};
    std::thread worker_;
    HANDLE stopEvent_{};
    std::atomic_bool stop_{};
    std::atomic_bool armed_{};
    std::atomic_bool connected_{};
    std::atomic<SharedGpuFrameConsumerStage> stage_{
        SharedGpuFrameConsumerStage::Idle};
    std::atomic<std::uint32_t> lastReceiveError_{};
    std::atomic<std::uint32_t> lastImportError_{};
    std::atomic<std::uint32_t> lastImportHresult_{};
    std::atomic<std::uint64_t> discoveryMisses_{};
    std::atomic<std::uint64_t> producerImageRejects_{};
    std::atomic<std::uint64_t> connectFailures_{};
    std::atomic<std::uint64_t> receiveFailures_{};
    std::atomic<std::uint64_t> copyFailures_{};
    std::atomic<std::uint64_t> acknowledgementFailures_{};
    std::atomic<std::uint64_t> receivedFrames_{};
    std::atomic<std::uint64_t> publishedFrames_{};
    std::atomic<std::uint64_t> acknowledgementsAccepted_{};
    std::atomic<std::uint64_t> acknowledgementsRejected_{};
    std::atomic<std::uint64_t> lastReceivedGeneration_{};
    std::atomic<std::uint64_t> lastPublishedGeneration_{};
    std::atomic<std::uint64_t> presentationUpdates_{};
    std::atomic<std::uint64_t> externalPresentationEpoch_{};
    std::atomic_bool externalPresentationVisible_{};
    std::mutex lifecycleMutex_;

    // Protects device binding, imported producer slots, and local frame-cache
    // identity. It is deliberately not acquired by Present: producer keyed-
    // mutex waits can legitimately take several GTA frames.
    std::mutex frameMutex_;
    // Serializes complete immediate-context sequences. Present has priority in
    // practice because the worker takes this gate only after a producer frame
    // is acquired and releases it immediately after CopyResource/Flush and
    // publication. Lifecycle operations also cross this boundary so an
    // existing PresentLease cannot race a device rebind.
    std::mutex contextMutex_;
    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
    bool multithreadProtectionReady_{};
    bool cpuBridgeAllowed_{}, cpuBridgeForTesting_{};
    CpuFrameTrace cpuTrace_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> cpuSpareTexture_;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> cpuSpareView_;
    AdapterLuidDiscoveryPublisher adapterLuidPublisher_;
    std::uint64_t deviceEpoch_{};
    std::array<ImportedD3D11SharedFrame, SharedGpuFrameMaximumSlots>
        importedSlots_{};
    std::atomic<std::uint64_t> importedResourceCount_{};
    Microsoft::WRL::ComPtr<ID3D11Texture2D> latestTexture_;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> latestView_;
    std::uint64_t latestGeneration_{};
    std::uint64_t latestSessionHigh_{};
    std::uint64_t latestSessionLow_{};
    std::uint64_t latestDeviceEpoch_{};
};

} // namespace rwui::transport
