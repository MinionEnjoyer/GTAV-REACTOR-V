#pragma once

#include "LegacyStartupHookCore.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

namespace reactorv::bootstrap {

// A signature is data supplied by a separately reviewed build profile. The
// scanner itself has no knowledge of GTA addresses or executable layouts.
struct LegacyPatternByte {
    std::uint8_t value{};
    bool wildcard{};
};

enum class LegacyPatternScanStatus : std::uint8_t {
    Invalid,
    Missing,
    Unique,
    Ambiguous,
};

struct LegacyPatternScanResult {
    LegacyPatternScanStatus status{LegacyPatternScanStatus::Invalid};
    std::size_t firstMatchOffset{};
    std::size_t matchCount{};

    [[nodiscard]] constexpr bool IsUnique() const noexcept {
        return status == LegacyPatternScanStatus::Unique;
    }
};

// GTA Legacy 3889 has been observed using either EAX (ModRM 05) or ECX
// (ModRM 0D) as the source of the same RIP-relative dword state store. Keep
// this profile-specific allowlist exact; other register encodings require
// independent live evidence before they can become discovery evidence.
inline constexpr std::size_t Legacy3889InitStateStoreInstructionLength = 6;

[[nodiscard]] constexpr bool IsLegacy3889InitStateStoreInstruction(
    const std::span<const std::uint8_t> instruction) noexcept {
    return instruction.size() ==
            Legacy3889InitStateStoreInstructionLength &&
        instruction[0] == 0x89 &&
        (instruction[1] == 0x05 || instruction[1] == 0x0D);
}

// Scans an already supplied, readable byte range. Wildcards match exactly one
// byte. An empty signature is invalid; an empty image is a valid non-match.
// Ambiguity is retained explicitly so callers cannot accidentally install a
// detour at the first of several candidates.
[[nodiscard]] LegacyPatternScanResult ScanLegacyPattern(
    std::span<const std::uint8_t> image,
    std::span<const LegacyPatternByte> signature) noexcept;

enum class LegacyRipDecodeStatus : std::uint8_t {
    Success,
    InvalidArguments,
    InstructionOutOfBounds,
    DisplacementOutOfBounds,
    TargetOutOfBounds,
};

struct LegacyRipDecodeResult {
    LegacyRipDecodeStatus status{LegacyRipDecodeStatus::InvalidArguments};
    std::size_t targetOffset{};
    std::int32_t displacement{};

    [[nodiscard]] constexpr bool IsSuccess() const noexcept {
        return status == LegacyRipDecodeStatus::Success;
    }
};

// Platform-neutral PE section facts used only for the build-3889 target
// validation exception. Windows can report a large mapped image region as
// PAGE_EXECUTE_READWRITE even when the PE section containing the target is
// ordinary non-executable data. Keep this inspection independent from the
// runtime page query so the exceptional decision is deterministic and fully
// testable.
struct LegacyPeSectionDescriptor {
    std::array<std::uint8_t, 8> name{};
    std::uint32_t virtualAddress{};
    std::uint32_t virtualSize{};
    std::uint32_t rawSize{};
    std::uint32_t characteristics{};
};

enum class LegacyDataSectionStatus : std::uint8_t {
    NotChecked,
    Accepted,
    InvalidArguments,
    HeaderTableRejected,
    HeaderReadFault,
    MalformedSectionRange,
    Missing,
    Ambiguous,
    TargetCrossesBoundary,
    NameRejected,
    CharacteristicsRejected,
};

struct LegacyDataSectionInspection {
    LegacyDataSectionStatus status{LegacyDataSectionStatus::NotChecked};
    std::size_t containingSectionCount{};
    LegacyPeSectionDescriptor section{};

