#pragma once

#include "ScriptProbeAbi.h"

#include <cstdint>

namespace reactorv::scriptprobe {

void PublishSnapshot(
    std::uint8_t bits,
    SnapshotStatus status,
    std::uint64_t sampledAtTickMilliseconds) noexcept;

bool ReadSnapshot(Snapshot& destination) noexcept;

} // namespace reactorv::scriptprobe
