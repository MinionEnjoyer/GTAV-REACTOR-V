#pragma once

#include "RageWebUI.Native.h"
#include "SharedGpuFrameDiscovery.h"
#include "SharedGpuFrameProducer.h"
#include "LegacyCpuFrameBridge.h"

#include <Windows.h>
#include <array>
#include <atomic>
#include <cstdint>
#include <mutex>
#include <thread>

namespace rwui::transport {

class SharedGpuFrameProducerRuntime final {
public:
    ~SharedGpuFrameProducerRuntime();

    bool Start(std::uint32_t targetGtaProcessId) noexcept;
    void Stop() noexcept;
    bool SubmitTransient(
        HANDLE handle,
        std::uint32_t width,
        std::uint32_t height,
        SharedGpuPixelFormat format,
        std::uint64_t generation) noexcept;
    RwuiSharedTextureSubmitStatus SubmitTransientStatus(
        HANDLE handle,
        std::uint32_t width,
        std::uint32_t height,
        SharedGpuPixelFormat format,
        std::uint64_t generation,
        bool bootstrapProbe = false) noexcept;
    RwuiSharedTextureProducerDiagnostics Diagnostics() const noexcept;
    RwuiSharedTextureSubmitStatus RecordRejectedAttempt(
        RwuiSharedTextureSubmitStatus status,
        std::uint64_t generation,
        bool bootstrapProbe) noexcept;

    bool Bound() const noexcept;
    bool ConsumerConnected() const noexcept;
    bool AcceleratedReady() const noexcept;
    bool SetPresentationVisible(bool visible) noexcept;
    std::uint64_t PresentationEpoch() const noexcept {
        return presentationEpoch_.load(std::memory_order_acquire);
    }
    std::uint64_t LastAcknowledgedGeneration() const noexcept {
        return lastAcknowledgedGeneration_.load(std::memory_order_acquire);
    }
    void FailNextPoolReleaseForTesting() noexcept {
        producer_.FailNextPoolReleaseForTesting();
    }
    void FailNextCopyCompletionForTesting() noexcept {
        producer_.FailNextCopyCompletionForTesting();
    }
    void EnableCpuBridgeForTesting() noexcept { cpuBridgeForTesting_ = true; }
    void FailNextCpuReadbackForTesting() noexcept { cpuBridge_.FailNextReadbackForTesting(); }
    UINT64 CpuRecoveryCountForTesting() const noexcept { return cpuRecoveries_.load(); }

private:
    bool SelectHardwareAdapter(
        HANDLE transientHandle) noexcept;
    void ResetProducerPoolAfterRecycleFailure() noexcept;
    void DemoteAcceleratedRoute() noexcept;
    RwuiSharedTextureSubmitStatus CompleteAttempt(
        RwuiSharedTextureSubmitStatus status,
        std::uint64_t generation) noexcept;
    void ResetDiagnostics() noexcept;
    void Worker() noexcept;

    SharedGpuFrameChannelEndpoint endpoint_{};
    SharedGpuFrameChannelServer server_;
    SharedGpuFrameDiscoveryPublisher discovery_;
    D3D11SharedFrameProducer producer_;
    LegacyCpuFrameBridge cpuBridge_;
    CpuFrameTrace cpuTrace_;
    bool cpuBridgeForTesting_{}, cpuBridgeEnabled_{};
    std::uint64_t lastCpuSubmitTick_{};
    std::uint64_t cpuRetryAfterTick_{};
    CpuFrameRecoveryBudget cpuRecoveryBudget_;
    std::atomic<UINT64> cpuRecoveries_{};
    std::thread worker_;
    HANDLE wakeEvent_{};

    mutable std::mutex lifecycleMutex_;
    mutable std::mutex submitMutex_;
    std::mutex outboxMutex_;
    SharedGpuFrameDescriptorV1 pending_{};
    bool hasPending_{};
    bool hasPendingPresentation_{};

    std::atomic_bool stop_{};
    std::atomic_bool bound_{};
    std::atomic_bool connected_{};
    std::atomic_bool consumerValidated_{};
    std::atomic_bool adapterReady_{};
    std::atomic_bool presentationVisible_{};
    std::atomic<std::uint64_t> presentationEpoch_{};
    std::atomic<std::uint64_t> lastAcknowledgedGeneration_{};
    std::atomic<RwuiSharedTextureSubmitStatus> lastStatus_{
        RwuiSharedTextureSubmitStatus::UnknownFailure};
    std::atomic<std::uint64_t> probeAttempts_{};
    std::atomic<std::uint64_t> submitAttempts_{};
    std::atomic<std::uint64_t> submitted_{};
    std::atomic<std::uint64_t> backpressure_{};
    std::atomic<std::uint64_t> sessionInvalid_{};
    std::atomic<std::uint64_t> adapterOrResourceInvalid_{};
    std::atomic<std::uint64_t> deviceOrCopyFailure_{};
    std::atomic<std::uint64_t> producerStopped_{};
    std::atomic<std::uint64_t> invalidFrame_{};
    std::atomic<std::uint64_t> unknownFailure_{};
    std::atomic<std::uint64_t> acknowledgementsAccepted_{};
    std::atomic<std::uint64_t> acknowledgementsRejected_{};
    std::atomic<std::uint64_t> acknowledgementFailures_{};
    std::atomic<std::uint64_t> lastAttemptedGeneration_{};
    std::atomic<std::uint64_t> lastSubmittedGeneration_{};
    LUID adapterLuid_{};
    std::uint32_t adapterVendorId_{};
    std::uint32_t adapterDeviceId_{};
    std::array<wchar_t, 128> adapterDescription_{};
};

SharedGpuFrameProducerRuntime& GlobalSharedGpuFrameProducerRuntime() noexcept;

} // namespace rwui::transport
