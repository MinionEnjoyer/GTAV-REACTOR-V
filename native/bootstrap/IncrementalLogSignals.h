#pragma once

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace reactorv::bootstrap {

enum class InitialLogContentPolicy : std::uint8_t {
    // Scan an existing tail only when its last write belongs to this process
    // lifetime. This is appropriate for per-launch logs which are truncated
    // or recreated by their owner.
    ScanCurrentSessionTail,

    // Treat the first observed size as a boundary and inspect only subsequent
    // writes. This is appropriate for aggregate append-only logs containing
    // records from older GTA sessions.
    IgnoreExistingContent,
};

struct IncrementalLogCounters {
    std::uint64_t metadataChecks{};
    std::uint64_t contentReads{};
    std::uint64_t bytesRead{};
};

// Incrementally detects a small, fixed set of startup markers without
// repeatedly reading an entire log tail. Detected signals are sticky for the
// lifetime of this object, so a transient sharing violation or log rotation
// cannot regress an already-observed startup stage.
class IncrementalLogSignals final {
public:
    static constexpr std::uint64_t DefaultMaximumReadBytes = 262144;
    static constexpr std::size_t MaximumMarkerLength = 1024;

    IncrementalLogSignals(
        std::filesystem::path path,
        std::uint64_t processStartFileTime,
        std::vector<std::string> markers,
        InitialLogContentPolicy initialContentPolicy =
            InitialLogContentPolicy::ScanCurrentSessionTail,
        std::uint64_t maximumReadBytes = DefaultMaximumReadBytes);

    // Refreshes metadata and reads only new/replaced content. Returns true
    // when at least one previously unseen marker is detected.
    bool Refresh();

    bool HasSignal(std::string_view marker) const;
    const IncrementalLogCounters& Counters() const noexcept;
    const std::filesystem::path& Path() const noexcept;

private:
    struct MarkerState {
        std::string marker;
        bool seen{};
    };

    struct FileIdentity {
        std::uint32_t volumeSerial{};
        std::uint32_t indexHigh{};
        std::uint32_t indexLow{};
        bool valid{};
    };

    bool ReadChangedContent(
        std::uint64_t size,
        std::uint64_t lastWrite,
        const FileIdentity& identity,
        void* fileHandle);
    bool Scan(std::string_view bytes);
    void UpdateBoundaryCarry(std::string_view combined);
    static bool SameIdentity(const FileIdentity& left, const FileIdentity& right);

    std::filesystem::path _path;
    std::uint64_t _processStartFileTime{};
    std::uint64_t _maximumReadBytes{};
    InitialLogContentPolicy _initialContentPolicy{};
    std::vector<MarkerState> _markers;
    std::size_t _longestMarker{};
    std::string _boundaryCarry;
    IncrementalLogCounters _counters;
    FileIdentity _identity;
    std::uint64_t _observedSize{};
    std::uint64_t _observedLastWrite{};
    std::uint64_t _readOffset{};
    bool _initialized{};
    bool _pathPresent{};
};

} // namespace reactorv::bootstrap
