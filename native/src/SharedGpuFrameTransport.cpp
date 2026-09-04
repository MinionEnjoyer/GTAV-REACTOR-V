#include "SharedGpuFrameTransport.h"

#include <array>
#include <cstring>
#include <limits>

namespace rwui::transport {
namespace {

constexpr std::uint32_t KnownFlags = SharedGpuFrameRequiredFlags;

bool InvalidHandleValue(const std::uint64_t value) noexcept {
    return value == 0 || value == std::numeric_limits<std::uint64_t>::max();
}

bool NonzeroSession(const SharedGpuFrameValidationContext& context) noexcept {
    return context.expectedSessionIdHigh != 0 ||
        context.expectedSessionIdLow != 0;
}

class WriterRelease final {
public:
    explicit WriterRelease(std::atomic_flag& flag) noexcept : flag_(flag) {}
    ~WriterRelease() { flag_.clear(std::memory_order_release); }

    WriterRelease(const WriterRelease&) = delete;
    WriterRelease& operator=(const WriterRelease&) = delete;

private:
    std::atomic_flag& flag_;
};

} // namespace

SharedGpuFrameValidationError ValidateSharedGpuFrame(
    const SharedGpuFrameDescriptorV1& descriptor,
    const SharedGpuFrameValidationContext& context) noexcept {
    if (context.expectedProducerProcessId == 0 ||
        context.expectedConsumerProcessId == 0 ||
        context.expectedProducerCreationTime == 0 ||
        context.expectedConsumerCreationTime == 0 ||
        !NonzeroSession(context) ||
        context.maximumWidth == 0 || context.maximumHeight == 0 ||
        context.maximumWidth > SharedGpuFrameMaximumDimension ||
        context.maximumHeight > SharedGpuFrameMaximumDimension) {
        return SharedGpuFrameValidationError::InvalidValidationContext;
    }
    if (descriptor.magic != SharedGpuFrameMagic) {
        return SharedGpuFrameValidationError::BadMagic;
    }
    if (descriptor.versionMajor != SharedGpuFrameVersionMajor) {
        return SharedGpuFrameValidationError::UnsupportedMajorVersion;
    }
    const bool cpu = descriptor.synchronization == SharedGpuSynchronization::CpuBgraMapping;
    if ((cpu && descriptor.versionMinor != CpuFrameVersionMinor) ||
        (!cpu && descriptor.versionMinor > SharedGpuFrameVersionMinor)) {
        return SharedGpuFrameValidationError::UnsupportedMinorVersion;
    }
    if (descriptor.byteSize != sizeof(SharedGpuFrameDescriptorV1)) {
        return SharedGpuFrameValidationError::InvalidByteSize;
    }
    if ((descriptor.flags & SharedGpuFrameRequiredFlags) !=
        SharedGpuFrameRequiredFlags) {
        return SharedGpuFrameValidationError::MissingRequiredFlags;
    }
    if ((descriptor.flags & ~KnownFlags) != 0) {
        return SharedGpuFrameValidationError::UnsupportedFlags;
    }
    if (descriptor.producerProcessId !=
        context.expectedProducerProcessId) {
        return SharedGpuFrameValidationError::ProducerProcessMismatch;
    }
    if (descriptor.consumerProcessId !=
        context.expectedConsumerProcessId) {
        return SharedGpuFrameValidationError::ConsumerProcessMismatch;
    }
    if (descriptor.producerCreationTime !=
        context.expectedProducerCreationTime) {
        return SharedGpuFrameValidationError::ProducerCreationTimeMismatch;
    }
    if (descriptor.consumerCreationTime !=
        context.expectedConsumerCreationTime) {
        return SharedGpuFrameValidationError::ConsumerCreationTimeMismatch;
    }
    if (descriptor.sessionIdHigh != context.expectedSessionIdHigh ||
        descriptor.sessionIdLow != context.expectedSessionIdLow) {
        return SharedGpuFrameValidationError::SessionMismatch;
    }
    if (descriptor.generation == 0) {
        return SharedGpuFrameValidationError::InvalidGeneration;
    }
    if (descriptor.resourceEpoch == 0) {
        return SharedGpuFrameValidationError::InvalidResourceEpoch;
    }
    if (descriptor.slotCount == 0 ||
        descriptor.slotCount > SharedGpuFrameMaximumSlots) {
        return SharedGpuFrameValidationError::InvalidSlotCount;
    }
    if (descriptor.slotIndex >= descriptor.slotCount) {
        return SharedGpuFrameValidationError::InvalidSlotIndex;
    }
    if (descriptor.width == 0 || descriptor.height == 0 ||
        descriptor.width > context.maximumWidth ||
        descriptor.height > context.maximumHeight) {
        return SharedGpuFrameValidationError::InvalidDimensions;
    }
    const auto frameBytes = static_cast<std::uint64_t>(descriptor.width) *
        static_cast<std::uint64_t>(descriptor.height) * 4ull;
    if (frameBytes == 0 || frameBytes > SharedGpuFrameMaximumBytes) {
        return SharedGpuFrameValidationError::FrameTooLarge;
    }
    if (descriptor.pixelFormat != SharedGpuPixelFormat::Bgra8Unorm &&
        descriptor.pixelFormat != SharedGpuPixelFormat::Bgra8UnormSrgb) {
        return SharedGpuFrameValidationError::UnsupportedPixelFormat;
    }
    if (InvalidHandleValue(descriptor.sharedTextureHandle)) {
        return SharedGpuFrameValidationError::InvalidTextureHandle;
    }

    switch (descriptor.synchronization) {
    case SharedGpuSynchronization::CpuBgraMapping:
        if (descriptor.slotCount != 1 || descriptor.slotIndex != 0 ||
            descriptor.sharedFenceHandle != 0 || descriptor.acquireValue != 0 || descriptor.releaseValue != 0)
            return SharedGpuFrameValidationError::InvalidSynchronization;
        if (frameBytes > CpuFrameMaximumBytes) return SharedGpuFrameValidationError::FrameTooLarge;
        break;
    case SharedGpuSynchronization::D3d11KeyedMutex:
        if (descriptor.sharedFenceHandle != 0) {
            return SharedGpuFrameValidationError::InvalidSynchronization;
        }
        if (descriptor.acquireValue == descriptor.releaseValue) {
            return SharedGpuFrameValidationError::InvalidSynchronizationValues;
        }
        break;
    case SharedGpuSynchronization::D3d12SharedFence:
        if (InvalidHandleValue(descriptor.sharedFenceHandle)) {
            return SharedGpuFrameValidationError::InvalidSynchronization;
        }
        if (descriptor.acquireValue == 0 ||
            descriptor.releaseValue <= descriptor.acquireValue) {
            return SharedGpuFrameValidationError::InvalidSynchronizationValues;
        }
        break;
    case SharedGpuSynchronization::None:
    default:
        return SharedGpuFrameValidationError::InvalidSynchronization;
    }

    for (const auto value : descriptor.reserved) {
        if (value != 0) {
            return SharedGpuFrameValidationError::ReservedFieldsNotZero;
        }
    }
    return SharedGpuFrameValidationError::None;
}

const char* SharedGpuFrameValidationErrorName(
    const SharedGpuFrameValidationError error) noexcept {
    switch (error) {
    case SharedGpuFrameValidationError::None: return "none";
    case SharedGpuFrameValidationError::InvalidValidationContext:
        return "invalid_validation_context";
    case SharedGpuFrameValidationError::BadMagic: return "bad_magic";
    case SharedGpuFrameValidationError::UnsupportedMajorVersion:
        return "unsupported_major_version";
    case SharedGpuFrameValidationError::UnsupportedMinorVersion:
        return "unsupported_minor_version";
    case SharedGpuFrameValidationError::InvalidByteSize:
        return "invalid_byte_size";
    case SharedGpuFrameValidationError::MissingRequiredFlags:
        return "missing_required_flags";
    case SharedGpuFrameValidationError::UnsupportedFlags:
        return "unsupported_flags";
    case SharedGpuFrameValidationError::ProducerProcessMismatch:
        return "producer_process_mismatch";
    case SharedGpuFrameValidationError::ConsumerProcessMismatch:
        return "consumer_process_mismatch";
    case SharedGpuFrameValidationError::ProducerCreationTimeMismatch:
        return "producer_creation_time_mismatch";
    case SharedGpuFrameValidationError::ConsumerCreationTimeMismatch:
        return "consumer_creation_time_mismatch";
    case SharedGpuFrameValidationError::SessionMismatch:
        return "session_mismatch";
    case SharedGpuFrameValidationError::InvalidGeneration:
        return "invalid_generation";
    case SharedGpuFrameValidationError::InvalidResourceEpoch:
        return "invalid_resource_epoch";
    case SharedGpuFrameValidationError::InvalidSlotCount:
        return "invalid_slot_count";
    case SharedGpuFrameValidationError::InvalidSlotIndex:
        return "invalid_slot_index";
    case SharedGpuFrameValidationError::InvalidDimensions:
        return "invalid_dimensions";
    case SharedGpuFrameValidationError::FrameTooLarge:
        return "frame_too_large";
    case SharedGpuFrameValidationError::UnsupportedPixelFormat:
        return "unsupported_pixel_format";
    case SharedGpuFrameValidationError::InvalidTextureHandle:
        return "invalid_texture_handle";
    case SharedGpuFrameValidationError::InvalidSynchronization:
        return "invalid_synchronization";
    case SharedGpuFrameValidationError::InvalidSynchronizationValues:
        return "invalid_synchronization_values";
    case SharedGpuFrameValidationError::ReservedFieldsNotZero:
        return "reserved_fields_not_zero";
    default: return "unknown";
    }
}

SharedGpuFramePublishResult LatestSharedGpuFrameMailbox::Publish(
    const SharedGpuFrameDescriptorV1& descriptor,
    const SharedGpuFrameValidationContext& context,
    SharedGpuFrameValidationError* const validationError) noexcept {
    const auto error = ValidateSharedGpuFrame(descriptor, context);
    if (validationError != nullptr) *validationError = error;
    if (error != SharedGpuFrameValidationError::None) {
        invalidFrames_.fetch_add(1, std::memory_order_relaxed);
        return SharedGpuFramePublishResult::Invalid;
    }
    if (writer_.test_and_set(std::memory_order_acquire)) {
        contendedPublications_.fetch_add(1, std::memory_order_relaxed);
        return SharedGpuFramePublishResult::WriterBusy;
    }
    const WriterRelease release(writer_);

    if (descriptor.generation <=
        latestGeneration_.load(std::memory_order_acquire)) {
        staleFrames_.fetch_add(1, std::memory_order_relaxed);
        return SharedGpuFramePublishResult::Stale;
    }

    std::array<std::uint64_t, DescriptorWordCount> words{};
    std::memcpy(words.data(), &descriptor, sizeof(descriptor));

    auto priorSequence = sequence_.load(std::memory_order_relaxed);
    if ((priorSequence & 1ull) != 0) ++priorSequence;
    sequence_.store(priorSequence + 1, std::memory_order_release);
    for (std::size_t index = 0; index < words.size(); ++index) {
        words_[index].store(words[index], std::memory_order_relaxed);
    }
    latestGeneration_.store(descriptor.generation, std::memory_order_release);
    sequence_.store(priorSequence + 2, std::memory_order_release);
    publishedFrames_.fetch_add(1, std::memory_order_relaxed);
    return SharedGpuFramePublishResult::Published;
}

bool LatestSharedGpuFrameMailbox::TryReadLatest(
    const std::uint64_t generationAlreadyConsumed,
    SharedGpuFrameDescriptorV1& destination) const noexcept {
    const auto firstSequence = sequence_.load(std::memory_order_acquire);
    if (firstSequence == 0 || (firstSequence & 1ull) != 0) {
        if ((firstSequence & 1ull) != 0) {
            contendedReads_.fetch_add(1, std::memory_order_relaxed);
        }
        return false;
    }

    std::array<std::uint64_t, DescriptorWordCount> words{};
    for (std::size_t index = 0; index < words.size(); ++index) {
        words[index] = words_[index].load(std::memory_order_relaxed);
    }
    const auto secondSequence = sequence_.load(std::memory_order_acquire);
    if (firstSequence != secondSequence || (secondSequence & 1ull) != 0) {
        contendedReads_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }

    SharedGpuFrameDescriptorV1 snapshot{};
    std::memcpy(&snapshot, words.data(), sizeof(snapshot));
    if (snapshot.generation == 0 ||
        snapshot.generation <= generationAlreadyConsumed) {
        return false;
    }
    destination = snapshot;
    return true;
}

bool LatestSharedGpuFrameMailbox::TryClear() noexcept {
    if (writer_.test_and_set(std::memory_order_acquire)) return false;
    const WriterRelease release(writer_);

    auto priorSequence = sequence_.load(std::memory_order_relaxed);
    if ((priorSequence & 1ull) != 0) ++priorSequence;
    sequence_.store(priorSequence + 1, std::memory_order_release);
    for (auto& word : words_) word.store(0, std::memory_order_relaxed);
    latestGeneration_.store(0, std::memory_order_release);
    sequence_.store(priorSequence + 2, std::memory_order_release);
    return true;
}

std::uint64_t LatestSharedGpuFrameMailbox::PublishedFrames() const noexcept {
    return publishedFrames_.load(std::memory_order_relaxed);
}

std::uint64_t LatestSharedGpuFrameMailbox::InvalidFrames() const noexcept {
    return invalidFrames_.load(std::memory_order_relaxed);
}

std::uint64_t LatestSharedGpuFrameMailbox::StaleFrames() const noexcept {
    return staleFrames_.load(std::memory_order_relaxed);
}

std::uint64_t LatestSharedGpuFrameMailbox::ContendedPublications() const noexcept {
    return contendedPublications_.load(std::memory_order_relaxed);
}

std::uint64_t LatestSharedGpuFrameMailbox::ContendedReads() const noexcept {
    return contendedReads_.load(std::memory_order_relaxed);
}

} // namespace rwui::transport
