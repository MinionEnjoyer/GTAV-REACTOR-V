#include "D3D11OverlayRenderer.h"
#include "StartupStatusPolicy.h"

#include <cstdint>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <string_view>
#include <vector>

#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

namespace {

int failures = 0;

void Check(const bool condition, const char* const message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        ++failures;
    }
}

std::string ExtractFunction(
    const std::string& source,
    const std::string_view signature) {
    const auto signaturePosition = source.find(signature);
    if (signaturePosition == std::string::npos) return {};
    const auto bodyPosition = source.find('{', signaturePosition);
    if (bodyPosition == std::string::npos) return {};

    std::size_t depth = 0;
    for (std::size_t index = bodyPosition; index < source.size(); ++index) {
        if (source[index] == '{') {
            ++depth;
        } else if (source[index] == '}' && --depth == 0) {
            return source.substr(signaturePosition, index - signaturePosition + 1);
        }
    }
    return {};
}

void CheckPresentSourceContract() {
#ifdef REACTORV_RENDERER_SOURCE_PATH
    std::ifstream input(REACTORV_RENDERER_SOURCE_PATH, std::ios::binary);
    const std::string source{
        std::istreambuf_iterator<char>(input),
        std::istreambuf_iterator<char>()};
    Check(!source.empty(), "renderer source contract file must be readable");

    const auto render = ExtractFunction(
        source, "bool D3D11OverlayRenderer::Render(");
    const auto renderShared = ExtractFunction(
        source, "bool D3D11OverlayRenderer::RenderShared(");
    const auto renderView = ExtractFunction(
        source, "bool D3D11OverlayRenderer::RenderViewLocked(");
    Check(!render.empty() && !renderShared.empty() && !renderView.empty(),
          "all Present-side renderer functions must be discoverable");
    Check(render.find("std::try_to_lock") != std::string::npos,
          "CPU Render must use a fail-open try-lock");
    Check(renderShared.find("std::try_to_lock") != std::string::npos,
          "shared Render must use a fail-open try-lock");
    Check(renderView.find("D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT") !=
              std::string::npos,
          "Present draw must preserve the complete fixed MRT binding set");
    Check(renderView.find("D3D11_SHADER_MAX_INTERFACES") !=
              std::string::npos,
          "Present draw must preserve fixed shader class-instance bindings");
    constexpr std::string_view shaderStageTokens[]{
        "GSGetShader", "HSGetShader", "DSGetShader",
        "GSSetShader(nullptr", "HSSetShader(nullptr", "DSSetShader(nullptr",
        "backup.geometryShader", "backup.hullShader", "backup.domainShader",
    };
    for (const auto token : shaderStageTokens) {
        Check(renderView.find(token) != std::string::npos,
              "overlay draw must disable and exactly restore non-overlay stages");
    }
    Check(renderView.find("GetPredication") != std::string::npos &&
              renderView.find("SetPredication(nullptr, FALSE)") !=
                  std::string::npos &&
              renderView.find("backup.predicate.Get()") != std::string::npos,
          "overlay draw must bypass and restore host predication");
    Check(renderView.find(
              "OMGetRenderTargetsAndUnorderedAccessViews") !=
              std::string::npos &&
          renderView.find(
              "OMSetRenderTargetsAndUnorderedAccessViews") !=
              std::string::npos &&
          renderView.find("keepUnorderedAccessViewCounters.fill(UINT(-1))") !=
              std::string::npos,
          "overlay draw must preserve OM UAV bindings and hidden counters");

    const std::string hotPath = render + renderShared + renderView;
    constexpr std::string_view forbidden[] = {
        "mailbox_",
        "ReadNewerThan",
        "TryReadNewerThanShared",
        "CreateTexture2D",
        "CreateShaderResourceView",
        "CreateRenderTargetView",
        "UpdateSubresource",
        "EnsurePipeline",
        "CompileShader",
        "std::thread",
        "std::vector",
    };
    for (const auto token : forbidden) {
        Check(hotPath.find(token) == std::string::npos,
              "Present-side functions must contain only prepared draw work");
    }
#else
    Check(false, "renderer source path must be provided by CMake");
#endif
}

} // namespace

