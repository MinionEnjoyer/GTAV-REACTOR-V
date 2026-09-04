#include "D3D11OverlayRenderer.h"

#include <algorithm>
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
    std::array<
        Microsoft::WRL::ComPtr<ID3D11RenderTargetView>,
        D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT> renderTargets{};
    UINT renderTargetCount{};
    Microsoft::WRL::ComPtr<ID3D11DepthStencilView> depthStencilView;
    std::array<
        Microsoft::WRL::ComPtr<ID3D11UnorderedAccessView>,
        D3D11_PS_CS_UAV_REGISTER_COUNT> unorderedAccessViews{};
    Microsoft::WRL::ComPtr<ID3D11BlendState> blendState;
    FLOAT blendFactor[4]{};
    UINT sampleMask{};
    Microsoft::WRL::ComPtr<ID3D11DepthStencilState> depthStencilState;
    UINT stencilReference{};
    Microsoft::WRL::ComPtr<ID3D11RasterizerState> rasterizerState;
    std::array<D3D11_VIEWPORT, D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE> viewports{};
    UINT viewportCount{D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE};
    Microsoft::WRL::ComPtr<ID3D11VertexShader> vertexShader;
    std::array<
        Microsoft::WRL::ComPtr<ID3D11ClassInstance>,
        D3D11_SHADER_MAX_INTERFACES> vertexClassInstances{};
    UINT vertexClassInstanceCount{};
    Microsoft::WRL::ComPtr<ID3D11PixelShader> pixelShader;
    std::array<
        Microsoft::WRL::ComPtr<ID3D11ClassInstance>,
        D3D11_SHADER_MAX_INTERFACES> pixelClassInstances{};
    UINT pixelClassInstanceCount{};
    Microsoft::WRL::ComPtr<ID3D11GeometryShader> geometryShader;
    std::array<
        Microsoft::WRL::ComPtr<ID3D11ClassInstance>,
        D3D11_SHADER_MAX_INTERFACES> geometryClassInstances{};
    UINT geometryClassInstanceCount{};
    Microsoft::WRL::ComPtr<ID3D11HullShader> hullShader;
    std::array<
        Microsoft::WRL::ComPtr<ID3D11ClassInstance>,
        D3D11_SHADER_MAX_INTERFACES> hullClassInstances{};
    UINT hullClassInstanceCount{};
    Microsoft::WRL::ComPtr<ID3D11DomainShader> domainShader;
    std::array<
        Microsoft::WRL::ComPtr<ID3D11ClassInstance>,
        D3D11_SHADER_MAX_INTERFACES> domainClassInstances{};
    UINT domainClassInstanceCount{};
    Microsoft::WRL::ComPtr<ID3D11Predicate> predicate;
    BOOL predicateValue{};
    Microsoft::WRL::ComPtr<ID3D11InputLayout> inputLayout;
    D3D11_PRIMITIVE_TOPOLOGY topology{};
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> shaderResource;
    Microsoft::WRL::ComPtr<ID3D11SamplerState> sampler;
};

template <typename T, std::size_t Size>
std::array<T*, Size> RawPointers(
    const std::array<Microsoft::WRL::ComPtr<T>, Size>& values) noexcept {
    std::array<T*, Size> result{};
    for (std::size_t index = 0; index < Size; ++index) {
        result[index] = values[index].Get();
    }
    return result;
}

template <typename T, std::size_t Size>
void AdoptPointers(
    std::array<Microsoft::WRL::ComPtr<T>, Size>& destination,
    const std::array<T*, Size>& values,
    const UINT count) noexcept {
    const auto boundedCount = std::min<std::size_t>(count, Size);
    for (std::size_t index = 0; index < boundedCount; ++index) {
        destination[index].Attach(values[index]);
    }
}

} // namespace

D3D11OverlayRenderer::D3D11OverlayRenderer(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    FrameMailbox& mailbox,
    const bool preserveHostState)
    : mailbox_(mailbox),
      preserveHostState_(preserveHostState),
      device_(device),
      context_(context) {
}

