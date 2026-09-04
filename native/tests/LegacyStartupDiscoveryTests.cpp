#include "LegacyStartupDiscovery.h"

#include <array>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <span>
#include <type_traits>

namespace {

using reactorv::bootstrap::Legacy3889ClassificationStatus;
using reactorv::bootstrap::Legacy3889ActiveInitState;
using reactorv::bootstrap::Legacy3889FrontendInitState;
using reactorv::bootstrap::Legacy3889RawInitStateClassifier;
using reactorv::bootstrap::Legacy3889TransitionInitState;
using reactorv::bootstrap::LegacyPatternByte;
using reactorv::bootstrap::LegacyPatternScanStatus;
using reactorv::bootstrap::LegacyRipDecodeStatus;
using reactorv::bootstrap::LegacyStartupEdgeDetector;
using reactorv::bootstrap::LegacyStartupObservation;
using reactorv::bootstrap::LegacyStartupObservationStatus;
using reactorv::bootstrap::LegacyStartupRawState;
using reactorv::bootstrap::LegacyStartupSessionBoundaryTracker;

void Require(const bool condition, const char* message) {
    if (!condition) {
        std::cerr << message << '\n';
        std::exit(1);
    }
}

void TestPatternScanner() {
    constexpr std::array<std::uint8_t, 10> Image{
        0x48, 0x8B, 0x05, 0x11, 0x22,
        0x48, 0x8B, 0x15, 0x11, 0x22,
    };
    constexpr std::array<LegacyPatternByte, 0> Empty{};
    const auto invalid = reactorv::bootstrap::ScanLegacyPattern(Image, Empty);
    Require(
        invalid.status == LegacyPatternScanStatus::Invalid &&
            invalid.matchCount == 0,
        "An empty signature must be invalid, not a match at every offset.");

    constexpr std::array<LegacyPatternByte, 3> Missing{
        LegacyPatternByte{0x90, false},
        LegacyPatternByte{0x90, false},
        LegacyPatternByte{0x90, false},
    };
    const auto missing = reactorv::bootstrap::ScanLegacyPattern(Image, Missing);
    Require(
        missing.status == LegacyPatternScanStatus::Missing &&
            missing.matchCount == 0,
        "A valid absent signature must be reported as missing.");

    constexpr std::array<LegacyPatternByte, 5> Unique{
        LegacyPatternByte{0x8B, false},
        LegacyPatternByte{0x00, true},
        LegacyPatternByte{0x11, false},
        LegacyPatternByte{0x22, false},
        LegacyPatternByte{0x48, false},
    };
    const auto unique = reactorv::bootstrap::ScanLegacyPattern(Image, Unique);
    Require(
        unique.IsUnique() && unique.firstMatchOffset == 1 &&
            unique.matchCount == 1,
        "A wildcard must consume one byte while retaining a unique offset.");

    constexpr std::array<LegacyPatternByte, 2> Ambiguous{
        LegacyPatternByte{0x48, false},
        LegacyPatternByte{0x00, true},
    };
    const auto ambiguous =
        reactorv::bootstrap::ScanLegacyPattern(Image, Ambiguous);
    Require(
        ambiguous.status == LegacyPatternScanStatus::Ambiguous &&
            ambiguous.firstMatchOffset == 0 && ambiguous.matchCount == 2,
        "Multiple candidates must be explicit and must retain their count.");

    constexpr std::array<LegacyPatternByte, 2> Boundary{
        LegacyPatternByte{0x11, false},
        LegacyPatternByte{0x22, false},
    };
    const auto boundary =
        reactorv::bootstrap::ScanLegacyPattern(Image, Boundary);
    Require(
        boundary.status == LegacyPatternScanStatus::Ambiguous &&
            boundary.matchCount == 2,
        "The final legal candidate offset must be included safely.");

    const std::span<const std::uint8_t> noImage{};
    const auto emptyImage =
        reactorv::bootstrap::ScanLegacyPattern(noImage, Missing);
    Require(
        emptyImage.status == LegacyPatternScanStatus::Missing,
        "An empty readable image is a valid non-match, not a bad signature.");
}

void TestLegacy3889StoreInstructionAllowlist() {
    constexpr std::array<std::uint8_t, 6> eaxStore{
        0x89, 0x05, 0x11, 0x22, 0x33, 0x44};
    constexpr std::array<std::uint8_t, 6> ecxStore{
        0x89, 0x0D, 0x11, 0x22, 0x33, 0x44};
    constexpr std::array<std::uint8_t, 6> edxStore{
        0x89, 0x15, 0x11, 0x22, 0x33, 0x44};
    constexpr std::array<std::uint8_t, 2> wrongOpcode{0x8B, 0x0D};
    constexpr std::array<std::uint8_t, 2> truncatedStore{0x89, 0x0D};
    constexpr std::array<std::uint8_t, 1> truncated{0x89};

    Require(
        reactorv::bootstrap::IsLegacy3889InitStateStoreInstruction(
            eaxStore) &&
            reactorv::bootstrap::IsLegacy3889InitStateStoreInstruction(
                ecxStore),
        "Legacy 3889 must accept only the evidenced EAX and ECX stores.");
    Require(
        !reactorv::bootstrap::IsLegacy3889InitStateStoreInstruction(
            edxStore) &&
            !reactorv::bootstrap::IsLegacy3889InitStateStoreInstruction(
                wrongOpcode) &&
            !reactorv::bootstrap::IsLegacy3889InitStateStoreInstruction(
                truncatedStore) &&
            !reactorv::bootstrap::IsLegacy3889InitStateStoreInstruction(
                truncated),
        "Unobserved registers, wrong opcodes, and truncated bytes must fail closed.");
}

void WriteDisplacement(
    std::array<std::uint8_t, 32>& image,
    const std::size_t offset,
    const std::int32_t displacement) {
    const auto encoded = static_cast<std::uint32_t>(displacement);
    image[offset] = static_cast<std::uint8_t>(encoded & 0xFFU);
    image[offset + 1] =
        static_cast<std::uint8_t>((encoded >> 8U) & 0xFFU);
    image[offset + 2] =
        static_cast<std::uint8_t>((encoded >> 16U) & 0xFFU);
    image[offset + 3] =
        static_cast<std::uint8_t>((encoded >> 24U) & 0xFFU);
}

void TestRipRelativeDecoder() {
    std::array<std::uint8_t, 32> image{};
    image[2] = 0x48;
    image[3] = 0x8D;
    image[4] = 0x0D;
    WriteDisplacement(image, 5, 8);
    const auto forward = reactorv::bootstrap::DecodeLegacyRipRelativeTarget(
        image,
        2,
        3,
        7,
        4);
    Require(
        forward.IsSuccess() && forward.displacement == 8 &&
            forward.targetOffset == 17,
        "A positive rel32 must be based on the end of the instruction.");

    image[10] = 0xE8;
    WriteDisplacement(image, 11, -10);
    const auto backward = reactorv::bootstrap::DecodeLegacyRipRelativeTarget(
        image,
        10,
        1,
        5,
        1);
    Require(
        backward.IsSuccess() && backward.displacement == -10 &&
            backward.targetOffset == 5,
        "A negative rel32 must decode without signed overflow.");

    Require(
        reactorv::bootstrap::DecodeLegacyRipRelativeTarget(
            image,
            0,
            0,
            0,
            1).status == LegacyRipDecodeStatus::InvalidArguments,
        "A zero-length instruction must be rejected.");
    Require(
        reactorv::bootstrap::DecodeLegacyRipRelativeTarget(
            image,
            0,
            0,
            5,
            0).status == LegacyRipDecodeStatus::InvalidArguments,
        "A zero-size target contract must be rejected.");
    Require(
        reactorv::bootstrap::DecodeLegacyRipRelativeTarget(
            image,
            image.size() - 2,
            0,
            5,
            1).status == LegacyRipDecodeStatus::InstructionOutOfBounds,
        "An instruction crossing the image boundary must be rejected.");
    Require(
        reactorv::bootstrap::DecodeLegacyRipRelativeTarget(
            image,
            0,
            3,
            6,
            1).status == LegacyRipDecodeStatus::DisplacementOutOfBounds,
        "The entire rel32 operand must fit within the instruction.");

    WriteDisplacement(image, 1, -20);
    Require(
        reactorv::bootstrap::DecodeLegacyRipRelativeTarget(
            image,
            0,
            1,
            5,
            1).status == LegacyRipDecodeStatus::TargetOutOfBounds,
        "A target before the image must fail closed.");
    WriteDisplacement(image, 1, 27);
    Require(
        reactorv::bootstrap::DecodeLegacyRipRelativeTarget(
            image,
            0,
            1,
            5,
            1).status == LegacyRipDecodeStatus::TargetOutOfBounds,
        "A target at the one-past-end offset is not readable.");
    WriteDisplacement(image, 1, 24);
    Require(
        reactorv::bootstrap::DecodeLegacyRipRelativeTarget(
            image,
            0,
            1,
            5,
            4).status == LegacyRipDecodeStatus::TargetOutOfBounds,
        "The caller's complete target width must fit within the image.");
}

void TestCopiedInstructionDecoder() {
    std::array<std::uint8_t, 6> instruction{0x89, 0x05, 0, 0, 0, 0};
    const std::int32_t forwardDisplacement = 40;
    const auto encoded = static_cast<std::uint32_t>(forwardDisplacement);
    instruction[2] = static_cast<std::uint8_t>(encoded & 0xFFU);
    instruction[3] = static_cast<std::uint8_t>((encoded >> 8U) & 0xFFU);
    instruction[4] = static_cast<std::uint8_t>((encoded >> 16U) & 0xFFU);
    instruction[5] = static_cast<std::uint8_t>((encoded >> 24U) & 0xFFU);
    const auto decoded =
        reactorv::bootstrap::DecodeLegacyRipRelativeInstruction(
            instruction,
            100,
            2,
            6,
            1000,
            sizeof(std::int32_t));
    Require(
        decoded.IsSuccess() && decoded.targetOffset == 146 &&
            decoded.displacement == 40,
        "A guarded instruction copy must retain its original image RVA.");

    Require(
        reactorv::bootstrap::DecodeLegacyRipRelativeInstruction(
            instruction,
            998,
            2,
            6,
            1000,
            sizeof(std::int32_t)).status ==
            LegacyRipDecodeStatus::InstructionOutOfBounds,
        "A copied instruction outside the mapped image must fail closed.");
    Require(
        reactorv::bootstrap::DecodeLegacyRipRelativeInstruction(
            std::span<const std::uint8_t>(instruction.data(), 5),
            100,
            2,
            6,
            1000,
            sizeof(std::int32_t)).status ==
            LegacyRipDecodeStatus::InstructionOutOfBounds,
        "A truncated guarded instruction copy must be rejected.");

    instruction = {0x89, 0x05, 0xFF, 0xFF, 0xFF, 0x7F};
    const auto outside =
        reactorv::bootstrap::DecodeLegacyRipRelativeInstruction(
            instruction,
            100,
            2,
            6,
            1000,
            sizeof(std::int32_t));
    Require(
        outside.status == LegacyRipDecodeStatus::TargetOutOfBounds &&
            outside.displacement == 0x7FFFFFFF,
        "A rejected rel32 must retain its signed displacement for diagnostics.");
}

reactorv::bootstrap::LegacyPeSectionDescriptor MakeDataSection() {
    reactorv::bootstrap::LegacyPeSectionDescriptor section{};
    section.name = {'.', 'd', 'a', 't', 'a', 0, 0, 0};
    section.virtualAddress = 0x1000;
    section.virtualSize = 0x3000;
    section.rawSize = 0x200;
    section.characteristics =
        reactorv::bootstrap::LegacyPeSectionInitializedData |
        reactorv::bootstrap::LegacyPeSectionRead |
        reactorv::bootstrap::LegacyPeSectionWrite;
    return section;
}

void TestDataSectionInspection() {
    using reactorv::bootstrap::InspectLegacyTargetDataSection;
    using reactorv::bootstrap::LegacyDataSectionStatus;

    std::array sections{MakeDataSection()};
    auto result = InspectLegacyTargetDataSection(
        sections,
        0x8000,
        0x2F00,
        sizeof(std::int32_t));
    Require(
        result.IsAccepted() && result.containingSectionCount == 1 &&
            result.section.rawSize == 0x200,
        "A target in the zero-filled virtual tail of exact .data must pass.");

    auto wrongName = sections;
    wrongName[0].name = {'.', 'r', 'd', 'a', 't', 'a', 0, 0};
    Require(
        InspectLegacyTargetDataSection(
            wrongName, 0x8000, 0x2F00, 4).status ==
            LegacyDataSectionStatus::NameRejected,
        "A writable section with any name other than exact .data must fail.");

    auto executable = sections;
    executable[0].characteristics |=
        reactorv::bootstrap::LegacyPeSectionExecute;
    Require(
        InspectLegacyTargetDataSection(
            executable, 0x8000, 0x2F00, 4).status ==
            LegacyDataSectionStatus::CharacteristicsRejected,
        "An executable .data descriptor must not authorize the RWX exception.");
    auto code = sections;
    code[0].characteristics |= reactorv::bootstrap::LegacyPeSectionCode;
    Require(
        InspectLegacyTargetDataSection(code, 0x8000, 0x2F00, 4).status ==
            LegacyDataSectionStatus::CharacteristicsRejected,
        "A code-marked .data descriptor must fail closed.");
    for (const auto missingFlag : {
             reactorv::bootstrap::LegacyPeSectionInitializedData,
             reactorv::bootstrap::LegacyPeSectionRead,
             reactorv::bootstrap::LegacyPeSectionWrite}) {
        auto missing = sections;
        missing[0].characteristics &= ~missingFlag;
        Require(
            InspectLegacyTargetDataSection(
                missing, 0x8000, 0x2F00, 4).status ==
                LegacyDataSectionStatus::CharacteristicsRejected,
            "Every required initialized/read/write flag must be present.");
    }

    auto malformed = sections;
    malformed[0].virtualAddress = 0x7000;
    malformed[0].virtualSize = 0x2000;
    Require(
        InspectLegacyTargetDataSection(
            malformed, 0x8000, 0x7000, 4).status ==
            LegacyDataSectionStatus::MalformedSectionRange,
        "An overflowing or out-of-image section range must be rejected.");

    Require(
        InspectLegacyTargetDataSection(
            sections, 0x8000, 0x3FFF, 4).status ==
            LegacyDataSectionStatus::TargetCrossesBoundary,
        "The complete dword must remain inside .data.");
    Require(
        InspectLegacyTargetDataSection(
            sections, 0x8000, 0x5000, 4).status ==
            LegacyDataSectionStatus::Missing,
        "A target outside all sections must fail closed.");

    std::array overlapping{MakeDataSection(), MakeDataSection()};
    Require(
        InspectLegacyTargetDataSection(
            overlapping, 0x8000, 0x2F00, 4).status ==
            LegacyDataSectionStatus::Ambiguous,
        "Overlapping containing sections must not acquire ownership.");

    const std::span<const reactorv::bootstrap::LegacyPeSectionDescriptor>
        none{};
    Require(
        InspectLegacyTargetDataSection(none, 0x8000, 0x2F00, 4).status ==
            LegacyDataSectionStatus::InvalidArguments,
        "Missing section-table evidence must fail closed.");
}

void TestTargetProtectionMatrix() {
    using reactorv::bootstrap::IsLegacyTargetProtectionAccepted;
    Require(
        IsLegacyTargetProtectionAccepted(true, false, false),
        "Ordinary writable non-executable pages retain the existing path.");
    Require(
        IsLegacyTargetProtectionAccepted(false, true, true),
        "Only an observed RWX page backed by verified .data may use the exception.");
    Require(
        !IsLegacyTargetProtectionAccepted(false, true, false) &&
            !IsLegacyTargetProtectionAccepted(false, false, true) &&
            !IsLegacyTargetProtectionAccepted(false, false, false),
        "RWX without section proof and section proof without RWX must fail.");
}

void TestTargetValidationClassifier() {
    using reactorv::bootstrap::ClassifyLegacyTargetValidation;
    using reactorv::bootstrap::LegacyTargetValidationEvidence;
    using reactorv::bootstrap::LegacyTargetValidationStatus;

    LegacyTargetValidationEvidence evidence{};
    Require(
        ClassifyLegacyTargetValidation(evidence) ==
            LegacyTargetValidationStatus::ImageBoundsRejected,
        "Target validation must begin with mapped-image bounds.");
    evidence.imageBoundsPass = true;
    Require(
        ClassifyLegacyTargetValidation(evidence) ==
            LegacyTargetValidationStatus::AlignmentRejected,
        "A decoded state integer must be naturally aligned.");
    evidence.alignmentPass = true;
    Require(
        ClassifyLegacyTargetValidation(evidence) ==
            LegacyTargetValidationStatus::RegionQueryFailed,
        "A target without queryable memory evidence must fail closed.");
    evidence.regionQueryPass = true;
    Require(
        ClassifyLegacyTargetValidation(evidence) ==
            LegacyTargetValidationStatus::RegionUnavailable,
        "Guarded, uncommitted, or inaccessible regions must be rejected.");
    evidence.regionUsablePass = true;
    Require(
        ClassifyLegacyTargetValidation(evidence) ==
            LegacyTargetValidationStatus::ProtectionRejected,
        "The observer target must be writable and non-executable.");
    evidence.protectionPass = true;
    Require(
        ClassifyLegacyTargetValidation(evidence) ==
            LegacyTargetValidationStatus::TypeRejected,
        "Private or mapped memory cannot satisfy a module-data signature.");
    evidence.typePass = true;
    Require(
        ClassifyLegacyTargetValidation(evidence) ==
            LegacyTargetValidationStatus::AllocationBaseRejected,
        "The target must belong to the allowlisted GTA image.");
    evidence.allocationBasePass = true;
    Require(
        ClassifyLegacyTargetValidation(evidence) ==
            LegacyTargetValidationStatus::RegionAddressOverflow,
        "Overflowing region arithmetic must be rejected before containment.");
    evidence.regionAddressPass = true;
    Require(
        ClassifyLegacyTargetValidation(evidence) ==
            LegacyTargetValidationStatus::TargetOutsideRegion,
        "The complete state integer must fit in the queried region.");
    evidence.targetContainedPass = true;
    Require(
        ClassifyLegacyTargetValidation(evidence) ==
            LegacyTargetValidationStatus::Accepted,
        "Only complete target evidence may be accepted.");
}

void TestClassifierRequiresStableFrontend() {
    Legacy3889RawInitStateClassifier classifier(3);
    Require(
        classifier.IsConfigurationValid() &&
            !classifier.IsFrontendArmed(),
        "The normal three-sample classifier must start unarmed.");

    auto result = classifier.Classify(Legacy3889FrontendInitState);
    Require(
        result.status == Legacy3889ClassificationStatus::Debouncing &&
            !result.IsGrounded() && result.consecutiveSamples == 1 &&
            !result.frontendArmed,
        "One frontend sample must not arm discovery.");
    result = classifier.Classify(Legacy3889FrontendInitState);
    Require(
        result.status == Legacy3889ClassificationStatus::Debouncing &&
            result.consecutiveSamples == 2 && !result.frontendArmed,
        "Two samples below the configured threshold must remain ungrounded.");
    result = classifier.Classify(Legacy3889FrontendInitState);
    Require(
        result.IsGrounded() && result.state == LegacyStartupRawState::Frontend &&
            result.frontendArmed && classifier.IsFrontendArmed(),
        "Only the complete stable frontend run may arm the classifier.");

    Require(
        classifier.Classify(Legacy3889TransitionInitState).status ==
            Legacy3889ClassificationStatus::Debouncing &&
            classifier.Classify(Legacy3889TransitionInitState).status ==
                Legacy3889ClassificationStatus::Debouncing,
        "Transition evidence must also be debounced.");
    result = classifier.Classify(Legacy3889TransitionInitState);
    Require(
        result.IsGrounded() &&
            result.state == LegacyStartupRawState::StoryTransition &&
            result.frontendArmed,
        "A stable transition may be grounded only after the frontend arm.");
}

void TestClassifierAbstentionAndReset() {
    Legacy3889RawInitStateClassifier attachLate(2);
    Require(
        attachLate.Classify(Legacy3889TransitionInitState).status ==
            Legacy3889ClassificationStatus::Debouncing,
        "An unarmed transition must debounce normally.");
    const auto unarmedTransition =
        attachLate.Classify(Legacy3889TransitionInitState);
    Require(
        unarmedTransition.status ==
                Legacy3889ClassificationStatus::StableButUnarmed &&
            !unarmedTransition.IsGrounded() &&
            unarmedTransition.observationStatus ==
                LegacyStartupObservationStatus::Unavailable &&
            unarmedTransition.state == LegacyStartupRawState::Unknown,
        "A stable transition without stable frontend evidence must be withheld.");

    Require(
        attachLate.Classify(Legacy3889ActiveInitState).status ==
            Legacy3889ClassificationStatus::Debouncing,
        "Late Story-active evidence must be debounced.");
    const auto active = attachLate.Classify(Legacy3889ActiveInitState);
    Require(
        active.IsGrounded() &&
            active.state == LegacyStartupRawState::StoryActive &&
            !active.frontendArmed,
        "StoryActive may be diagnosed after late attach but must never arm entry.");

    Legacy3889RawInitStateClassifier noisy(2);
    static_cast<void>(noisy.Classify(Legacy3889FrontendInitState));
    Require(
        noisy.Classify(Legacy3889FrontendInitState).IsGrounded(),
        "Fixture must first arm.");
    const auto unsupported = noisy.Classify(4);
    Require(
        unsupported.status ==
                Legacy3889ClassificationStatus::UnsupportedRawValue &&
            !unsupported.IsGrounded() && !unsupported.frontendArmed &&
            !noisy.IsFrontendArmed(),
        "An unsupported raw state must break the provisional sequence.");
    static_cast<void>(noisy.Classify(Legacy3889TransitionInitState));
    Require(
        noisy.Classify(Legacy3889TransitionInitState).status ==
            Legacy3889ClassificationStatus::StableButUnarmed,
        "A transition after unknown evidence must require a fresh frontend arm.");

    noisy.Reset();
    Require(
        !noisy.IsFrontendArmed() &&
            noisy.Classify(Legacy3889FrontendInitState).status ==
                Legacy3889ClassificationStatus::Debouncing,
        "Reset must discard both candidate history and the frontend arm.");

    Legacy3889RawInitStateClassifier invalidZero(0);
    Legacy3889RawInitStateClassifier invalidOne(1);
    Require(
        !invalidZero.IsConfigurationValid() &&
            !invalidOne.IsConfigurationValid() &&
            invalidZero.Classify(Legacy3889FrontendInitState).status ==
                Legacy3889ClassificationStatus::InvalidConfiguration &&
            invalidOne.Classify(Legacy3889FrontendInitState).status ==
                Legacy3889ClassificationStatus::InvalidConfiguration,
        "The classifier must require multiple stable frontend samples by contract.");
}

void TestExactLegacy3889LiveTrace() {
    static_assert(Legacy3889FrontendInitState == 23);
    static_assert(Legacy3889TransitionInitState == 14);
    static_assert(Legacy3889ActiveInitState == 0);

    Legacy3889RawInitStateClassifier classifier(3);
    LegacyStartupSessionBoundaryTracker sessionBoundary;
    LegacyStartupEdgeDetector edgeDetector;
    std::uint64_t sessionGeneration = 1;
    std::uint64_t observationSequence = 0;
    std::uint64_t enteringStoryEdges = 0;
    std::uint64_t lastEdgeSequence = 0;

    Require(
        edgeDetector.BeginSession(1, sessionGeneration),
        "The exact-build live-trace fixture must begin grounded.");

    const auto sample = [&](const std::int32_t rawValue) {
        const auto classification = classifier.Classify(rawValue);
        if (!classification.IsGrounded()) return classification;

        if (sessionBoundary.ObserveGrounded(classification.state)) {
            ++sessionGeneration;
            observationSequence = 0;
            Require(
                edgeDetector.BeginSession(1, sessionGeneration),
                "A grounded frontend return must begin one newer session.");
        }

        const LegacyStartupObservation observation{
            1,
            sessionGeneration,
            ++observationSequence,
            1000 + observationSequence,
            classification.state,
            classification.observationStatus,
        };
        const auto reduction = edgeDetector.Observe(observation);
        if (reduction.HasEnteringStoryEdge()) {
            ++enteringStoryEdges;
            lastEdgeSequence = reduction.edge.edgeSequence;
        }
        return classification;
    };

    // This is the exact ordering observed before the stable main-menu state.
    // Early zero is active-state diagnostics without an arm; 2 and 5 are
    // deliberately unsupported and clear provisional evidence.
    for (int index = 0; index < 3; ++index) {
        static_cast<void>(sample(Legacy3889ActiveInitState));
    }
    Require(
        enteringStoryEdges == 0 && !classifier.IsFrontendArmed(),
        "Early active-state evidence must never invent an entering-Story edge.");
    Require(
        sample(2).status == Legacy3889ClassificationStatus::UnsupportedRawValue &&
            sample(5).status ==
                Legacy3889ClassificationStatus::UnsupportedRawValue &&
            enteringStoryEdges == 0 && !classifier.IsFrontendArmed(),
        "Observed startup values 2 and 5 must fail closed and keep the route unarmed.");

    for (int index = 0; index < 3; ++index) {
        static_cast<void>(sample(Legacy3889FrontendInitState));
    }
    Require(
        classifier.IsFrontendArmed() && sessionGeneration == 2 &&
            enteringStoryEdges == 0,
        "Only stable raw 23 may arm the post-startup frontend session.");

    for (int index = 0; index < 3; ++index) {
        static_cast<void>(sample(Legacy3889TransitionInitState));
    }
    Require(
        enteringStoryEdges == 1 && lastEdgeSequence == 1,
        "Stable raw 14 after raw 23 must emit exactly one entering-Story edge.");

    // Held transition samples and the final active state must remain deduped.
    for (int index = 0; index < 3; ++index) {
        static_cast<void>(sample(Legacy3889TransitionInitState));
    }
    for (int index = 0; index < 3; ++index) {
        static_cast<void>(sample(Legacy3889ActiveInitState));
    }
    Require(
        enteringStoryEdges == 1 && lastEdgeSequence == 1,
        "The complete 23 -> 14 -> 0 route must emit one edge and no duplicates.");
}

void TestSessionBoundaryRecovery() {
    LegacyStartupSessionBoundaryTracker tracker;
    Require(
        !tracker.ObserveGrounded(LegacyStartupRawState::Frontend) &&
            !tracker.ObserveGrounded(LegacyStartupRawState::Frontend),
        "Initial and repeated frontend samples stay in the first session.");
    Require(
        !tracker.ObserveGrounded(LegacyStartupRawState::StoryTransition) &&
            !tracker.ObserveGrounded(LegacyStartupRawState::StoryActive),
        "Leaving the frontend records evidence but does not roll immediately.");
    Require(
        tracker.ObserveGrounded(LegacyStartupRawState::Frontend) &&
            !tracker.ObserveGrounded(LegacyStartupRawState::Frontend),
        "A grounded return to the frontend starts exactly one new session.");

    tracker.Reset();
    Require(
        !tracker.ObserveGrounded(LegacyStartupRawState::StoryActive) &&
            tracker.ObserveGrounded(LegacyStartupRawState::Frontend),
        "An attach-late Story-active observation can recover on frontend return.");
}

static_assert(
    !std::is_same_v<
        reactorv::bootstrap::Legacy3889Classification,
        reactorv::bootstrap::LegacyEnteringStoryEdge>,
    "Discovery classification must remain evidence, never a route edge.");

} // namespace

int main() {
    TestPatternScanner();
    TestLegacy3889StoreInstructionAllowlist();
    TestRipRelativeDecoder();
    TestCopiedInstructionDecoder();
    TestDataSectionInspection();
    TestTargetProtectionMatrix();
    TestTargetValidationClassifier();
    TestClassifierRequiresStableFrontend();
    TestClassifierAbstentionAndReset();
    TestExactLegacy3889LiveTrace();
    TestSessionBoundaryRecovery();
    return 0;
}
