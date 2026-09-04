#include "ScriptProbeAbi.h"
#include "ScriptProbeState.h"

#include <cstdint>
#include <stdexcept>

namespace {

void Require(const bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

} // namespace

int main() {
    using reactorv::scriptprobe::Snapshot;
    using reactorv::scriptprobe::SnapshotStatus;

    Snapshot neverPublished{};
    neverPublished.structSize = sizeof(Snapshot);
    Require(
        !reactorv::scriptprobe::ReadSnapshot(neverPublished),
        "An unpublished snapshot must not be reported as grounded evidence.");

    Snapshot undersized{};
    undersized.structSize = sizeof(Snapshot) - 1;
    Require(
        !reactorv::scriptprobe::ReadSnapshot(undersized),
        "The exported ABI must reject an undersized caller structure.");

    const std::uint8_t frontendBits =
        reactorv::scriptprobe::LoadingAvailableBit |
        reactorv::scriptprobe::PlayerPlayingAvailableBit |
        reactorv::scriptprobe::FrontendReadyAvailableBit |
        reactorv::scriptprobe::FrontendReadyBit |
        reactorv::scriptprobe::LandingMenuAvailableBit |
        reactorv::scriptprobe::LandingMenuActiveBit;
    reactorv::scriptprobe::PublishSnapshot(
        frontendBits,
        SnapshotStatus::Ready,
        12345);

    Snapshot published{};
    published.structSize = sizeof(Snapshot);
    Require(
        reactorv::scriptprobe::ReadSnapshot(published),
        "A completed publication must be readable.");
    Require(
        published.abiVersion == reactorv::scriptprobe::SnapshotAbiVersion,
        "The snapshot reader must publish the exact ABI version.");
    Require(
        published.bits == frontendBits &&
            published.status == SnapshotStatus::Ready &&
            published.sampledAtTickMilliseconds == 12345,
        "The snapshot reader must copy one coherent publication.");
    Require(
        published.sequence != 0 && (published.sequence & 1U) == 0,
        "Readers must only observe an even, completed publication sequence.");

    reactorv::scriptprobe::PublishSnapshot(
        0,
        SnapshotStatus::NativeFailure,
        45678);
    Snapshot nativeFailure{};
    nativeFailure.structSize = sizeof(Snapshot);
    Require(
        reactorv::scriptprobe::ReadSnapshot(nativeFailure) &&
            nativeFailure.status == SnapshotStatus::NativeFailure &&
            nativeFailure.bits == 0 &&
            nativeFailure.sampledAtTickMilliseconds == 45678 &&
            nativeFailure.sequence > published.sequence,
        "A scheduled companion fiber must preserve NativeFailure as lifecycle evidence instead of looking unpublished.");

    reactorv::scriptprobe::PublishSnapshot(
        0,
        SnapshotStatus::ParkedAfterRuntimeHandoff,
        67890);
    Snapshot parked{};
    parked.structSize = sizeof(Snapshot);
    Require(
        reactorv::scriptprobe::ReadSnapshot(parked) &&
            parked.status == SnapshotStatus::ParkedAfterRuntimeHandoff &&
            parked.bits == 0 &&
            parked.sampledAtTickMilliseconds == 67890 &&
            parked.sequence > published.sequence,
        "Runtime handoff must atomically replace rather than mix snapshots.");

    return 0;
}
