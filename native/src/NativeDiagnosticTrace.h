#pragma once
#include <windows.h>
#include <array>
#include <atomic>
#include <cstdint>

namespace rwui {
struct NativeDiagnosticEvent {
    const char* event{}; // static literals only; no borrowed strings/resources
    std::uint64_t sequence{}, tick{}, detail{};
    DWORD thread{};
    int result{};
    const void* target{}; // identity only, never dereferenced by the writer
};

// Fixed storage, no allocation, file I/O, mutex wait, or retry in a callback.
// Contention/full queue drops diagnostics, never delays or changes rendering.
class NativeDiagnosticBuffer final {
public:
    static constexpr std::size_t Capacity = 128;
    bool Push(NativeDiagnosticEvent event) noexcept {
        if (gate_.test_and_set(std::memory_order_acquire)) {
            dropped_.fetch_add(1, std::memory_order_relaxed); return false;
        }
        const bool accepted = count_ < Capacity;
        if (accepted) {
            event.sequence = ++sequence_;
            events_[(read_ + count_) % Capacity] = event;
            ++count_;
        } else dropped_.fetch_add(1, std::memory_order_relaxed);
        gate_.clear(std::memory_order_release);
        return accepted;
    }
    bool Pop(NativeDiagnosticEvent& event) noexcept {
        if (gate_.test_and_set(std::memory_order_acquire)) return false;
        const bool available = count_ != 0;
        if (available) {
            event = events_[read_]; read_ = (read_ + 1) % Capacity; --count_;
        }
        gate_.clear(std::memory_order_release);
        return available;
    }
    std::uint64_t TakeDropped() noexcept { return dropped_.exchange(0); }
private:
    std::array<NativeDiagnosticEvent, Capacity> events_{};
    std::atomic_flag gate_ = ATOMIC_FLAG_INIT;
    std::atomic_uint64_t dropped_{};
    std::size_t read_{}, count_{};
    std::uint64_t sequence_{};
};

class NativeDiagnosticSampleGate final {
public:
    bool Take(std::uint64_t now) noexcept {
        if (count_.fetch_add(1, std::memory_order_relaxed) < 8) {
            last_.store(now, std::memory_order_relaxed); return true;
        }
        auto last = last_.load(std::memory_order_relaxed);
        return now >= last && now - last >= 5000 &&
            last_.compare_exchange_strong(last, now, std::memory_order_relaxed);
    }
private:
    std::atomic_uint64_t count_{}, last_{};
};

inline NativeDiagnosticBuffer nativeDiagnosticBuffer;
inline NativeDiagnosticSampleGate presentDiagnosticGate;
inline NativeDiagnosticSampleGate prepareDiagnosticGate;
inline NativeDiagnosticSampleGate gpuCopyDiagnosticGate;

inline void RecordNativeDiagnostic(const char* event, int result = 0,
    const void* target = nullptr, std::uint64_t detail = 0) noexcept {
    nativeDiagnosticBuffer.Push({event, 0, GetTickCount64(), detail,
        GetCurrentThreadId(), result, target});
}

// Worker/control paths only. Records carry their original thread and monotonic
// time; log-line time/thread identify the later drain, not the callback itself.
void DrainNativeDiagnostics() noexcept;
}
