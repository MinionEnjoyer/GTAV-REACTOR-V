#include "NativeDiagnosticTrace.h"
#include <atomic>
#include <iostream>
#include <thread>
#include <vector>

int main() {
    rwui::NativeDiagnosticBuffer buffer;
    rwui::NativeDiagnosticEvent record;
    if (buffer.Pop(record)) return 1;
    for (std::size_t i = 0; i < buffer.Capacity; ++i) {
        if (!buffer.Push({"test", 0, i, i * 3, 42, -7, nullptr})) return 2;
    }
    if (buffer.Push({"overflow"}) || buffer.TakeDropped() != 1 || buffer.TakeDropped() != 0) return 3;
    for (std::size_t i = 0; i < buffer.Capacity; ++i) {
        if (!buffer.Pop(record) || record.sequence != i + 1 || record.tick != i ||
            record.detail != i * 3 || record.thread != 42 || record.result != -7) return 4;
    }
    if (buffer.Pop(record)) return 5;

    // Concurrent producer/consumer coherence and exact loss accounting.
    constexpr int Producers = 4, PerProducer = 10000;
    std::atomic_int finished{};
    std::vector<std::thread> producers;
    for (int p = 0; p < Producers; ++p) producers.emplace_back([&, p] {
        for (int i = 0; i < PerProducer; ++i) {
            const auto value = static_cast<std::uint64_t>(p * PerProducer + i);
            buffer.Push({"parallel", 0, value, value * 3, 42, -7, nullptr});
        }
        ++finished;
    });
    std::uint64_t consumed{}, sequence = buffer.Capacity;
    bool coherent = true;
    while (finished != Producers) {
        if (buffer.Pop(record)) {
            coherent &= record.sequence == ++sequence && record.detail == record.tick * 3 &&
                record.thread == 42 && record.result == -7;
            ++consumed;
        } else std::this_thread::yield();
    }
    for (auto& producer : producers) producer.join();
    while (buffer.Pop(record)) {
        coherent &= record.sequence == ++sequence && record.detail == record.tick * 3;
        ++consumed;
    }
    if (!coherent || consumed + buffer.TakeDropped() != Producers * PerProducer) return 6;
    rwui::NativeDiagnosticSampleGate gate;
    for (int i = 0; i < 8; ++i) if (!gate.Take(100)) return 7;
    if (gate.Take(100) || gate.Take(5099) || !gate.Take(5100) || gate.Take(5100)) return 8;
    if (!gate.Take(10100)) return 9;
    std::cout << "PASS: bounded FIFO, loss accounting, concurrent coherence, rate limit\n";
    return 0;
}
