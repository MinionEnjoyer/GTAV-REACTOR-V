#include "LegacyStartupHookCore.h"

#include <limits>

namespace reactorv::bootstrap {
namespace {

constexpr std::uint16_t Amd64Machine = 0x8664;

constexpr bool VersionsEqual(
    const LegacyFileVersion& left,
    const LegacyFileVersion& right) noexcept {
    return left.major == right.major &&
        left.minor == right.minor &&
        left.build == right.build &&
        left.revision == right.revision;
}

constexpr bool VersionIsSpecified(const LegacyFileVersion& version) noexcept {
    return version.major != 0 || version.minor != 0 ||
        version.build != 0 || version.revision != 0;
}

LegacyStartupPollResult Decision(
    const LegacyStartupPollDecision decision) noexcept {
    LegacyStartupPollResult result{};
    result.decision = decision;
    return result;
}

} // namespace

static_assert(
    std::atomic<std::uint64_t>::is_always_lock_free,
    "The Legacy startup callback requires lock-free 64-bit atomics on x64.");
static_assert(
    std::atomic<std::uint8_t>::is_always_lock_free,
    "The Legacy startup callback requires lock-free byte atomics on x64.");

LegacyHookGateReport EvaluateLegacyHookGate(
    const LegacyBuildIdentity& identity,
    const LegacyBuildProfile& profile,
    const LegacySignatureEvidence& signatureEvidence) noexcept {
    LegacyHookGateReport report{};
    report.requiredSignatureMask = profile.requiredSignatureMask;

    if (profile.profileRevision == 0 ||
        profile.expectedPeMachine != Amd64Machine ||
        !VersionIsSpecified(profile.expectedFileVersion) ||
        profile.expectedPeTimestamp == 0 ||
        profile.expectedSizeOfImage == 0 ||
        profile.requiredSignatureMask == 0) {
        report.status = LegacyHookGateStatus::InvalidProfile;
        return report;
    }
    if (identity.executableKind != LegacyExecutableKind::LegacyGta5) {
        report.status = LegacyHookGateStatus::NotLegacyExecutable;
        return report;
    }
    if (identity.peMachine != profile.expectedPeMachine) {
        report.status = LegacyHookGateStatus::UnsupportedMachine;
        return report;
    }
    if (!VersionsEqual(identity.fileVersion, profile.expectedFileVersion)) {
        report.status = LegacyHookGateStatus::UnsupportedFileVersion;
        return report;
    }
    if (identity.peTimestamp != profile.expectedPeTimestamp ||
        identity.sizeOfImage != profile.expectedSizeOfImage) {
        report.status = LegacyHookGateStatus::UnsupportedPeIdentity;
        return report;
    }

    // Matched/faulted evidence must have been explicitly checked, and one
    // signature cannot simultaneously match and fault.
    if ((signatureEvidence.matchedMask & ~signatureEvidence.checkedMask) != 0 ||
        (signatureEvidence.readFaultMask & ~signatureEvidence.checkedMask) != 0 ||
        (signatureEvidence.matchedMask & signatureEvidence.readFaultMask) != 0) {
        report.status = LegacyHookGateStatus::InvalidSignatureEvidence;
        return report;
    }

    report.faultedSignatureMask =
        profile.requiredSignatureMask & signatureEvidence.readFaultMask;
    report.missingSignatureMask =
        profile.requiredSignatureMask & ~signatureEvidence.checkedMask;
    report.mismatchedSignatureMask =
        profile.requiredSignatureMask &
        signatureEvidence.checkedMask &
        ~signatureEvidence.matchedMask &
        ~signatureEvidence.readFaultMask;

    if (report.faultedSignatureMask != 0) {
        report.status = LegacyHookGateStatus::SignatureReadFault;
        return report;
    }
    if (report.missingSignatureMask != 0) {
        report.status = LegacyHookGateStatus::SignatureEvidenceMissing;
        return report;
    }
    if (report.mismatchedSignatureMask != 0) {
        report.status = LegacyHookGateStatus::SignatureMismatch;
        return report;
    }

    report.status = LegacyHookGateStatus::Ready;
    return report;
}

void LegacyStartupObservationMailbox::Reset() noexcept {
    _publicationSequence.store(0, std::memory_order_release);
    _adapterGeneration.store(0, std::memory_order_relaxed);
    _sessionGeneration.store(0, std::memory_order_relaxed);
    _observationSequence.store(0, std::memory_order_relaxed);
    _observedAtTickMilliseconds.store(0, std::memory_order_relaxed);
    _state.store(
        static_cast<std::uint8_t>(LegacyStartupRawState::Unknown),
        std::memory_order_relaxed);
    _status.store(
        static_cast<std::uint8_t>(LegacyStartupObservationStatus::Unavailable),
        std::memory_order_relaxed);
}

void LegacyStartupObservationMailbox::PublishFromHook(
    const LegacyStartupObservation& observation) noexcept {
    // The detour is the sole writer. Odd/even publication lets the worker copy
    // a coherent observation without acquiring a lock or running hook work on
    // the polling thread.
    const auto writeSequence =
        _publicationSequence.fetch_add(1, std::memory_order_acq_rel) + 1U;
    _adapterGeneration.store(
        observation.adapterGeneration,
        std::memory_order_relaxed);
    _sessionGeneration.store(
        observation.sessionGeneration,
        std::memory_order_relaxed);
    _observationSequence.store(
        observation.observationSequence,
        std::memory_order_relaxed);
    _observedAtTickMilliseconds.store(
        observation.observedAtTickMilliseconds,
        std::memory_order_relaxed);
    _state.store(
        static_cast<std::uint8_t>(observation.state),
        std::memory_order_relaxed);
    _status.store(
        static_cast<std::uint8_t>(observation.status),
        std::memory_order_relaxed);
    _publicationSequence.store(writeSequence + 1U, std::memory_order_release);
}

bool LegacyStartupObservationMailbox::TryRead(
    LegacyStartupObservation& destination) const noexcept {
    for (unsigned int attempt = 0; attempt < 8; ++attempt) {
        const auto before = _publicationSequence.load(std::memory_order_acquire);
        if (before == 0 || (before & 1U) != 0) continue;

        LegacyStartupObservation snapshot{};
        snapshot.adapterGeneration =
            _adapterGeneration.load(std::memory_order_relaxed);
        snapshot.sessionGeneration =
            _sessionGeneration.load(std::memory_order_relaxed);
        snapshot.observationSequence =
            _observationSequence.load(std::memory_order_relaxed);
        snapshot.observedAtTickMilliseconds =
            _observedAtTickMilliseconds.load(std::memory_order_relaxed);
        snapshot.state = static_cast<LegacyStartupRawState>(
            _state.load(std::memory_order_relaxed));
        snapshot.status = static_cast<LegacyStartupObservationStatus>(
            _status.load(std::memory_order_relaxed));

        const auto after = _publicationSequence.load(std::memory_order_acquire);
        if (before != after || (after & 1U) != 0) continue;
        destination = snapshot;
        return true;
    }
    return false;
}

bool LegacyStartupEdgeDetector::BeginSession(
    const std::uint64_t adapterGeneration,
    const std::uint64_t sessionGeneration) noexcept {
    if (adapterGeneration == 0 || sessionGeneration == 0) return false;
    _adapterGeneration = adapterGeneration;
    _sessionGeneration = sessionGeneration;
    _lastObservationSequence = 0;
    _phase = Phase::AwaitingFrontend;
    return true;
}

LegacyStartupPollResult LegacyStartupEdgeDetector::Observe(
    const LegacyStartupObservation& observation) noexcept {
    if (_adapterGeneration == 0 || _sessionGeneration == 0 ||
        observation.adapterGeneration != _adapterGeneration ||
        observation.sessionGeneration != _sessionGeneration) {
        return Decision(LegacyStartupPollDecision::InvalidGeneration);
    }
    if (observation.observationSequence == 0) {
        return Decision(LegacyStartupPollDecision::InvalidObservation);
    }
    if (observation.observationSequence <= _lastObservationSequence) {
        return Decision(LegacyStartupPollDecision::StaleObservation);
    }
    _lastObservationSequence = observation.observationSequence;

    if (observation.status != LegacyStartupObservationStatus::Grounded) {
        // A gap before the edge invalidates the armed transition sequence.
        // Once an edge has been emitted (or Story is already active), keep the
        // session terminal instead of allowing a fault to re-arm it.
        if (_phase == Phase::FrontendArmed) {
            _phase = Phase::AwaitingFrontend;
        }
        return Decision(LegacyStartupPollDecision::UngroundedObservation);
    }
    if (observation.state == LegacyStartupRawState::Unknown) {
        if (_phase == Phase::FrontendArmed) {
            _phase = Phase::AwaitingFrontend;
        }
        return Decision(LegacyStartupPollDecision::InvalidObservation);
    }
    if (observation.state == LegacyStartupRawState::ShuttingDown) {
        _phase = Phase::Terminal;
        return Decision(LegacyStartupPollDecision::NoEdge);
    }
    if (_phase == Phase::Terminal) {
        return Decision(LegacyStartupPollDecision::NoEdge);
    }

    switch (observation.state) {
        case LegacyStartupRawState::Frontend:
            // A session is armed exactly once. Returning to the frontend after
            // an emitted/active transition requires a new session generation.
            if (_phase == Phase::AwaitingFrontend) {
                _phase = Phase::FrontendArmed;
            }
            return Decision(LegacyStartupPollDecision::NoEdge);

        case LegacyStartupRawState::StoryTransition:
            if (_phase != Phase::FrontendArmed) {
                return Decision(LegacyStartupPollDecision::NoEdge);
            }
            if (_edgeSequence == std::numeric_limits<std::uint64_t>::max()) {
                _phase = Phase::Terminal;
                return Decision(LegacyStartupPollDecision::NoEdge);
            }
            _phase = Phase::TransitionEmitted;
            ++_edgeSequence;
            return {
                LegacyStartupPollDecision::EnteringStory,
                {
                    _edgeSequence,
                    observation.adapterGeneration,
                    observation.sessionGeneration,
                    observation.observationSequence,
                    observation.observedAtTickMilliseconds,
                },
            };

        case LegacyStartupRawState::StoryActive:
            _phase = Phase::StoryActive;
            return Decision(LegacyStartupPollDecision::NoEdge);

        case LegacyStartupRawState::ShuttingDown:
        case LegacyStartupRawState::Unknown:
        default:
            break;
    }
    return Decision(LegacyStartupPollDecision::InvalidObservation);
}

LegacyStartupActivationResult LegacyStartupHookCore::Activate(
    const LegacyBuildIdentity& identity,
    const LegacyBuildProfile& profile,
    const LegacySignatureEvidence& signatureEvidence,
    const std::uint64_t adapterGeneration,
    const std::uint64_t sessionGeneration) noexcept {
    // Fail closed before changing generations or allowing the detour callback
    // to publish. The caller installs the actual detour only after IsActive().
    _active.store(false, std::memory_order_release);
    const auto gate = EvaluateLegacyHookGate(identity, profile, signatureEvidence);
    _gate = gate;
    if (!gate.IsReady()) {
        return {LegacyStartupActivationStatus::GateClosed, gate};
    }
    if (adapterGeneration == 0 || sessionGeneration == 0) {
        return {LegacyStartupActivationStatus::InvalidGeneration, gate};
    }

    const auto previousAdapter =
        _adapterGeneration.load(std::memory_order_acquire);
    const auto previousSession =
        _sessionGeneration.load(std::memory_order_acquire);
    if (previousAdapter != 0 &&
        (adapterGeneration < previousAdapter ||
         (adapterGeneration == previousAdapter &&
          sessionGeneration <= previousSession))) {
        return {LegacyStartupActivationStatus::NonMonotonicGeneration, gate};
    }
    if (!_edgeDetector.BeginSession(adapterGeneration, sessionGeneration)) {
        return {LegacyStartupActivationStatus::InvalidGeneration, gate};
    }

    _mailbox.Reset();
    _adapterGeneration.store(adapterGeneration, std::memory_order_release);
    _sessionGeneration.store(sessionGeneration, std::memory_order_release);
    _active.store(true, std::memory_order_release);
    return {LegacyStartupActivationStatus::Active, gate};
}

void LegacyStartupHookCore::Deactivate() noexcept {
    // The integration first removes/quiesces its detour, then deactivates and
    // drains this core. Generation values remain as replay protection.
    _active.store(false, std::memory_order_release);
}

void LegacyStartupHookCore::PublishFromHook(
    const LegacyStartupObservation& observation) noexcept {
    if (!_active.load(std::memory_order_acquire)) return;
    if (observation.adapterGeneration !=
            _adapterGeneration.load(std::memory_order_acquire) ||
        observation.sessionGeneration !=
            _sessionGeneration.load(std::memory_order_acquire)) {
        return;
    }
    _mailbox.PublishFromHook(observation);
}

LegacyStartupPollResult LegacyStartupHookCore::Poll() noexcept {
    if (!_active.load(std::memory_order_acquire) || !_gate.IsReady()) {
        return Decision(LegacyStartupPollDecision::GateClosed);
    }
    LegacyStartupObservation observation{};
    if (!_mailbox.TryRead(observation)) {
        return Decision(LegacyStartupPollDecision::NoObservation);
    }
    return _edgeDetector.Observe(observation);
}

bool LegacyStartupHookCore::IsActive() const noexcept {
    return _active.load(std::memory_order_acquire) && _gate.IsReady();
}

} // namespace reactorv::bootstrap
