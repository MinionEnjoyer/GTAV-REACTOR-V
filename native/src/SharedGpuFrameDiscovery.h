#pragma once

#include "SharedGpuFrameChannel.h"

#include <Windows.h>
#include <cstdint>

namespace rwui::transport {

class SharedGpuFrameDiscoveryPublisher final {
public:
    ~SharedGpuFrameDiscoveryPublisher();
    bool Publish(const SharedGpuFrameChannelEndpoint& endpoint) noexcept;
    void Close() noexcept;

private:
    HANDLE mapping_{};
    void* view_{};
};

bool DiscoverSharedGpuFrameProducer(
    std::uint32_t targetConsumerProcessId,
    SharedGpuFrameChannelEndpoint& endpoint) noexcept;

} // namespace rwui::transport
