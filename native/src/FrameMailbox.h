#pragma once

#include <cstdint>
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

class FrameMailbox final {
public:
    bool Submit(
        const void* pixels,
        std::int32_t width,
        std::int32_t height,
        std::int32_t stride,
        std::uint64_t generation);
    bool ReadNewerThan(std::uint64_t generation, FrameSnapshot& destination) const;
    void Clear();
    std::uint64_t SubmittedFrames() const;
    std::uint64_t DroppedFrames() const;

private:
    static constexpr std::int32_t MaximumDimension = 8192;
    static constexpr std::size_t MaximumBytes = 256u * 1024u * 1024u;

    mutable std::mutex mutex_;
    FrameSnapshot latest_;
    std::uint64_t submittedFrames_{};
    std::uint64_t droppedFrames_{};
};

} // namespace rwui

