#include "SharedGpuFrameDiscovery.h"

#include "SharedGpuFrameD3D11.h"

#include <Windows.h>
#include <array>
#include <cwchar>

namespace rwui::transport {
namespace {

constexpr std::uint32_t DiscoveryMagic = 0x44475652u; // "RVGD"
constexpr std::uint16_t DiscoveryMajor = 1;
constexpr std::uint16_t DiscoveryMinor = 2;

struct alignas(8) DiscoveryRecord final {
    volatile LONG ready{};
    std::uint32_t magic{DiscoveryMagic};
    std::uint16_t major{DiscoveryMajor};
    std::uint16_t minor{DiscoveryMinor};
    std::uint32_t byteSize{80};
    std::uint32_t producerProcessId{};
    std::uint32_t consumerProcessId{};
    volatile LONG reservedFlags{};
    std::uint64_t producerCreationTime{};
    std::uint64_t consumerCreationTime{};
    std::uint64_t sessionIdHigh{};
    std::uint64_t sessionIdLow{};
    volatile LONG64 publicationEpoch{};
    std::uint64_t reserved{};
};
static_assert(sizeof(DiscoveryRecord) == 80);
static_assert(offsetof(DiscoveryRecord, publicationEpoch) % 8 == 0);

using DiscoveryName = std::array<wchar_t, 80>;

bool FormatName(
    const std::uint32_t targetPid,
    DiscoveryName& buffer) noexcept {
    buffer = {};
    const auto count = std::swprintf(
        buffer.data(), buffer.size(),
        L"Local\\ReactorV.FrameDiscovery.v1.%08X", targetPid);
    return count > 0 && static_cast<std::size_t>(count) < buffer.size();
}

bool Valid(
    const DiscoveryRecord& value,
    const std::uint32_t targetPid,
    const SharedGpuFrameChannelEndpoint* const expected = nullptr) {
    return value.ready == 1 && value.magic == DiscoveryMagic &&
        value.major == DiscoveryMajor && value.minor == DiscoveryMinor &&
        value.byteSize == sizeof(DiscoveryRecord) &&
        value.producerProcessId != 0 &&
        value.consumerProcessId == targetPid &&
        value.producerCreationTime != 0 &&
        value.consumerCreationTime != 0 &&
        (value.sessionIdHigh != 0 || value.sessionIdLow != 0) &&
        value.reservedFlags == 0 &&
        value.publicationEpoch > 0 && value.reserved == 0 &&
        (expected == nullptr ||
            (value.producerProcessId == expected->producerProcessId &&
             value.consumerProcessId == expected->targetConsumerProcessId &&
             value.producerCreationTime == expected->producerCreationTime &&
             value.consumerCreationTime ==
                expected->targetConsumerCreationTime &&
             value.sessionIdHigh == expected->sessionIdHigh &&
             value.sessionIdLow == expected->sessionIdLow));
}

void CopyRecord(
    const DiscoveryRecord* const record,
    DiscoveryRecord& snapshot) noexcept {
    snapshot = {};
    if (record == nullptr || record->ready != 1) return;
    MemoryBarrier();
    snapshot.ready = record->ready;
    snapshot.magic = record->magic;
    snapshot.major = record->major;
    snapshot.minor = record->minor;
    snapshot.byteSize = record->byteSize;
    snapshot.producerProcessId = record->producerProcessId;
    snapshot.consumerProcessId = record->consumerProcessId;
    snapshot.reservedFlags = record->reservedFlags;
    snapshot.producerCreationTime = record->producerCreationTime;
    snapshot.consumerCreationTime = record->consumerCreationTime;
    snapshot.sessionIdHigh = record->sessionIdHigh;
    snapshot.sessionIdLow = record->sessionIdLow;
    snapshot.publicationEpoch = record->publicationEpoch;
    snapshot.reserved = record->reserved;
}

} // namespace

SharedGpuFrameDiscoveryPublisher::~SharedGpuFrameDiscoveryPublisher() {
    Close();
}

bool SharedGpuFrameDiscoveryPublisher::Publish(
    const SharedGpuFrameChannelEndpoint& endpoint) noexcept {
    Close();
    if (endpoint.producerProcessId != GetCurrentProcessId() ||
        endpoint.targetConsumerProcessId == 0 ||
        endpoint.targetConsumerCreationTime == 0) return false;
    DiscoveryName name{};
    if (!FormatName(endpoint.targetConsumerProcessId, name)) return false;
    mapping_ = CreateFileMappingW(
        INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
        sizeof(DiscoveryRecord), name.data());
    if (mapping_ == nullptr || GetLastError() == ERROR_ALREADY_EXISTS) {
        Close();
        return false;
    }
    view_ = MapViewOfFile(
        mapping_, FILE_MAP_WRITE, 0, 0, sizeof(DiscoveryRecord));
    if (view_ == nullptr) {
        Close();
        return false;
    }
    auto* record = static_cast<DiscoveryRecord*>(view_);
    *record = {};
    record->magic = DiscoveryMagic;
    record->major = DiscoveryMajor;
    record->minor = DiscoveryMinor;
    record->byteSize = sizeof(DiscoveryRecord);
    record->producerProcessId = endpoint.producerProcessId;
    record->consumerProcessId = endpoint.targetConsumerProcessId;
    record->producerCreationTime = endpoint.producerCreationTime;
    record->consumerCreationTime = endpoint.targetConsumerCreationTime;
    record->sessionIdHigh = endpoint.sessionIdHigh;
    record->sessionIdLow = endpoint.sessionIdLow;
    record->reservedFlags = 0;
    record->publicationEpoch = 1;
    MemoryBarrier();
    InterlockedExchange(&record->ready, 1);
    return true;
}

void SharedGpuFrameDiscoveryPublisher::Close() noexcept {
    if (view_ != nullptr) UnmapViewOfFile(view_);
    if (mapping_ != nullptr) CloseHandle(mapping_);
    view_ = nullptr;
    mapping_ = nullptr;
}

bool DiscoverSharedGpuFrameProducer(
    const std::uint32_t targetConsumerProcessId,
    SharedGpuFrameChannelEndpoint& endpoint) noexcept {
    endpoint = {};
    if (targetConsumerProcessId == 0) return false;
    DiscoveryName name{};
    if (!FormatName(targetConsumerProcessId, name)) return false;
    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, name.data());
    if (mapping == nullptr) return false;
    const void* view = MapViewOfFile(
        mapping, FILE_MAP_READ, 0, 0, sizeof(DiscoveryRecord));
    if (view == nullptr) {
        CloseHandle(mapping);
        return false;
    }
    DiscoveryRecord snapshot{};
    CopyRecord(static_cast<const DiscoveryRecord*>(view), snapshot);
    UnmapViewOfFile(view);
    CloseHandle(mapping);
    if (!Valid(snapshot, targetConsumerProcessId)) return false;
    WindowsProcessIdentity identity{};
    if (!QueryWindowsProcessIdentity(snapshot.producerProcessId, identity) ||
        identity.creationTime != snapshot.producerCreationTime) return false;
    WindowsProcessIdentity consumerIdentity{};
    if (!QueryWindowsProcessIdentity(
            snapshot.consumerProcessId, consumerIdentity) ||
        consumerIdentity.creationTime != snapshot.consumerCreationTime) {
        return false;
    }
    endpoint = {
        snapshot.producerProcessId,
        snapshot.producerCreationTime,
        snapshot.consumerProcessId,
        snapshot.consumerCreationTime,
        snapshot.sessionIdHigh,
        snapshot.sessionIdLow,
    };
    return true;
}

} // namespace rwui::transport
