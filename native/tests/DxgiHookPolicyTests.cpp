#include "DxgiHookPolicy.h"

#include <array>
#include <iostream>

namespace {

int failures = 0;

void Check(const bool condition, const char* message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        ++failures;
    }
}

rwui::QueueBindingCandidate Candidate(
    const std::uintptr_t swapChain,
    const std::uintptr_t window,
    const std::uintptr_t queue,
    const rwui::QueueBindingSource source,
    const bool direct = true) {
    return {swapChain, window, queue, direct, source};
}

} // namespace

int main() {
    using rwui::QueueBindingSelection;
    using rwui::QueueBindingSource;

    constexpr std::uintptr_t targetWindow = 0x100;
    const std::array<std::uintptr_t, 2> registeredAddresses{0x10, 0x20};
    Check(rwui::ClassifyHookAddress(0x30, registeredAddresses, true) ==
        rwui::HookAddressDisposition::Register,
        "new required hook address is registered");
    Check(rwui::ClassifyHookAddress(0, registeredAddresses, false) ==
        rwui::HookAddressDisposition::SkipOptional,
        "missing optional extended method is skipped");
    Check(rwui::ClassifyHookAddress(0x20, registeredAddresses, false) ==
        rwui::HookAddressDisposition::SkipOptional,
        "duplicate optional extended method is skipped");
    Check(rwui::ClassifyHookAddress(0x20, registeredAddresses, true) ==
        rwui::HookAddressDisposition::RejectRequired,
        "duplicate required hook address fails closed");
    constexpr std::array<std::uintptr_t, 3> oneQueue{0x41, 0x41, 0x41};
    constexpr std::array<std::uintptr_t, 3> mixedQueues{0x41, 0x42, 0x41};
    Check(rwui::IsUniformPresentQueueSet(oneQueue),
        "one canonical queue may serve every ResizeBuffers1 back buffer");
    Check(!rwui::IsUniformPresentQueueSet(mixedQueues),
        "mixed per-buffer queues fail the single-compositor route open");
    Check(!rwui::IsUniformPresentQueueSet({}),
        "an empty present-queue set is not a render binding");
    Check(rwui::IsTestPresent(DXGI_PRESENT_TEST) &&
        rwui::IsTestPresent(DXGI_PRESENT_TEST | DXGI_PRESENT_DO_NOT_WAIT) &&
        !rwui::IsTestPresent(0),
        "test Presents are classified without mutating the render surface");
    Check(rwui::DidPresentCommit(S_OK) &&
        !rwui::DidPresentCommit(DXGI_STATUS_OCCLUDED) &&
        !rwui::DidPresentCommit(DXGI_ERROR_DEVICE_REMOVED),
        "only S_OK retains visible-overlay input capture");
    Check(rwui::IsDxgiDeviceFailure(DXGI_ERROR_DEVICE_REMOVED) &&
        rwui::IsDxgiDeviceFailure(DXGI_ERROR_DEVICE_RESET) &&
        rwui::IsDxgiDeviceFailure(DXGI_ERROR_DEVICE_HUNG) &&
        rwui::IsDxgiDeviceFailure(DXGI_ERROR_DRIVER_INTERNAL_ERROR) &&
        !rwui::IsDxgiDeviceFailure(DXGI_STATUS_OCCLUDED),
        "device-loss HRESULTs request compositor recovery");

    const auto exact = Candidate(
        0x200, targetWindow, 0x300, QueueBindingSource::FactoryCreation);
    const auto unrelated = Candidate(
        0x201, 0x101, 0x301, QueueBindingSource::FactoryCreation);
    const auto computeQueue = Candidate(
        0x200, targetWindow, 0x302, QueueBindingSource::FactoryCreation, false);

    Check(rwui::IsTargetQueueCandidate(exact, targetWindow),
        "factory candidate for the target window is accepted");
    Check(!rwui::IsTargetQueueCandidate(unrelated, targetWindow),
        "unrelated HWND is rejected");
    Check(!rwui::IsTargetQueueCandidate(computeQueue, targetWindow),
        "non-direct queue is rejected");
    Check(!rwui::IsTargetQueueCandidate(
        Candidate(0, targetWindow, 0x300, QueueBindingSource::FactoryCreation),
        targetWindow),
        "zero swap-chain identity is rejected");

    QueueBindingSelection selection{};
    const auto fallback = Candidate(
        0x200, targetWindow, 0x310, QueueBindingSource::ExecuteFallback);
    selection = rwui::SelectQueueBinding(selection, fallback, targetWindow);
    Check(selection.source == QueueBindingSource::None &&
        selection.queue == 0,
        "process-global execution evidence cannot seed a render binding");

    selection = rwui::SelectQueueBinding(selection, exact, targetWindow);
    Check(selection.source == QueueBindingSource::FactoryCreation &&
        selection.swapChain == 0x200 && selection.queue == 0x300,
        "factory capture seeds an exact empty binding");

    selection = rwui::SelectQueueBinding(
        selection,
        Candidate(0x200, targetWindow, 0x311, QueueBindingSource::ExecuteFallback),
        targetWindow);
    Check(selection.source == QueueBindingSource::FactoryCreation &&
        selection.queue == 0x300,
        "process-global execution evidence cannot overwrite an exact binding");

    selection = rwui::SelectQueueBinding(
        selection,
        Candidate(0x200, targetWindow, 0x320, QueueBindingSource::ResizeBuffers1),
        targetWindow);
    Check(selection.source == QueueBindingSource::ResizeBuffers1 &&
        selection.queue == 0x320,
        "ResizeBuffers1 refreshes the same swap-chain binding");

    selection = rwui::SelectQueueBinding(
        selection,
        Candidate(0x201, targetWindow, 0x321, QueueBindingSource::ResizeBuffers1),
        targetWindow);
    Check(selection.swapChain == 0x200 && selection.queue == 0x320,
        "ResizeBuffers1 cannot hijack a different swap chain");

    selection = rwui::SelectQueueBinding(
        selection,
        Candidate(0x201, targetWindow, 0x330, QueueBindingSource::FactoryCreation),
        targetWindow);
    Check(selection.swapChain == 0x201 && selection.queue == 0x330 &&
        selection.source == QueueBindingSource::FactoryCreation,
        "a newly created target swap chain replaces the old exact binding");

    if (failures == 0) std::cout << "PASS: DXGI hook policy tests\n";
    return failures == 0 ? 0 : 1;
}
