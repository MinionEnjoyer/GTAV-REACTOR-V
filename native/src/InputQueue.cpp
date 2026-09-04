#include "InputQueue.h"

#include <algorithm>
#include <windowsx.h>

namespace rwui {

std::atomic<std::shared_ptr<const InputQueue::CallbackBindings>>
    InputQueue::bindings_{std::make_shared<const CallbackBindings>()};
std::mutex InputQueue::bindingLifecycleMutex_;

bool InputQueue::Attach(HWND window) {
    if (window == nullptr || !IsWindow(window) ||
        window_.load(std::memory_order_acquire) != nullptr) {
        return false;
    }
    std::scoped_lock bindingLock(bindingLifecycleMutex_);
    const auto loadedBindings = bindings_.load(std::memory_order_acquire);
    auto compacted = std::make_shared<CallbackBindings>();
    compacted->reserve(MaximumCallbackBindings);
    for (const auto& binding : *loadedBindings) {
        if (binding->window == window || IsWindow(binding->window)) {
            compacted->push_back(binding);
        }
    }
    const std::shared_ptr<const CallbackBindings> currentBindings = compacted;
    if (compacted->size() != loadedBindings->size()) {
        bindings_.store(currentBindings, std::memory_order_release);
    }
    for (const auto& binding : *currentBindings) {
        if (binding->window != window) continue;
        InputQueue* expected{};
        if (!binding->owner.compare_exchange_strong(
                expected, this, std::memory_order_acq_rel,
                std::memory_order_acquire)) return false;
        window_.store(window, std::memory_order_release);
        return true;
    }
    if (currentBindings->size() >= MaximumCallbackBindings) return false;

    auto binding = std::make_shared<CallbackBinding>();
    binding->owner.store(this, std::memory_order_relaxed);
    binding->window = window;
    binding->previousProcedure.store(
        reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC)),
        std::memory_order_relaxed);
    auto updated = std::make_shared<CallbackBindings>(*currentBindings);
    updated->push_back(binding);
    bindings_.store(updated, std::memory_order_release);

    SetLastError(0);
    const auto previous = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(
        window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&WindowProcedure)));
    if (previous == nullptr && GetLastError() != 0) {
        binding->owner.store(nullptr, std::memory_order_release);
        bindings_.store(currentBindings, std::memory_order_release);
        return false;
    }
    binding->previousProcedure.store(previous, std::memory_order_release);
    window_.store(window, std::memory_order_release);
    return true;
}

void InputQueue::Detach() {
    SetCapture(false);
    const auto window = window_.exchange(nullptr, std::memory_order_acq_rel);
    std::scoped_lock bindingLock(bindingLifecycleMutex_);
    const auto currentBindings = bindings_.load(std::memory_order_acquire);
    const auto found = std::find_if(
        currentBindings->begin(), currentBindings->end(),
        [&](const auto& binding) {
            return binding->window == window &&
                binding->owner.load(std::memory_order_acquire) == this;
        });
    if (found != currentBindings->end()) {
        const auto binding = *found;
        binding->owner.store(nullptr, std::memory_order_release);
        const auto current = IsWindow(window)
            ? reinterpret_cast<WNDPROC>(
                GetWindowLongPtrW(window, GWLP_WNDPROC))
            : nullptr;
        if (current == &WindowProcedure) {
            SetWindowLongPtrW(
                window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(
                    binding->previousProcedure.load(
                        std::memory_order_acquire)));
            auto updated = std::make_shared<CallbackBindings>(*currentBindings);
            updated->erase(updated->begin() +
                std::distance(currentBindings->begin(), found));
            bindings_.store(updated, std::memory_order_release);
        }
    }
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
    capture_.store(capture, std::memory_order_release);
}

LRESULT CALLBACK InputQueue::WindowProcedure(
    HWND window,
    const UINT message,
    const WPARAM wParam,
    const LPARAM lParam) {
    const auto snapshot = bindings_.load(std::memory_order_acquire);
    const auto found = std::find_if(
        snapshot->begin(), snapshot->end(),
        [&](const auto& binding) { return binding->window == window; });
    if (found == snapshot->end()) {
        return DefWindowProcW(window, message, wParam, lParam);
    }
    const auto binding = *found;
    const auto previous = binding->previousProcedure.load(
        std::memory_order_acquire);
    auto* const owner = binding->owner.load(std::memory_order_acquire);
    LRESULT result{};
    try {
        result = owner != nullptr
            ? owner->HandleMessage(window, message, wParam, lParam, previous)
            : previous != nullptr
            ? CallWindowProcW(previous, window, message, wParam, lParam)
            : DefWindowProcW(window, message, wParam, lParam);
    } catch (...) {
        // Never unwind through user32. In particular, a failed deque growth
        // must drop only that input event and preserve the exact subclass
        // chain installed before ReactorV.
        result = previous != nullptr
            ? CallWindowProcW(previous, window, message, wParam, lParam)
            : DefWindowProcW(window, message, wParam, lParam);
    }
    if (message == WM_NCDESTROY) RetireBinding(binding);
    return result;
}

LRESULT InputQueue::HandleMessage(
    HWND window,
    const UINT message,
    const WPARAM wParam,
    const LPARAM lParam,
    const WNDPROC previousProcedure) {
    if (capture_.load(std::memory_order_acquire)) {
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

    return previousProcedure != nullptr
        ? CallWindowProcW(previousProcedure, window, message, wParam, lParam)
        : DefWindowProcW(window, message, wParam, lParam);
}

void InputQueue::RetireBinding(
    const std::shared_ptr<CallbackBinding>& binding) noexcept {
    try {
        if (auto* const owner = binding->owner.exchange(
                nullptr, std::memory_order_acq_rel)) {
            HWND expected = binding->window;
            owner->window_.compare_exchange_strong(
                expected, nullptr, std::memory_order_acq_rel,
                std::memory_order_acquire);
        }
        std::scoped_lock lock(bindingLifecycleMutex_);
        const auto current = bindings_.load(std::memory_order_acquire);
        auto updated = std::make_shared<CallbackBindings>(*current);
        std::erase(*updated, binding);
        bindings_.store(updated, std::memory_order_release);
    } catch (...) {
        // A destruction callback must always forward and return to user32.
    }
}

void InputQueue::Push(RwuiInputEvent inputEvent) noexcept {
    try {
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
    } catch (...) {
        // Input is lossy by contract. Fail open rather than propagating an
        // allocation/synchronization exception through the HWND callback.
    }
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
