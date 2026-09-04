#pragma once

#include "LegacyStartupShadowProbe.h"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <stop_token>
#include <thread>

namespace reactorv::bootstrap {

inline constexpr std::size_t LegacyShadowMaximumDiscoveryAttempts = 32;

struct LegacyShadowDiscoveryAttempt {
    LegacyShadowDiscoveryReceipt receipt{};
    std::uint32_t attempt{};
    std::uint64_t durationMilliseconds{};
    bool final{};
    bool exhausted{};
};

[[nodiscard]] bool IsLegacyShadowDiscoveryRetryable(
    LegacyShadowDiscoveryStatus status) noexcept;
[[nodiscard]] std::uint32_t LegacyShadowDiscoveryRetryDelayMilliseconds(
    std::uint32_t completedAttempts) noexcept;

using LegacyShadowDiscoverFunction = LegacyShadowDiscoveryReceipt(*)(
    void* context,
    const std::stop_token* stopToken) noexcept;

// Runs expensive runtime-image discovery away from Bootstrap's message/input
// worker. The discovery thread owns the probe until Completed is published and
// Join has transferred exclusive ownership back to the caller.
class LegacyStartupShadowRunner final {
public:
    LegacyStartupShadowRunner(
        void* context,
        LegacyShadowDiscoverFunction discover) noexcept;
    ~LegacyStartupShadowRunner();

    LegacyStartupShadowRunner(const LegacyStartupShadowRunner&) = delete;
    LegacyStartupShadowRunner& operator=(
        const LegacyStartupShadowRunner&) = delete;

    [[nodiscard]] bool Start() noexcept;
    void RequestStop() noexcept;
    void Join() noexcept;
    [[nodiscard]] bool IsCompleted() const noexcept;
    [[nodiscard]] bool IsReady() const noexcept;
    [[nodiscard]] bool TryReadAttempt(
        std::size_t index,
        LegacyShadowDiscoveryAttempt& destination) const noexcept;
    [[nodiscard]] std::size_t PublishedAttemptCount() const noexcept;

private:
    void Run(std::stop_token stopToken) noexcept;

    void* _context{};
    LegacyShadowDiscoverFunction _discover{};
    std::array<
        LegacyShadowDiscoveryAttempt,
        LegacyShadowMaximumDiscoveryAttempts> _attempts{};
    std::atomic<std::size_t> _publishedAttempts{0};
    std::atomic<bool> _started{false};
    std::atomic<bool> _completed{false};
    std::atomic<bool> _ready{false};
    std::jthread _worker{};
};

} // namespace reactorv::bootstrap
