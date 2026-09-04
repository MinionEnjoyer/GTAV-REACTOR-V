#include "SharedGpuFrameTransport.h"

#include <atomic>
#include <iostream>
#include <thread>

namespace {

int failures = 0;

void Check(const bool condition, const char* const message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        ++failures;
    }
}

rwui::transport::SharedGpuFrameValidationContext Context() {
    return {
        1001,
        2002,
        0x0102030405060708ull,
        0x0807060504030201ull,
        0x1112131415161718ull,
        0x2122232425262728ull,
        8192,
        8192,
    };
}

rwui::transport::SharedGpuFrameDescriptorV1 Descriptor(
    const std::uint64_t generation = 1) {
    using namespace rwui::transport;
    const auto context = Context();
    SharedGpuFrameDescriptorV1 descriptor{};
    descriptor.producerProcessId = context.expectedProducerProcessId;
    descriptor.consumerProcessId = context.expectedConsumerProcessId;
    descriptor.producerCreationTime = context.expectedProducerCreationTime;
    descriptor.consumerCreationTime = context.expectedConsumerCreationTime;
    descriptor.sessionIdHigh = context.expectedSessionIdHigh;
    descriptor.sessionIdLow = context.expectedSessionIdLow;
    descriptor.generation = generation;
    descriptor.resourceEpoch = 1;
    descriptor.slotIndex = 0;
    descriptor.slotCount = 3;
    descriptor.width = 1920;
    descriptor.height = 1080;
    descriptor.pixelFormat = SharedGpuPixelFormat::Bgra8Unorm;
    descriptor.synchronization =
        SharedGpuSynchronization::D3d11KeyedMutex;
    descriptor.sharedTextureHandle = 0x1234;
    descriptor.acquireValue = generation * 2 - 1;
    descriptor.releaseValue = generation * 2;
    return descriptor;
}

template <typename Mutator>
void Rejects(
    const rwui::transport::SharedGpuFrameValidationError expected,
    Mutator mutator,
    const char* const message) {
    auto descriptor = Descriptor();
    auto context = Context();
    mutator(descriptor, context);
    Check(rwui::transport::ValidateSharedGpuFrame(descriptor, context) ==
        expected, message);
}

} // namespace

