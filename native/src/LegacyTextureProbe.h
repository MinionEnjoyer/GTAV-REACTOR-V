#pragma once
#include "D3D11OverlayRenderer.h"
#include <atomic>
#include <filesystem>
#include <thread>

namespace rwui::probe {
// Private, fixed-size diagnostic IPC, not the production frame protocol.
constexpr UINT Width = 320, Height = 180;
constexpr ULONG Magic = 0x52565032, Version = 1;
enum class Kind : ULONG { Local, Nt, Kmt };
enum class Step : ULONG {
    Device, Interface, Duplicate, Open, Mutex, Acquire, Source, Query,
    CopyComplete, Staging, Map, Pixels, Release, Count
};
struct Packet {
    ULONG magic{Magic}, version{Version}, bytes{sizeof(Packet)}, kind{};
    ULONG ownerPid{}, adapterLow{};
    LONG adapterHigh{};
    ULONG width{Width}, height{Height};
    UINT64 textureHandle{};
    HRESULT results[static_cast<ULONG>(Step::Count)]{};
    volatile LONG done{};
};
bool ValidPacket(const Packet& packet, DWORD ownerPid) noexcept;
UINT Pixel(UINT x, UINT y, Kind kind) noexcept;
int TextureProbeChild(HANDLE mapping, HANDLE owner) noexcept;

// Device allocation / process startup / logging are worker-only. Draw is a
// cached try-lock operation and never controls input or production readiness.
class LegacyTextureProbe final {
public:
    ~LegacyTextureProbe();
    void Configure(const std::filesystem::path& helper, const std::filesystem::path& log);
    void Start(ID3D11Device* device);
    void Stop() noexcept;
    bool Active() const noexcept { return active_.load(); }
    bool Draw(D3D11OverlayRenderer& renderer, ID3D11Texture2D* backBuffer,
        ID3D11DeviceContext* context, bool fullscreen) noexcept;
    UINT64 Draws() const noexcept { return totalDraws_.load(); }
    // Test harness only: reduce the display interval; never implicit in live use.
    void SetDisplayMillisecondsForTest(ULONG ms) noexcept { displayMs_ = ms; }
private:
    void Run(Microsoft::WRL::ComPtr<ID3D11Device> device) noexcept;
    bool Partner(Packet& packet);
    void Log(const char* mode, const char* step, HRESULT hr);
    bool Cancelled() const noexcept { return stop_.load(); }
    std::filesystem::path helper_, log_;
    std::thread worker_;
    std::mutex viewMutex_;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> view_;
    Microsoft::WRL::ComPtr<IDXGIKeyedMutex> keyed_;
    std::atomic_bool active_{}, stop_{};
    std::atomic_uint64_t totalDraws_{}, phaseDraws_{}, fullscreenDraws_{};
    std::atomic<ULONG> acquireFailure_{};
    ULONG displayMs_{4000};
};
}
