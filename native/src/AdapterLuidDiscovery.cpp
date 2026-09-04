#include "AdapterLuidDiscovery.h"

#include <Windows.h>
#include <array>
#include <cstddef>
#include <cwchar>

namespace rwui::transport {
namespace {

constexpr std::uint32_t AdapterDiscoveryMagic = 0x41475652u; // "RVGA"
constexpr std::uint16_t AdapterDiscoveryMajor = 1;
constexpr std::uint16_t AdapterDiscoveryMinor = 0;

struct alignas(8) AdapterDiscoveryRecord final {
    volatile LONG ready{};
    std::uint32_t magic{AdapterDiscoveryMagic};
    std::uint16_t major{AdapterDiscoveryMajor};
    std::uint16_t minor{AdapterDiscoveryMinor};
    std::uint32_t byteSize{64};
    std::uint32_t publisherProcessId{};
    std::int32_t adapterHighPart{};
    std::uint32_t adapterLowPart{};
    volatile LONG reservedFlags{};
    std::uint64_t publisherCreationTime{};
    volatile LONG64 publicationEpoch{};
    std::uint64_t reserved[2]{};
};
static_assert(sizeof(AdapterDiscoveryRecord) == 64);
static_assert(offsetof(AdapterDiscoveryRecord, publisherCreationTime) % 8 == 0);
static_assert(offsetof(AdapterDiscoveryRecord, publicationEpoch) % 8 == 0);

using DiscoveryName = std::array<wchar_t, 80>;

bool FormatName(
    const std::uint32_t targetProcessId,
    DiscoveryName& buffer) noexcept {
    buffer = {};
    const auto count = std::swprintf(
        buffer.data(), buffer.size(),
        L"Local\\ReactorV.AdapterLuid.v1.%08X", targetProcessId);
    return count > 0 && static_cast<std::size_t>(count) < buffer.size();
}

bool QueryProcessCreationTime(
    const std::uint32_t processId,
    std::uint64_t& creationTime) noexcept {
    creationTime = 0;
    if (processId == 0) return false;
    HANDLE process = OpenProcess(
        PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
    if (process == nullptr) return false;
    FILETIME created{}, exited{}, kernel{}, user{};
    const bool queried = GetProcessTimes(
        process, &created, &exited, &kernel, &user) != FALSE;
    CloseHandle(process);
    if (!queried) return false;
    ULARGE_INTEGER value{};
    value.LowPart = created.dwLowDateTime;
    value.HighPart = created.dwHighDateTime;
    creationTime = value.QuadPart;
    return creationTime != 0;
}

bool CopyStableRecord(
    const AdapterDiscoveryRecord* const record,
    AdapterDiscoveryRecord& snapshot) noexcept {
    snapshot = {};
    if (record == nullptr || record->ready != 1) return false;
    MemoryBarrier();
    const auto initialEpoch = record->publicationEpoch;
    snapshot.ready = record->ready;
    snapshot.magic = record->magic;
    snapshot.major = record->major;
    snapshot.minor = record->minor;
    snapshot.byteSize = record->byteSize;
    snapshot.publisherProcessId = record->publisherProcessId;
    snapshot.adapterHighPart = record->adapterHighPart;
    snapshot.adapterLowPart = record->adapterLowPart;
    snapshot.reservedFlags = record->reservedFlags;
    snapshot.publisherCreationTime = record->publisherCreationTime;
    snapshot.publicationEpoch = initialEpoch;
    snapshot.reserved[0] = record->reserved[0];
    snapshot.reserved[1] = record->reserved[1];
    MemoryBarrier();
    return record->ready == 1 && record->publicationEpoch == initialEpoch;
}

bool Valid(
    const AdapterDiscoveryRecord& record,
    const std::uint32_t targetProcessId,
    const std::uint64_t targetCreationTime) noexcept {
    return record.ready == 1 &&
        record.magic == AdapterDiscoveryMagic &&
        record.major == AdapterDiscoveryMajor &&
        record.minor == AdapterDiscoveryMinor &&
        record.byteSize == sizeof(AdapterDiscoveryRecord) &&
        record.publisherProcessId == targetProcessId &&
        record.publisherCreationTime == targetCreationTime &&
        record.publicationEpoch > 0 &&
        record.reservedFlags == 0 &&
        record.reserved[0] == 0 && record.reserved[1] == 0;
}

} // namespace

AdapterLuidDiscoveryPublisher::~AdapterLuidDiscoveryPublisher() {
    Close();
}

bool AdapterLuidDiscoveryPublisher::Publish(
    const LUID& adapterLuid) noexcept {
    const auto processId = GetCurrentProcessId();
    std::uint64_t currentCreationTime{};
    if (!QueryProcessCreationTime(processId, currentCreationTime)) return false;

    if (mapping_ == nullptr || view_ == nullptr) {
        DiscoveryName name{};
        if (!FormatName(processId, name)) return false;
        mapping_ = CreateFileMappingW(
            INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
            sizeof(AdapterDiscoveryRecord), name.data());
        if (mapping_ == nullptr || GetLastError() == ERROR_ALREADY_EXISTS) {
            Close();
            return false;
        }
        view_ = MapViewOfFile(
            mapping_, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0,
            sizeof(AdapterDiscoveryRecord));
        if (view_ == nullptr) {
            Close();
            return false;
        }
        processCreationTime_ = currentCreationTime;
        publicationEpoch_ = 0;
    }

    if (processCreationTime_ != currentCreationTime) {
        Close();
        return false;
    }

    auto* const record = static_cast<AdapterDiscoveryRecord*>(view_);
    InterlockedExchange(&record->ready, 0);
    MemoryBarrier();
    record->magic = AdapterDiscoveryMagic;
    record->major = AdapterDiscoveryMajor;
    record->minor = AdapterDiscoveryMinor;
    record->byteSize = sizeof(AdapterDiscoveryRecord);
    record->publisherProcessId = processId;
    record->adapterHighPart = adapterLuid.HighPart;
    record->adapterLowPart = adapterLuid.LowPart;
    record->reservedFlags = 0;
    record->publisherCreationTime = processCreationTime_;
    record->publicationEpoch = static_cast<LONG64>(++publicationEpoch_);
    record->reserved[0] = 0;
    record->reserved[1] = 0;
    MemoryBarrier();
    InterlockedExchange(&record->ready, 1);
    return true;
}

void AdapterLuidDiscoveryPublisher::Clear() noexcept {
    if (view_ == nullptr) return;
    auto* const record = static_cast<AdapterDiscoveryRecord*>(view_);
    InterlockedExchange(&record->ready, 0);
    MemoryBarrier();
    record->adapterHighPart = 0;
    record->adapterLowPart = 0;
    record->publicationEpoch = static_cast<LONG64>(++publicationEpoch_);
}

void AdapterLuidDiscoveryPublisher::Close() noexcept {
    Clear();
    if (view_ != nullptr) UnmapViewOfFile(view_);
    if (mapping_ != nullptr) CloseHandle(mapping_);
    view_ = nullptr;
    mapping_ = nullptr;
    processCreationTime_ = 0;
    publicationEpoch_ = 0;
}

bool DiscoverAuthoritativeAdapterLuid(
    const std::uint32_t targetProcessId,
    LUID& adapterLuid) noexcept {
    adapterLuid = {};
    std::uint64_t expectedCreationTime{};
    if (!QueryProcessCreationTime(
            targetProcessId, expectedCreationTime)) return false;

    DiscoveryName name{};
    if (!FormatName(targetProcessId, name)) return false;
    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, name.data());
    if (mapping == nullptr) return false;
    const void* const view = MapViewOfFile(
        mapping, FILE_MAP_READ, 0, 0, sizeof(AdapterDiscoveryRecord));
    if (view == nullptr) {
        CloseHandle(mapping);
        return false;
    }

    AdapterDiscoveryRecord snapshot{};
    const bool stable = CopyStableRecord(
        static_cast<const AdapterDiscoveryRecord*>(view), snapshot);
    UnmapViewOfFile(view);
    CloseHandle(mapping);
    if (!stable || !Valid(
            snapshot, targetProcessId, expectedCreationTime)) return false;

    // Re-read identity after consuming the mapping. This closes the race where
    // the target exits and Windows recycles its PID during discovery.
    std::uint64_t confirmedCreationTime{};
    if (!QueryProcessCreationTime(
            targetProcessId, confirmedCreationTime) ||
        confirmedCreationTime != expectedCreationTime) return false;

    adapterLuid.HighPart = snapshot.adapterHighPart;
    adapterLuid.LowPart = snapshot.adapterLowPart;
    return true;
}

} // namespace rwui::transport