int main() {
    using namespace rwui::transport;

    Check(ValidateSharedGpuFrame(Descriptor(), Context()) ==
        SharedGpuFrameValidationError::None,
        "a complete version-1 keyed-mutex descriptor is accepted");

    auto cpu = Descriptor();
    cpu.versionMinor = CpuFrameVersionMinor;
    cpu.synchronization = SharedGpuSynchronization::CpuBgraMapping;
    cpu.slotCount = 1;
    cpu.acquireValue = cpu.releaseValue = 0;
    const auto rejectsCpu = [&](auto mutate, SharedGpuFrameValidationError expected, const char* message) {
        auto changed = cpu; mutate(changed);
        Check(ValidateSharedGpuFrame(changed, Context()) == expected, message);
    };
    Check(ValidateSharedGpuFrame(cpu, Context()) == SharedGpuFrameValidationError::None,
        "explicit 1.2 CPU mapping retains the authenticated descriptor contract");
    rejectsCpu([](auto& f) { f.versionMinor = 1; }, SharedGpuFrameValidationError::UnsupportedMinorVersion,
        "CPU handles cannot masquerade as version-1.1 GPU textures");
    rejectsCpu([](auto& f) { f.width = 4096; f.height = 2160; }, SharedGpuFrameValidationError::FrameTooLarge,
        "CPU payloads are capped at 32 MiB even inside the larger GPU budget");
    rejectsCpu([](auto& f) { f.acquireValue = 1; }, SharedGpuFrameValidationError::InvalidSynchronization,
        "CPU buffers use authenticated ACK ownership, never a GPU mutex key");
    rejectsCpu([](auto& f) { f.slotCount = 2; }, SharedGpuFrameValidationError::InvalidSynchronization,
        "CPU diagnostic keeps exactly one in-flight mapping");
    rejectsCpu([](auto& f) { ++f.producerCreationTime; }, SharedGpuFrameValidationError::ProducerCreationTimeMismatch,
        "CPU transport preserves producer PID-reuse rejection");
    rejectsCpu([](auto& f) { ++f.sessionIdLow; }, SharedGpuFrameValidationError::SessionMismatch,
        "CPU transport preserves session isolation");

    Rejects(SharedGpuFrameValidationError::InvalidValidationContext,
        [](auto&, auto& context) { context.expectedProducerProcessId = 0; },
        "an unbound validation context fails closed");
    Rejects(SharedGpuFrameValidationError::BadMagic,
        [](auto& value, auto&) { value.magic ^= 1; },
        "bad protocol magic is rejected");
    Rejects(SharedGpuFrameValidationError::UnsupportedMajorVersion,
        [](auto& value, auto&) { ++value.versionMajor; },
        "unknown major versions are rejected");
    Rejects(SharedGpuFrameValidationError::UnsupportedMinorVersion,
        [](auto& value, auto&) { ++value.versionMinor; },
        "newer minor versions are rejected until understood");
    Rejects(SharedGpuFrameValidationError::InvalidByteSize,
        [](auto& value, auto&) { --value.byteSize; },
        "truncated descriptors are rejected");
    Rejects(SharedGpuFrameValidationError::MissingRequiredFlags,
        [](auto& value, auto&) {
            value.flags &= ~static_cast<std::uint32_t>(
                SharedGpuFrameFlags::ProducerLocalNtHandles);
        },
        "consumer-local or ambiguous handle semantics are rejected");
    Rejects(SharedGpuFrameValidationError::UnsupportedFlags,
        [](auto& value, auto&) { value.flags |= 1u << 31; },
        "unknown flags fail closed");
    Rejects(SharedGpuFrameValidationError::ProducerProcessMismatch,
        [](auto& value, auto&) { ++value.producerProcessId; },
        "a descriptor cannot redirect the expected producer PID");
    Rejects(SharedGpuFrameValidationError::ConsumerProcessMismatch,
        [](auto& value, auto&) { ++value.consumerProcessId; },
        "a descriptor for another game process is rejected");
    Rejects(SharedGpuFrameValidationError::ProducerCreationTimeMismatch,
        [](auto& value, auto&) { ++value.producerCreationTime; },
        "PID reuse is detected from process creation time");
    Rejects(SharedGpuFrameValidationError::ConsumerCreationTimeMismatch,
        [](auto& value, auto&) { ++value.consumerCreationTime; },
        "target PID reuse is detected from process creation time");
    Rejects(SharedGpuFrameValidationError::SessionMismatch,
        [](auto& value, auto&) { ++value.sessionIdLow; },
        "another IPC session cannot publish frames");
    Rejects(SharedGpuFrameValidationError::InvalidGeneration,
        [](auto& value, auto&) { value.generation = 0; },
        "generation zero is reserved");
    Rejects(SharedGpuFrameValidationError::InvalidResourceEpoch,
        [](auto& value, auto&) { value.resourceEpoch = 0; },
        "resource epoch zero is reserved");
    Rejects(SharedGpuFrameValidationError::InvalidSlotCount,
        [](auto& value, auto&) {
            value.slotCount = SharedGpuFrameMaximumSlots + 1;
        },
        "the retained GPU pool cannot exceed three slots");
    Rejects(SharedGpuFrameValidationError::InvalidSlotIndex,
        [](auto& value, auto&) { value.slotIndex = value.slotCount; },
        "slot index must address the bounded pool");
    Rejects(SharedGpuFrameValidationError::InvalidDimensions,
        [](auto& value, auto&) { value.width = 0; },
        "empty textures are rejected");
    Rejects(SharedGpuFrameValidationError::FrameTooLarge,
        [](auto& value, auto&) {
            value.width = 8192;
            value.height = 8192;
        },
        "oversized GPU allocations are rejected before handle import");
    Rejects(SharedGpuFrameValidationError::UnsupportedPixelFormat,
        [](auto& value, auto&) {
            value.pixelFormat = SharedGpuPixelFormat::Unknown;
        },
        "unknown texture formats are rejected");
    Rejects(SharedGpuFrameValidationError::InvalidTextureHandle,
        [](auto& value, auto&) { value.sharedTextureHandle = 0; },
        "null texture handles are rejected");
    Rejects(SharedGpuFrameValidationError::InvalidSynchronization,
        [](auto& value, auto&) {
            value.synchronization = SharedGpuSynchronization::None;
        },
        "unsynchronized cross-process textures are rejected");
    Rejects(SharedGpuFrameValidationError::InvalidSynchronizationValues,
        [](auto& value, auto&) {
            value.releaseValue = value.acquireValue;
        },
        "keyed-mutex ownership cannot be released to the acquisition key");
    Rejects(SharedGpuFrameValidationError::ReservedFieldsNotZero,
        [](auto& value, auto&) { value.reserved[2] = 1; },
        "reserved fields must remain zero");

    auto fenceDescriptor = Descriptor();
    fenceDescriptor.synchronization =
        SharedGpuSynchronization::D3d12SharedFence;
    fenceDescriptor.sharedFenceHandle = 0x5678;
    fenceDescriptor.acquireValue = 10;
    fenceDescriptor.releaseValue = 11;
    Check(ValidateSharedGpuFrame(fenceDescriptor, Context()) ==
        SharedGpuFrameValidationError::None,
        "the versioned contract reserves a validated D3D12 fence path");

    LatestSharedGpuFrameMailbox mailbox;
    SharedGpuFrameValidationError error{};
    Check(mailbox.Publish(Descriptor(1), Context(), &error) ==
        SharedGpuFramePublishResult::Published &&
        error == SharedGpuFrameValidationError::None,
        "first valid frame is published");
    Check(mailbox.Publish(Descriptor(2), Context()) ==
        SharedGpuFramePublishResult::Published,
        "a newer frame replaces the old one without a queue");
    Check(mailbox.Publish(Descriptor(3), Context()) ==
        SharedGpuFramePublishResult::Published,
        "latest-frame-wins does not retain intermediate descriptors");

    SharedGpuFrameDescriptorV1 snapshot{};
    Check(mailbox.TryReadLatest(0, snapshot) && snapshot.generation == 3,
        "the consumer observes only the newest published generation");
    Check(!mailbox.TryReadLatest(3, snapshot),
        "an already consumed generation is not returned twice");
    Check(mailbox.Publish(Descriptor(2), Context()) ==
        SharedGpuFramePublishResult::Stale,
        "out-of-order publication cannot roll the displayed frame back");

    auto bad = Descriptor(4);
    bad.consumerProcessId++;
    Check(mailbox.Publish(bad, Context(), &error) ==
        SharedGpuFramePublishResult::Invalid &&
        error == SharedGpuFrameValidationError::ConsumerProcessMismatch,
        "invalid frames are rejected before they enter the mailbox");
    Check(mailbox.TryReadLatest(0, snapshot) && snapshot.generation == 3,
        "invalid publication preserves the last known-good frame");
    Check(mailbox.PublishedFrames() == 3 &&
        mailbox.StaleFrames() == 1 && mailbox.InvalidFrames() == 1,
        "transport counters distinguish accepted, stale, and invalid frames");

    Check(mailbox.TryClear(), "lifecycle thread can clear an idle mailbox");
    Check(!mailbox.TryReadLatest(0, snapshot),
        "clear removes the published descriptor");

    // A modest producer/consumer stress pass exercises the atomic snapshot.
    // The consumer never loops inside TryReadLatest; a racing publication is
    // allowed to return false and Present would simply try on its next frame.
    LatestSharedGpuFrameMailbox concurrent;
    std::atomic_bool start{false};
    std::atomic_bool done{false};
    std::atomic_bool malformed{false};
    std::thread producer([&] {
        while (!start.load(std::memory_order_acquire)) {}
        for (std::uint64_t generation = 1; generation <= 10000; ++generation) {
            if (concurrent.Publish(Descriptor(generation), Context()) !=
                SharedGpuFramePublishResult::Published) {
                malformed.store(true, std::memory_order_release);
                break;
            }
        }
        done.store(true, std::memory_order_release);
    });
    start.store(true, std::memory_order_release);
    std::uint64_t observed{};
    while (!done.load(std::memory_order_acquire)) {
        SharedGpuFrameDescriptorV1 candidate{};
        if (concurrent.TryReadLatest(observed, candidate)) {
            if (ValidateSharedGpuFrame(candidate, Context()) !=
                    SharedGpuFrameValidationError::None ||
                candidate.generation <= observed) {
                malformed.store(true, std::memory_order_release);
                break;
            }
            observed = candidate.generation;
        }
    }
    producer.join();
    SharedGpuFrameDescriptorV1 finalFrame{};
    Check(concurrent.TryReadLatest(observed, finalFrame) || observed == 10000,
        "consumer can obtain the terminal generation after publication");
    if (finalFrame.generation > observed) observed = finalFrame.generation;
    Check(!malformed.load(std::memory_order_acquire) && observed == 10000,
        "atomic snapshots remain complete and monotonic under contention");

    if (failures == 0) {
        std::cout << "PASS: shared GPU frame transport policy tests\n";
    }
    return failures == 0 ? 0 : 1;
}
