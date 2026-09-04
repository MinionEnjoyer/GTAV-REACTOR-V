#pragma once
#include "D3D11OverlayRenderer.h"
#include <d3d11_1.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <atomic>
#include <filesystem>
#include <mutex>
#include <thread>
#include <memory>

namespace rwui::probe {
// Opt-in diagnostic only: independent ClearView control and a locally uploaded
// texture using the production shader renderer. No browser or shared handles.
class LegacyVisibilityProbe final {
public:
    ~LegacyVisibilityProbe();
    void Configure(const std::filesystem::path& log);
    bool Prepare(IDXGISwapChain* chain, ID3D11Texture2D* buffer,
        ID3D11Device* device, ID3D11DeviceContext* context);
    void Invalidate();
    void Stop();
    bool Draw(IDXGISwapChain* chain) noexcept;
    void Presented(HRESULT result) noexcept;
    void Rearm() noexcept { rearm_.store(true); }
    bool Enabled() const noexcept { return enabled_.load(); }
    // Not exported to the game ABI: standalone harness may use a hidden HWND.
    void SetTestMode(UINT durationMs = 30000, bool corruptPattern = false) {
        testMode_ = true; durationMs_ = durationMs; corruptPatternForTest_ = corruptPattern;
    }
private:
    static constexpr UINT PatternWidth = 640, PatternHeight = 360, PatternSamples = 9;
    static constexpr UINT SampleCount = 3 + PatternSamples;
    static UINT PatternPixel(UINT x, UINT y) noexcept;
    void PreparePattern(ID3D11Device* device);
    void Worker() noexcept;
    void Report(const char* event);
    void ResetResources();
    void PollSample(ULONGLONG now);
    std::mutex mutex_;
    std::thread worker_;
    std::filesystem::path log_;
    std::atomic_bool enabled_{}, stop_{}, rearm_{};
    Microsoft::WRL::ComPtr<IDXGISwapChain> chain_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> buffer_, staging_;
    Microsoft::WRL::ComPtr<ID3D11RenderTargetView> rtv_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext1> context_;
    Microsoft::WRL::ComPtr<ID3D11Query> query_;
    FrameMailbox patternMailbox_;
    std::unique_ptr<D3D11OverlayRenderer> patternRenderer_;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> patternView_;
    DXGI_SWAP_CHAIN_DESC chainDesc_{};
    D3D11_TEXTURE2D_DESC bufferDesc_{};
    D3D11_RECT outer_{}, left_{}, right_{}, patternBackground_{};
    std::array<POINT, SampleCount> samplePoints_{};
    std::array<UINT, PatternSamples> patternExpected_{};
    bool active_{}, pending_{}, testMode_{};
    bool pendingPattern_{}, corruptPatternForTest_{};
    UINT durationMs_{30000}, elapsedMs_{};
    ULONGLONG lastDrawTick_{}, sampleTick_{}, nextSampleTick_{};
    HRESULT prepareHr_{E_PENDING}, sampleHr_{E_PENDING};
    HRESULT patternUploadHr_{E_PENDING}, patternViewHr_{E_PENDING}, patternPipelineHr_{E_PENDING};
    UINT pixels_[SampleCount]{};
    UINT64 draws_{}, fullscreenDraws_{}, samples_{}, matches_{}, mismatches_{}, timeouts_{};
    UINT64 patternDraws_{}, patternDrawFailures_{}, patternChecks_{}, patternMatches_{}, patternMismatches_{}, patternNotRun_{};
    UINT64 wrongChain_{}, wrongBuffer_{};
    bool foreground_{}, fullscreen_{};
    std::atomic_uint64_t committed_{}, occluded_{}, failed_{}, revision_{};
    std::atomic<HRESULT> presentHr_{E_PENDING};
};
}