int main() {
    CheckPresentSourceContract();

    Microsoft::WRL::ComPtr<ID3D11Device> device;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
    const D3D_FEATURE_LEVEL requested[]{D3D_FEATURE_LEVEL_11_0};
    D3D_FEATURE_LEVEL selected{};
    const auto result = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_WARP,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        requested,
        1,
        D3D11_SDK_VERSION,
        &device,
        &selected,
        &context);
    if (FAILED(result)) {
        std::cout << "SKIP: D3D11 WARP device unavailable\n";
        return 125;
    }

    rwui::FrameMailbox mailbox;
    rwui::D3D11OverlayRenderer renderer(device.Get(), context.Get(), mailbox);

    D3D11_TEXTURE2D_DESC targetDescription{};
    targetDescription.Width = 64;
    targetDescription.Height = 64;
    targetDescription.MipLevels = 1;
    targetDescription.ArraySize = 1;
    targetDescription.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    targetDescription.SampleDesc.Count = 1;
    targetDescription.Usage = D3D11_USAGE_DEFAULT;
    targetDescription.BindFlags = D3D11_BIND_RENDER_TARGET;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> target;
    Check(SUCCEEDED(device->CreateTexture2D(
              &targetDescription, nullptr, &target)),
          "test render target must be created");
    if (target == nullptr) return 1;

    Microsoft::WRL::ComPtr<ID3D11Texture2D> secondTarget;
    Check(SUCCEEDED(device->CreateTexture2D(
              &targetDescription, nullptr, &secondTarget)),
          "second host render target must be created");
    Microsoft::WRL::ComPtr<ID3D11RenderTargetView> firstHostTarget;
    Microsoft::WRL::ComPtr<ID3D11RenderTargetView> secondHostTarget;
    Check(SUCCEEDED(device->CreateRenderTargetView(
              target.Get(), nullptr, &firstHostTarget)) &&
              SUCCEEDED(device->CreateRenderTargetView(
                  secondTarget.Get(), nullptr, &secondHostTarget)),
          "host MRT views must be created");
    D3D11_TEXTURE2D_DESC depthDescription = targetDescription;
    depthDescription.Format = DXGI_FORMAT_D24_UNORM_S8_UINT;
    depthDescription.BindFlags = D3D11_BIND_DEPTH_STENCIL;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> depthTarget;
    Microsoft::WRL::ComPtr<ID3D11DepthStencilView> hostDepthView;
    Check(SUCCEEDED(device->CreateTexture2D(
              &depthDescription, nullptr, &depthTarget)) &&
              SUCCEEDED(device->CreateDepthStencilView(
                  depthTarget.Get(), nullptr, &hostDepthView)),
          "host depth-stencil binding must be created");

    constexpr std::int32_t width = 8;
    constexpr std::int32_t height = 8;
    constexpr std::int32_t stride = width * 4;
    std::vector<std::uint8_t> pixels(
        static_cast<std::size_t>(stride) * height,
        0xffu);
    Check(mailbox.Submit(pixels.data(), width, height, stride, 1),
          "CPU frame submission must succeed");
    Check(renderer.StageLatestCpuFrame(),
          "CPU frame must stage away from Present");
    Check(renderer.CpuFramesStaged() == 1,
          "CPU staging counter must advance");
    Check(renderer.CpuTextureRebuilds() == 1,
          "first CPU frame must precreate one texture");
    Check(mailbox.SharedFramesRead() == 1 &&
              mailbox.SharedBytesReferenced() == pixels.size(),
          "staging must share the producer-owned allocation without a pixel copy");

    Check(!renderer.Render(target.Get(), false),
          "an unprepared target must fail open");
    Check(renderer.UnpreparedRenderAttempts() == 1,
          "unprepared target attempt must be observable");
    Check(renderer.PrepareBackBuffer(target.Get()),
          "back buffer preparation must succeed off Present");
    Check(renderer.PipelineBuilds() == 1,
          "first back buffer preparation builds the D3D11 pipeline once");

    constexpr char SuppressingGeometryShader[] = R"(
