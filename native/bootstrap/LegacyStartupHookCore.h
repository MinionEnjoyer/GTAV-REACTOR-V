#pragma once

#include <atomic>
#include <cstdint>

namespace reactorv::bootstrap {

// The discovery layer translates the loaded PE name into this closed enum.
// The startup-hook core never guesses an edition from an address or version.
enum class LegacyExecutableKind : std::uint8_t {
    Unknown,
    LegacyGta5,
    EnhancedGta5,
    Other,
};

struct LegacyFileVersion {
    std::uint16_t major{};
    std::uint16_t minor{};
    std::uint16_t build{};
    std::uint16_t revision{};
};

struct LegacyBuildIdentity {
    LegacyExecutableKind executableKind{LegacyExecutableKind::Unknown};
    std::uint16_t peMachine{};
    LegacyFileVersion fileVersion{};
    std::uint32_t peTimestamp{};
    std::uint32_t sizeOfImage{};
};

using LegacySignatureMask = std::uint64_t;

// A profile is supplied by a separately reviewed, build-specific adapter.
// This core deliberately contains no unverified GTA addresses or byte patterns.
struct LegacyBuildProfile {
    std::uint32_t profileRevision{};
    std::uint16_t expectedPeMachine{};
    LegacyFileVersion expectedFileVersion{};
    std::uint32_t expectedPeTimestamp{};
    std::uint32_t expectedSizeOfImage{};
    LegacySignatureMask requiredSignatureMask{};
};

// Signature scanning occurs once on a worker before any detour is installed.
// Every required bit must be checked and matched. A guarded read failure is
// retained separately so it cannot accidentally look like a simple mismatch.
struct LegacySignatureEvidence {
    LegacySignatureMask checkedMask{};
    LegacySignatureMask matchedMask{};
    LegacySignatureMask readFaultMask{};
};

enum class LegacyHookGateStatus : std::uint8_t {
    Ready,
    InvalidProfile,
    NotLegacyExecutable,
    UnsupportedMachine,
    UnsupportedFileVersion,
    UnsupportedPeIdentity,
    InvalidSignatureEvidence,
    SignatureReadFault,
    SignatureEvidenceMissing,
    SignatureMismatch,
};

struct LegacyHookGateReport {
    LegacyHookGateStatus status{LegacyHookGateStatus::InvalidProfile};
    LegacySignatureMask requiredSignatureMask{};
    LegacySignatureMask missingSignatureMask{};
    LegacySignatureMask mismatchedSignatureMask{};
    LegacySignatureMask faultedSignatureMask{};

    [[nodiscard]] constexpr bool IsReady() const noexcept {
        return status == LegacyHookGateStatus::Ready;
    }
};

[[nodiscard]] LegacyHookGateReport EvaluateLegacyHookGate(
    const LegacyBuildIdentity& identity,
    const LegacyBuildProfile& profile,
    const LegacySignatureEvidence& signatureEvidence) noexcept;

enum class LegacyStartupRawState : std::uint8_t {
    Unknown,
    Frontend,
    StoryTransition,
    StoryActive,
    ShuttingDown,
};

enum class LegacyStartupObservationStatus : std::uint8_t {
    Unavailable,
    Grounded,
    ReadFault,
};

// The build-specific detour publishes this small value object. Adapter and
// session generations make stale callbacks harmless after a hook reinstall or
// a new GTA lifecycle session. observationSequence must increase monotonically
// for one adapter/session pair.
struct LegacyStartupObservation {
    std::uint64_t adapterGeneration{};
    std::uint64_t sessionGeneration{};
    std::uint64_t observationSequence{};
    std::uint64_t observedAtTickMilliseconds{};
    LegacyStartupRawState state{LegacyStartupRawState::Unknown};
    LegacyStartupObservationStatus status{
        LegacyStartupObservationStatus::Unavailable};
};

struct LegacyEnteringStoryEdge {
    std::uint64_t edgeSequence{};
    std::uint64_t adapterGeneration{};
    std::uint64_t sessionGeneration{};
    std::uint64_t sourceObservationSequence{};
    std::uint64_t observedAtTickMilliseconds{};
};

enum class LegacyStartupPollDecision : std::uint8_t {
    GateClosed,
    NoObservation,
    InvalidGeneration,
    InvalidObservation,
    StaleObservation,
    UngroundedObservation,
    NoEdge,
    EnteringStory,
};

struct LegacyStartupPollResult {
    LegacyStartupPollDecision decision{LegacyStartupPollDecision::NoObservation};
    LegacyEnteringStoryEdge edge{};

