#include "AdapterLuidDiscovery.h"

#include <Windows.h>
#include <cstdint>
#include <iostream>

int main() {
    using rwui::transport::AdapterLuidDiscoveryPublisher;
    using rwui::transport::DiscoverAuthoritativeAdapterLuid;

    AdapterLuidDiscoveryPublisher publisher;
    const LUID first{0xF2345678u, -17};
    if (!publisher.Publish(first)) {
        std::cerr << "Could not publish the first adapter LUID.\n";
        return 1;
    }

    LUID discovered{};
    if (!DiscoverAuthoritativeAdapterLuid(
            GetCurrentProcessId(), discovered) ||
        discovered.HighPart != first.HighPart ||
        discovered.LowPart != first.LowPart) {
        std::cerr << "The authoritative adapter LUID did not round-trip.\n";
        return 2;
    }

    const LUID second{0x81234567u, 9};
    if (!publisher.Publish(second) ||
        !DiscoverAuthoritativeAdapterLuid(
            GetCurrentProcessId(), discovered) ||
        discovered.HighPart != second.HighPart ||
        discovered.LowPart != second.LowPart) {
        std::cerr << "A device rebind did not update the existing mapping.\n";
        return 3;
    }

    AdapterLuidDiscoveryPublisher duplicate;
    if (duplicate.Publish(first)) {
        std::cerr << "A second publisher replaced a live process mapping.\n";
        return 4;
    }

    publisher.Clear();
    if (DiscoverAuthoritativeAdapterLuid(
            GetCurrentProcessId(), discovered)) {
        std::cerr << "A cleared adapter mapping remained discoverable.\n";
        return 5;
    }
    if (DiscoverAuthoritativeAdapterLuid(0, discovered)) {
        std::cerr << "PID zero was accepted.\n";
        return 6;
    }
    return 0;
}
