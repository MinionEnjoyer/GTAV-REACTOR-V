#include "HookCleanup.h"
#include <array>
#include <iostream>
#include <set>
#include <vector>

namespace {
int failures{};
void Check(bool condition, const char* message) {
    if (!condition) { ++failures; std::cerr << "FAIL: " << message << '\n'; }
}
struct Backend {
    std::array<int, 3> addresses{1, 2, 3};
    std::set<int> remaining{1, 2, 3};
    std::vector<int> removed;
    int failDisable{}, failRemove{}, drainCalls{}, uninitializeCalls{};
    bool callbacksActive{}, failUninitialize{};
    rwui::HookCleanupResult Run(bool owns = true, bool published = false) {
        return rwui::TryCompleteHookCleanup(addresses, owns, published,
            [&](int address) { return address != failDisable; },
            [&] { ++drainCalls; return !callbacksActive; },
            [&](int address) {
                if (address == failRemove) return false;
                remaining.erase(address); removed.push_back(address); return true;
            },
            [&] { ++uninitializeCalls; return !failUninitialize; });
    }
};
}
int main() {
    using Result = rwui::HookCleanupResult;
    Backend normal;
    Check(normal.Run() == Result::Complete, "normal cleanup succeeds");
    Check(normal.removed == std::vector<int>({3, 2, 1}), "remove in reverse order");
    Backend disable;
    disable.failDisable = 2;
    Check(disable.Run() == Result::DisableFailed, "partial disable is deferred");
    Check(disable.drainCalls == 0 && disable.removed.empty() && disable.uninitializeCalls == 0,
          "never free trampolines after disable failure");
    disable.failDisable = 0;
    Check(disable.Run() == Result::Complete, "disable failure remains retryable");
    Backend held;
    held.callbacksActive = true;
    Check(held.Run() == Result::CallbacksPending, "in-flight callbacks defer cleanup");
    Check(held.removed.empty() && held.uninitializeCalls == 0, "held callback keeps trampolines");
    held.callbacksActive = false;
    Check(held.Run() == Result::Complete, "drained callbacks allow retry");
    Backend remove;
    remove.failRemove = 2;
    Check(remove.Run() == Result::RemoveFailed && remove.uninitializeCalls == 0,
          "partial remove cannot uninitialize library");
    Check(remove.remaining == std::set<int>({2}), "only successful removals complete");
    remove.failRemove = 0;
    Check(remove.Run() == Result::Complete && remove.remaining.empty(), "partial removal retry is idempotent");
    Backend uninitialize;
    uninitialize.failUninitialize = true;
    Check(uninitialize.Run() == Result::UninitializeFailed, "uninitialize failure is not success");
    uninitialize.failUninitialize = false;
    Check(uninitialize.Run() == Result::Complete, "uninitialize failure can retry");
    Backend borrowed;
    Check(borrowed.Run(false) == Result::Complete && borrowed.uninitializeCalls == 0,
          "never uninitialize an unowned library");
    Backend published;
    Check(published.Run(true, true) == Result::Complete && published.removed.empty() &&
          published.uninitializeCalls == 0, "published trampolines survive a quiet interval");
    published.callbacksActive = true;
    Check(published.Run(true, true) == Result::CallbacksPending,
          "published callback still defers resource cleanup");
    published.callbacksActive = false;
    Check(published.Run(true, true) == Result::Complete && published.remaining.size() == 3,
          "published hooks remain available for rearm after deferred cleanup");
    return failures ? 1 : 0;
}