    [[nodiscard]] constexpr bool IsAccepted() const noexcept {
        return status == LegacyDataSectionStatus::Accepted &&
            containingSectionCount == 1;
    }
};

inline constexpr std::uint32_t LegacyPeSectionCode = 0x00000020U;
inline constexpr std::uint32_t LegacyPeSectionInitializedData = 0x00000040U;
inline constexpr std::uint32_t LegacyPeSectionExecute = 0x20000000U;
inline constexpr std::uint32_t LegacyPeSectionRead = 0x40000000U;
inline constexpr std::uint32_t LegacyPeSectionWrite = 0x80000000U;

[[nodiscard]] constexpr bool IsExactLegacyDataSectionName(
    const std::array<std::uint8_t, 8>& name) noexcept {
    return name[0] == '.' && name[1] == 'd' && name[2] == 'a' &&
        name[3] == 't' && name[4] == 'a' && name[5] == 0 &&
        name[6] == 0 && name[7] == 0;
}

[[nodiscard]] constexpr LegacyDataSectionInspection
InspectLegacyTargetDataSection(
    const std::span<const LegacyPeSectionDescriptor> sections,
    const std::size_t imageSize,
    const std::size_t targetRva,
    const std::size_t targetSize) noexcept {
    LegacyDataSectionInspection result{};
    if (sections.empty() || imageSize == 0 || targetSize == 0 ||
        targetRva > imageSize || targetSize > imageSize - targetRva) {
        result.status = LegacyDataSectionStatus::InvalidArguments;
        return result;
    }

    const auto targetEnd = targetRva + targetSize;
    std::size_t startMatchCount{};
    std::size_t completeMatchCount{};
    for (const auto& section : sections) {
        const auto start = static_cast<std::size_t>(section.virtualAddress);
        const auto size = static_cast<std::size_t>(section.virtualSize);
        if (size == 0) {
            continue;
        }
        if (start > imageSize || size > imageSize - start) {
            result.status = LegacyDataSectionStatus::MalformedSectionRange;
            return result;
        }
        const auto end = start + size;
        if (targetRva >= start && targetRva < end) {
            ++startMatchCount;
            if (targetEnd <= end) {
                ++completeMatchCount;
                result.section = section;
            }
        }
    }

    result.containingSectionCount = startMatchCount;
    if (startMatchCount == 0) {
        result.status = LegacyDataSectionStatus::Missing;
        return result;
    }
    if (startMatchCount != 1 || completeMatchCount > 1) {
        result.status = LegacyDataSectionStatus::Ambiguous;
        return result;
    }
    if (completeMatchCount != 1) {
        result.status = LegacyDataSectionStatus::TargetCrossesBoundary;
        return result;
    }
    if (!IsExactLegacyDataSectionName(result.section.name)) {
        result.status = LegacyDataSectionStatus::NameRejected;
        return result;
    }
    const auto flags = result.section.characteristics;
    const bool required =
        (flags & LegacyPeSectionInitializedData) != 0 &&
        (flags & LegacyPeSectionRead) != 0 &&
        (flags & LegacyPeSectionWrite) != 0;
    const bool forbidden = (flags & LegacyPeSectionExecute) != 0 ||
        (flags & LegacyPeSectionCode) != 0;
    if (!required || forbidden) {
        result.status = LegacyDataSectionStatus::CharacteristicsRejected;
        return result;
    }
    result.status = LegacyDataSectionStatus::Accepted;
    return result;
}

// Pure, address-free facts used to classify a decoded target after the
// platform adapter has queried its mapped-memory region. Keeping the policy
// here makes every rejection deterministic and unit-testable without exposing
// ASLR-sensitive process addresses in logs or receipts.
struct LegacyTargetValidationEvidence {
    bool imageBoundsPass{};
    bool alignmentPass{};
    bool regionQueryPass{};
    bool regionUsablePass{};
    bool ordinaryProtectionPass{};
    bool executeReadWriteObserved{};
    bool dataSectionBackedProtectionPass{};
    bool protectionPass{};
    bool typePass{};
    bool allocationBasePass{};
    bool regionAddressPass{};
    bool targetContainedPass{};
};

[[nodiscard]] constexpr bool IsLegacyTargetProtectionAccepted(
    const bool ordinaryWritableNonExecutable,
    const bool executeReadWriteObserved,
    const bool exactDataSectionAccepted) noexcept {
    return ordinaryWritableNonExecutable ||
        (executeReadWriteObserved && exactDataSectionAccepted);
}

enum class LegacyTargetValidationStatus : std::uint8_t {
    NotChecked,
    Accepted,
    ImageBoundsRejected,
    AlignmentRejected,
    RegionQueryFailed,
    RegionUnavailable,
    ProtectionRejected,
    TypeRejected,
    AllocationBaseRejected,
    RegionAddressOverflow,
    TargetOutsideRegion,
};

[[nodiscard]] constexpr LegacyTargetValidationStatus
ClassifyLegacyTargetValidation(
    const LegacyTargetValidationEvidence& evidence) noexcept {
    if (!evidence.imageBoundsPass) {
        return LegacyTargetValidationStatus::ImageBoundsRejected;
    }
    if (!evidence.alignmentPass) {
        return LegacyTargetValidationStatus::AlignmentRejected;
    }
    if (!evidence.regionQueryPass) {
        return LegacyTargetValidationStatus::RegionQueryFailed;
    }
    if (!evidence.regionUsablePass) {
        return LegacyTargetValidationStatus::RegionUnavailable;
    }
    if (!evidence.protectionPass) {
        return LegacyTargetValidationStatus::ProtectionRejected;
    }
    if (!evidence.typePass) {
        return LegacyTargetValidationStatus::TypeRejected;
    }
    if (!evidence.allocationBasePass) {
        return LegacyTargetValidationStatus::AllocationBaseRejected;
    }
    if (!evidence.regionAddressPass) {
        return LegacyTargetValidationStatus::RegionAddressOverflow;
    }
    if (!evidence.targetContainedPass) {
        return LegacyTargetValidationStatus::TargetOutsideRegion;
    }
    return LegacyTargetValidationStatus::Accepted;
}

// Decodes a little-endian signed rel32 operand without dereferencing it. All
// offsets are relative to the supplied image, so no process address is baked
// into discovery. targetSize is the minimum readable range required at the
// decoded destination.
[[nodiscard]] LegacyRipDecodeResult DecodeLegacyRipRelativeTarget(
    std::span<const std::uint8_t> image,
    std::size_t instructionOffset,
    std::size_t displacementOffsetWithinInstruction,
    std::size_t instructionLength,
    std::size_t targetSize = 1) noexcept;

// Decodes a copied instruction without reading through an untrusted process
// image span. instructionRva is the instruction's position in the original
// image; imageSize is the complete mapped image size. The caller remains
// responsible for validating the instruction opcode before accepting the
// result as build evidence.
[[nodiscard]] LegacyRipDecodeResult DecodeLegacyRipRelativeInstruction(
    std::span<const std::uint8_t> instruction,
    std::size_t instructionRva,
    std::size_t displacementOffsetWithinInstruction,
    std::size_t instructionLength,
    std::size_t imageSize,
    std::size_t targetSize = 1) noexcept;

// Exact-build raw init-state values corroborated by a live Legacy 1.0.3889.0
// trace for PE timestamp 0x6A4F9ED3 and image size 0x03E5BC00. Startup first
// exposed 0, 2, and 5; the stable main-menu -> Story route was 23 -> 14 -> 0.
// An early 0 is diagnostic Story-active evidence only and cannot arm an edge;
// 2 and 5 remain unsupported and reset any provisional sequence. Keep these
// values isolated here rather than promoting them to a generic GTA lifecycle
// contract or applying them to another executable identity.
inline constexpr std::int32_t Legacy3889FrontendInitState = 23;
inline constexpr std::int32_t Legacy3889TransitionInitState = 14;
inline constexpr std::int32_t Legacy3889ActiveInitState = 0;
inline constexpr std::uint32_t Legacy3889DefaultStableSamples = 3;

enum class Legacy3889ClassificationStatus : std::uint8_t {
    InvalidConfiguration,
    UnsupportedRawValue,
    Debouncing,
    StableButUnarmed,
    Grounded,
};

struct Legacy3889Classification {
    Legacy3889ClassificationStatus status{
        Legacy3889ClassificationStatus::InvalidConfiguration};
    LegacyStartupRawState state{LegacyStartupRawState::Unknown};
    LegacyStartupObservationStatus observationStatus{
        LegacyStartupObservationStatus::Unavailable};
    std::int32_t rawValue{};
    std::uint32_t consecutiveSamples{};
    bool frontendArmed{};

