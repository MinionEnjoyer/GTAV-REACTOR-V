#pragma once

#include "ReactorV.SharedGpuFrame.h"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>

namespace rwui::transport {

struct SharedGpuFrameValidationContext final {
    std::uint32_t expectedProducerProcessId{};
    std::uint32_t expectedConsumerProcessId{};
    std::uint64_t expectedProducerCreationTime{};
    std::uint64_t expectedConsumerCreationTime{};
    std::uint64_t expectedSessionIdHigh{};
    std::uint64_t expectedSessionIdLow{};
    std::uint32_t maximumWidth{SharedGpuFrameMaximumDimension};
    std::uint32_t maximumHeight{SharedGpuFrameMaximumDimension};
};

enum class SharedGpuFrameValidationError : std::uint8_t {
    None = 0,
    InvalidValidationContext,
    BadMagic,
    UnsupportedMajorVersion,
    UnsupportedMinorVersion,
    InvalidByteSize,
    MissingRequiredFlags,
    UnsupportedFlags,
    ProducerProcessMismatch,
    ConsumerProcessMismatch,
    ProducerCreationTimeMismatch,
    ConsumerCreationTimeMismatch,
    SessionMismatch,
    InvalidGeneration,
    InvalidResourceEpoch,
    InvalidSlotCount,
    InvalidSlotIndex,
    InvalidDimensions,
    FrameTooLarge,
    UnsupportedPixelFormat,
    InvalidTextureHandle,
    InvalidSynchronization,
    InvalidSynchronizationValues,
    ReservedFieldsNotZero,
};

SharedGpuFrameValidationError ValidateSharedGpuFrame(
    const SharedGpuFrameDescriptorV1& descriptor,
    const SharedGpuFrameValidationContext& context) noexcept;

const char* SharedGpuFrameValidationErrorName(
    SharedGpuFrameValidationError error) noexcept;

enum class SharedGpuFramePublishResult : std::uint8_t {
    Published = 0,
    Invalid,
    Stale,
    WriterBusy,
};

// Single-consumer, bounded latest-frame mailbox.
//
// The IPC/import worker is the producer of descriptors. Present is the
// consumer. TryReadLatest performs one sequence check and never locks, spins,
// waits on a handle, or allocates. If a publication overlaps the read, Present
// simply skips that frame and tries again at its next opportunity.
class LatestSharedGpuFrameMailbox final {
public:
    SharedGpuFramePublishResult Publish(
        const SharedGpuFrameDescriptorV1& descriptor,
        const SharedGpuFrameValidationContext& context,
        SharedGpuFrameValidationError* validationError = nullptr) noexcept;

    bool TryReadLatest(
        std::uint64_t generationAlreadyConsumed,
        SharedGpuFrameDescriptorV1& destination) const noexcept;

    // Clear is intended for the lifecycle/control thread, not Present. It is
    // best-effort and returns false rather than waiting for a writer.
    bool TryClear() noexcept;

    std::uint64_t PublishedFrames() const noexcept;
    std::uint64_t InvalidFrames() const noexcept;
    std::uint64_t StaleFrames() const noexcept;
    std::uint64_t ContendedPublications() const noexcept;
    std::uint64_t ContendedReads() const noexcept;

private:
    static constexpr std::size_t DescriptorWordCount =
        sizeof(SharedGpuFrameDescriptorV1) / sizeof(std::uint64_t);

    std::array<std::atomic<std::uint64_t>, DescriptorWordCount> words_{};
    std::atomic<std::uint64_t> sequence_{};
    std::atomic<std::uint64_t> latestGeneration_{};
    std::atomic_flag writer_ = ATOMIC_FLAG_INIT;

    std::atomic<std::uint64_t> publishedFrames_{};
    std::atomic<std::uint64_t> invalidFrames_{};
    std::atomic<std::uint64_t> staleFrames_{};
    std::atomic<std::uint64_t> contendedPublications_{};
    mutable std::atomic<std::uint64_t> contendedReads_{};
};

} // namespace rwui::transport
