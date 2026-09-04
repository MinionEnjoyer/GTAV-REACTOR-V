#include "LegacyStartupHookCore.h"

#include <cstdint>
#include <cstdlib>
#include <iostream>

namespace {

using reactorv::bootstrap::LegacyBuildIdentity;
using reactorv::bootstrap::LegacyBuildProfile;
using reactorv::bootstrap::LegacyExecutableKind;
using reactorv::bootstrap::LegacyHookGateStatus;
using reactorv::bootstrap::LegacySignatureEvidence;
using reactorv::bootstrap::LegacyStartupActivationStatus;
using reactorv::bootstrap::LegacyStartupHookCore;
using reactorv::bootstrap::LegacyStartupObservation;
using reactorv::bootstrap::LegacyStartupObservationMailbox;
using reactorv::bootstrap::LegacyStartupObservationStatus;
using reactorv::bootstrap::LegacyStartupPollDecision;
using reactorv::bootstrap::LegacyStartupRawState;

constexpr std::uint16_t Amd64Machine = 0x8664;
constexpr std::uint64_t RequiredSignatures = 0b111;

void Require(const bool condition, const char* message) {
    if (!condition) {
        std::cerr << message << '\n';
        std::exit(1);
    }
}

LegacyBuildIdentity SupportedIdentity() {
    return {
        LegacyExecutableKind::LegacyGta5,
        Amd64Machine,
        {1, 0, 3889, 0},
        0x1234ABCD,
        0x02400000,
    };
}

LegacyBuildProfile SupportedProfile() {
    const auto identity = SupportedIdentity();
    return {
        7,
        identity.peMachine,
        identity.fileVersion,
        identity.peTimestamp,
        identity.sizeOfImage,
        RequiredSignatures,
    };
}

LegacySignatureEvidence MatchingEvidence() {
    return {
        RequiredSignatures,
        RequiredSignatures,
        0,
    };
}

LegacyStartupObservation Observation(
    const std::uint64_t adapterGeneration,
    const std::uint64_t sessionGeneration,
    const std::uint64_t observationSequence,
    const LegacyStartupRawState state,
    const LegacyStartupObservationStatus status =
        LegacyStartupObservationStatus::Grounded) {
    return {
        adapterGeneration,
        sessionGeneration,
        observationSequence,
        1000 + observationSequence,
        state,
        status,
    };
}

void RequireGateStatus(
    const LegacyBuildIdentity& identity,
    const LegacyBuildProfile& profile,
    const LegacySignatureEvidence& evidence,
    const LegacyHookGateStatus expected,
    const char* message) {
    const auto report = reactorv::bootstrap::EvaluateLegacyHookGate(
        identity,
        profile,
        evidence);
    Require(report.status == expected, message);
    Require(
        report.IsReady() == (expected == LegacyHookGateStatus::Ready),
        "Gate readiness must agree with the reported status.");
}

void TestBuildAndSignatureGate() {
    const auto identity = SupportedIdentity();
    const auto profile = SupportedProfile();
    const auto evidence = MatchingEvidence();

    const auto ready = reactorv::bootstrap::EvaluateLegacyHookGate(
        identity,
        profile,
        evidence);
    Require(ready.IsReady(), "The exact allowlisted Legacy build must pass.");
    Require(
        ready.requiredSignatureMask == RequiredSignatures &&
            ready.missingSignatureMask == 0 &&
            ready.mismatchedSignatureMask == 0 &&
            ready.faultedSignatureMask == 0,
        "A ready gate must preserve a clean signature-evidence receipt.");

    for (unsigned int invalidField = 0; invalidField < 5; ++invalidField) {
        auto invalid = profile;
        switch (invalidField) {
            case 0: invalid.profileRevision = 0; break;
            case 1: invalid.expectedPeMachine = 0x014C; break;
            case 2: invalid.expectedPeTimestamp = 0; break;
            case 3: invalid.expectedSizeOfImage = 0; break;
            case 4: invalid.requiredSignatureMask = 0; break;
            default: break;
        }
        RequireGateStatus(
            identity,
            invalid,
            evidence,
            LegacyHookGateStatus::InvalidProfile,
            "Every incomplete or non-x64 injected profile must fail closed.");
    }

    for (const auto kind : {
             LegacyExecutableKind::Unknown,
             LegacyExecutableKind::EnhancedGta5,
             LegacyExecutableKind::Other,
         }) {
        auto wrongExecutable = identity;
        wrongExecutable.executableKind = kind;
        RequireGateStatus(
            wrongExecutable,
            profile,
            evidence,
            LegacyHookGateStatus::NotLegacyExecutable,
            "The Legacy hook must reject unknown, Enhanced, and unrelated executables.");
    }

    auto wrongMachine = identity;
    wrongMachine.peMachine = 0x014C;
    RequireGateStatus(
        wrongMachine,
        profile,
        evidence,
        LegacyHookGateStatus::UnsupportedMachine,
        "A non-x64 game image must fail closed.");

    auto wrongVersion = identity;
    ++wrongVersion.fileVersion.revision;
    RequireGateStatus(
        wrongVersion,
        profile,
        evidence,
        LegacyHookGateStatus::UnsupportedFileVersion,
        "An unreviewed file version must fail closed.");

    auto wrongTimestamp = identity;
    ++wrongTimestamp.peTimestamp;
    RequireGateStatus(
        wrongTimestamp,
        profile,
        evidence,
        LegacyHookGateStatus::UnsupportedPeIdentity,
        "A timestamp mismatch must fail closed even when the version matches.");

    auto wrongImageSize = identity;
    ++wrongImageSize.sizeOfImage;
    RequireGateStatus(
        wrongImageSize,
        profile,
        evidence,
        LegacyHookGateStatus::UnsupportedPeIdentity,
        "An image-size mismatch must fail closed even when the version matches.");

    RequireGateStatus(
        identity,
        profile,
        {0b001, 0b011, 0},
        LegacyHookGateStatus::InvalidSignatureEvidence,
        "A match that was not checked must be rejected as impossible evidence.");
    RequireGateStatus(
        identity,
        profile,
        {0b001, 0, 0b010},
        LegacyHookGateStatus::InvalidSignatureEvidence,
        "A read fault that was not checked must be rejected as impossible evidence.");
    RequireGateStatus(
        identity,
        profile,
        {RequiredSignatures, 0b001, 0b001},
        LegacyHookGateStatus::InvalidSignatureEvidence,
        "One signature cannot simultaneously match and fault.");

    const auto readFault = reactorv::bootstrap::EvaluateLegacyHookGate(
        identity,
        profile,
        {RequiredSignatures, 0b011, 0b100});
    Require(
        readFault.status == LegacyHookGateStatus::SignatureReadFault &&
            readFault.faultedSignatureMask == 0b100,
        "A guarded-read failure must be identified and fail closed.");

    const auto missing = reactorv::bootstrap::EvaluateLegacyHookGate(
        identity,
        profile,
        {0b011, 0b011, 0});
    Require(
        missing.status == LegacyHookGateStatus::SignatureEvidenceMissing &&
            missing.missingSignatureMask == 0b100,
        "Unchecked required signatures must be reported as missing evidence.");

    const auto mismatch = reactorv::bootstrap::EvaluateLegacyHookGate(
        identity,
        profile,
        {RequiredSignatures, 0b101, 0});
    Require(
        mismatch.status == LegacyHookGateStatus::SignatureMismatch &&
            mismatch.mismatchedSignatureMask == 0b010,
        "A checked non-match, including a non-unique adapter result, must fail closed.");
}

void TestMailboxPublication() {
    LegacyStartupObservationMailbox mailbox;
    LegacyStartupObservation read{};
    Require(
        !mailbox.TryRead(read),
        "A never-published mailbox must not invent an observation.");

    const auto first = Observation(
        11,
        21,
        1,
        LegacyStartupRawState::Frontend);
    mailbox.PublishFromHook(first);
    Require(
        mailbox.TryRead(read),
        "A complete hook publication must be readable.");
    Require(
        read.adapterGeneration == first.adapterGeneration &&
            read.sessionGeneration == first.sessionGeneration &&
            read.observationSequence == first.observationSequence &&
            read.observedAtTickMilliseconds ==
                first.observedAtTickMilliseconds &&
            read.state == first.state &&
            read.status == first.status,
        "The mailbox must return one coherent observation tuple.");

    const auto second = Observation(
        12,
        22,
        99,
        LegacyStartupRawState::StoryTransition,
        LegacyStartupObservationStatus::ReadFault);
    mailbox.PublishFromHook(second);
    Require(
        mailbox.TryRead(read) &&
            read.adapterGeneration == second.adapterGeneration &&
            read.sessionGeneration == second.sessionGeneration &&
            read.observationSequence == second.observationSequence &&
            read.observedAtTickMilliseconds ==
                second.observedAtTickMilliseconds &&
            read.state == second.state &&
            read.status == second.status,
        "A newer publication must atomically replace every older field.");

    mailbox.Reset();
    Require(
        !mailbox.TryRead(read),
        "Reset must remove the previous session publication.");
}

void TestEdgeDetector() {
    reactorv::bootstrap::LegacyStartupEdgeDetector detector;
    Require(
        !detector.BeginSession(0, 1) && !detector.BeginSession(1, 0),
        "Zero adapter or session generations must be rejected.");
    Require(detector.BeginSession(11, 21), "A grounded session must begin.");

    Require(
        detector.Observe(Observation(
            10,
            21,
            1,
            LegacyStartupRawState::Frontend)).decision ==
            LegacyStartupPollDecision::InvalidGeneration,
        "An observation from an older adapter generation must be rejected.");
    Require(
        detector.Observe(Observation(
            11,
            20,
            1,
            LegacyStartupRawState::Frontend)).decision ==
            LegacyStartupPollDecision::InvalidGeneration,
        "An observation from another lifecycle session must be rejected.");

    auto invalidSequence = Observation(
        11,
        21,
        0,
        LegacyStartupRawState::Frontend);
    Require(
        detector.Observe(invalidSequence).decision ==
            LegacyStartupPollDecision::InvalidObservation,
        "A zero observation sequence must fail closed.");

    Require(
        detector.Observe(Observation(
            11,
            21,
            1,
            LegacyStartupRawState::Frontend)).decision ==
            LegacyStartupPollDecision::NoEdge,
        "A grounded frontend observation must only arm the session.");

    const auto entering = detector.Observe(Observation(
        11,
        21,
        2,
        LegacyStartupRawState::StoryTransition));
    Require(
        entering.HasEnteringStoryEdge() &&
            entering.edge.edgeSequence == 1 &&
            entering.edge.adapterGeneration == 11 &&
            entering.edge.sessionGeneration == 21 &&
            entering.edge.sourceObservationSequence == 2 &&
            entering.edge.observedAtTickMilliseconds == 1002,
        "Frontend to StoryTransition must emit one fully attributed edge.");

    Require(
        detector.Observe(Observation(
            11,
            21,
            2,
            LegacyStartupRawState::StoryTransition)).decision ==
            LegacyStartupPollDecision::StaleObservation,
        "A duplicate callback observation must be rejected.");
    Require(
        detector.Observe(Observation(
            11,
            21,
            1,
            LegacyStartupRawState::Frontend)).decision ==
            LegacyStartupPollDecision::StaleObservation,
        "An out-of-order callback observation must be rejected.");
    Require(
        detector.Observe(Observation(
            11,
            21,
            3,
            LegacyStartupRawState::StoryTransition)).decision ==
            LegacyStartupPollDecision::NoEdge,
        "A held or repeated transition must not emit a second edge.");
    Require(
        detector.Observe(Observation(
            11,
            21,
            4,
            LegacyStartupRawState::StoryActive)).decision ==
            LegacyStartupPollDecision::NoEdge,
        "Story becoming active must not emit another startup edge.");
    Require(
        detector.Observe(Observation(
            11,
            21,
            5,
            LegacyStartupRawState::Frontend)).decision ==
            LegacyStartupPollDecision::NoEdge &&
            detector.Observe(Observation(
                11,
                21,
                6,
                LegacyStartupRawState::StoryTransition)).decision ==
                LegacyStartupPollDecision::NoEdge,
        "Returning to the frontend cannot re-arm the same session generation.");

    Require(
        detector.BeginSession(11, 22),
        "A new monotonic lifecycle session must be accepted.");
    Require(
        detector.Observe(Observation(
            11,
            22,
            1,
            LegacyStartupRawState::Frontend)).decision ==
            LegacyStartupPollDecision::NoEdge,
        "The next lifecycle session must arm independently.");
    const auto secondEntering = detector.Observe(Observation(
        11,
        22,
        2,
        LegacyStartupRawState::StoryTransition));
    Require(
        secondEntering.HasEnteringStoryEdge() &&
            secondEntering.edge.edgeSequence == 2,
        "A distinct session may emit exactly one later edge.");
}

void TestAbstentionAndTerminalStates() {
    reactorv::bootstrap::LegacyStartupEdgeDetector attachLate;
    Require(attachLate.BeginSession(31, 41), "Attach-late fixture must begin.");
    Require(
        attachLate.Observe(Observation(
            31,
            41,
            1,
            LegacyStartupRawState::StoryActive)).decision ==
            LegacyStartupPollDecision::NoEdge,
        "Attaching after Story is active must abstain.");
    Require(
        attachLate.Observe(Observation(
            31,
            41,
            2,
            LegacyStartupRawState::Frontend)).decision ==
            LegacyStartupPollDecision::NoEdge &&
            attachLate.Observe(Observation(
                31,
                41,
                3,
                LegacyStartupRawState::StoryTransition)).decision ==
                LegacyStartupPollDecision::NoEdge,
        "An attach-late session must not retroactively arm after StoryActive.");

    reactorv::bootstrap::LegacyStartupEdgeDetector ungrounded;
    Require(ungrounded.BeginSession(51, 61), "Ungrounded fixture must begin.");
    Require(
        ungrounded.Observe(Observation(
            51,
            61,
            1,
            LegacyStartupRawState::Frontend,
            LegacyStartupObservationStatus::Unavailable)).decision ==
            LegacyStartupPollDecision::UngroundedObservation,
        "Unavailable lifecycle evidence must abstain.");
    Require(
        ungrounded.Observe(Observation(
            51,
            61,
            2,
            LegacyStartupRawState::StoryTransition,
            LegacyStartupObservationStatus::ReadFault)).decision ==
            LegacyStartupPollDecision::UngroundedObservation,
        "A guarded lifecycle read fault must abstain.");
    Require(
        ungrounded.Observe(Observation(
            51,
            61,
            3,
            LegacyStartupRawState::StoryTransition)).decision ==
            LegacyStartupPollDecision::NoEdge,
        "A read fault must invalidate the previous frontend arm.");
    Require(
        ungrounded.Observe(Observation(
            51,
            61,
            4,
            LegacyStartupRawState::Unknown)).decision ==
            LegacyStartupPollDecision::InvalidObservation,
        "A grounded Unknown value must still fail closed.");
    Require(
        ungrounded.Observe(Observation(
            51,
            61,
            5,
            LegacyStartupRawState::Frontend)).decision ==
            LegacyStartupPollDecision::NoEdge,
        "Later grounded frontend evidence may arm the untouched session.");
    Require(
        ungrounded.Observe(Observation(
            51,
            61,
            6,
            LegacyStartupRawState::StoryTransition)).HasEnteringStoryEdge(),
        "A fresh grounded frontend may arm a later transition after a fault.");
    Require(
        ungrounded.Observe(Observation(
            51,
            61,
            7,
            LegacyStartupRawState::ShuttingDown)).decision ==
            LegacyStartupPollDecision::NoEdge &&
            ungrounded.Observe(Observation(
                51,
                61,
                8,
                LegacyStartupRawState::StoryTransition)).decision ==
                LegacyStartupPollDecision::NoEdge,
        "Shutdown must make the current detector session terminal.");
}

void TestHookCoreActivationAndTeardown() {
    const auto identity = SupportedIdentity();
    const auto profile = SupportedProfile();
    const auto evidence = MatchingEvidence();
    LegacyStartupHookCore core;

    Require(
        core.Poll().decision == LegacyStartupPollDecision::GateClosed &&
            !core.IsActive(),
        "A never-activated core must fail closed.");

    auto invalidProfile = profile;
    invalidProfile.profileRevision = 0;
    const auto gateClosed = core.Activate(
        identity,
        invalidProfile,
        evidence,
        1,
        1);
    Require(
        gateClosed.status == LegacyStartupActivationStatus::GateClosed &&
            gateClosed.gate.status == LegacyHookGateStatus::InvalidProfile &&
            !core.IsActive(),
        "Activation must not partially open a rejected gate.");

    Require(
        core.Activate(identity, profile, evidence, 0, 1).status ==
                LegacyStartupActivationStatus::InvalidGeneration &&
            core.Activate(identity, profile, evidence, 1, 0).status ==
                LegacyStartupActivationStatus::InvalidGeneration,
        "Activation requires nonzero adapter and session generations.");

    const auto activated = core.Activate(
        identity,
        profile,
        evidence,
        1,
        1);
    Require(
        activated.IsActive() && core.IsActive(),
        "An exact gate with monotonic generations must activate.");
    Require(
        core.Poll().decision == LegacyStartupPollDecision::NoObservation,
        "Activation must clear publications from older attempts.");

    core.PublishFromHook(Observation(
        2,
        1,
        1,
        LegacyStartupRawState::Frontend));
    Require(
        core.Poll().decision == LegacyStartupPollDecision::NoObservation,
        "The callback boundary must drop a mismatched adapter generation.");
    core.PublishFromHook(Observation(
        1,
        2,
        1,
        LegacyStartupRawState::Frontend));
    Require(
        core.Poll().decision == LegacyStartupPollDecision::NoObservation,
        "The callback boundary must drop a mismatched session generation.");

    core.PublishFromHook(Observation(
        1,
        1,
        1,
        LegacyStartupRawState::Frontend));
    Require(
        core.Poll().decision == LegacyStartupPollDecision::NoEdge,
        "The active core must arm from grounded frontend evidence.");
    core.PublishFromHook(Observation(
        1,
        1,
        2,
        LegacyStartupRawState::StoryTransition));
    Require(
        core.Poll().HasEnteringStoryEdge(),
        "The active core must surface its one entering-Story edge.");
    Require(
        core.Poll().decision == LegacyStartupPollDecision::StaleObservation,
        "Polling the same mailbox generation twice must not duplicate an edge.");

    core.Deactivate();
    core.Deactivate();
    Require(
        !core.IsActive() &&
            core.Poll().decision == LegacyStartupPollDecision::GateClosed,
        "Teardown must be idempotent and close the gate.");
    core.PublishFromHook(Observation(
        1,
        1,
        3,
        LegacyStartupRawState::StoryTransition));
    Require(
        core.Poll().decision == LegacyStartupPollDecision::GateClosed,
        "A callback racing after teardown must not publish another edge.");

    const auto sameGeneration = core.Activate(
        identity,
        profile,
        evidence,
        1,
        1);
    Require(
        sameGeneration.status ==
                LegacyStartupActivationStatus::NonMonotonicGeneration &&
            !core.IsActive(),
        "Reusing the same generation after teardown must fail closed.");

    const auto nextSession = core.Activate(
        identity,
        profile,
        evidence,
        1,
        2);
    Require(
        nextSession.IsActive(),
        "A strictly newer session generation must reactivate the adapter.");
    core.PublishFromHook(Observation(
        1,
        2,
        1,
        LegacyStartupRawState::Frontend));
    Require(
        core.Poll().decision == LegacyStartupPollDecision::NoEdge,
        "The next session must begin without replaying an old observation.");
    core.PublishFromHook(Observation(
        1,
        2,
        2,
        LegacyStartupRawState::StoryTransition));
    Require(
        core.Poll().HasEnteringStoryEdge(),
        "A newer session may emit its own entering-Story edge.");

    const auto olderAdapter = core.Activate(
        identity,
        profile,
        evidence,
        0,
        99);
    Require(
        olderAdapter.status == LegacyStartupActivationStatus::InvalidGeneration &&
            !core.IsActive(),
        "A zero adapter generation must deactivate and fail closed.");

    const auto nextAdapter = core.Activate(
        identity,
        profile,
        evidence,
        2,
        1);
    Require(
        nextAdapter.IsActive(),
        "A newer adapter generation may begin its own session sequence.");
    const auto replayedAdapter = core.Activate(
        identity,
        profile,
        evidence,
        1,
        99);
    Require(
        replayedAdapter.status ==
                LegacyStartupActivationStatus::NonMonotonicGeneration &&
            !core.IsActive(),
        "A stale adapter generation must never be revived by a larger session number.");
}

} // namespace

int main() {
    TestBuildAndSignatureGate();
    TestMailboxPublication();
    TestEdgeDetector();
    TestAbstentionAndTerminalStates();
    TestHookCoreActivationAndTeardown();
    return 0;
}
