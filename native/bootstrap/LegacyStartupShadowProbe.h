#pragma once

#include "LegacyStartupDiscovery.h"
#include "LegacyStartupHookCore.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <stop_token>

namespace reactorv::bootstrap {

// Shadow mode is intentionally observational. Nothing in this component can
// signal a Reactor route, patch GTA memory, or invoke a GTA native function.
enum class LegacyShadowDiscoveryStatus : std::uint8_t {
    Uninitialized,
    Ready,
    UnsupportedExecutable,
    InvalidImage,
    VersionUnavailable,
    UnsupportedBuild,
    SignatureMissing,
    SignatureAmbiguous,
    SignatureReadFault,
    UnstableEvidence,
    InvalidTarget,
    GateClosed,
};

inline constexpr std::size_t LegacyShadowInstructionByteCount =
    Legacy3889InitStateStoreInstructionLength;

enum class LegacyShadowInstructionStatus : std::uint8_t {
    NotChecked,
    SignatureRevalidationMismatch,
    RangeInvalid,
    ReadFault,
    OpcodeMismatch,
    OpcodeMatched,
};

struct LegacyShadowTargetDiagnostics {
    std::size_t instructionRva{};
    std::array<std::uint8_t, LegacyShadowInstructionByteCount>
        instructionBytes{};
    std::size_t instructionBytesRead{};
    LegacyShadowInstructionStatus instructionStatus{
        LegacyShadowInstructionStatus::NotChecked};
    bool decodeAttempted{};
    LegacyRipDecodeStatus decodeStatus{
        LegacyRipDecodeStatus::InvalidArguments};
    std::int32_t displacement{};
    std::size_t candidateTargetRva{};
    LegacyTargetValidationStatus validationStatus{
        LegacyTargetValidationStatus::NotChecked};
    LegacyTargetValidationEvidence validationEvidence{};
    unsigned long regionQueryError{};
    std::uint32_t regionState{};
    std::uint32_t regionProtect{};
    std::uint32_t regionType{};
    std::size_t regionBaseRva{};
    std::size_t regionSize{};
    LegacyDataSectionStatus dataSectionStatus{
        LegacyDataSectionStatus::NotChecked};
    unsigned long dataSectionReadError{};
    std::size_t dataSectionMatchCount{};
    std::array<std::uint8_t, 8> dataSectionName{};
    std::uint32_t dataSectionRva{};
    std::uint32_t dataSectionVirtualSize{};
    std::uint32_t dataSectionRawSize{};
    std::uint32_t dataSectionCharacteristics{};
};

struct LegacyShadowDiscoveryReceipt {
    LegacyShadowDiscoveryStatus status{
        LegacyShadowDiscoveryStatus::Uninitialized};
    LegacyPatternScanStatus patternStatus{LegacyPatternScanStatus::Invalid};
    LegacyHookGateStatus gateStatus{LegacyHookGateStatus::InvalidProfile};
    std::uint32_t matchCount{};
    std::size_t matchRva{};
    std::size_t targetRva{};
    std::uint32_t peTimestamp{};
    std::uint32_t sizeOfImage{};
    unsigned long readError{};
    std::size_t bytesRead{};
    LegacyShadowTargetDiagnostics targetDiagnostics{};

    [[nodiscard]] constexpr bool IsReady() const noexcept {
        return status == LegacyShadowDiscoveryStatus::Ready &&
            patternStatus == LegacyPatternScanStatus::Unique &&
            matchCount == 1 &&
            gateStatus == LegacyHookGateStatus::Ready &&
            targetDiagnostics.instructionStatus ==
                LegacyShadowInstructionStatus::OpcodeMatched &&
            targetDiagnostics.decodeAttempted &&
            targetDiagnostics.decodeStatus == LegacyRipDecodeStatus::Success &&
            targetDiagnostics.validationStatus ==
                LegacyTargetValidationStatus::Accepted;
    }
};

enum class LegacyShadowPollStatus : std::uint8_t {
    NotReady,
    ReadFault,
    UnsupportedRawValue,
    Debouncing,
    StableButUnarmed,
    Grounded,
};

struct LegacyShadowPollReceipt {
    LegacyShadowPollStatus status{LegacyShadowPollStatus::NotReady};
    std::int32_t rawValue{};
    bool rawValueChanged{};
    Legacy3889Classification classification{};
    LegacyStartupPollDecision reductionDecision{
        LegacyStartupPollDecision::NoObservation};
    bool wouldEnterStory{};
    std::uint64_t diagnosticEdgeSequence{};
    std::uint64_t diagnosticSourceObservationSequence{};
    std::uint64_t adapterGeneration{};
    std::uint64_t sessionGeneration{};
    std::uint64_t observationSequence{};
    unsigned long readError{};
    std::size_t bytesRead{};
    std::uint32_t consecutiveReadFaults{};
};

// Build-3889-only read observer. Discover() is worker-only and may scan the
// unpacked executable image. Poll() performs one guarded, read-only sample.
// The owning bootstrap decides how to journal receipts; this type never logs
// or changes presentation state itself.
class LegacyStartupShadowProbe final {
public:
    LegacyStartupShadowProbe() noexcept = default;
    LegacyStartupShadowProbe(const LegacyStartupShadowProbe&) = delete;
    LegacyStartupShadowProbe& operator=(
        const LegacyStartupShadowProbe&) = delete;

    [[nodiscard]] LegacyShadowDiscoveryReceipt Discover(
        const std::stop_token* stopToken = nullptr) noexcept;
    [[nodiscard]] LegacyShadowPollReceipt Poll(
        std::uint64_t observedAtTickMilliseconds) noexcept;
    void Reset() noexcept;
    [[nodiscard]] bool IsReady() const noexcept;
    [[nodiscard]] const LegacyShadowDiscoveryReceipt& Receipt() const noexcept;

private:
    void* _moduleBase{};
    const std::int32_t* _initState{};
    std::uint32_t _imageSize{};
    LegacyShadowDiscoveryReceipt _receipt{};
    LegacyBuildIdentity _identity{};
    LegacyBuildProfile _profile{};
    LegacySignatureEvidence _signatureEvidence{};
    LegacyStartupHookCore _hookCore{};
    Legacy3889RawInitStateClassifier _classifier{};
    LegacyStartupSessionBoundaryTracker _sessionBoundaryTracker{};
    std::uint64_t _adapterGeneration{};
    std::uint64_t _sessionGeneration{1};
    std::uint64_t _observationSequence{};
    std::int32_t _lastRawValue{};
    bool _hasLastRawValue{};
    std::uint32_t _consecutiveReadFaults{};
};

} // namespace reactorv::bootstrap
