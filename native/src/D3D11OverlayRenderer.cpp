#include "D3D11OverlayRenderer.h"

#include <array>
#include <d3dcompiler.h>

namespace rwui {

namespace {

constexpr char VertexShaderSource[] = R"(
struct Output { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
Output main(uint id : SV_VertexID) {
    Output output;
    float2 uv = float2((id << 1) & 2, id & 2);
    output.position = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    output.uv = uv;
    return output;
})";

constexpr char PixelShaderSource[] = R"(
Texture2D overlayTexture : register(t0);
SamplerState overlaySampler : register(s0);
float4 main(float4 position : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET {
    return overlayTexture.Sample(overlaySampler, uv);
})";

struct PipelineBackup {
    Microsoft::WRL::ComPtr<ID3D11RenderTargetView> renderTarget;
    Microsoft::WRL::ComPtr<ID3D11DepthStencilView> depthStencilView;
    Microsoft::WRL::ComPtr<ID3D11BlendState> blendState;
    FLOAT blendFactor[4]{};
    UINT sampleMask{};
    Microsoft::WRL::ComPtr<ID3D11DepthStencilState> depthStencilState;
    UINT stencilReference{};
    Microsoft::WRL::ComPtr<ID3D11RasterizerState> rasterizerState;
    std::array<D3D11_VIEWPORT, D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE> viewports{};
    UINT viewportCount{D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE};
    Microsoft::WRL::ComPtr<ID3D11VertexShader> vertexShader;
    Microsoft::WRL::ComPtr<ID3D11PixelShader> pixelShader;
    Microsoft::WRL::ComPtr<ID3D11InputLayout> inputLayout;
    D3D11_PRIMITIVE_TOPOLOGY topology{};
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> shaderResource;
    Microsoft::WRL::ComPtr<ID3D11SamplerState> sampler;
};

} // namespace

D3D11OverlayRenderer::D3D11OverlayRenderer(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    FrameMailbox& mailbox)
    : mailbox_(mailbox), device_(device), context_(context) {
}

bool D3D11OverlayRenderer::Render(ID3D11Texture2D* backBuffer, const bool clearBeforeOverlay) {
    if (backBuffer == nullptr || !UploadLatestFrame() || frameView_ == nullptr) {
        return false;
    }

    D3D11_TEXTURE2D_DESC backBufferDescription{};
    backBuffer->GetDesc(&backBufferDescription);
    if (!EnsurePipeline(backBufferDescription.Format)) {
        return false;
    }

    if (cachedBackBuffer_.Get() != backBuffer) {
        cachedBackBuffer_ = backBuffer;
        cachedRenderTarget_.Reset();
        if (FAILED(device_->CreateRenderTargetView(backBuffer, nullptr, &cachedRenderTarget_))) {
            cachedBackBuffer_.Reset();
            return false;
        }
    }

    PipelineBackup backup;
    context_->OMGetRenderTargets(1, &backup.renderTarget, &backup.depthStencilView);
    context_->OMGetBlendState(&backup.blendState, backup.blendFactor, &backup.sampleMask);
    context_->OMGetDepthStencilState(&backup.depthStencilState, &backup.stencilReference);
    context_->RSGetState(&backup.rasterizerState);
    context_->RSGetViewports(&backup.viewportCount, backup.viewports.data());
    context_->VSGetShader(&backup.vertexShader, nullptr, nullptr);
    context_->PSGetShader(&backup.pixelShader, nullptr, nullptr);
    context_->IAGetInputLayout(&backup.inputLayout);
    context_->IAGetPrimitiveTopology(&backup.topology);
    context_->PSGetShaderResources(0, 1, &backup.shaderResource);
    context_->PSGetSamplers(0, 1, &backup.sampler);

    ID3D11RenderTargetView* target = cachedRenderTarget_.Get();
    context_->OMSetRenderTargets(1, &target, nullptr);
    if (clearBeforeOverlay) {
        constexpr FLOAT clearColor[4]{0.018f, 0.027f, 0.032f, 1.0f};
        context_->ClearRenderTargetView(target, clearColor);
    }
    constexpr FLOAT blendFactor[4]{};
    context_->OMSetBlendState(blendState_.Get(), blendFactor, 0xffffffffu);
    context_->OMSetDepthStencilState(depthStencilState_.Get(), 0);
    context_->RSSetState(rasterizerState_.Get());
    const D3D11_VIEWPORT viewport{
        0.0f,
        0.0f,
        static_cast<FLOAT>(backBufferDescription.Width),
        static_cast<FLOAT>(backBufferDescription.Height),
        0.0f,
        1.0f,
    };
    context_->RSSetViewports(1, &viewport);
    context_->IASetInputLayout(nullptr);
    context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context_->VSSetShader(vertexShader_.Get(), nullptr, 0);
    context_->PSSetShader(pixelShader_.Get(), nullptr, 0);
    ID3D11ShaderResourceView* view = frameView_.Get();
    ID3D11SamplerState* sampler = sampler_.Get();
    context_->PSSetShaderResources(0, 1, &view);
    context_->PSSetSamplers(0, 1, &sampler);
    context_->Draw(3, 0);

    ID3D11ShaderResourceView* nullView = nullptr;
    context_->PSSetShaderResources(0, 1, &nullView);
    target = backup.renderTarget.Get();
    context_->OMSetRenderTargets(1, &target, backup.depthStencilView.Get());
    context_->OMSetBlendState(backup.blendState.Get(), backup.blendFactor, backup.sampleMask);
    context_->OMSetDepthStencilState(backup.depthStencilState.Get(), backup.stencilReference);
    context_->RSSetState(backup.rasterizerState.Get());
    context_->RSSetViewports(backup.viewportCount, backup.viewports.data());
    context_->VSSetShader(backup.vertexShader.Get(), nullptr, 0);
    context_->PSSetShader(backup.pixelShader.Get(), nullptr, 0);
    context_->IASetInputLayout(backup.inputLayout.Get());
    context_->IASetPrimitiveTopology(backup.topology);
    view = backup.shaderResource.Get();
    sampler = backup.sampler.Get();
    context_->PSSetShaderResources(0, 1, &view);
    context_->PSSetSamplers(0, 1, &sampler);

    ++renderedFrames_;
    return true;
}

void D3D11OverlayRenderer::InvalidateBackBuffer() {
    cachedRenderTarget_.Reset();
    cachedBackBuffer_.Reset();
}

bool D3D11OverlayRenderer::EnsurePipeline(const DXGI_FORMAT renderTargetFormat) {
    if (vertexShader_ != nullptr && pixelShader_ != nullptr && pipelineFormat_ == renderTargetFormat) {
        return true;
    }

    vertexShader_.Reset();
    pixelShader_.Reset();
    sampler_.Reset();
    blendState_.Reset();
    rasterizerState_.Reset();
    depthStencilState_.Reset();

    Microsoft::WRL::ComPtr<ID3DBlob> vertexBytecode;
    Microsoft::WRL::ComPtr<ID3DBlob> pixelBytecode;
    if (!CompileShader(VertexShaderSource, "vs_5_0", &vertexBytecode) ||
        !CompileShader(PixelShaderSource, "ps_5_0", &pixelBytecode) ||
        FAILED(device_->CreateVertexShader(
            vertexBytecode->GetBufferPointer(), vertexBytecode->GetBufferSize(), nullptr, &vertexShader_)) ||
        FAILED(device_->CreatePixelShader(
            pixelBytecode->GetBufferPointer(), pixelBytecode->GetBufferSize(), nullptr, &pixelShader_))) {
        return false;
    }

    D3D11_SAMPLER_DESC samplerDescription{};
    samplerDescription.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    samplerDescription.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    samplerDescription.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    samplerDescription.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    samplerDescription.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(device_->CreateSamplerState(&samplerDescription, &sampler_))) return false;

