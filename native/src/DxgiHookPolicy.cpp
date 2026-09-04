#include "DxgiHookPolicy.h"

#include <algorithm>

namespace rwui {

HookAddressDisposition ClassifyHookAddress(
    const std::uintptr_t address,
    const std::span<const std::uintptr_t> registeredAddresses,
    const bool required) noexcept {
    const bool unavailable = address == 0;
    const bool duplicate = !unavailable &&
        std::find(registeredAddresses.begin(), registeredAddresses.end(), address) !=
            registeredAddresses.end();
    if (!unavailable && !duplicate) return HookAddressDisposition::Register;
    return required
        ? HookAddressDisposition::RejectRequired
        : HookAddressDisposition::SkipOptional;
}

bool IsUniformPresentQueueSet(
    const std::span<const std::uintptr_t> canonicalQueueIdentities) noexcept {
    if (canonicalQueueIdentities.empty() ||
        canonicalQueueIdentities.front() == 0) return false;
    return std::all_of(
        canonicalQueueIdentities.begin() + 1,
        canonicalQueueIdentities.end(),
        [&](const auto identity) {
            return identity == canonicalQueueIdentities.front();
        });
}

bool IsDxgiDeviceFailure(const HRESULT result) noexcept {
    return result == DXGI_ERROR_DEVICE_REMOVED ||
        result == DXGI_ERROR_DEVICE_RESET ||
        result == DXGI_ERROR_DEVICE_HUNG ||
        result == DXGI_ERROR_DRIVER_INTERNAL_ERROR;
}

bool IsTestPresent(const UINT flags) noexcept {
    return (flags & DXGI_PRESENT_TEST) != 0;
}

bool DidPresentCommit(const HRESULT result) noexcept {
    return result == S_OK;
}

bool IsTargetQueueCandidate(
    const QueueBindingCandidate& candidate,
    const std::uintptr_t targetWindow) noexcept {
    return targetWindow != 0 &&
        candidate.swapChain != 0 &&
        candidate.window == targetWindow &&
        candidate.queue != 0 &&
        candidate.directQueue &&
        candidate.source != QueueBindingSource::None;
}

bool ShouldReplaceQueueBinding(
    const QueueBindingSelection& current,
    const QueueBindingCandidate& candidate,
    const std::uintptr_t targetWindow) noexcept {
    if (!IsTargetQueueCandidate(candidate, targetWindow)) return false;
    // ExecuteCommandLists is process-global. Even a DIRECT queue from the
    // target device is not proof that it owns this swap chain's Present.
    // Retain that observation for diagnostics only; never render from it.
    if (candidate.source == QueueBindingSource::ExecuteFallback) return false;
    if (current.source == QueueBindingSource::None ||
        current.swapChain == 0 || current.queue == 0) {
        return true;
    }

    switch (candidate.source) {
    case QueueBindingSource::FactoryCreation:
        // A factory callback identifies both the returned swap chain and the
        // queue used to create it. It supersedes a heuristic fallback and an
        // older target-window swap chain.
        return true;
    case QueueBindingSource::ResizeBuffers1:
        // ResizeBuffers1 supplies the present queue array for this exact swap
        // chain. It must never be allowed to redirect another swap chain.
        return current.swapChain == candidate.swapChain;
    case QueueBindingSource::ExecuteFallback:
        // Rejected before the empty-selection case above.
        return false;
    case QueueBindingSource::None:
    default:
        return false;
    }
}

QueueBindingSelection SelectQueueBinding(
    const QueueBindingSelection& current,
    const QueueBindingCandidate& candidate,
    const std::uintptr_t targetWindow) noexcept {
    if (!ShouldReplaceQueueBinding(current, candidate, targetWindow)) {
        return current;
    }
    return QueueBindingSelection{
        candidate.swapChain,
        candidate.queue,
        candidate.source,
    };
}

} // namespace rwui
