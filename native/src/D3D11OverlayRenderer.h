#pragma once

#include "FrameMailbox.h"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <d3d11.h>
#include <mutex>
#include <wrl/client.h>

namespace rwui {

class D3D11OverlayRenderer final {
public:
    D3D11OverlayRenderer(
        ID3D11Device* device,
        ID3D11DeviceContext* context,
        FrameMailbox& mailbox,
        bool preserveHostState = true);
    // These preparation APIs may perform resource creation or an upload and
    // must be called away from the swap-chain Present hot path.
    bool PrepareBackBuffer(ID3D11Texture2D* backBuffer);
    bool StageLatestCpuFrame();
    // Render and RenderShared are draw-only, try-lock, fail-open operations.
    bool Render(ID3D11Texture2D* backBuffer, bool clearBeforeOverlay,
        const D3D11_VIEWPORT* viewport = nullptr);
    bool RenderShared(
        ID3D11Texture2D* backBuffer,
        ID3D11ShaderResourceView* frameView,
        std::uint64_t generation,
        bool clearBeforeOverlay);
    void InvalidateBackBuffer();
    std::uint64_t RenderedFrames() const noexcept {
        return renderedFrames_.load(std::memory_order_relaxed);
    }
    std::uint64_t LastGeneration() const noexcept {
        return lastRenderedGeneration_.load(std::memory_order_relaxed);
    }
    std::uint64_t CpuFramesStaged() const noexcept {
        return cpuFramesStaged_.load(std::memory_order_relaxed);
    }
    std::uint64_t CpuTextureRebuilds() const noexcept {
        return cpuTextureRebuilds_.load(std::memory_order_relaxed);
    }
    std::uint64_t CpuStageContentions() const noexcept {
        return cpuStageContentions_.load(std::memory_order_relaxed);
    }
    std::uint64_t CpuStageFailures() const noexcept {
        return cpuStageFailures_.load(std::memory_order_relaxed);
    }
    std::uint64_t BackBuffersPrepared() const noexcept {
        return backBuffersPrepared_.load(std::memory_order_relaxed);
    }
    std::uint64_t PipelineBuilds() const noexcept {
        return pipelineBuilds_.load(std::memory_order_relaxed);
    }
    std::uint64_t PresentContentions() const noexcept {
        return presentContentions_.load(std::memory_order_relaxed);
    }
    std::uint64_t UnpreparedRenderAttempts() const noexcept {
        return unpreparedRenderAttempts_.load(std::memory_order_relaxed);
    }
    std::uint64_t HostStatePreservations() const noexcept {
        return hostStatePreservations_.load(std::memory_order_relaxed);
    }

private:
    static constexpr std::size_t MaximumPreparedBackBuffers = 16;

    struct PreparedBackBuffer {
        Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
        Microsoft::WRL::ComPtr<ID3D11RenderTargetView> renderTarget;
        UINT width{};
        UINT height{};
        DXGI_FORMAT format{DXGI_FORMAT_UNKNOWN};
    };

    bool EnsurePipeline(DXGI_FORMAT renderTargetFormat);
    bool UploadFrameLocked(const SharedFrameSnapshot& frame);
    PreparedBackBuffer* FindPreparedBackBufferLocked(ID3D11Texture2D* backBuffer) noexcept;
    bool RenderViewLocked(
        ID3D11Texture2D* backBuffer,
        ID3D11ShaderResourceView* frameView,
        bool clearBeforeOverlay,
        const D3D11_VIEWPORT* viewport = nullptr);
    static bool CompileShader(const char* source, const char* target, ID3DBlob** result);

    FrameMailbox& mailbox_;
    const bool preserveHostState_;
    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
    Microsoft::WRL::ComPtr<ID3D11VertexShader> vertexShader_;
    Microsoft::WRL::ComPtr<ID3D11PixelShader> pixelShader_;
    Microsoft::WRL::ComPtr<ID3D11SamplerState> sampler_;
    Microsoft::WRL::ComPtr<ID3D11BlendState> blendState_;
    Microsoft::WRL::ComPtr<ID3D11RasterizerState> rasterizerState_;
    Microsoft::WRL::ComPtr<ID3D11DepthStencilState> depthStencilState_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> frameTexture_;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> frameView_;
    std::array<PreparedBackBuffer, MaximumPreparedBackBuffers> preparedBackBuffers_{};
    std::size_t nextPreparedBackBuffer_{};
    DXGI_FORMAT pipelineFormat_{DXGI_FORMAT_UNKNOWN};
    std::int32_t frameWidth_{};
    std::int32_t frameHeight_{};
    mutable std::mutex rendererMutex_;
    std::mutex stageMutex_;
    // CPU mailbox and external GPU generations are independent sequences.
    // A high shared generation must never suppress a later CPU fallback frame.
    std::atomic<std::uint64_t> uploadedGeneration_{};
    std::atomic<std::uint64_t> lastRenderedGeneration_{};
    std::atomic<std::uint64_t> renderedFrames_{};
    std::atomic<std::uint64_t> cpuFramesStaged_{};
    std::atomic<std::uint64_t> cpuTextureRebuilds_{};
    std::atomic<std::uint64_t> cpuStageContentions_{};
    std::atomic<std::uint64_t> cpuStageFailures_{};
    std::atomic<std::uint64_t> backBuffersPrepared_{};
    std::atomic<std::uint64_t> pipelineBuilds_{};
    std::atomic<std::uint64_t> presentContentions_{};
    std::atomic<std::uint64_t> unpreparedRenderAttempts_{};
    std::atomic<std::uint64_t> hostStatePreservations_{};
};

} // namespace rwui
