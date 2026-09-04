#pragma once

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <vector>

namespace rwui {

struct FrameSnapshot {
    std::int32_t width{};
    std::int32_t height{};
    std::int32_t stride{};
    std::uint64_t generation{};
    std::vector<std::uint8_t> pixels;
};

struct SharedFrameSnapshot {
    std::int32_t width{};
    std::int32_t height{};
    std::int32_t stride{};
    std::uint64_t generation{};
    std::shared_ptr<const std::vector<std::uint8_t>> pixels;
};

class FrameMailbox final {
public:
    bool Submit(
        const void* pixels,
        std::int32_t width,
        std::int32_t height,
        std::int32_t stride,
        std::uint64_t generation);
    bool ReadNewerThan(std::uint64_t generation, FrameSnapshot& destination) const;
    // Latest-only, fail-open consumer path. The producer-owned immutable
    // allocation is shared without copying its pixels and remains replayable
    // after a renderer/device rebuild.
    bool TryReadNewerThanShared(
        std::uint64_t generation,
        SharedFrameSnapshot& destination) const;
    void Clear();
    std::uint64_t SubmittedFrames() const noexcept;
    std::uint64_t DroppedFrames() const noexcept;
    std::uint64_t SharedReadAttempts() const noexcept;
    std::uint64_t ContendedSharedReads() const noexcept;
    std::uint64_t SharedFramesRead() const noexcept;
    std::uint64_t SharedBytesReferenced() const noexcept;

private:
    static constexpr std::int32_t MaximumDimension = 8192;
    static constexpr std::size_t MaximumBytes = 256u * 1024u * 1024u;

    mutable std::mutex mutex_;
    SharedFrameSnapshot latest_;
    std::uint64_t newestGeneration_{};
    std::atomic<std::uint64_t> submittedFrames_{};
    std::atomic<std::uint64_t> droppedFrames_{};
    mutable std::atomic<std::uint64_t> sharedReadAttempts_{};
    mutable std::atomic<std::uint64_t> contendedSharedReads_{};
    mutable std::atomic<std::uint64_t> sharedFramesRead_{};
    mutable std::atomic<std::uint64_t> sharedBytesReferenced_{};
};

} // namespace rwui