struct Varying { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
[maxvertexcount(3)]
void main(triangle Varying input[3], inout TriangleStream<Varying> output) {
})";
    Microsoft::WRL::ComPtr<ID3DBlob> geometryBytecode;
    Microsoft::WRL::ComPtr<ID3DBlob> geometryErrors;
    Microsoft::WRL::ComPtr<ID3D11GeometryShader> hostGeometryShader;
    const bool geometryReady = SUCCEEDED(D3DCompile(
            SuppressingGeometryShader,
            sizeof(SuppressingGeometryShader) - 1,
            nullptr, nullptr, nullptr, "main", "gs_5_0",
            D3DCOMPILE_ENABLE_STRICTNESS, 0,
            &geometryBytecode, &geometryErrors)) &&
        SUCCEEDED(device->CreateGeometryShader(
            geometryBytecode->GetBufferPointer(),
            geometryBytecode->GetBufferSize(),
            nullptr, &hostGeometryShader));
    Check(geometryReady,
          "fixture creates a host geometry shader that suppresses triangles");

    ID3D11RenderTargetView* hostTargets[]{
        firstHostTarget.Get(), secondHostTarget.Get()};
    context->OMSetRenderTargets(2, hostTargets, hostDepthView.Get());
    context->GSSetShader(hostGeometryShader.Get(), nullptr, 0);
    constexpr FLOAT black[4]{};
    context->ClearRenderTargetView(firstHostTarget.Get(), black);
    Check(renderer.Render(target.Get(), false),
          "prepared overlay must render over an existing MRT pipeline");
    Microsoft::WRL::ComPtr<ID3D11GeometryShader> observedGeometryShader;
    context->GSGetShader(&observedGeometryShader, nullptr, nullptr);
    Check(observedGeometryShader.Get() == hostGeometryShader.Get(),
          "overlay draw restores the host geometry shader exactly");
    D3D11_TEXTURE2D_DESC readbackDescription = targetDescription;
    readbackDescription.Usage = D3D11_USAGE_STAGING;
    readbackDescription.BindFlags = 0;
    readbackDescription.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> readback;
    Check(SUCCEEDED(device->CreateTexture2D(
              &readbackDescription, nullptr, &readback)),
          "fixture creates overlay readback storage");
    context->CopyResource(readback.Get(), target.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    const bool mappedOverlay = readback != nullptr && SUCCEEDED(context->Map(
        readback.Get(), 0, D3D11_MAP_READ, 0, &mapped));
    Check(mappedOverlay &&
              static_cast<const std::uint8_t*>(mapped.pData)[0] == 0xffu,
          "overlay disables a suppressing host geometry shader for its draw");
    if (mappedOverlay) context->Unmap(readback.Get(), 0);
    std::array<
        ID3D11RenderTargetView*,
        D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT> observedTargets{};
    ID3D11DepthStencilView* observedDepth{};
    context->OMGetRenderTargets(
        static_cast<UINT>(observedTargets.size()),
        observedTargets.data(),
        &observedDepth);
    Check(observedTargets[0] == firstHostTarget.Get() &&
              observedTargets[1] == secondHostTarget.Get() &&
              observedDepth == hostDepthView.Get(),
          "overlay draw must restore every host MRT and DSV binding");
    bool higherTargetsRemainNull = true;
    for (std::size_t index = 2; index < observedTargets.size(); ++index) {
        higherTargetsRemainNull = higherTargetsRemainNull &&
            observedTargets[index] == nullptr;
    }
    Check(higherTargetsRemainNull,
          "overlay draw must not invent higher MRT bindings");
    for (auto* view : observedTargets) {
        if (view != nullptr) view->Release();
    }
    if (observedDepth != nullptr) observedDepth->Release();

    D3D11_BUFFER_DESC unorderedBufferDescription{};
    unorderedBufferDescription.ByteWidth = 16 * sizeof(std::uint32_t);
    unorderedBufferDescription.Usage = D3D11_USAGE_DEFAULT;
    unorderedBufferDescription.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
    unorderedBufferDescription.MiscFlags =
        D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    unorderedBufferDescription.StructureByteStride = sizeof(std::uint32_t);
    Microsoft::WRL::ComPtr<ID3D11Buffer> unorderedBuffer;
    Microsoft::WRL::ComPtr<ID3D11UnorderedAccessView> hostUnorderedAccessView;
    Check(SUCCEEDED(device->CreateBuffer(
              &unorderedBufferDescription, nullptr, &unorderedBuffer)) &&
          SUCCEEDED(device->CreateUnorderedAccessView(
              unorderedBuffer.Get(), nullptr, &hostUnorderedAccessView)),
          "host OM UAV fixture must be created");
    ID3D11UnorderedAccessView* hostUnorderedAccessViews[]{
        hostUnorderedAccessView.Get()};
    constexpr UINT keepCounter = UINT(-1);
    context->OMSetRenderTargetsAndUnorderedAccessViews(
        0, nullptr, hostDepthView.Get(),
        0, 1, hostUnorderedAccessViews, &keepCounter);
    Check(renderer.Render(target.Get(), false),
          "overlay renders while the host owns OM UAV slot zero");

    std::array<
        ID3D11RenderTargetView*,
        D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT> uavFixtureTargets{};
    ID3D11DepthStencilView* uavFixtureDepth{};
    context->OMGetRenderTargets(
        static_cast<UINT>(uavFixtureTargets.size()),
        uavFixtureTargets.data(), &uavFixtureDepth);
    std::array<
        ID3D11UnorderedAccessView*,
        D3D11_PS_CS_UAV_REGISTER_COUNT> observedUnorderedAccessViews{};
    context->OMGetRenderTargetsAndUnorderedAccessViews(
        0, nullptr, nullptr,
        0, static_cast<UINT>(observedUnorderedAccessViews.size()),
        observedUnorderedAccessViews.data());
    bool uavFixtureTargetsRemainNull = true;
    for (auto* view : uavFixtureTargets) {
        uavFixtureTargetsRemainNull =
            uavFixtureTargetsRemainNull && view == nullptr;
        if (view != nullptr) view->Release();
    }
    bool higherUnorderedAccessViewsRemainNull = true;
    for (std::size_t index = 1;
         index < observedUnorderedAccessViews.size(); ++index) {
        higherUnorderedAccessViewsRemainNull =
            higherUnorderedAccessViewsRemainNull &&
            observedUnorderedAccessViews[index] == nullptr;
    }
    Check(uavFixtureTargetsRemainNull &&
              uavFixtureDepth == hostDepthView.Get() &&
              observedUnorderedAccessViews[0] ==
                  hostUnorderedAccessView.Get() &&
              higherUnorderedAccessViewsRemainNull,
          "overlay restores exact zero-RTV, DSV, and OM UAV slot-zero state");
    if (uavFixtureDepth != nullptr) uavFixtureDepth->Release();
    for (auto* view : observedUnorderedAccessViews) {
        if (view != nullptr) view->Release();
    }

    const auto sharedReadAttempts = mailbox.SharedReadAttempts();
    const auto stagedFrames = renderer.CpuFramesStaged();
    const auto textureRebuilds = renderer.CpuTextureRebuilds();
    const auto preparedTargets = renderer.BackBuffersPrepared();
    for (int index = 0; index < 5; ++index) {
        Check(renderer.Render(target.Get(), index == 0),
              "prepared CPU overlay must render repeatedly");
    }
    Check(mailbox.SharedReadAttempts() == sharedReadAttempts,
          "Render must not read from the mailbox");
    Check(renderer.CpuFramesStaged() == stagedFrames,
          "Render must not stage or upload a CPU frame");
    Check(renderer.CpuTextureRebuilds() == textureRebuilds,
          "Render must not create a CPU texture");
    Check(renderer.BackBuffersPrepared() == preparedTargets,
          "Render must not create a render-target view");
    Check(renderer.RenderedFrames() == 7 && renderer.LastGeneration() == 1,
          "render statistics must describe the staged generation");

    pixels.front() = 0x20u;
    Check(mailbox.Submit(pixels.data(), width, height, stride, 2),
          "second CPU frame submission must succeed");
    Check(renderer.StageLatestCpuFrame(),
          "same-size CPU frame must reuse the staged texture");
    Check(renderer.CpuTextureRebuilds() == textureRebuilds,
          "same-size staging must reuse its precreated texture");
    Check(renderer.Render(target.Get(), false) && renderer.LastGeneration() == 2,
          "new CPU generation must render after explicit staging");

    renderer.InvalidateBackBuffer();
    const auto pipelineBuilds = renderer.PipelineBuilds();
    Check(!renderer.Render(target.Get(), false),
          "invalidated target must fail open until prepared again");
    Check(renderer.PrepareBackBuffer(target.Get()),
          "invalidated target can be explicitly prepared again");
    Check(renderer.PipelineBuilds() == pipelineBuilds,
          "backbuffer-only retirement must preserve compiled shaders and state");
    Check(renderer.Render(target.Get(), false),
          "reprepared target must resume rendering");

    for (UINT resizeCycle = 0; resizeCycle < 6; ++resizeCycle) {
        D3D11_TEXTURE2D_DESC resizedDescription = targetDescription;
        resizedDescription.Width = 96 + resizeCycle * 16;
        resizedDescription.Height = 54 + resizeCycle * 9;
        resizedDescription.Format = (resizeCycle % 2) == 0
            ? DXGI_FORMAT_R8G8B8A8_UNORM
            : DXGI_FORMAT_B8G8R8A8_UNORM;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> resizedTarget;
        Check(SUCCEEDED(device->CreateTexture2D(
                  &resizedDescription, nullptr, &resizedTarget)),
              "repeated-resize target must be created");
        renderer.InvalidateBackBuffer();
        Check(renderer.PrepareBackBuffer(resizedTarget.Get()),
              "repeated-resize target must be prepared");
        Check(renderer.PipelineBuilds() == pipelineBuilds,
              "repeated backbuffer replacement must not rebuild the pipeline");
        Check(renderer.Render(resizedTarget.Get(), false) &&
                  renderer.LastGeneration() == 2,
              "staged frame remains renderable across repeated backbuffer replacement");
    }

    rwui::D3D11OverlayRenderer privateContextRenderer(
        device.Get(), context.Get(), mailbox, false);
    Check(privateContextRenderer.PrepareBackBuffer(target.Get()) &&
              privateContextRenderer.StageLatestCpuFrame() &&
              privateContextRenderer.Render(target.Get(), false),
          "private D3D11On12-style context renders prepared content");
    Check(privateContextRenderer.HostStatePreservations() == 0,
          "private D3D11On12-style context skips host pipeline capture");
    Check(renderer.HostStatePreservations() > 0,
          "Legacy renderer retains complete host pipeline preservation");

    // Passive HUD: real GPU readback proves the small viewport does not cover
    // gameplay, and the existing host viewport survives every draw.
    Check(rwui::ValidStartupStatusFrame(560, 68, 2240) &&
          !rwui::ValidStartupStatusFrame(561, 68, 2244), "startup frame dimensions are bounded");
    Check(rwui::ShouldRenderStartupStatus(true, false, true) &&
          !rwui::ShouldRenderStartupStatus(true, true, true) &&
          !rwui::ShouldRenderStartupStatus(true, false, false) &&
          !rwui::ShouldRenderStartupStatus(false, false, true), "status is passive, Legacy-only, and hidden behind menus");
    for (const auto size : {std::pair{640,360}, std::pair{800,450}, std::pair{2560,1440}}) {
        rwui::FrameMailbox hudMailbox;
        std::vector<std::uint8_t> hudPixels(560 * 68 * 4, 0xff);
        // Transparent left half verifies premultiplied blending, not merely a rectangle.
        for (int y = 0; y < 68; ++y) for (int x = 0; x < 280; ++x)
            for (int c = 0; c < 4; ++c) hudPixels[(y * 560 + x) * 4 + c] = 0;
        Check(hudMailbox.Submit(hudPixels.data(), 560, 68, 2240, 1), "HUD frame accepted");
        rwui::D3D11OverlayRenderer hud(device.Get(), context.Get(), hudMailbox);
        auto desc = targetDescription;
        desc.Width = size.first; desc.Height = size.second; desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> hudTarget, hudReadback;
        Check(SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &hudTarget)), "HUD target created");
        Microsoft::WRL::ComPtr<ID3D11RenderTargetView> hudRtv;
        Check(SUCCEEDED(device->CreateRenderTargetView(hudTarget.Get(), nullptr, &hudRtv)), "HUD RTV created");
        const float clear[]{0, 0, 0, 1};
        context->ClearRenderTargetView(hudRtv.Get(), clear);
        Check(hud.PrepareBackBuffer(hudTarget.Get()) && hud.StageLatestCpuFrame(), "HUD is staged before draw");
        const auto placement = rwui::StartupStatusPlacement(size.first, size.second);
        const D3D11_VIEWPORT hudViewport{placement.x, placement.y, placement.width, placement.height, 0, 1};
        const D3D11_VIEWPORT hostViewport{3, 5, 100, 100, 0, 1};
        context->RSSetViewports(1, &hostViewport);
        Check(hud.Render(hudTarget.Get(), false, &hudViewport), "HUD viewport draws");
        D3D11_VIEWPORT restored{}; UINT viewportCount = 1;
        context->RSGetViewports(&viewportCount, &restored);
        Check(viewportCount == 1 && restored.TopLeftX == 3 && restored.TopLeftY == 5 && restored.Width == 100,
            "HUD restores game viewport");
        desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        Check(SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &hudReadback)), "HUD readback created");
        context->CopyResource(hudReadback.Get(), hudTarget.Get());
        D3D11_MAPPED_SUBRESOURCE hudMap{};
        if (SUCCEEDED(context->Map(hudReadback.Get(), 0, D3D11_MAP_READ, 0, &hudMap))) {
            const auto sample = [&](int x, int y) { return static_cast<const std::uint8_t*>(hudMap.pData)[y * hudMap.RowPitch + x * 4]; };
            Check(sample(1, 1) == 0 && sample(size.first / 2, size.second / 2) == 0,
                "HUD leaves gameplay pixels unchanged");
            Check(sample(static_cast<int>(placement.x + 10), 30) == 0,
                "transparent HUD pixels do not black out game");
            Check(sample(size.first - 35, 30) > 240, "HUD reaches top-right with safe margin");
            context->Unmap(hudReadback.Get(), 0);
        } else Check(false, "HUD readback maps");
        Check(hud.CpuFramesStaged() == 1, "HUD draw does not reupload texture");
    }

    if (failures == 0) {
        std::cout << "PASS: D3D11 overlay hot-path tests\n";
    }
    return failures == 0 ? 0 : 1;
}
