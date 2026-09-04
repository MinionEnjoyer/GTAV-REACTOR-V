#pragma once

#include <cstddef>
#include <cstdint>
#include <type_traits>

// ReactorV shared-GPU frame protocol, version 1.
//
// The descriptor is intentionally a fixed-size, pointer-free wire type. The
// producer sends producer-local NT handle values over an authenticated IPC
// channel. The consumer must duplicate those handles from the already
// authenticated producer process before opening the resource. A raw value in
// this structure is never a consumer-local HANDLE and must never be passed
// directly to OpenSharedResource/OpenSharedHandle.
namespace rwui::transport {

inline constexpr std::uint32_t SharedGpuFrameMagic = 0x46475652u; // "RVGF"
inline constexpr std::uint16_t SharedGpuFrameVersionMajor = 1;
inline constexpr std::uint16_t SharedGpuFrameVersionMinor = 1;
// Diagnostic 1.2 is explicitly typed; existing GPU descriptors stay at 1.1.
inline constexpr std::uint16_t CpuFrameVersionMinor = 2;
inline constexpr std::uint64_t CpuFrameMaximumBytes = 32ull * 1024ull * 1024ull;
inline constexpr std::uint32_t SharedGpuFrameMaximumDimension = 8192;
inline constexpr std::uint64_t SharedGpuFrameMaximumBytes =
    128ull * 1024ull * 1024ull;
inline constexpr std::uint32_t SharedGpuFrameMaximumSlots = 3;
inline constexpr std::uint32_t SharedGpuFrameDescriptorV1ByteSize = 152;

enum class SharedGpuPixelFormat : std::uint32_t {
    Unknown = 0,
    // Numeric values deliberately match DXGI_FORMAT so the import boundary
    // can compare the opened resource without a translation table.
    Bgra8Unorm = 87,
    Bgra8UnormSrgb = 91,
};

enum class SharedGpuSynchronization : std::uint32_t {
    None = 0,
    D3d11KeyedMutex = 1,
    D3d12SharedFence = 2,
    // sharedTextureHandle is an NT file-mapping handle, never a GPU handle.
    // Packed BGRA rows remain immutable until the authenticated channel ACK.
    CpuBgraMapping = 3,
};

enum class SharedGpuFrameFlags : std::uint32_t {
    None = 0,
    // Handle values belong to producerProcessId. The consumer duplicates
    // them from that process; they are not directly usable in the consumer.
    ProducerLocalNtHandles = 1u << 0,
    PremultipliedAlpha = 1u << 1,
    TopLeftOrigin = 1u << 2,
};

constexpr SharedGpuFrameFlags operator|(
    const SharedGpuFrameFlags left,
    const SharedGpuFrameFlags right) noexcept {
    return static_cast<SharedGpuFrameFlags>(
        static_cast<std::uint32_t>(left) |
        static_cast<std::uint32_t>(right));
}

constexpr std::uint32_t SharedGpuFrameRequiredFlags =
    static_cast<std::uint32_t>(
        SharedGpuFrameFlags::ProducerLocalNtHandles |
        SharedGpuFrameFlags::PremultipliedAlpha |
        SharedGpuFrameFlags::TopLeftOrigin);

// producerCreationTime and consumerCreationTime are the 64-bit FILETIME values
// returned by GetProcessTimes. Together with their PIDs they prevent a stale
// descriptor from being accepted after Windows reuses either endpoint PID.
// sessionIdHigh/sessionIdLow are a random 128-bit value negotiated by the
// authenticated IPC session.
//
// resourceEpoch changes whenever a slot is backed by a newly created shared
// texture/handle. generation changes for every published frame. slotCount is
// bounded to keep the producer's retained GPU allocation count deterministic.
struct alignas(8) SharedGpuFrameDescriptorV1 final {
    std::uint32_t magic{SharedGpuFrameMagic};
    std::uint16_t versionMajor{SharedGpuFrameVersionMajor};
    std::uint16_t versionMinor{SharedGpuFrameVersionMinor};
    std::uint32_t byteSize{SharedGpuFrameDescriptorV1ByteSize};
    std::uint32_t flags{SharedGpuFrameRequiredFlags};

    std::uint32_t producerProcessId{};
    std::uint32_t consumerProcessId{};
    std::uint64_t producerCreationTime{};
    std::uint64_t sessionIdHigh{};
    std::uint64_t sessionIdLow{};

    std::uint64_t generation{};
    std::uint64_t resourceEpoch{};
    std::uint32_t slotIndex{};
    std::uint32_t slotCount{};

    std::uint32_t width{};
    std::uint32_t height{};
    SharedGpuPixelFormat pixelFormat{SharedGpuPixelFormat::Unknown};
    SharedGpuSynchronization synchronization{SharedGpuSynchronization::None};

    // These are producer-local NT handle values. For D3D11 keyed-mutex
    // transport sharedFenceHandle must be zero. For D3D12 shared-fence
    // transport it names the producer-local shared fence handle.
    std::uint64_t sharedTextureHandle{};
    std::uint64_t sharedFenceHandle{};
    std::uint64_t acquireValue{};
    std::uint64_t releaseValue{};

    // Added in protocol 1.1 without changing the fixed wire size.
    std::uint64_t consumerCreationTime{};
    std::uint64_t reserved[3]{};
};

static_assert(std::is_standard_layout_v<SharedGpuFrameDescriptorV1>);
static_assert(std::is_trivially_copyable_v<SharedGpuFrameDescriptorV1>);
static_assert(sizeof(SharedGpuFrameDescriptorV1) ==
    SharedGpuFrameDescriptorV1ByteSize);
static_assert(sizeof(SharedGpuFrameDescriptorV1) % sizeof(std::uint64_t) == 0);

} // namespace rwui::transport
