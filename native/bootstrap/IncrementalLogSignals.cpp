#include "IncrementalLogSignals.h"

#include <windows.h>

#include <algorithm>
#include <limits>
#include <stdexcept>
#include <utility>

namespace reactorv::bootstrap {
namespace {

std::uint64_t FileTimeValue(const FILETIME& value) {
    ULARGE_INTEGER result{};
    result.LowPart = value.dwLowDateTime;
    result.HighPart = value.dwHighDateTime;
    return result.QuadPart;
}

std::uint64_t FileSizeValue(const DWORD high, const DWORD low) {
    ULARGE_INTEGER result{};
    result.HighPart = high;
    result.LowPart = low;
    return result.QuadPart;
}

class ScopedHandle final {
public:
    explicit ScopedHandle(HANDLE handle) noexcept : _handle(handle) {}
    ~ScopedHandle() {
        if (_handle != nullptr && _handle != INVALID_HANDLE_VALUE) {
            CloseHandle(_handle);
        }
    }
    ScopedHandle(const ScopedHandle&) = delete;
    ScopedHandle& operator=(const ScopedHandle&) = delete;
    HANDLE Get() const noexcept { return _handle; }

private:
    HANDLE _handle{};
};

} // namespace

IncrementalLogSignals::IncrementalLogSignals(
    std::filesystem::path path,
    const std::uint64_t processStartFileTime,
    std::vector<std::string> markers,
    const InitialLogContentPolicy initialContentPolicy,
    const std::uint64_t maximumReadBytes)
    : _path(std::move(path)),
      _processStartFileTime(processStartFileTime),
      _maximumReadBytes(maximumReadBytes),
      _initialContentPolicy(initialContentPolicy) {
    if (_path.empty()) {
        throw std::invalid_argument("The startup log path must not be empty.");
    }
    if (_processStartFileTime == 0) {
        throw std::invalid_argument("The process start FILETIME must be positive.");
    }
    if (_maximumReadBytes == 0 ||
        _maximumReadBytes > static_cast<std::uint64_t>(std::numeric_limits<DWORD>::max())) {
        throw std::invalid_argument("The incremental log read limit is invalid.");
    }
    if (markers.empty()) {
        throw std::invalid_argument("At least one startup marker is required.");
    }

    _markers.reserve(markers.size());
    for (auto& marker : markers) {
        if (marker.empty() || marker.size() > MaximumMarkerLength) {
            throw std::invalid_argument("Startup markers must contain 1-1024 bytes.");
        }
        const auto duplicate = std::find_if(
            _markers.begin(),
            _markers.end(),
            [&marker](const MarkerState& existing) {
                return existing.marker == marker;
            });
        if (duplicate != _markers.end()) continue;
        _longestMarker = std::max(_longestMarker, marker.size());
        _markers.push_back({std::move(marker), false});
    }
    if (_markers.empty()) {
        throw std::invalid_argument("At least one unique startup marker is required.");
    }
}

bool IncrementalLogSignals::Refresh() {
    ++_counters.metadataChecks;

    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (!GetFileAttributesExW(_path.c_str(), GetFileExInfoStandard, &attributes) ||
        (attributes.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
        // Completing the baseline while the path is absent is important for
        // append-only logs: a file created later belongs to this process and
        // must not be mistaken for pre-existing aggregate content.
        if (!_initialized) {
            _initialized = true;
            _observedSize = 0;
            _observedLastWrite = 0;
            _readOffset = 0;
        }
        _pathPresent = false;
        return false;
    }

    const auto attributeSize = FileSizeValue(attributes.nFileSizeHigh, attributes.nFileSizeLow);
    const auto attributeWrite = FileTimeValue(attributes.ftLastWriteTime);
    if (_initialized && _pathPresent &&
        attributeSize == _observedSize &&
        attributeWrite == _observedLastWrite) {
        return false;
    }

    ScopedHandle file(CreateFileW(
        _path.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr));
    if (file.Get() == INVALID_HANDLE_VALUE) {
        _pathPresent = false;
        return false;
    }

    BY_HANDLE_FILE_INFORMATION information{};
    if (!GetFileInformationByHandle(file.Get(), &information)) {
        _pathPresent = false;
        return false;
    }
    const auto size = FileSizeValue(information.nFileSizeHigh, information.nFileSizeLow);
    const auto lastWrite = FileTimeValue(information.ftLastWriteTime);
    const FileIdentity identity{
        information.dwVolumeSerialNumber,
        information.nFileIndexHigh,
        information.nFileIndexLow,
        true,
    };

    return ReadChangedContent(size, lastWrite, identity, file.Get());
}

bool IncrementalLogSignals::ReadChangedContent(
    const std::uint64_t size,
    const std::uint64_t lastWrite,
    const FileIdentity& identity,
    void* const fileHandle) {
    const bool firstObservation = !_initialized;
    const bool replaced = _initialized && !SameIdentity(_identity, identity);
    const bool truncated = _initialized && !replaced && size < _readOffset;
    const bool rewrittenInPlace =
        _initialized && !replaced && !truncated &&
        size == _readOffset && lastWrite != _observedLastWrite;

    _initialized = true;
    _pathPresent = true;
    _identity = identity;
    _observedSize = size;
    _observedLastWrite = lastWrite;

    // A current process can never legitimately write before its own creation
    // time. Fail closed rather than applying a clock tolerance that can admit
    // a rapidly restarted session's terminal markers.
    if (lastWrite < _processStartFileTime) {
        _readOffset = size;
        _boundaryCarry.clear();
        return false;
    }

    if (firstObservation &&
        _initialContentPolicy == InitialLogContentPolicy::IgnoreExistingContent) {
        _readOffset = size;
        _boundaryCarry.clear();
        return false;
    }

    std::uint64_t start = _readOffset;
    if (firstObservation || replaced || truncated || rewrittenInPlace) {
        start = size > _maximumReadBytes ? size - _maximumReadBytes : 0;
        _boundaryCarry.clear();
    } else if (size - start > _maximumReadBytes) {
        // Do not join marker fragments across bytes deliberately skipped by
        // the bounded tail policy.
        start = size - _maximumReadBytes;
        _boundaryCarry.clear();
    }

    if (start >= size) {
        _readOffset = size;
        return false;
    }

    LARGE_INTEGER offset{};
    offset.QuadPart = static_cast<LONGLONG>(start);
    auto* const nativeHandle = static_cast<HANDLE>(fileHandle);
    if (!SetFilePointerEx(nativeHandle, offset, nullptr, FILE_BEGIN)) {
        return false;
    }

    const auto requested = static_cast<DWORD>(size - start);
    std::string bytes(requested, '\0');
    DWORD bytesRead{};
    if (!ReadFile(nativeHandle, bytes.data(), requested, &bytesRead, nullptr)) {
        return false;
    }
    bytes.resize(bytesRead);
    ++_counters.contentReads;
    _counters.bytesRead += bytesRead;
    _readOffset = start + bytesRead;

    if (_readOffset < size) {
        // The writer grew the file while this sample was in flight. Retain the
        // exact consumed boundary and pick up the remainder on the next tick.
        _observedSize = _readOffset;
    }
    return Scan(bytes);
}

bool IncrementalLogSignals::Scan(const std::string_view bytes) {
    std::string combined;
    combined.reserve(_boundaryCarry.size() + bytes.size());
    combined.append(_boundaryCarry);
    combined.append(bytes.data(), bytes.size());

    bool discovered = false;
    for (auto& marker : _markers) {
        if (!marker.seen && combined.find(marker.marker) != std::string::npos) {
            marker.seen = true;
            discovered = true;
        }
    }
    UpdateBoundaryCarry(combined);
    return discovered;
}

void IncrementalLogSignals::UpdateBoundaryCarry(const std::string_view combined) {
    const auto maximumCarry = _longestMarker > 0 ? _longestMarker - 1 : 0;
    const auto carry = std::min(maximumCarry, combined.size());
    _boundaryCarry.assign(combined.substr(combined.size() - carry));
}

bool IncrementalLogSignals::HasSignal(const std::string_view marker) const {
    const auto found = std::find_if(
        _markers.begin(),
        _markers.end(),
        [marker](const MarkerState& state) { return state.marker == marker; });
    return found != _markers.end() && found->seen;
}

const IncrementalLogCounters& IncrementalLogSignals::Counters() const noexcept {
    return _counters;
}

const std::filesystem::path& IncrementalLogSignals::Path() const noexcept {
    return _path;
}

bool IncrementalLogSignals::SameIdentity(
    const FileIdentity& left,
    const FileIdentity& right) {
    return left.valid && right.valid &&
        left.volumeSerial == right.volumeSerial &&
        left.indexHigh == right.indexHigh &&
        left.indexLow == right.indexLow;
}

} // namespace reactorv::bootstrap
