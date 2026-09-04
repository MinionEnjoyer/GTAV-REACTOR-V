#include "LegacyStartupDiscovery.h"

#include <bit>
#include <limits>

namespace reactorv::bootstrap {
namespace {

constexpr bool MatchesPatternByte(
    const std::uint8_t imageByte,
    const LegacyPatternByte& patternByte) noexcept {
    return patternByte.wildcard || imageByte == patternByte.value;
}

constexpr bool IsSupportedRawValue(const std::int32_t rawValue) noexcept {
    return rawValue == Legacy3889FrontendInitState ||
        rawValue == Legacy3889TransitionInitState ||
        rawValue == Legacy3889ActiveInitState;
}

constexpr LegacyStartupRawState MapRawValue(
    const std::int32_t rawValue) noexcept {
    if (rawValue == Legacy3889FrontendInitState) {
        return LegacyStartupRawState::Frontend;
    }
    if (rawValue == Legacy3889TransitionInitState) {
        return LegacyStartupRawState::StoryTransition;
    }
    if (rawValue == Legacy3889ActiveInitState) {
        return LegacyStartupRawState::StoryActive;
    }
    return LegacyStartupRawState::Unknown;
}

Legacy3889Classification Classification(
    const Legacy3889ClassificationStatus status,
    const LegacyStartupRawState state,
    const LegacyStartupObservationStatus observationStatus,
    const std::int32_t rawValue,
    const std::uint32_t consecutiveSamples,
    const bool frontendArmed) noexcept {
    return {
        status,
        state,
        observationStatus,
        rawValue,
        consecutiveSamples,
        frontendArmed,
    };
}

} // namespace

LegacyPatternScanResult ScanLegacyPattern(
    const std::span<const std::uint8_t> image,
    const std::span<const LegacyPatternByte> signature) noexcept {
    if (signature.empty()) {
        return {LegacyPatternScanStatus::Invalid, 0, 0};
    }
    if (signature.size() > image.size()) {
        return {LegacyPatternScanStatus::Missing, 0, 0};
    }

    LegacyPatternScanResult result{
        LegacyPatternScanStatus::Missing,
        0,
        0,
    };
    const auto lastCandidate = image.size() - signature.size();
    for (std::size_t candidate = 0; candidate <= lastCandidate; ++candidate) {
        bool matches = true;
        for (std::size_t index = 0; index < signature.size(); ++index) {
            if (!MatchesPatternByte(
                    image[candidate + index],
                    signature[index])) {
                matches = false;
                break;
            }
        }
        if (!matches) continue;

        if (result.matchCount == 0) result.firstMatchOffset = candidate;
        if (result.matchCount != std::numeric_limits<std::size_t>::max()) {
            ++result.matchCount;
        }
    }

    if (result.matchCount == 1) {
        result.status = LegacyPatternScanStatus::Unique;
    } else if (result.matchCount > 1) {
        result.status = LegacyPatternScanStatus::Ambiguous;
    }
    return result;
}

LegacyRipDecodeResult DecodeLegacyRipRelativeTarget(
    const std::span<const std::uint8_t> image,
    const std::size_t instructionOffset,
    const std::size_t displacementOffsetWithinInstruction,
    const std::size_t instructionLength,
    const std::size_t targetSize) noexcept {
    constexpr std::size_t DisplacementSize = sizeof(std::int32_t);
    if (instructionLength == 0 || targetSize == 0) {
        return {LegacyRipDecodeStatus::InvalidArguments, 0, 0};
    }
    if (instructionOffset > image.size() ||
        instructionLength > image.size() - instructionOffset) {
        return {LegacyRipDecodeStatus::InstructionOutOfBounds, 0, 0};
    }
    if (displacementOffsetWithinInstruction > instructionLength ||
        DisplacementSize >
            instructionLength - displacementOffsetWithinInstruction) {
        return {LegacyRipDecodeStatus::DisplacementOutOfBounds, 0, 0};
    }

    const auto displacementOffset =
        instructionOffset + displacementOffsetWithinInstruction;
    const auto encoded =
        static_cast<std::uint32_t>(image[displacementOffset]) |
        (static_cast<std::uint32_t>(image[displacementOffset + 1]) << 8U) |
        (static_cast<std::uint32_t>(image[displacementOffset + 2]) << 16U) |
        (static_cast<std::uint32_t>(image[displacementOffset + 3]) << 24U);
    const auto displacement = std::bit_cast<std::int32_t>(encoded);
    const auto nextInstruction = instructionOffset + instructionLength;

    std::size_t targetOffset{};
    if (displacement >= 0) {
        const auto positive = static_cast<std::uint32_t>(displacement);
        if (static_cast<std::size_t>(positive) >
            std::numeric_limits<std::size_t>::max() - nextInstruction) {
            return {
                LegacyRipDecodeStatus::TargetOutOfBounds,
                0,
                displacement,
            };
        }
        targetOffset = nextInstruction + static_cast<std::size_t>(positive);
    } else {
        // Unsigned two's-complement magnitude also handles INT32_MIN without
        // invoking signed overflow.
        const auto magnitude =
            static_cast<std::uint32_t>(0U - encoded);
        if (static_cast<std::size_t>(magnitude) > nextInstruction) {
            return {
                LegacyRipDecodeStatus::TargetOutOfBounds,
                0,
                displacement,
            };
        }
        targetOffset = nextInstruction - static_cast<std::size_t>(magnitude);
    }

    if (targetOffset > image.size() ||
        targetSize > image.size() - targetOffset) {
        return {
            LegacyRipDecodeStatus::TargetOutOfBounds,
            0,
            displacement,
        };
    }
    return {
        LegacyRipDecodeStatus::Success,
        targetOffset,
        displacement,
    };
}

LegacyRipDecodeResult DecodeLegacyRipRelativeInstruction(
    const std::span<const std::uint8_t> instruction,
    const std::size_t instructionRva,
    const std::size_t displacementOffsetWithinInstruction,
    const std::size_t instructionLength,
    const std::size_t imageSize,
    const std::size_t targetSize) noexcept {
    constexpr std::size_t DisplacementSize = sizeof(std::int32_t);
    if (instructionLength == 0 || targetSize == 0) {
        return {LegacyRipDecodeStatus::InvalidArguments, 0, 0};
    }
    if (instructionLength > instruction.size()) {
        return {LegacyRipDecodeStatus::InstructionOutOfBounds, 0, 0};
    }
    if (displacementOffsetWithinInstruction > instructionLength ||
        DisplacementSize >
            instructionLength - displacementOffsetWithinInstruction) {
        return {LegacyRipDecodeStatus::DisplacementOutOfBounds, 0, 0};
    }
    if (instructionRva > imageSize ||
        instructionLength > imageSize - instructionRva) {
        return {LegacyRipDecodeStatus::InstructionOutOfBounds, 0, 0};
    }

    const auto encoded =
        static_cast<std::uint32_t>(
            instruction[displacementOffsetWithinInstruction]) |
        (static_cast<std::uint32_t>(
             instruction[displacementOffsetWithinInstruction + 1]) << 8U) |
        (static_cast<std::uint32_t>(
             instruction[displacementOffsetWithinInstruction + 2]) << 16U) |
        (static_cast<std::uint32_t>(
             instruction[displacementOffsetWithinInstruction + 3]) << 24U);
    const auto displacement = std::bit_cast<std::int32_t>(encoded);
    const auto nextInstruction = instructionRva + instructionLength;

    std::size_t targetRva{};
    if (displacement >= 0) {
        const auto positive = static_cast<std::uint32_t>(displacement);
        if (static_cast<std::size_t>(positive) >
            std::numeric_limits<std::size_t>::max() - nextInstruction) {
            return {
                LegacyRipDecodeStatus::TargetOutOfBounds,
                0,
                displacement,
            };
        }
        targetRva = nextInstruction + static_cast<std::size_t>(positive);
    } else {
        const auto magnitude = static_cast<std::uint32_t>(0U - encoded);
        if (static_cast<std::size_t>(magnitude) > nextInstruction) {
            return {
                LegacyRipDecodeStatus::TargetOutOfBounds,
                0,
                displacement,
            };
        }
        targetRva = nextInstruction - static_cast<std::size_t>(magnitude);
    }

    if (targetRva > imageSize || targetSize > imageSize - targetRva) {
        return {
            LegacyRipDecodeStatus::TargetOutOfBounds,
            0,
            displacement,
        };
    }
    return {
        LegacyRipDecodeStatus::Success,
        targetRva,
        displacement,
    };
}

Legacy3889RawInitStateClassifier::Legacy3889RawInitStateClassifier(
    const std::uint32_t requiredStableSamples) noexcept
    : _requiredStableSamples(requiredStableSamples) {}

void Legacy3889RawInitStateClassifier::Reset() noexcept {
    _consecutiveSamples = 0;
    _candidateRawValue = 0;
    _hasCandidate = false;
    _frontendArmed = false;
}

Legacy3889Classification Legacy3889RawInitStateClassifier::Classify(
    const std::int32_t rawValue) noexcept {
    if (!IsConfigurationValid()) {
        Reset();
        return Classification(
            Legacy3889ClassificationStatus::InvalidConfiguration,
            LegacyStartupRawState::Unknown,
            LegacyStartupObservationStatus::Unavailable,
            rawValue,
            0,
            false);
    }
    if (!IsSupportedRawValue(rawValue)) {
        // An unknown intermediate state invalidates the provisional sequence.
        // The classifier must observe a fresh stable frontend before it can
        // ground a later transition.
        Reset();
        return Classification(
            Legacy3889ClassificationStatus::UnsupportedRawValue,
            LegacyStartupRawState::Unknown,
            LegacyStartupObservationStatus::Unavailable,
            rawValue,
            0,
            false);
    }

    if (!_hasCandidate || rawValue != _candidateRawValue) {
        _candidateRawValue = rawValue;
        _consecutiveSamples = 1;
        _hasCandidate = true;
    } else if (_consecutiveSamples <
               std::numeric_limits<std::uint32_t>::max()) {
        ++_consecutiveSamples;
    }

    const auto mappedState = MapRawValue(rawValue);
    if (_consecutiveSamples < _requiredStableSamples) {
        return Classification(
            Legacy3889ClassificationStatus::Debouncing,
            LegacyStartupRawState::Unknown,
            LegacyStartupObservationStatus::Unavailable,
            rawValue,
            _consecutiveSamples,
            _frontendArmed);
    }

    if (mappedState == LegacyStartupRawState::Frontend) {
        _frontendArmed = true;
        return Classification(
            Legacy3889ClassificationStatus::Grounded,
            mappedState,
            LegacyStartupObservationStatus::Grounded,
            rawValue,
            _consecutiveSamples,
            true);
    }
    if (mappedState == LegacyStartupRawState::StoryTransition) {
        if (!_frontendArmed) {
            return Classification(
                Legacy3889ClassificationStatus::StableButUnarmed,
                LegacyStartupRawState::Unknown,
                LegacyStartupObservationStatus::Unavailable,
                rawValue,
                _consecutiveSamples,
                false);
        }
        return Classification(
            Legacy3889ClassificationStatus::Grounded,
            mappedState,
            LegacyStartupObservationStatus::Grounded,
            rawValue,
            _consecutiveSamples,
            true);
    }

    // StoryActive can be reported for diagnostics when attaching late, but it
    // never creates an arm. LegacyStartupEdgeDetector will consequently
    // abstain from inventing an entering-Story edge.
    _frontendArmed = false;
    return Classification(
        Legacy3889ClassificationStatus::Grounded,
        LegacyStartupRawState::StoryActive,
        LegacyStartupObservationStatus::Grounded,
        rawValue,
        _consecutiveSamples,
        false);
}

bool Legacy3889RawInitStateClassifier::IsConfigurationValid() const noexcept {
    return _requiredStableSamples >= 2;
}

bool Legacy3889RawInitStateClassifier::IsFrontendArmed() const noexcept {
    return _frontendArmed;
}

void LegacyStartupSessionBoundaryTracker::Reset() noexcept {
    _leftFrontend = false;
}

bool LegacyStartupSessionBoundaryTracker::ObserveGrounded(
    const LegacyStartupRawState state) noexcept {
    switch (state) {
        case LegacyStartupRawState::Frontend:
            if (_leftFrontend) {
                _leftFrontend = false;
                return true;
            }
            return false;

        case LegacyStartupRawState::StoryTransition:
        case LegacyStartupRawState::StoryActive:
        case LegacyStartupRawState::ShuttingDown:
            _leftFrontend = true;
            return false;

        case LegacyStartupRawState::Unknown:
        default:
            return false;
    }
}

} // namespace reactorv::bootstrap
