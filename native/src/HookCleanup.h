#pragma once

namespace rwui {

enum class HookCleanupResult {
    Complete,
    DisableFailed,
    CallbacksPending,
    RemoveFailed,
    UninitializeFailed,
};

// The adapters must treat an already-disabled/removed hook as success so a
// partially completed cleanup can be retried without losing its ledger.
template<class Addresses, class Disable, class Drain, class Remove, class Uninitialize>
HookCleanupResult TryCompleteHookCleanup(
    const Addresses& addresses, bool ownsLibrary, bool callbacksPublished,
    Disable disable, Drain drain, Remove remove, Uninitialize uninitialize) {
    bool disabled = true;
    for (auto address : addresses) {
        if (!disable(address)) disabled = false;
    }
    if (!disabled) return HookCleanupResult::DisableFailed;
    if (!drain()) return HookCleanupResult::CallbacksPending;
    // A thread may have branched to a detour but not yet incremented its
    // callback counter. A quiet interval is not proof that its trampoline
    // can be freed. Published trampolines remain resident and reusable.
    if (callbacksPublished) return HookCleanupResult::Complete;
    bool removed = true;
    for (auto it = addresses.rbegin(); it != addresses.rend(); ++it) {
        if (!remove(*it)) removed = false;
    }
    if (!removed) return HookCleanupResult::RemoveFailed;
    if (ownsLibrary && !uninitialize()) return HookCleanupResult::UninitializeFailed;
    return HookCleanupResult::Complete;
}

} // namespace rwui