    D3D11_BLEND_DESC blendDescription{};
    auto& target = blendDescription.RenderTarget[0];
    target.BlendEnable = TRUE;
    target.SrcBlend = D3D11_BLEND_ONE;
    target.DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    target.BlendOp = D3D11_BLEND_OP_ADD;
    target.SrcBlendAlpha = D3D11_BLEND_ONE;
    target.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    target.BlendOpAlpha = D3D11_BLEND_OP_ADD;
    target.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    if (FAILED(device_->CreateBlendState(&blendDescription, &blendState_))) return false;

    D3D11_RASTERIZER_DESC rasterizerDescription{};
    rasterizerDescription.FillMode = D3D11_FILL_SOLID;
    rasterizerDescription.CullMode = D3D11_CULL_NONE;
    rasterizerDescription.DepthClipEnable = TRUE;
    if (FAILED(device_->CreateRasterizerState(&rasterizerDescription, &rasterizerState_))) return false;

    D3D11_DEPTH_STENCIL_DESC depthDescription{};
    depthDescription.DepthEnable = FALSE;
    depthDescription.StencilEnable = FALSE;
    if (FAILED(device_->CreateDepthStencilState(&depthDescription, &depthStencilState_))) return false;

    pipelineFormat_ = renderTargetFormat;
    return true;
}

bool D3D11OverlayRenderer::UploadLatestFrame() {
    FrameSnapshot frame;
    if (!mailbox_.ReadNewerThan(uploadedGeneration_, frame)) {
        return frameView_ != nullptr;
    }

    if (frameWidth_ != frame.width || frameHeight_ != frame.height || frameTexture_ == nullptr) {
        frameTexture_.Reset();
        frameView_.Reset();

        D3D11_TEXTURE2D_DESC textureDescription{};
        textureDescription.Width = static_cast<UINT>(frame.width);
        textureDescription.Height = static_cast<UINT>(frame.height);
        textureDescription.MipLevels = 1;
        textureDescription.ArraySize = 1;
        textureDescription.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        textureDescription.SampleDesc.Count = 1;
        textureDescription.Usage = D3D11_USAGE_DEFAULT;
        textureDescription.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(device_->CreateTexture2D(&textureDescription, nullptr, &frameTexture_)) ||
            FAILED(device_->CreateShaderResourceView(frameTexture_.Get(), nullptr, &frameView_))) {
            frameTexture_.Reset();
            frameView_.Reset();
            return false;
        }
        frameWidth_ = frame.width;
        frameHeight_ = frame.height;
    }

    context_->UpdateSubresource(frameTexture_.Get(), 0, nullptr, frame.pixels.data(), frame.stride, 0);
    uploadedGeneration_ = frame.generation;
    return true;
}

bool D3D11OverlayRenderer::CompileShader(
    const char* source,
    const char* target,
    ID3DBlob** result) {
    Microsoft::WRL::ComPtr<ID3DBlob> errors;
    return SUCCEEDED(D3DCompile(
        source,
        std::char_traits<char>::length(source),
        nullptr,
        nullptr,
        nullptr,
        "main",
        target,
        D3DCOMPILE_OPTIMIZATION_LEVEL3,
        0,
        result,
        &errors));
}

} // namespace rwui

