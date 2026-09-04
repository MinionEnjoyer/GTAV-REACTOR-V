#pragma once
#include "SharedGpuFrameD3D11.h"
#include <atomic>
#include <filesystem>

namespace rwui::transport {
bool LegacyCpuFramesEnabled(DWORD targetPid) noexcept;
std::filesystem::path CpuFrameLogPath(DWORD targetPid, const wchar_t* role) noexcept;
class CpuFrameTrace {
public:
    void SetPath(std::filesystem::path path) { path_ = std::move(path); }
    void Record(HRESULT hr, UINT64 generation, UINT64 bytes, UINT64 elapsedUs,
        UINT64 dropped = 0, const char* stage = "upload", bool recovered = false) noexcept;
private:
    std::filesystem::path path_;
    UINT64 count_{}, failures_{}, bytes_{}, totalUs_{}, maxUs_{}, nextLog_{};
};
// A short device stall may discard a frame, but cannot cause an unlimited
// stream of device allocations. Success does not replenish the window budget.
class CpuFrameRecoveryBudget {
public:
    bool TryRecover(HRESULT hr, UINT64 now) noexcept {
        if (hr != HRESULT_FROM_WIN32(ERROR_TIMEOUT) && hr != DXGI_ERROR_WAS_STILL_DRAWING) return false;
        if (!started_ || now - windowStart_ >= 10000) { started_ = true; windowStart_ = now; count_ = 0; }
        if (count_ >= 3) return false;
        ++count_; return true;
    }
private:
    bool started_{};
    UINT64 windowStart_{};
    unsigned count_{};
};
// Worker-owned, single in-flight mapping. Reuse only AFTER channel ACK/NACK.
// GPU readback stays in Preloader, not GTA or either process's Present callback.
class LegacyCpuFrameBridge final {
public:
    ~LegacyCpuFrameBridge();
    HRESULT Convert(const SharedGpuFrameDescriptorV1& gpu, LUID adapter,
        const std::atomic_bool& stop, SharedGpuFrameDescriptorV1& cpu) noexcept;
    void Reset() noexcept;
    void FailNextReadbackForTesting() noexcept { failNextReadback_.store(true); }
    const char* Stage() const noexcept { return stage_; }
private:
    HRESULT Prepare(const SharedGpuFrameDescriptorV1& frame, LUID adapter);
    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> staging_;
    Microsoft::WRL::ComPtr<ID3D11Query> query_;
    LUID adapter_{};
    HANDLE mapping_{};
    void* pixels_{};
    UINT width_{}, height_{};
    SharedGpuPixelFormat format_{};
    UINT64 epoch_{};
    std::atomic_bool failNextReadback_{};
    const char* stage_{"idle"};
};
class CpuFrameMapping final {
public:
    CpuFrameMapping() = default;
    CpuFrameMapping(const CpuFrameMapping&) = delete;
    CpuFrameMapping& operator=(const CpuFrameMapping&) = delete;
    ~CpuFrameMapping();
    HRESULT Open(const SharedGpuFrameDescriptorV1& frame,
        const SharedGpuFrameValidationContext& validation) noexcept;
    const void* Pixels() const noexcept { return pixels_; }
private:
    HANDLE mapping_{};
    void* pixels_{};
};
UINT64 CpuFrameTimestampUs() noexcept;
}
