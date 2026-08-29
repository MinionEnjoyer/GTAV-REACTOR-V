#include "FrameMailbox.h"

#include <cstring>
#include <limits>

namespace rwui {

bool FrameMailbox::Submit(
    const void* pixels,
    const std::int32_t width,
    const std::int32_t height,
    const std::int32_t stride,
    const std::uint64_t generation) {
    if (pixels == nullptr || width <= 0 || height <= 0 ||
        width > MaximumDimension || height > MaximumDimension || stride < width * 4) {
        std::scoped_lock lock(mutex_);
        ++droppedFrames_;
        return false;
    }

    const auto byteCount = static_cast<std::size_t>(stride) * static_cast<std::size_t>(height);
    if (byteCount == 0 || byteCount > MaximumBytes ||
        byteCount > static_cast<std::size_t>(std::numeric_limits<std::int32_t>::max())) {
        std::scoped_lock lock(mutex_);
        ++droppedFrames_;
        return false;
    }

    std::vector<std::uint8_t> copy(byteCount);
    std::memcpy(copy.data(), pixels, byteCount);

    std::scoped_lock lock(mutex_);
    if (generation <= latest_.generation && latest_.generation != 0) {
        ++droppedFrames_;
        return false;
    }

    latest_.width = width;
    latest_.height = height;
    latest_.stride = stride;
    latest_.generation = generation;
    latest_.pixels = std::move(copy);
    ++submittedFrames_;
    return true;
}

bool FrameMailbox::ReadNewerThan(const std::uint64_t generation, FrameSnapshot& destination) const {
    std::scoped_lock lock(mutex_);
    if (latest_.generation == 0 || latest_.generation <= generation || latest_.pixels.empty()) {
        return false;
    }

    destination = latest_;
    return true;
}

void FrameMailbox::Clear() {
    std::scoped_lock lock(mutex_);
    latest_ = {};
}

std::uint64_t FrameMailbox::SubmittedFrames() const {
    std::scoped_lock lock(mutex_);
    return submittedFrames_;
}

std::uint64_t FrameMailbox::DroppedFrames() const {
    std::scoped_lock lock(mutex_);
    return droppedFrames_;
}

} // namespace rwui

