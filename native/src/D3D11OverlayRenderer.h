#pragma once

#include "FrameMailbox.h"

#include <cstdint>
#include <d3d11.h>
#include <wrl/client.h>

namespace rwui {

class D3D11OverlayRenderer final {
public:
    D3D11OverlayRenderer(
        ID3D11Device* device,
        ID3D11DeviceContext* context,
        FrameMailbox& mailbox);
    bool Render(ID3D11Texture2D* backBuffer, bool clearBeforeOverlay);
    void InvalidateBackBuffer();
    std::uint64_t RenderedFrames() const noexcept { return renderedFrames_; }
    std::uint64_t LastGeneration() const noexcept { return uploadedGeneration_; }

private:
    bool EnsurePipeline(DXGI_FORMAT renderTargetFormat);
    bool UploadLatestFrame();
    static bool CompileShader(const char* source, const char* target, ID3DBlob** result);

    FrameMailbox& mailbox_;
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
    Microsoft::WRL::ComPtr<ID3D11Texture2D> cachedBackBuffer_;
    Microsoft::WRL::ComPtr<ID3D11RenderTargetView> cachedRenderTarget_;
    DXGI_FORMAT pipelineFormat_{DXGI_FORMAT_UNKNOWN};
    std::int32_t frameWidth_{};
    std::int32_t frameHeight_{};
    std::uint64_t uploadedGeneration_{};
    std::uint64_t renderedFrames_{};
};

} // namespace rwui

