#pragma once

#include "RageWebUI.Native.h"

#include <atomic>
#include <deque>
#include <memory>
#include <mutex>
#include <vector>
#include <windows.h>

namespace rwui {

class InputQueue final {
public:
    bool Attach(HWND window);
    void Detach();
    bool Poll(RwuiInputEvent& inputEvent);
    void SetCapture(bool capture);

private:
    struct CallbackBinding final {
        std::atomic<InputQueue*> owner{};
        HWND window{};
        std::atomic<WNDPROC> previousProcedure{};
    };
    using CallbackBindings = std::vector<std::shared_ptr<CallbackBinding>>;

    static constexpr std::size_t MaximumEvents = 1024;
    static constexpr std::size_t MaximumCallbackBindings = 8;
    static LRESULT CALLBACK WindowProcedure(
        HWND window,
        UINT message,
        WPARAM wParam,
        LPARAM lParam);
    LRESULT HandleMessage(
        HWND window,
        UINT message,
        WPARAM wParam,
        LPARAM lParam,
        WNDPROC previousProcedure);
    static void RetireBinding(
        const std::shared_ptr<CallbackBinding>& binding) noexcept;
    void Push(RwuiInputEvent inputEvent) noexcept;
    static std::uint32_t ReadModifiers();

    static std::atomic<std::shared_ptr<const CallbackBindings>> bindings_;
    static std::mutex bindingLifecycleMutex_;
    std::atomic<HWND> window_{};
    std::atomic_bool capture_{};
    std::mutex mutex_;
    std::deque<RwuiInputEvent> events_;
};

} // namespace rwui
