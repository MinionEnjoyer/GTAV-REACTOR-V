#pragma once
#include <algorithm>
#include <cstdint>

namespace rwui {
constexpr int StartupStatusWidth = 560;
constexpr int StartupStatusHeight = 68;
inline bool ValidStartupStatusFrame(int width, int height, int stride) noexcept {
    return width == StartupStatusWidth && height == StartupStatusHeight && stride == width * 4;
}
struct StartupStatusBounds { float x, y, width, height; };
inline StartupStatusBounds StartupStatusPlacement(int width, int height) noexcept {
    const float scale = std::max(0.0f, std::min({1.0f, (width - 48.0f) / StartupStatusWidth,
        (height - 48.0f) / StartupStatusHeight}));
    const float w = StartupStatusWidth * scale, h = StartupStatusHeight * scale;
    return {std::max(0.0f, width - w - 24.0f), std::min(24.0f, std::max(0.0f, height - h)), w, h};
}
inline bool ShouldRenderStartupStatus(bool active, bool menuVisible, bool legacy) noexcept {
    return active && !menuVisible && legacy;
}
}
