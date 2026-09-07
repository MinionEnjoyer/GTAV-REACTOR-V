#pragma once

namespace rwui {
// Lifecycle/control paths only. Never call from a Present/input callback or
// an exception handler: this intentionally uses normal safe file I/O.
void WriteNativeLifecycle(const char* event, const char* operation,
                          int result = 0, const void* target = nullptr) noexcept;
}
