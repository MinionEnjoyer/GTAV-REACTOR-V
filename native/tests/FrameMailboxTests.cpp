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
    Check(!mailbox.Submit(pixels.data(), 2, 1, 8, 0), "generation zero must be rejected");
    Check(mailbox.Submit(pixels.data(), 2, 1, 8, 1), "valid frame must be accepted");
    Check(mailbox.SubmittedFrames() == 1, "accepted frame increments submitted count");
    Check(mailbox.ReadNewerThan(0, snapshot), "new generation must be readable");
    Check(snapshot.width == 2 && snapshot.height == 1 && snapshot.stride == 8, "frame dimensions round-trip");
    Check(snapshot.pixels == std::vector<std::uint8_t>(pixels.begin(), pixels.end()), "frame bytes round-trip");
    Check(!mailbox.ReadNewerThan(1, snapshot), "same generation must not be returned twice");
    Check(!mailbox.Submit(pixels.data(), 2, 1, 8, 1), "stale generation must be rejected");
    Check(mailbox.Submit(pixels.data(), 2, 1, 8, 2), "newer generation must be accepted");
    Check(mailbox.ReadNewerThan(1, snapshot) && snapshot.generation == 2, "newer generation replaces the frame");

    rwui::SharedFrameSnapshot sharedSnapshot;
    Check(mailbox.TryReadNewerThanShared(1, sharedSnapshot),
          "staging consumer can lease the newest frame");
    Check(sharedSnapshot.generation == 2, "leased generation round-trips");
    Check(sharedSnapshot.pixels != nullptr &&
              *sharedSnapshot.pixels == std::vector<std::uint8_t>(pixels.begin(), pixels.end()),
          "leased frame retains the producer allocation contents");
    Check(mailbox.SharedReadAttempts() == 1, "shared-read attempts are observable");
    Check(mailbox.SharedFramesRead() == 1, "successful shared reads are observable");
    Check(mailbox.SharedBytesReferenced() == pixels.size(),
          "referenced byte count is observable");
    Check(mailbox.ContendedSharedReads() == 0,
          "uncontended shared read does not report contention");
    Check(mailbox.ReadNewerThan(0, snapshot),
          "shared read preserves the latest frame for renderer rebuilds");
    rwui::SharedFrameSnapshot reboundSnapshot;
    Check(mailbox.TryReadNewerThanShared(0, reboundSnapshot),
          "a rebuilt renderer can lease the same retained generation");
    Check(reboundSnapshot.pixels == sharedSnapshot.pixels,
          "renderer rebuild reuses the immutable producer allocation");
    Check(!mailbox.Submit(pixels.data(), 2, 1, 8, 2),
          "taking a frame does not permit a duplicate generation");

    mailbox.Clear();
    Check(!mailbox.ReadNewerThan(0, snapshot), "clear removes the current frame");
    Check(mailbox.Submit(pixels.data(), 2, 1, 8, 1),
          "clear begins a new mailbox generation sequence");

    if (failures == 0) std::cout << "PASS: FrameMailbox tests\n";
    return failures == 0 ? 0 : 1;
}
