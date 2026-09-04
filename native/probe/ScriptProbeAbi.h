#pragma once

#include <cstdint>

namespace reactorv::scriptprobe {

inline constexpr std::uint32_t SnapshotAbiVersion = 1;

inline constexpr std::uint8_t LoadingAvailableBit = 1U << 0U;
inline constexpr std::uint8_t LoadingBit = 1U << 1U;
inline constexpr std::uint8_t PlayerPlayingAvailableBit = 1U << 2U;
inline constexpr std::uint8_t PlayerPlayingBit = 1U << 3U;
inline constexpr std::uint8_t FrontendReadyAvailableBit = 1U << 4U;
inline constexpr std::uint8_t FrontendReadyBit = 1U << 5U;
inline constexpr std::uint8_t LandingMenuAvailableBit = 1U << 6U;
inline constexpr std::uint8_t LandingMenuActiveBit = 1U << 7U;

enum class SnapshotStatus : std::uint8_t {
    Unavailable = 0,
    Ready = 1,
    NativeFailure = 2,
    ParkedAfterRuntimeHandoff = 3,
};

// Versioned, fixed-width POD boundary shared between two independently loaded
// ASIs. Callers initialize structSize before invoking the exported reader.
// Reserved fields and the explicit sequence keep future additions ABI-safe.
struct Snapshot {
    std::uint32_t structSize{};
    std::uint32_t abiVersion{};
    std::uint8_t bits{};
    SnapshotStatus status{SnapshotStatus::Unavailable};
    std::uint16_t reserved{};
    std::uint32_t sequence{};
    std::uint64_t sampledAtTickMilliseconds{};
};

static_assert(sizeof(Snapshot) == 24, "Script probe ABI layout changed.");

using ReadSnapshotFunction = int(__stdcall*)(Snapshot* snapshot);

inline constexpr wchar_t ModuleName[] = L"ReactorV.ScriptProbe.asi";
inline constexpr char ReadSnapshotExportName[] =
    "ReactorVScriptProbeReadSnapshot";

} // namespace reactorv::scriptprobe
