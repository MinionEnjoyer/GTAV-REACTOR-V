#pragma once

#include <atomic>
#include <cstdint>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>

namespace rwui::transport {

enum class LegacyD3D11BridgeStage : std::uint32_t {
    Idle, PeerDevice, GameTexture, SharedHandle, PeerOpen, KeyedMutex,
    Ready, PeerAcquire, PeerCopy, PeerRelease, GameAcquire, GameRelease,
    InvalidSource, DeviceRemoved,
};

// Worker-only compatibility bridge. The cross-process input remains the
// validated NT-handle protocol. Only a game-created, process-local intermediate
// uses legacy DXGI sharing; its non-NT handle never leaves this process.
class LegacyD3D11FrameBridge final {
public:
    ~LegacyD3D11FrameBridge() { Reset(); }
    HRESULT Initialize(ID3D11Device* gameDevice) noexcept;
    HRESULT StageSource(ID3D11Texture2D* source, HANDLE stopEvent) noexcept;
    HRESULT ReleaseGame() noexcept;
    void Reset() noexcept;

    ID3D11Device* ImportDevice() const noexcept { return peerDevice_.Get(); }
    ID3D11Texture2D* GameTexture() const noexcept {
        return gameAcquired_ ? gameTexture_.Get() : nullptr;
    }
    std::uint32_t Stage() const noexcept { return stage_.load(); }
    std::uint32_t LastHresult() const noexcept { return lastHresult_.load(); }

private:
    HRESULT Record(LegacyD3D11BridgeStage stage, HRESULT result) noexcept;
    HRESULT PrepareTexture(ID3D11Texture2D* source) noexcept;
    void ResetTexture() noexcept;
    Microsoft::WRL::ComPtr<ID3D11Device> gameDevice_, peerDevice_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> peerContext_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> gameTexture_, peerTexture_;
    Microsoft::WRL::ComPtr<IDXGIKeyedMutex> gameMutex_, peerMutex_;
    bool gameAcquired_{};
    std::atomic<std::uint32_t> stage_{};
    std::atomic<std::uint32_t> lastHresult_{};
};

} // namespace rwui::transport
