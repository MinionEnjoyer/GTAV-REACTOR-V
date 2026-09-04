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
    if (pixels == nullptr || width <= 0 || height <= 0 || generation == 0 ||
        width > MaximumDimension || height > MaximumDimension || stride < width * 4) {
        droppedFrames_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }

    const auto byteCount = static_cast<std::size_t>(stride) * static_cast<std::size_t>(height);
    if (byteCount == 0 || byteCount > MaximumBytes ||
        byteCount > static_cast<std::size_t>(std::numeric_limits<std::int32_t>::max())) {
        droppedFrames_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }

    auto copy = std::make_shared<std::vector<std::uint8_t>>(byteCount);
    std::memcpy(copy->data(), pixels, byteCount);

    std::scoped_lock lock(mutex_);
    if (generation <= newestGeneration_ && newestGeneration_ != 0) {
        droppedFrames_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }

    latest_.width = width;
    latest_.height = height;
    latest_.stride = stride;
    latest_.generation = generation;
    latest_.pixels = std::move(copy);
    newestGeneration_ = generation;
    submittedFrames_.fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool FrameMailbox::ReadNewerThan(const std::uint64_t generation, FrameSnapshot& destination) const {
    SharedFrameSnapshot shared;
    {
        std::scoped_lock lock(mutex_);
        if (latest_.generation == 0 || latest_.generation <= generation ||
            latest_.pixels == nullptr || latest_.pixels->empty()) {
            return false;
        }
        shared = latest_;
    }

    destination.width = shared.width;
    destination.height = shared.height;
    destination.stride = shared.stride;
    destination.generation = shared.generation;
    destination.pixels = *shared.pixels;
    return true;
}

bool FrameMailbox::TryReadNewerThanShared(
    const std::uint64_t generation,
    SharedFrameSnapshot& destination) const {
    sharedReadAttempts_.fetch_add(1, std::memory_order_relaxed);
    std::unique_lock lock(mutex_, std::try_to_lock);
    if (!lock.owns_lock()) {
        contendedSharedReads_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    if (latest_.generation == 0 || latest_.generation <= generation ||
        latest_.pixels == nullptr || latest_.pixels->empty()) {
        return false;
    }

    destination = latest_;
    const auto byteCount = destination.pixels->size();
    sharedFramesRead_.fetch_add(1, std::memory_order_relaxed);
    sharedBytesReferenced_.fetch_add(
        static_cast<std::uint64_t>(byteCount),
        std::memory_order_relaxed);
    return true;
}

void FrameMailbox::Clear() {
    std::scoped_lock lock(mutex_);
    latest_ = {};
    newestGeneration_ = 0;
}

std::uint64_t FrameMailbox::SubmittedFrames() const noexcept {
    return submittedFrames_.load(std::memory_order_relaxed);
}

std::uint64_t FrameMailbox::DroppedFrames() const noexcept {
    return droppedFrames_.load(std::memory_order_relaxed);
}

std::uint64_t FrameMailbox::SharedReadAttempts() const noexcept {
    return sharedReadAttempts_.load(std::memory_order_relaxed);
}

std::uint64_t FrameMailbox::ContendedSharedReads() const noexcept {
    return contendedSharedReads_.load(std::memory_order_relaxed);
}

std::uint64_t FrameMailbox::SharedFramesRead() const noexcept {
    return sharedFramesRead_.load(std::memory_order_relaxed);
}

std::uint64_t FrameMailbox::SharedBytesReferenced() const noexcept {
    return sharedBytesReferenced_.load(std::memory_order_relaxed);
}

} // namespace rwui
