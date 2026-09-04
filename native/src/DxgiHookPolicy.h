#pragma once

#include <cstdint>
#include <dxgi.h>
#include <span>

namespace rwui {

enum class QueueBindingSource : std::uint8_t {
    None = 0,
    ExecuteFallback = 1,
    FactoryCreation = 2,
    ResizeBuffers1 = 3,
};

struct QueueBindingCandidate final {
    std::uintptr_t swapChain{};
    std::uintptr_t window{};
    std::uintptr_t queue{};
    bool directQueue{};
    QueueBindingSource source{QueueBindingSource::None};
};

struct QueueBindingSelection final {
    std::uintptr_t swapChain{};
    std::uintptr_t queue{};
    QueueBindingSource source{QueueBindingSource::None};
};

enum class HookAddressDisposition : std::uint8_t {
    Register = 0,
    SkipOptional = 1,
    RejectRequired = 2,
};

HookAddressDisposition ClassifyHookAddress(
    std::uintptr_t address,
    std::span<const std::uintptr_t> registeredAddresses,
    bool required) noexcept;

// Reactor owns one compositor command queue. ResizeBuffers1 is supported only
// when every back buffer names the same canonical DIRECT queue; alternating
// queues would otherwise force a device rebuild on every Present.
bool IsUniformPresentQueueSet(
    std::span<const std::uintptr_t> canonicalQueueIdentities) noexcept;

bool IsDxgiDeviceFailure(HRESULT result) noexcept;
bool IsTestPresent(UINT flags) noexcept;
bool DidPresentCommit(HRESULT result) noexcept;

bool IsTargetQueueCandidate(
    const QueueBindingCandidate& candidate,
    std::uintptr_t targetWindow) noexcept;

bool ShouldReplaceQueueBinding(
    const QueueBindingSelection& current,
    const QueueBindingCandidate& candidate,
    std::uintptr_t targetWindow) noexcept;

QueueBindingSelection SelectQueueBinding(
    const QueueBindingSelection& current,
    const QueueBindingCandidate& candidate,
    std::uintptr_t targetWindow) noexcept;

} // namespace rwui
