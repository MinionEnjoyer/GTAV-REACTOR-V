#include "ScriptProbeState.h"

#include <atomic>

namespace reactorv::scriptprobe {
namespace {

std::atomic<std::uint32_t> sequence{0};
std::atomic<std::uint8_t> publishedBits{0};
std::atomic<std::uint8_t> publishedStatus{
    static_cast<std::uint8_t>(SnapshotStatus::Unavailable)};
std::atomic<std::uint64_t> publishedAt{0};

} // namespace

void PublishSnapshot(
    const std::uint8_t bits,
    const SnapshotStatus status,
    const std::uint64_t sampledAtTickMilliseconds) noexcept {
    // One ScriptHook fiber is the sole writer. The odd/even sequence lets
    // Bootstrap copy a coherent bits/status/timestamp tuple without sharing a
    // mutex or ever executing GTA-native work on its worker/input threads.
    const auto writeSequence =
        sequence.fetch_add(1, std::memory_order_acq_rel) + 1U;
    publishedBits.store(bits, std::memory_order_relaxed);
    publishedStatus.store(
        static_cast<std::uint8_t>(status),
        std::memory_order_relaxed);
    publishedAt.store(sampledAtTickMilliseconds, std::memory_order_relaxed);
    sequence.store(writeSequence + 1U, std::memory_order_release);
}

bool ReadSnapshot(Snapshot& destination) noexcept {
    if (destination.structSize < sizeof(Snapshot)) return false;

    for (unsigned int attempt = 0; attempt < 8; ++attempt) {
        const auto before = sequence.load(std::memory_order_acquire);
        if (before == 0 || (before & 1U) != 0) continue;

        const auto bits = publishedBits.load(std::memory_order_relaxed);
        const auto status = static_cast<SnapshotStatus>(
            publishedStatus.load(std::memory_order_relaxed));
        const auto sampledAt = publishedAt.load(std::memory_order_relaxed);
        const auto after = sequence.load(std::memory_order_acquire);
        if (before != after || (after & 1U) != 0) continue;

        destination.structSize = sizeof(Snapshot);
        destination.abiVersion = SnapshotAbiVersion;
        destination.bits = bits;
        destination.status = status;
        destination.reserved = 0;
        destination.sequence = after;
        destination.sampledAtTickMilliseconds = sampledAt;
        return true;
    }
    return false;
}

} // namespace reactorv::scriptprobe
