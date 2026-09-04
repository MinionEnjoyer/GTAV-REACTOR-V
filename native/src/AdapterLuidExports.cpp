#include "AdapterLuidDiscovery.h"

#include <Windows.h>
#include <cstdint>

extern "C" __declspec(dllexport) int __cdecl
RWUI_QueryTargetAdapterLuid(
    const std::uint32_t targetProcessId,
    std::int32_t* const highPart,
    std::uint32_t* const lowPart) noexcept {
    if (targetProcessId == 0 || highPart == nullptr || lowPart == nullptr) {
        return 0;
    }
    LUID luid{};
    if (!rwui::transport::DiscoverAuthoritativeAdapterLuid(
            targetProcessId, luid)) return 0;
    *highPart = luid.HighPart;
    *lowPart = luid.LowPart;
    return 1;
}
