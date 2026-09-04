#include "LegacyStartupShadowRunner.h"

#include <windows.h>

#include <atomic>
#include <cstdlib>
#include <iostream>

namespace {

using reactorv::bootstrap::LegacyShadowDiscoveryReceipt;
using reactorv::bootstrap::LegacyShadowDiscoveryStatus;
using reactorv::bootstrap::LegacyShadowDiscoveryAttempt;
using reactorv::bootstrap::LegacyStartupShadowRunner;

void Require(const bool condition, const char* message) {
    if (!condition) {
        std::cerr << message << '\n';
        std::exit(1);
    }
}

struct FakeDiscovery {
    std::atomic<unsigned int> calls{0};
    LegacyShadowDiscoveryStatus first{LegacyShadowDiscoveryStatus::Ready};
    LegacyShadowDiscoveryStatus later{LegacyShadowDiscoveryStatus::Ready};
    bool changeSecondInstruction{};
};

LegacyShadowDiscoveryReceipt MakeReceipt(
    const LegacyShadowDiscoveryStatus status) noexcept {
    LegacyShadowDiscoveryReceipt receipt{};
    receipt.status = status;
    if (status != LegacyShadowDiscoveryStatus::Ready) return receipt;

    receipt.patternStatus =
        reactorv::bootstrap::LegacyPatternScanStatus::Unique;
    receipt.gateStatus = reactorv::bootstrap::LegacyHookGateStatus::Ready;
    receipt.matchCount = 1;
    receipt.matchRva = 100;
    receipt.targetRva = 512;
    receipt.peTimestamp = 1234;
    receipt.sizeOfImage = 4096;
    auto& diagnostics = receipt.targetDiagnostics;
    diagnostics.instructionRva = 155;
    diagnostics.instructionBytes = {0x89, 0x05, 0x5F, 0x01, 0x00, 0x00};
    diagnostics.instructionBytesRead = diagnostics.instructionBytes.size();
    diagnostics.instructionStatus =
        reactorv::bootstrap::LegacyShadowInstructionStatus::OpcodeMatched;
    diagnostics.decodeAttempted = true;
    diagnostics.decodeStatus =
        reactorv::bootstrap::LegacyRipDecodeStatus::Success;
    diagnostics.displacement = 351;
    diagnostics.candidateTargetRva = receipt.targetRva;
    diagnostics.validationStatus =
        reactorv::bootstrap::LegacyTargetValidationStatus::Accepted;
    auto& validation = diagnostics.validationEvidence;
    validation.imageBoundsPass = true;
    validation.alignmentPass = true;
    validation.regionQueryPass = true;
    validation.regionUsablePass = true;
    validation.ordinaryProtectionPass = true;
    validation.protectionPass = true;
    validation.typePass = true;
    validation.allocationBasePass = true;
    validation.regionAddressPass = true;
    validation.targetContainedPass = true;
    diagnostics.regionState = MEM_COMMIT;
    diagnostics.regionProtect = PAGE_READWRITE;
    diagnostics.regionType = MEM_IMAGE;
    diagnostics.regionBaseRva = 256;
    diagnostics.regionSize = 1024;
    return receipt;
}

LegacyShadowDiscoveryReceipt DiscoverFake(
    void* context,
    const std::stop_token*) noexcept {
    auto& fake = *static_cast<FakeDiscovery*>(context);
    const auto call = fake.calls.fetch_add(1, std::memory_order_acq_rel);
    auto receipt = MakeReceipt(call == 0 ? fake.first : fake.later);
    if (fake.changeSecondInstruction && call == 1 && receipt.IsReady()) {
        ++receipt.targetDiagnostics.instructionBytes[5];
    }
    return receipt;
}

bool WaitUntilCompleted(
    const LegacyStartupShadowRunner& runner,
    const DWORD timeoutMilliseconds) {
    const auto deadline = GetTickCount64() + timeoutMilliseconds;
    while (!runner.IsCompleted() && GetTickCount64() < deadline) Sleep(1);
    return runner.IsCompleted();
}

void TestRetryPolicy() {
    Require(
        reactorv::bootstrap::IsLegacyShadowDiscoveryRetryable(
            LegacyShadowDiscoveryStatus::SignatureMissing) &&
        reactorv::bootstrap::IsLegacyShadowDiscoveryRetryable(
            LegacyShadowDiscoveryStatus::SignatureReadFault),
        "Only transient discovery evidence should be retried.");
    Require(
        reactorv::bootstrap::IsLegacyShadowDiscoveryRetryable(
            LegacyShadowDiscoveryStatus::UnstableEvidence),
        "A first Ready scan must remain provisional until corroborated.");
    Require(
        !reactorv::bootstrap::IsLegacyShadowDiscoveryRetryable(
            LegacyShadowDiscoveryStatus::UnsupportedBuild) &&
        !reactorv::bootstrap::IsLegacyShadowDiscoveryRetryable(
            LegacyShadowDiscoveryStatus::SignatureAmbiguous) &&
        !reactorv::bootstrap::IsLegacyShadowDiscoveryRetryable(
            LegacyShadowDiscoveryStatus::InvalidTarget),
        "Identity, ambiguity, and target failures must remain terminal.");
    Require(
        reactorv::bootstrap::LegacyShadowDiscoveryRetryDelayMilliseconds(1) ==
                250 &&
            reactorv::bootstrap::LegacyShadowDiscoveryRetryDelayMilliseconds(7) ==
                20000,
        "Retry backoff must be bounded and deterministic.");
}

void TestReadyAndTerminalOwnership() {
    LegacyShadowDiscoveryReceipt forgedReady{};
    forgedReady.status = LegacyShadowDiscoveryStatus::Ready;
    Require(
        !forgedReady.IsReady(),
        "A Ready label without unique, gated target evidence must fail closed.");

    FakeDiscovery ready{};
    LegacyStartupShadowRunner readyRunner(&ready, DiscoverFake);
    Require(readyRunner.Start(), "A valid discovery runner must start once.");
    Require(!readyRunner.Start(), "A discovery runner must reject a second start.");
    Require(
        WaitUntilCompleted(readyRunner, 1000),
        "A ready discovery must complete promptly.");
    readyRunner.Join();
    Require(
        readyRunner.IsReady() && ready.calls.load() == 2 &&
            readyRunner.PublishedAttemptCount() == 2,
        "Ready must transfer ownership only after two matching scans.");
    LegacyShadowDiscoveryAttempt attempt{};
    Require(
        readyRunner.TryReadAttempt(0, attempt) && !attempt.final &&
            attempt.receipt.status ==
                LegacyShadowDiscoveryStatus::UnstableEvidence,
        "The first Ready result must be published as provisional evidence.");
    Require(
        readyRunner.TryReadAttempt(1, attempt) && attempt.final &&
            !attempt.exhausted && attempt.attempt == 2 &&
            attempt.receipt.status == LegacyShadowDiscoveryStatus::Ready,
        "The corroborating Ready receipt must be fully attributed.");

    FakeDiscovery blocked{};
    blocked.first = LegacyShadowDiscoveryStatus::UnsupportedBuild;
    LegacyStartupShadowRunner blockedRunner(&blocked, DiscoverFake);
    Require(blockedRunner.Start(), "A terminal fixture must start.");
    Require(
        WaitUntilCompleted(blockedRunner, 1000),
        "A terminal discovery must not wait for retry backoff.");
    blockedRunner.Join();
    Require(
        !blockedRunner.IsReady() && blocked.calls.load() == 1,
        "An unsupported build must fail closed without retry.");
}

void TestChangedDiagnosticsRequireFreshCorroboration() {
    FakeDiscovery changed{};
    changed.changeSecondInstruction = true;
    LegacyStartupShadowRunner runner(&changed, DiscoverFake);
    Require(runner.Start(), "A diagnostic corroboration fixture must start.");
    Require(
        WaitUntilCompleted(runner, 5000),
        "Changed diagnostic evidence must eventually be corroborated.");
    runner.Join();
    Require(
        runner.IsReady() && changed.calls.load() == 4 &&
            runner.PublishedAttemptCount() == 4,
        "A changed live instruction must reset the two-scan evidence window.");

    LegacyShadowDiscoveryAttempt attempt{};
    Require(
        runner.TryReadAttempt(1, attempt) && !attempt.final &&
            attempt.receipt.status ==
                LegacyShadowDiscoveryStatus::UnstableEvidence,
        "The changed second instruction must never acquire ownership.");
}

void TestRetryAndInterruptibleStop() {
    FakeDiscovery retryThenReady{};
    retryThenReady.first = LegacyShadowDiscoveryStatus::SignatureMissing;
    LegacyStartupShadowRunner retryRunner(&retryThenReady, DiscoverFake);
    Require(retryRunner.Start(), "A retry fixture must start.");
    Require(
        WaitUntilCompleted(retryRunner, 2000),
        "A transient miss followed by Ready must complete.");
    retryRunner.Join();
    Require(
        retryRunner.IsReady() && retryThenReady.calls.load() == 3,
        "A transient miss still requires two later matching Ready scans.");

    FakeDiscovery alwaysMissing{};
    alwaysMissing.first = LegacyShadowDiscoveryStatus::SignatureMissing;
    alwaysMissing.later = LegacyShadowDiscoveryStatus::SignatureMissing;
    LegacyStartupShadowRunner stopped(&alwaysMissing, DiscoverFake);
    Require(stopped.Start(), "A stoppable retry fixture must start.");
    const auto deadline = GetTickCount64() + 1000;
    while (stopped.PublishedAttemptCount() == 0 &&
           GetTickCount64() < deadline) {
        Sleep(1);
    }
    const auto stopStarted = GetTickCount64();
    stopped.RequestStop();
    stopped.Join();
    Require(
        GetTickCount64() - stopStarted < 250 &&
            alwaysMissing.calls.load() == 1,
        "Stop must interrupt retry backoff without another discovery call.");
}

} // namespace

int main() {
    TestRetryPolicy();
    TestReadyAndTerminalOwnership();
    TestChangedDiagnosticsRequireFreshCorroboration();
    TestRetryAndInterruptibleStop();
    return 0;
}
