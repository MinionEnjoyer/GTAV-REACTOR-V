#pragma once

#include "RageWebUI.Native.h"

#include <deque>
#include <mutex>
#include <windows.h>

namespace rwui {

class InputQueue final {
public:
    bool Attach(HWND window);
    void Detach();
    bool Poll(RwuiInputEvent& inputEvent);
    void SetCapture(bool capture);

private:
    static constexpr std::size_t MaximumEvents = 1024;
    static LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam);
    LRESULT HandleMessage(HWND window, UINT message, WPARAM wParam, LPARAM lParam);
    void Push(RwuiInputEvent inputEvent);
    static std::uint32_t ReadModifiers();

    static InputQueue* active_;
    HWND window_{};
    WNDPROC previousProcedure_{};
    bool capture_{};
    std::mutex mutex_;
    std::deque<RwuiInputEvent> events_;
};

} // namespace rwui

