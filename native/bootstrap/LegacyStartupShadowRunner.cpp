#include "LegacyStartupShadowRunner.h"

#include <windows.h>

#include <algorithm>
#include <chrono>

namespace reactorv::bootstrap {
namespace {

constexpr std::uint32_t StopCheckMilliseconds = 25;

bool SameValidationEvidence(
    const LegacyTargetValidationEvidence& left,
    const LegacyTargetValidationEvidence& right) noexcept {
    return left.imageBoundsPass == right.imageBoundsPass &&
        left.alignmentPass == right.alignmentPass &&
        left.regionQueryPass == right.regionQueryPass &&
        left.regionUsablePass == right.regionUsablePass &&
        left.ordinaryProtectionPass == right.ordinaryProtectionPass &&
        left.executeReadWriteObserved == right.executeReadWriteObserved &&
        left.dataSectionBackedProtectionPass ==
            right.dataSectionBackedProtectionPass &&
        left.protectionPass == right.protectionPass &&
        left.typePass == right.typePass &&
        left.allocationBasePass == right.allocationBasePass &&
        left.regionAddressPass == right.regionAddressPass &&
        left.targetContainedPass == right.targetContainedPass;
}

bool SameTargetDiagnostics(
    const LegacyShadowTargetDiagnostics& left,
    const LegacyShadowTargetDiagnostics& right) noexcept {
    return left.instructionRva == right.instructionRva &&
        left.instructionBytes == right.instructionBytes &&
        left.instructionBytesRead == right.instructionBytesRead &&
        left.instructionStatus == right.instructionStatus &&
        left.decodeAttempted == right.decodeAttempted &&
        left.decodeStatus == right.decodeStatus &&
        left.displacement == right.displacement &&
        left.candidateTargetRva == right.candidateTargetRva &&
        left.validationStatus == right.validationStatus &&
        SameValidationEvidence(
            left.validationEvidence,
            right.validationEvidence) &&
        left.regionQueryError == right.regionQueryError &&
        left.regionState == right.regionState &&
        left.regionProtect == right.regionProtect &&
        left.regionType == right.regionType &&
        left.regionBaseRva == right.regionBaseRva &&
        left.regionSize == right.regionSize &&
        left.dataSectionStatus == right.dataSectionStatus &&
        left.dataSectionReadError == right.dataSectionReadError &&
        left.dataSectionMatchCount == right.dataSectionMatchCount &&
        left.dataSectionName == right.dataSectionName &&
        left.dataSectionRva == right.dataSectionRva &&
        left.dataSectionVirtualSize == right.dataSectionVirtualSize &&
        left.dataSectionRawSize == right.dataSectionRawSize &&
        left.dataSectionCharacteristics ==
            right.dataSectionCharacteristics;
}

bool SameReadyEvidence(
    const LegacyShadowDiscoveryReceipt& left,
    const LegacyShadowDiscoveryReceipt& right) noexcept {
    return left.patternStatus == right.patternStatus &&
        left.matchCount == right.matchCount &&
        left.matchRva == right.matchRva &&
        left.targetRva == right.targetRva &&
        left.peTimestamp == right.peTimestamp &&
        left.sizeOfImage == right.sizeOfImage &&
        left.gateStatus == right.gateStatus &&
        SameTargetDiagnostics(
            left.targetDiagnostics,
            right.targetDiagnostics);
}

bool WaitForRetry(
    const std::stop_token& stopToken,
    const std::uint32_t delayMilliseconds) noexcept {
    std::uint32_t waited{};
    while (waited < delayMilliseconds) {
        if (stopToken.stop_requested()) return false;
        const auto slice = std::min(
            StopCheckMilliseconds,
            delayMilliseconds - waited);
        Sleep(slice);
        waited += slice;
    }
    return !stopToken.stop_requested();
}

} // namespace

bool IsLegacyShadowDiscoveryRetryable(
    const LegacyShadowDiscoveryStatus status) noexcept {
    return status == LegacyShadowDiscoveryStatus::SignatureMissing ||
        status == LegacyShadowDiscoveryStatus::SignatureReadFault ||
        status == LegacyShadowDiscoveryStatus::UnstableEvidence;
}

std::uint32_t LegacyShadowDiscoveryRetryDelayMilliseconds(
    const std::uint32_t completedAttempts) noexcept {
    switch (completedAttempts) {
        case 0: return 0;
        case 1: return 250;
        case 2: return 750;
        case 3: return 1500;
        case 4: return 3000;
        case 5: return 6000;
        case 6: return 12000;
        default: return 20000;
    }
}

LegacyStartupShadowRunner::LegacyStartupShadowRunner(
    void* context,
    const LegacyShadowDiscoverFunction discover) noexcept
    : _context(context), _discover(discover) {}

LegacyStartupShadowRunner::~LegacyStartupShadowRunner() {
    RequestStop();
    Join();
}

bool LegacyStartupShadowRunner::Start() noexcept {
    if (_context == nullptr || _discover == nullptr) return false;
    bool expected = false;
    if (!_started.compare_exchange_strong(
            expected,
            true,
            std::memory_order_acq_rel)) {
        return false;
    }
    try {
        _worker = std::jthread(
            [this](const std::stop_token stopToken) noexcept {
                Run(stopToken);
            });
        return true;
    } catch (...) {
        _completed.store(true, std::memory_order_release);
        return false;
    }
}

void LegacyStartupShadowRunner::RequestStop() noexcept {
    if (_worker.joinable()) _worker.request_stop();
}

void LegacyStartupShadowRunner::Join() noexcept {
    if (_worker.joinable() &&
        _worker.get_id() != std::this_thread::get_id()) {
        _worker.join();
    }
}

bool LegacyStartupShadowRunner::IsCompleted() const noexcept {
    return _completed.load(std::memory_order_acquire);
}

bool LegacyStartupShadowRunner::IsReady() const noexcept {
    return IsCompleted() && _ready.load(std::memory_order_acquire);
}

bool LegacyStartupShadowRunner::TryReadAttempt(
    const std::size_t index,
    LegacyShadowDiscoveryAttempt& destination) const noexcept {
    if (index >= _publishedAttempts.load(std::memory_order_acquire) ||
        index >= _attempts.size()) {
        return false;
    }
    destination = _attempts[index];
    return true;
}

std::size_t LegacyStartupShadowRunner::PublishedAttemptCount() const noexcept {
    return _publishedAttempts.load(std::memory_order_acquire);
}

void LegacyStartupShadowRunner::Run(
    const std::stop_token stopToken) noexcept {
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
    LegacyShadowDiscoveryReceipt provisionalReady{};
    bool hasProvisionalReady = false;
    for (std::size_t index = 0; index < _attempts.size(); ++index) {
        if (stopToken.stop_requested()) break;

        const auto startedAt = GetTickCount64();
        const auto receipt = _discover(_context, &stopToken);
        const auto duration = GetTickCount64() - startedAt;
        if (stopToken.stop_requested()) break;

        auto publishedReceipt = receipt;
        bool stableReady = false;
        if (receipt.IsReady()) {
            stableReady = hasProvisionalReady &&
                SameReadyEvidence(provisionalReady, receipt);
            if (!stableReady) {
                provisionalReady = receipt;
                hasProvisionalReady = true;
                publishedReceipt.status =
                    LegacyShadowDiscoveryStatus::UnstableEvidence;
            }
        } else {
            hasProvisionalReady = false;
            provisionalReady = {};
        }

        const bool retryable =
            IsLegacyShadowDiscoveryRetryable(publishedReceipt.status);
        const bool exhausted = retryable && index + 1 == _attempts.size();
        const bool final = stableReady || !retryable || exhausted;
        _attempts[index] = {
            publishedReceipt,
            static_cast<std::uint32_t>(index + 1),
            duration,
            final,
            exhausted,
        };
        _publishedAttempts.store(index + 1, std::memory_order_release);

        if (stableReady) {
            _ready.store(true, std::memory_order_release);
            break;
        }
        if (final) break;
        const auto delay = LegacyShadowDiscoveryRetryDelayMilliseconds(
            static_cast<std::uint32_t>(index + 1));
        if (!WaitForRetry(stopToken, delay)) break;
    }
    _completed.store(true, std::memory_order_release);
}

} // namespace reactorv::bootstrap
