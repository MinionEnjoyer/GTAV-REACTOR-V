#include "InputQueue.h"

#include <windowsx.h>

namespace rwui {

InputQueue* InputQueue::active_ = nullptr;

bool InputQueue::Attach(HWND window) {
    if (window == nullptr || !IsWindow(window) || active_ != nullptr) {
        return false;
    }

    SetLastError(0);
    const auto previous = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(
        window,
        GWLP_WNDPROC,
        reinterpret_cast<LONG_PTR>(&WindowProcedure)));
    if (previous == nullptr && GetLastError() != 0) {
        return false;
    }

    window_ = window;
    previousProcedure_ = previous;
    active_ = this;
    return true;
}

void InputQueue::Detach() {
    if (window_ != nullptr && previousProcedure_ != nullptr && IsWindow(window_)) {
        const auto current = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window_, GWLP_WNDPROC));
        if (current == &WindowProcedure) {
            SetWindowLongPtrW(window_, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(previousProcedure_));
        }
    }
    if (active_ == this) {
        active_ = nullptr;
    }
    window_ = nullptr;
    previousProcedure_ = nullptr;
    std::scoped_lock lock(mutex_);
    events_.clear();
}

bool InputQueue::Poll(RwuiInputEvent& inputEvent) {
    std::scoped_lock lock(mutex_);
    if (events_.empty()) {
        return false;
    }
    inputEvent = events_.front();
    events_.pop_front();
    return true;
}

void InputQueue::SetCapture(const bool capture) {
    capture_ = capture;
}

LRESULT CALLBACK InputQueue::WindowProcedure(
    HWND window,
    const UINT message,
    const WPARAM wParam,
    const LPARAM lParam) {
    if (active_ == nullptr) {
        return DefWindowProcW(window, message, wParam, lParam);
    }
    return active_->HandleMessage(window, message, wParam, lParam);
}

LRESULT InputQueue::HandleMessage(
    HWND window,
    const UINT message,
    const WPARAM wParam,
    const LPARAM lParam) {
    if (capture_) {
        RwuiInputEvent input{};
        input.modifiers = ReadModifiers();
        input.timestamp = GetTickCount64();
        bool recognized = true;

        switch (message) {
        case WM_MOUSEMOVE:
            input.type = RwuiInputType::MouseMove;
            input.x = GET_X_LPARAM(lParam);
            input.y = GET_Y_LPARAM(lParam);
            break;
        case WM_LBUTTONDOWN:
        case WM_RBUTTONDOWN:
        case WM_MBUTTONDOWN:
            input.type = RwuiInputType::MouseDown;
            input.x = GET_X_LPARAM(lParam);
            input.y = GET_Y_LPARAM(lParam);
            input.key = message == WM_LBUTTONDOWN ? 0 : message == WM_RBUTTONDOWN ? 1 : 2;
            break;
        case WM_LBUTTONUP:
        case WM_RBUTTONUP:
        case WM_MBUTTONUP:
            input.type = RwuiInputType::MouseUp;
            input.x = GET_X_LPARAM(lParam);
            input.y = GET_Y_LPARAM(lParam);
            input.key = message == WM_LBUTTONUP ? 0 : message == WM_RBUTTONUP ? 1 : 2;
            break;
        case WM_MOUSEWHEEL: {
            input.type = RwuiInputType::MouseWheel;
            POINT point{ GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam) };
            ScreenToClient(window, &point);
            input.x = point.x;
            input.y = point.y;
            input.delta = GET_WHEEL_DELTA_WPARAM(wParam);
            break;
        }
        case WM_KEYDOWN:
        case WM_SYSKEYDOWN:
            input.type = RwuiInputType::KeyDown;
            input.key = static_cast<std::int32_t>(wParam);
            input.delta = static_cast<std::int32_t>((lParam >> 16) & 0x1ff);
            break;
        case WM_KEYUP:
        case WM_SYSKEYUP:
            input.type = RwuiInputType::KeyUp;
            input.key = static_cast<std::int32_t>(wParam);
            input.delta = static_cast<std::int32_t>((lParam >> 16) & 0x1ff);
            break;
        case WM_CHAR:
            input.type = RwuiInputType::Character;
            input.key = static_cast<std::int32_t>(wParam);
            break;
        case WM_SIZE:
            input.type = RwuiInputType::Resize;
            input.x = LOWORD(lParam);
            input.y = HIWORD(lParam);
            break;
        default:
            recognized = false;
            break;
        }

        if (recognized) {
            Push(input);
        }
    }

    return CallWindowProcW(previousProcedure_, window, message, wParam, lParam);
}

void InputQueue::Push(RwuiInputEvent inputEvent) {
    std::scoped_lock lock(mutex_);
    if (inputEvent.type == RwuiInputType::MouseMove && !events_.empty() &&
        events_.back().type == RwuiInputType::MouseMove) {
        events_.back() = inputEvent;
        return;
    }
    if (events_.size() == MaximumEvents) {
        events_.pop_front();
    }
    events_.push_back(inputEvent);
}

std::uint32_t InputQueue::ReadModifiers() {
    std::uint32_t result = 0;
    if ((GetKeyState(VK_SHIFT) & 0x8000) != 0) result |= 1u;
    if ((GetKeyState(VK_CONTROL) & 0x8000) != 0) result |= 2u;
    if ((GetKeyState(VK_MENU) & 0x8000) != 0) result |= 4u;
    if ((GetKeyState(VK_LBUTTON) & 0x8000) != 0) result |= 8u;
    if ((GetKeyState(VK_RBUTTON) & 0x8000) != 0) result |= 16u;
    if ((GetKeyState(VK_MBUTTON) & 0x8000) != 0) result |= 32u;
    return result;
}

} // namespace rwui

