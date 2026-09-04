#pragma once

#include <Windows.h>
#include <cstdint>

namespace rwui::transport {

// Publishes the adapter selected by the graphics device captured inside GTA.
// The mapping is process-scoped and carries the publisher creation time so a
// recycled PID can never be mistaken for the target game process.
class AdapterLuidDiscoveryPublisher final {
public:
    ~AdapterLuidDiscoveryPublisher();

    bool Publish(const LUID& adapterLuid) noexcept;
    void Clear() noexcept;
    void Close() noexcept;

private:
    HANDLE mapping_{};
    void* view_{};
    std::uint64_t processCreationTime_{};
    std::uint64_t publicationEpoch_{};
};

bool DiscoverAuthoritativeAdapterLuid(
    std::uint32_t targetProcessId,
    LUID& adapterLuid) noexcept;

} // namespace rwui::transport
