#include "FrameMailbox.h"

#include <array>
#include <cstdint>
#include <iostream>

namespace {

int failures = 0;

void Check(const bool condition, const char* message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        ++failures;
    }
}

} // namespace

int main() {
    rwui::FrameMailbox mailbox;
    rwui::FrameSnapshot snapshot;
    const std::array<std::uint8_t, 8> pixels{1, 2, 3, 4, 5, 6, 7, 8};

    Check(!mailbox.Submit(nullptr, 2, 1, 8, 1), "null frame must be rejected");
    Check(mailbox.DroppedFrames() == 1, "rejected frame increments dropped count");
    Check(mailbox.Submit(pixels.data(), 2, 1, 8, 1), "valid frame must be accepted");
    Check(mailbox.SubmittedFrames() == 1, "accepted frame increments submitted count");
    Check(mailbox.ReadNewerThan(0, snapshot), "new generation must be readable");
    Check(snapshot.width == 2 && snapshot.height == 1 && snapshot.stride == 8, "frame dimensions round-trip");
    Check(snapshot.pixels == std::vector<std::uint8_t>(pixels.begin(), pixels.end()), "frame bytes round-trip");
    Check(!mailbox.ReadNewerThan(1, snapshot), "same generation must not be returned twice");
    Check(!mailbox.Submit(pixels.data(), 2, 1, 8, 1), "stale generation must be rejected");
    Check(mailbox.Submit(pixels.data(), 2, 1, 8, 2), "newer generation must be accepted");
    Check(mailbox.ReadNewerThan(1, snapshot) && snapshot.generation == 2, "newer generation replaces the frame");

    mailbox.Clear();
    Check(!mailbox.ReadNewerThan(0, snapshot), "clear removes the current frame");

    if (failures == 0) std::cout << "PASS: FrameMailbox tests\n";
    return failures == 0 ? 0 : 1;
}