    [[nodiscard]] constexpr bool IsGrounded() const noexcept {
        return status == Legacy3889ClassificationStatus::Grounded &&
            observationStatus == LegacyStartupObservationStatus::Grounded;
    }
};

// Pure sampling state for the Legacy 3889 hypothesis. It performs no reads,
// detours, IPC, timing, or routing. A frontend observation is not grounded and
// cannot arm the sequence until multiple consecutive samples agree. A stable
// transition without that prior arm is explicitly withheld.
class Legacy3889RawInitStateClassifier final {
public:
    explicit Legacy3889RawInitStateClassifier(
        std::uint32_t requiredStableSamples =
            Legacy3889DefaultStableSamples) noexcept;

    void Reset() noexcept;
    [[nodiscard]] Legacy3889Classification Classify(
        std::int32_t rawValue) noexcept;
    [[nodiscard]] bool IsConfigurationValid() const noexcept;
    [[nodiscard]] bool IsFrontendArmed() const noexcept;

private:
    std::uint32_t _requiredStableSamples{};
    std::uint32_t _consecutiveSamples{};
    std::int32_t _candidateRawValue{};
    bool _hasCandidate{};
    bool _frontendArmed{};
};

// Tracks lifecycle boundaries independently of edge emission. This allows a
// shadow probe that attached while Story was already active to recover when
// GTA later returns to the frontend, without treating that first late attach
// as an EnteringStory edge.
class LegacyStartupSessionBoundaryTracker final {
public:
    void Reset() noexcept;
    [[nodiscard]] bool ObserveGrounded(
        LegacyStartupRawState state) noexcept;

private:
    bool _leftFrontend{};
};

} // namespace reactorv::bootstrap