    [[nodiscard]] constexpr bool HasEnteringStoryEdge() const noexcept {
        return decision == LegacyStartupPollDecision::EnteringStory;
    }
};

// Single-writer mailbox for a detour callback and one worker-side reader.
// PublishFromHook performs atomic stores only: no allocation, locks, logging,
// IPC, pattern scans, native calls, or window operations are allowed there.
class LegacyStartupObservationMailbox final {
public:
    LegacyStartupObservationMailbox() noexcept = default;
    LegacyStartupObservationMailbox(const LegacyStartupObservationMailbox&) = delete;
    LegacyStartupObservationMailbox& operator=(
        const LegacyStartupObservationMailbox&) = delete;

    void Reset() noexcept;
    void PublishFromHook(const LegacyStartupObservation& observation) noexcept;
    [[nodiscard]] bool TryRead(LegacyStartupObservation& destination) const noexcept;

private:
    std::atomic<std::uint64_t> _publicationSequence{0};
    std::atomic<std::uint64_t> _adapterGeneration{0};
    std::atomic<std::uint64_t> _sessionGeneration{0};
    std::atomic<std::uint64_t> _observationSequence{0};
    std::atomic<std::uint64_t> _observedAtTickMilliseconds{0};
    std::atomic<std::uint8_t> _state{
        static_cast<std::uint8_t>(LegacyStartupRawState::Unknown)};
    std::atomic<std::uint8_t> _status{
        static_cast<std::uint8_t>(LegacyStartupObservationStatus::Unavailable)};
};

// Pure worker-side state reduction. One session can emit at most one edge.
// Seeing StoryActive without a prior grounded Frontend -> StoryTransition
// sequence deliberately abstains rather than inventing a late transition.
class LegacyStartupEdgeDetector final {
public:
    LegacyStartupEdgeDetector() noexcept = default;

    [[nodiscard]] bool BeginSession(
        std::uint64_t adapterGeneration,
        std::uint64_t sessionGeneration) noexcept;
    [[nodiscard]] LegacyStartupPollResult Observe(
        const LegacyStartupObservation& observation) noexcept;

private:
    enum class Phase : std::uint8_t {
        AwaitingFrontend,
        FrontendArmed,
        TransitionEmitted,
        StoryActive,
        Terminal,
    };

    std::uint64_t _adapterGeneration{};
    std::uint64_t _sessionGeneration{};
    std::uint64_t _lastObservationSequence{};
    std::uint64_t _edgeSequence{};
    Phase _phase{Phase::AwaitingFrontend};
};

enum class LegacyStartupActivationStatus : std::uint8_t {
    Active,
    GateClosed,
    InvalidGeneration,
    NonMonotonicGeneration,
};

struct LegacyStartupActivationResult {
    LegacyStartupActivationStatus status{
        LegacyStartupActivationStatus::GateClosed};
    LegacyHookGateReport gate{};

    [[nodiscard]] constexpr bool IsActive() const noexcept {
        return status == LegacyStartupActivationStatus::Active;
    }
};

// Narrow adapter core. Activation and deactivation are worker-only operations
// and must occur while the build-specific detour is uninstalled/quiescent.
// The callback-facing publication method remains constant-time and wait-free
// on the supported x64 target.
class LegacyStartupHookCore final {
public:
    LegacyStartupHookCore() noexcept = default;
    LegacyStartupHookCore(const LegacyStartupHookCore&) = delete;
    LegacyStartupHookCore& operator=(const LegacyStartupHookCore&) = delete;

    [[nodiscard]] LegacyStartupActivationResult Activate(
        const LegacyBuildIdentity& identity,
        const LegacyBuildProfile& profile,
        const LegacySignatureEvidence& signatureEvidence,
        std::uint64_t adapterGeneration,
        std::uint64_t sessionGeneration) noexcept;
    void Deactivate() noexcept;

    void PublishFromHook(const LegacyStartupObservation& observation) noexcept;
    [[nodiscard]] LegacyStartupPollResult Poll() noexcept;
    [[nodiscard]] bool IsActive() const noexcept;

private:
    LegacyStartupObservationMailbox _mailbox{};
    LegacyStartupEdgeDetector _edgeDetector{};
    std::atomic<bool> _active{false};
    std::atomic<std::uint64_t> _adapterGeneration{0};
    std::atomic<std::uint64_t> _sessionGeneration{0};
    LegacyHookGateReport _gate{};
};

} // namespace reactorv::bootstrap