bool D3D11OverlayRenderer::PrepareBackBuffer(ID3D11Texture2D* const backBuffer) {
    if (backBuffer == nullptr || device_ == nullptr || context_ == nullptr) {
        return false;
    }

    D3D11_TEXTURE2D_DESC description{};
    backBuffer->GetDesc(&description);

    std::scoped_lock lock(rendererMutex_);
    if (!EnsurePipeline(description.Format)) {
        return false;
    }
    if (FindPreparedBackBufferLocked(backBuffer) != nullptr) {
        return true;
    }

    Microsoft::WRL::ComPtr<ID3D11RenderTargetView> renderTarget;
    if (FAILED(device_->CreateRenderTargetView(backBuffer, nullptr, &renderTarget))) {
        return false;
    }

    std::size_t targetIndex = MaximumPreparedBackBuffers;
    for (std::size_t index = 0; index < preparedBackBuffers_.size(); ++index) {
        if (preparedBackBuffers_[index].texture == nullptr) {
            targetIndex = index;
            break;
        }
    }
    if (targetIndex == MaximumPreparedBackBuffers) {
        targetIndex = nextPreparedBackBuffer_;
        nextPreparedBackBuffer_ =
            (nextPreparedBackBuffer_ + 1u) % MaximumPreparedBackBuffers;
    }

    auto& prepared = preparedBackBuffers_[targetIndex];
    prepared.texture = backBuffer;
    prepared.renderTarget = std::move(renderTarget);
    prepared.width = description.Width;
    prepared.height = description.Height;
    prepared.format = description.Format;
    backBuffersPrepared_.fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool D3D11OverlayRenderer::StageLatestCpuFrame() {
    std::unique_lock stageLock(stageMutex_, std::try_to_lock);
    if (!stageLock.owns_lock()) {
        cpuStageContentions_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }

    SharedFrameSnapshot frame;
    if (!mailbox_.TryReadNewerThanShared(
            uploadedGeneration_.load(std::memory_order_acquire),
            frame)) {
        return false;
    }

    std::scoped_lock rendererLock(rendererMutex_);
    if (frame.generation <= uploadedGeneration_.load(std::memory_order_relaxed)) {
        return false;
    }
    if (!UploadFrameLocked(frame)) {
        cpuStageFailures_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    return true;
}

bool D3D11OverlayRenderer::Render(ID3D11Texture2D* backBuffer, const bool clearBeforeOverlay,
    const D3D11_VIEWPORT* viewport) {
    if (backBuffer == nullptr) {
        return false;
    }

    std::unique_lock lock(rendererMutex_, std::try_to_lock);
    if (!lock.owns_lock()) {
        presentContentions_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    if (frameView_ == nullptr) {
        return false;
    }

    const bool rendered = RenderViewLocked(
        backBuffer, frameView_.Get(), clearBeforeOverlay, viewport);
    if (rendered) {
        lastRenderedGeneration_.store(
            uploadedGeneration_.load(std::memory_order_relaxed),
            std::memory_order_release);
    }
    return rendered;
}

bool D3D11OverlayRenderer::RenderShared(
    ID3D11Texture2D* const backBuffer,
    ID3D11ShaderResourceView* const frameView,
    const std::uint64_t generation,
    const bool clearBeforeOverlay) {
    if (backBuffer == nullptr || frameView == nullptr || generation == 0) {
        return false;
    }

    std::unique_lock lock(rendererMutex_, std::try_to_lock);
    if (!lock.owns_lock()) {
        presentContentions_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    const bool rendered = RenderViewLocked(backBuffer, frameView, clearBeforeOverlay);
    if (rendered) {
        lastRenderedGeneration_.store(generation, std::memory_order_release);
    }
    return rendered;
}

bool D3D11OverlayRenderer::RenderViewLocked(
    ID3D11Texture2D* const backBuffer,
    ID3D11ShaderResourceView* const frameView,
    const bool clearBeforeOverlay,
    const D3D11_VIEWPORT* requestedViewport) {
    auto* const prepared = FindPreparedBackBufferLocked(backBuffer);
    if (prepared == nullptr) {
        unpreparedRenderAttempts_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    if (vertexShader_ == nullptr || pixelShader_ == nullptr || sampler_ == nullptr ||
        blendState_ == nullptr || rasterizerState_ == nullptr ||
        depthStencilState_ == nullptr || prepared->renderTarget == nullptr) {
        return false;
    }

    // Enhanced renders through a Reactor-owned D3D11On12 immediate context;
    // there is no host D3D11 pipeline to preserve. Avoid dozens of COM Get/
    // restore calls on every GTA Present, while retaining the complete state
    // guard for Legacy's real game-owned D3D11 context.
    if (!preserveHostState_) {
        ID3D11RenderTargetView* target = prepared->renderTarget.Get();
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
            static_cast<FLOAT>(prepared->width),
            static_cast<FLOAT>(prepared->height),
            0.0f,
            1.0f,
        };
        context_->RSSetViewports(1, requestedViewport ? requestedViewport : &viewport);
        context_->IASetInputLayout(nullptr);
        context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context_->VSSetShader(vertexShader_.Get(), nullptr, 0);
        context_->PSSetShader(pixelShader_.Get(), nullptr, 0);
        context_->GSSetShader(nullptr, nullptr, 0);
        context_->HSSetShader(nullptr, nullptr, 0);
        context_->DSSetShader(nullptr, nullptr, 0);
        context_->SetPredication(nullptr, FALSE);
        ID3D11ShaderResourceView* view = frameView;
        ID3D11SamplerState* sampler = sampler_.Get();
        context_->PSSetShaderResources(0, 1, &view);
        context_->PSSetSamplers(0, 1, &sampler);
        context_->Draw(3, 0);
        ID3D11ShaderResourceView* nullView = nullptr;
        context_->PSSetShaderResources(0, 1, &nullView);
        context_->OMSetRenderTargets(0, nullptr, nullptr);
        renderedFrames_.fetch_add(1, std::memory_order_relaxed);
        return true;
    }

    PipelineBackup backup;
    hostStatePreservations_.fetch_add(1, std::memory_order_relaxed);
    std::array<
        ID3D11RenderTargetView*,
        D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT> rawRenderTargets{};
    context_->OMGetRenderTargets(
        static_cast<UINT>(rawRenderTargets.size()),
        rawRenderTargets.data(),
        &backup.depthStencilView);
    AdoptPointers(backup.renderTargets, rawRenderTargets,
        static_cast<UINT>(rawRenderTargets.size()));
    for (std::size_t index = 0; index < backup.renderTargets.size(); ++index) {
        if (backup.renderTargets[index] != nullptr) {
            backup.renderTargetCount = static_cast<UINT>(index + 1);
        }
    }
    std::array<
        ID3D11UnorderedAccessView*,
        D3D11_PS_CS_UAV_REGISTER_COUNT> rawUnorderedAccessViews{};
    context_->OMGetRenderTargetsAndUnorderedAccessViews(
        0, nullptr, nullptr,
        0, static_cast<UINT>(rawUnorderedAccessViews.size()),
        rawUnorderedAccessViews.data());
    AdoptPointers(
        backup.unorderedAccessViews,
        rawUnorderedAccessViews,
        static_cast<UINT>(rawUnorderedAccessViews.size()));
    context_->OMGetBlendState(&backup.blendState, backup.blendFactor, &backup.sampleMask);
    context_->OMGetDepthStencilState(&backup.depthStencilState, &backup.stencilReference);
    context_->RSGetState(&backup.rasterizerState);
    context_->RSGetViewports(&backup.viewportCount, backup.viewports.data());
    std::array<ID3D11ClassInstance*, D3D11_SHADER_MAX_INTERFACES>
        rawVertexClassInstances{};
    backup.vertexClassInstanceCount = static_cast<UINT>(
        rawVertexClassInstances.size());
    context_->VSGetShader(
        &backup.vertexShader,
        rawVertexClassInstances.data(),
        &backup.vertexClassInstanceCount);
    AdoptPointers(
        backup.vertexClassInstances,
        rawVertexClassInstances,
        backup.vertexClassInstanceCount);
    std::array<ID3D11ClassInstance*, D3D11_SHADER_MAX_INTERFACES>
        rawPixelClassInstances{};
    backup.pixelClassInstanceCount = static_cast<UINT>(
        rawPixelClassInstances.size());
    context_->PSGetShader(
        &backup.pixelShader,
        rawPixelClassInstances.data(),
        &backup.pixelClassInstanceCount);
    AdoptPointers(
        backup.pixelClassInstances,
        rawPixelClassInstances,
        backup.pixelClassInstanceCount);
    std::array<ID3D11ClassInstance*, D3D11_SHADER_MAX_INTERFACES>
        rawGeometryClassInstances{};
    backup.geometryClassInstanceCount = static_cast<UINT>(
        rawGeometryClassInstances.size());
    context_->GSGetShader(
        &backup.geometryShader,
        rawGeometryClassInstances.data(),
        &backup.geometryClassInstanceCount);
    AdoptPointers(
        backup.geometryClassInstances,
        rawGeometryClassInstances,
        backup.geometryClassInstanceCount);
    std::array<ID3D11ClassInstance*, D3D11_SHADER_MAX_INTERFACES>
        rawHullClassInstances{};
    backup.hullClassInstanceCount = static_cast<UINT>(
        rawHullClassInstances.size());
    context_->HSGetShader(
        &backup.hullShader,
        rawHullClassInstances.data(),
        &backup.hullClassInstanceCount);
    AdoptPointers(
        backup.hullClassInstances,
        rawHullClassInstances,
        backup.hullClassInstanceCount);
    std::array<ID3D11ClassInstance*, D3D11_SHADER_MAX_INTERFACES>
        rawDomainClassInstances{};
    backup.domainClassInstanceCount = static_cast<UINT>(
        rawDomainClassInstances.size());
    context_->DSGetShader(
        &backup.domainShader,
        rawDomainClassInstances.data(),
        &backup.domainClassInstanceCount);
    AdoptPointers(
        backup.domainClassInstances,
        rawDomainClassInstances,
        backup.domainClassInstanceCount);
    context_->GetPredication(&backup.predicate, &backup.predicateValue);
    context_->IAGetInputLayout(&backup.inputLayout);
    context_->IAGetPrimitiveTopology(&backup.topology);
    context_->PSGetShaderResources(0, 1, &backup.shaderResource);
    context_->PSGetSamplers(0, 1, &backup.sampler);

    ID3D11RenderTargetView* target = prepared->renderTarget.Get();
    const auto retainedUnorderedAccessViews = RawPointers(
        backup.unorderedAccessViews);
    std::array<UINT, D3D11_PS_CS_UAV_REGISTER_COUNT>
        keepUnorderedAccessViewCounters{};
    keepUnorderedAccessViewCounters.fill(UINT(-1));
    // RTV and pixel-UAV slots share the OM namespace. Slot zero is occupied by
    // the overlay target; preserve every nonconflicting host UAV and its hidden
    // append/consume counter while the fullscreen triangle is drawn.
    context_->OMSetRenderTargetsAndUnorderedAccessViews(
        1, &target, nullptr,
        1, D3D11_PS_CS_UAV_REGISTER_COUNT - 1,
        retainedUnorderedAccessViews.data() + 1,
        keepUnorderedAccessViewCounters.data() + 1);
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
        static_cast<FLOAT>(prepared->width),
        static_cast<FLOAT>(prepared->height),
        0.0f,
        1.0f,
    };
    context_->RSSetViewports(1, requestedViewport ? requestedViewport : &viewport);
    context_->IASetInputLayout(nullptr);
    context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context_->VSSetShader(vertexShader_.Get(), nullptr, 0);
    context_->PSSetShader(pixelShader_.Get(), nullptr, 0);
    context_->GSSetShader(nullptr, nullptr, 0);
    context_->HSSetShader(nullptr, nullptr, 0);
    context_->DSSetShader(nullptr, nullptr, 0);
    context_->SetPredication(nullptr, FALSE);
    ID3D11ShaderResourceView* view = frameView;
    ID3D11SamplerState* sampler = sampler_.Get();
    context_->PSSetShaderResources(0, 1, &view);
    context_->PSSetSamplers(0, 1, &sampler);
    context_->Draw(3, 0);

    ID3D11ShaderResourceView* nullView = nullptr;
    context_->PSSetShaderResources(0, 1, &nullView);
    const auto restoredRenderTargets = RawPointers(backup.renderTargets);
    const auto restoredUnorderedAccessViews = RawPointers(
        backup.unorderedAccessViews);
    const auto restoredUnorderedAccessViewCount =
        D3D11_PS_CS_UAV_REGISTER_COUNT - backup.renderTargetCount;
    context_->OMSetRenderTargetsAndUnorderedAccessViews(
        backup.renderTargetCount,
        backup.renderTargetCount == 0 ? nullptr : restoredRenderTargets.data(),
        backup.depthStencilView.Get(),
        backup.renderTargetCount,
        restoredUnorderedAccessViewCount,
        restoredUnorderedAccessViewCount == 0 ? nullptr :
            restoredUnorderedAccessViews.data() + backup.renderTargetCount,
        restoredUnorderedAccessViewCount == 0 ? nullptr :
            keepUnorderedAccessViewCounters.data() + backup.renderTargetCount);
    context_->OMSetBlendState(backup.blendState.Get(), backup.blendFactor, backup.sampleMask);
    context_->OMSetDepthStencilState(backup.depthStencilState.Get(), backup.stencilReference);
    context_->RSSetState(backup.rasterizerState.Get());
    context_->RSSetViewports(backup.viewportCount, backup.viewports.data());
    const auto restoredVertexClassInstances = RawPointers(
        backup.vertexClassInstances);
    context_->VSSetShader(
        backup.vertexShader.Get(),
        backup.vertexClassInstanceCount == 0
            ? nullptr : restoredVertexClassInstances.data(),
        backup.vertexClassInstanceCount);
    const auto restoredPixelClassInstances = RawPointers(
        backup.pixelClassInstances);
    context_->PSSetShader(
        backup.pixelShader.Get(),
        backup.pixelClassInstanceCount == 0
            ? nullptr : restoredPixelClassInstances.data(),
        backup.pixelClassInstanceCount);
    const auto restoredGeometryClassInstances = RawPointers(
        backup.geometryClassInstances);
    context_->GSSetShader(
        backup.geometryShader.Get(),
        backup.geometryClassInstanceCount == 0
            ? nullptr : restoredGeometryClassInstances.data(),
        backup.geometryClassInstanceCount);
    const auto restoredHullClassInstances = RawPointers(
        backup.hullClassInstances);
    context_->HSSetShader(
        backup.hullShader.Get(),
        backup.hullClassInstanceCount == 0
            ? nullptr : restoredHullClassInstances.data(),
        backup.hullClassInstanceCount);
    const auto restoredDomainClassInstances = RawPointers(
        backup.domainClassInstances);
    context_->DSSetShader(
        backup.domainShader.Get(),
        backup.domainClassInstanceCount == 0
            ? nullptr : restoredDomainClassInstances.data(),
        backup.domainClassInstanceCount);
    context_->SetPredication(
        backup.predicate.Get(), backup.predicateValue);
    context_->IASetInputLayout(backup.inputLayout.Get());
    context_->IASetPrimitiveTopology(backup.topology);
    view = backup.shaderResource.Get();
    sampler = backup.sampler.Get();
    context_->PSSetShaderResources(0, 1, &view);
    context_->PSSetSamplers(0, 1, &sampler);

    renderedFrames_.fetch_add(1, std::memory_order_relaxed);
    return true;
}

void D3D11OverlayRenderer::InvalidateBackBuffer() {
    std::scoped_lock lock(rendererMutex_);
    for (auto& prepared : preparedBackBuffers_) {
        prepared = {};
    }
    nextPreparedBackBuffer_ = 0;
}

bool D3D11OverlayRenderer::EnsurePipeline(const DXGI_FORMAT renderTargetFormat) {
    // The shaders and fixed-function state are format-independent. Render
    // target views are prepared separately per concrete swap-chain buffer.
    if (vertexShader_ != nullptr && pixelShader_ != nullptr && sampler_ != nullptr &&
        blendState_ != nullptr && rasterizerState_ != nullptr &&
        depthStencilState_ != nullptr) {
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
    pipelineBuilds_.fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool D3D11OverlayRenderer::UploadFrameLocked(const SharedFrameSnapshot& frame) {
    if (frame.width <= 0 || frame.height <= 0 || frame.stride < frame.width * 4 ||
        frame.generation == 0 || frame.pixels == nullptr || frame.pixels->empty()) {
        return false;
    }

    if (frameWidth_ != frame.width || frameHeight_ != frame.height || frameTexture_ == nullptr) {
        D3D11_TEXTURE2D_DESC textureDescription{};
        textureDescription.Width = static_cast<UINT>(frame.width);
        textureDescription.Height = static_cast<UINT>(frame.height);
        textureDescription.MipLevels = 1;
        textureDescription.ArraySize = 1;
        textureDescription.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        textureDescription.SampleDesc.Count = 1;
        textureDescription.Usage = D3D11_USAGE_DEFAULT;
        textureDescription.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
        Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> view;
        if (FAILED(device_->CreateTexture2D(&textureDescription, nullptr, &texture)) ||
            FAILED(device_->CreateShaderResourceView(texture.Get(), nullptr, &view))) {
            return false;
        }
        frameTexture_ = std::move(texture);
        frameView_ = std::move(view);
        frameWidth_ = frame.width;
        frameHeight_ = frame.height;
        cpuTextureRebuilds_.fetch_add(1, std::memory_order_relaxed);
    }

    context_->UpdateSubresource(
        frameTexture_.Get(), 0, nullptr, frame.pixels->data(), frame.stride, 0);
    uploadedGeneration_.store(frame.generation, std::memory_order_release);
    cpuFramesStaged_.fetch_add(1, std::memory_order_relaxed);
    return true;
}

D3D11OverlayRenderer::PreparedBackBuffer*
D3D11OverlayRenderer::FindPreparedBackBufferLocked(
    ID3D11Texture2D* const backBuffer) noexcept {
    for (auto& prepared : preparedBackBuffers_) {
        if (prepared.texture.Get() == backBuffer) {
            return &prepared;
        }
    }
    return nullptr;
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
