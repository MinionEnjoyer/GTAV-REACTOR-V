#include "LegacyVisibilityProbe.h"
#include <algorithm>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <vector>

namespace rwui::probe {
using Microsoft::WRL::ComPtr;
LegacyVisibilityProbe::~LegacyVisibilityProbe() { Stop(); }

void LegacyVisibilityProbe::Configure(const std::filesystem::path& log) {
    Stop();
    if (!log.is_absolute()) return;
    log_ = log; stop_.store(false); enabled_.store(true); rearm_.store(true);
    worker_ = std::thread([this] { Worker(); });
}
void LegacyVisibilityProbe::ResetResources() {
    pending_ = false; active_ = false; lastDrawTick_ = 0;
    pendingPattern_ = false;
    patternRenderer_.reset(); patternView_.Reset();
    patternUploadHr_ = patternViewHr_ = patternPipelineHr_ = E_PENDING;
    query_.Reset(); staging_.Reset(); rtv_.Reset(); buffer_.Reset(); chain_.Reset(); context_.Reset();
}
void LegacyVisibilityProbe::Invalidate() {
    std::scoped_lock lock(mutex_); ResetResources(); ++revision_;
}
void LegacyVisibilityProbe::Stop() {
    enabled_.store(false); stop_.store(true);
    if (worker_.joinable()) worker_.join();
    std::scoped_lock lock(mutex_); ResetResources();
}
bool LegacyVisibilityProbe::Prepare(IDXGISwapChain* chain, ID3D11Texture2D* buffer,
    ID3D11Device* device, ID3D11DeviceContext* context) {
    if (!Enabled() || !chain || !buffer || !device || !context) return false;
    std::scoped_lock lock(mutex_);
    ResetResources(); ++revision_;
    prepareHr_ = context->QueryInterface(IID_PPV_ARGS(&context_));
    if (FAILED(prepareHr_)) return false;
    chain_ = chain; buffer_ = buffer;
    prepareHr_ = chain->GetDesc(&chainDesc_);
    if (FAILED(prepareHr_)) return false;
    buffer->GetDesc(&bufferDesc_);
    const auto format = bufferDesc_.Format;
    // Strict readback format contract: don't interpret HDR/MSAA bytes as BGRA.
    if (bufferDesc_.Width < 64 || bufferDesc_.Height < 64 || bufferDesc_.SampleDesc.Count != 1 ||
        (format != DXGI_FORMAT_B8G8R8A8_UNORM && format != DXGI_FORMAT_R8G8B8A8_UNORM)) {
        prepareHr_ = E_INVALIDARG; return false;
    }
    prepareHr_ = device->CreateRenderTargetView(buffer, nullptr, &rtv_);
    if (FAILED(prepareHr_)) return false;
    D3D11_TEXTURE2D_DESC read{};
    read.Width = SampleCount; read.Height = read.MipLevels = read.ArraySize = read.SampleDesc.Count = 1;
    read.Format = format; read.Usage = D3D11_USAGE_STAGING; read.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    prepareHr_ = device->CreateTexture2D(&read, nullptr, &staging_);
    if (FAILED(prepareHr_)) return false;
    const D3D11_QUERY_DESC q{D3D11_QUERY_EVENT, 0};
    prepareHr_ = device->CreateQuery(&q, &query_);
    if (FAILED(prepareHr_)) return false;
    const LONG w = static_cast<LONG>(bufferDesc_.Width), h = static_cast<LONG>(bufferDesc_.Height);
    outer_ = {w / 3, h * 2 / 5, w * 2 / 3, h * 3 / 5};
    left_ = {outer_.left + 3, outer_.top + 3, w / 2, outer_.bottom - 3};
    right_ = {w / 2, outer_.top + 3, outer_.right - 3, outer_.bottom - 3};
    samplePoints_[0] = {outer_.left + 1, (outer_.top + outer_.bottom) / 2};
    samplePoints_[1] = {left_.left + 1, samplePoints_[0].y};
    samplePoints_[2] = {right_.left + 1, samplePoints_[0].y};
    // A separate plate below the proven control gives alpha tests a known
    // opaque black destination. The texture is transparent everywhere else.
    patternBackground_ = {w / 4, h * 2 / 3, w * 3 / 4, h * 328 / 360};
    PreparePattern(device); // Diagnostic texture failure never suppresses ClearView.
    rearm_.store(true); prepareHr_ = S_OK;
    return true;
}

UINT LegacyVisibilityProbe::PatternPixel(const UINT x, const UINT y) noexcept {
    if (x < 160 || x >= 480 || y < 240 || y >= 328) return 0;
    if (x < 164 || x >= 476 || y < 244 || y >= 324) return 0xffffffffu;
    if (y < 280) {
        if (x < 244) return 0xffff0000u;
        if (x < 324) return 0xff00ff00u;
        if (x < 404) return 0xff0000ffu;
        return ((x / 8 + y / 8) & 1) ? 0xffffffffu : 0xff000000u;
    }
    if (y < 288) return 0xff202020u;
    // Premultiplied red, green, white at 50% alpha, then fully transparent.
    if (x < 244) return 0x80800000u;
    if (x < 324) return 0x80008000u;
    if (x < 404) return 0x80808080u;
    return 0;
}

void LegacyVisibilityProbe::PreparePattern(ID3D11Device* device) {
    const POINT locations[PatternSamples]{{200, 260}, {280, 260}, {360, 260}, {444, 260}, {452, 260},
        {200, 310}, {280, 310}, {360, 310}, {440, 310}};
    for (UINT i = 0; i < PatternSamples; ++i) {
        samplePoints_[3 + i] = {static_cast<LONG>(locations[i].x * bufferDesc_.Width / PatternWidth),
            static_cast<LONG>(locations[i].y * bufferDesc_.Height / PatternHeight)};
        // All pattern samples lie well inside constant-color cells. Blending
        // premultiplied source over opaque black leaves RGB unchanged, alpha=1.
        UINT pixel = PatternPixel(locations[i].x, locations[i].y) | 0xff000000u;
        if (bufferDesc_.Format == DXGI_FORMAT_R8G8B8A8_UNORM)
            pixel = (pixel & 0xff00ff00u) | ((pixel & 0x00ff0000u) >> 16) | ((pixel & 0xffu) << 16);
        patternExpected_[i] = pixel;
    }
    std::vector<UINT> pixels(PatternWidth * PatternHeight);
    for (UINT y = 0; y < PatternHeight; ++y) for (UINT x = 0; x < PatternWidth; ++x) {
        const auto expected = PatternPixel(x, y);
        // Harness-only fault injection proves successful clears cannot hide a
        // wrong textured result. This switch is not exposed in the game ABI.
        pixels[y * PatternWidth + x] = testMode_ && corruptPatternForTest_ && expected ? 0xff202020u : expected;
    }
    D3D11_TEXTURE2D_DESC d{};
    d.Width = PatternWidth; d.Height = PatternHeight; d.MipLevels = d.ArraySize = d.SampleDesc.Count = 1;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    // Initial CPU bytes, uploaded once off Present. Deliberately no sharing flags.
    const D3D11_SUBRESOURCE_DATA initial{pixels.data(), PatternWidth * sizeof(UINT), 0};
    ComPtr<ID3D11Texture2D> texture;
    patternUploadHr_ = device->CreateTexture2D(&d, &initial, &texture);
    if (patternUploadHr_ != S_OK) return;
    patternViewHr_ = device->CreateShaderResourceView(texture.Get(), nullptr, &patternView_);
    if (patternViewHr_ != S_OK) return;
    patternRenderer_ = std::make_unique<D3D11OverlayRenderer>(device, context_.Get(), patternMailbox_);
    patternPipelineHr_ = patternRenderer_->PrepareBackBuffer(buffer_.Get()) ? S_OK : E_FAIL;
}
void LegacyVisibilityProbe::PollSample(const ULONGLONG now) {
    if (!pending_) return;
    const auto hr = context_->GetData(query_.Get(), nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
    if (hr == S_FALSE || hr == DXGI_ERROR_WAS_STILL_DRAWING) {
        if (now - sampleTick_ > 2000) {
            // Don't recycle an in-flight GPU query. Disable sampling until resize/rearm preparation.
            if (sampleHr_ != HRESULT_FROM_WIN32(ERROR_TIMEOUT)) ++timeouts_;
            sampleHr_ = HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        }
        return;
    }
    sampleHr_ = hr;
    if (hr != S_OK) { pending_ = false; nextSampleTick_ = now + 1000; return; }
    D3D11_MAPPED_SUBRESOURCE read{};
    sampleHr_ = context_->Map(staging_.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &read);
    if (sampleHr_ == DXGI_ERROR_WAS_STILL_DRAWING) return;
    pending_ = false;
    if (sampleHr_ != S_OK) return;
    std::memcpy(pixels_, read.pData, sizeof(pixels_)); context_->Unmap(staging_.Get(), 0);
    const UINT cyan = bufferDesc_.Format == DXGI_FORMAT_B8G8R8A8_UNORM ? 0xff00ffffu : 0xffffff00u;
    ++samples_;
    if (pixels_[0] == 0xffffffffu && pixels_[1] == cyan && pixels_[2] == 0xffff00ffu) ++matches_;
    else ++mismatches_;
    if (pendingPattern_) {
        ++patternChecks_;
        bool match = true;
        for (UINT i = 0; i < PatternSamples; ++i) match &= pixels_[3 + i] == patternExpected_[i];
        if (match) ++patternMatches_; else ++patternMismatches_;
    } else ++patternNotRun_;
}
bool LegacyVisibilityProbe::Draw(IDXGISwapChain* chain) noexcept {
    if (!Enabled()) return false;
    try {
        std::unique_lock lock(mutex_, std::try_to_lock);
        if (!lock.owns_lock() || prepareHr_ != S_OK || !rtv_) return false;
        if (chain != chain_.Get()) { ++wrongChain_; ++revision_; return false; }
        const auto now = GetTickCount64();
        if (rearm_.exchange(false)) { active_ = true; elapsedMs_ = 0; lastDrawTick_ = 0; nextSampleTick_ = 0; }
        if (!active_ && !pending_) return false;
        // Resolve buffer zero from the actual intercepted Present, not an assumed swap-chain index.
        ComPtr<ID3D11Texture2D> actual;
        if (FAILED(chain->GetBuffer(0, IID_PPV_ARGS(&actual))) || actual.Get() != buffer_.Get()) {
            ++wrongBuffer_; ++revision_; return false;
        }
        PollSample(now);
        foreground_ = GetForegroundWindow() == chainDesc_.OutputWindow;
        BOOL exclusive{};
        fullscreen_ = chain->GetFullscreenState(&exclusive, nullptr) == S_OK && exclusive;
        if (!active_ || (!testMode_ && (!foreground_ || IsIconic(chainDesc_.OutputWindow)))) {
            lastDrawTick_ = 0; ++revision_; return false;
        }
        if (lastDrawTick_) elapsedMs_ += static_cast<UINT>(std::min<ULONGLONG>(now - lastDrawTick_, 250));
        lastDrawTick_ = now;
        if (elapsedMs_ >= durationMs_) { active_ = false; ++revision_; return false; }
        // Keep the confirmed ClearView control independent of the shader test.
        // Predication can suppress resource operations; save/disable/restore explicitly.
        ComPtr<ID3D11Predicate> predicate; BOOL predicateValue{};
        context_->GetPredication(&predicate, &predicateValue); context_->SetPredication(nullptr, FALSE);
        const FLOAT white[]{1, 1, 1, 1}, cyan[]{0, 1, 1, 1}, magenta[]{1, 0, 1, 1};
        context_->ClearView(rtv_.Get(), white, &outer_, 1);
        context_->ClearView(rtv_.Get(), cyan, &left_, 1);
        context_->ClearView(rtv_.Get(), magenta, &right_, 1);
        const FLOAT black[]{0, 0, 0, 1};
        context_->ClearView(rtv_.Get(), black, &patternBackground_, 1);
        // Same shader/sampler/blend/state preservation as the menu renderer,
        // but a dedicated local texture and entirely separate readiness counters.
        const bool patternRendered = patternPipelineHr_ == S_OK && patternRenderer_ && patternView_ &&
            patternRenderer_->RenderShared(actual.Get(), patternView_.Get(), 1, false);
        if (patternRendered) ++patternDraws_; else ++patternDrawFailures_;
        if (!pending_ && now >= nextSampleTick_) {
            for (UINT i = 0; i < SampleCount; ++i) {
                const auto x = static_cast<UINT>(samplePoints_[i].x), y = static_cast<UINT>(samplePoints_[i].y);
                const D3D11_BOX box{x, y, 0, x + 1, y + 1, 1};
                context_->CopySubresourceRegion(staging_.Get(), 0, i, 0, 0, actual.Get(), 0, &box);
            }
            pendingPattern_ = patternRendered;
            context_->End(query_.Get()); pending_ = true; sampleTick_ = now; nextSampleTick_ = now + 1000;
        }
        context_->SetPredication(predicate.Get(), predicateValue);
        ++draws_; if (fullscreen_) ++fullscreenDraws_; ++revision_;
        return true;
    } catch (...) { return false; }
}
void LegacyVisibilityProbe::Presented(const HRESULT result) noexcept {
    presentHr_.store(result);
    if (result == S_OK) ++committed_;
    else if (result == DXGI_STATUS_OCCLUDED) ++occluded_;
    else ++failed_;
    ++revision_;
}
void LegacyVisibilityProbe::Report(const char* event) {
    std::scoped_lock lock(mutex_);
    SYSTEMTIME now{}; GetLocalTime(&now);
    std::ofstream out(log_, std::ios::app);
    out << std::setfill('0') << std::setw(4) << now.wYear << '-' << std::setw(2) << now.wMonth << '-'
        << std::setw(2) << now.wDay << 'T' << std::setw(2) << now.wHour << ':' << std::setw(2) << now.wMinute
        << ':' << std::setw(2) << now.wSecond << " pid=" << GetCurrentProcessId() << " probe=visibility_v3 event=" << event
        << " chain=" << chain_.Get() << " hwnd=" << chainDesc_.OutputWindow << " buffer=" << buffer_.Get()
        << " size=" << bufferDesc_.Width << 'x' << bufferDesc_.Height << " format=" << bufferDesc_.Format
        << " swap_effect=" << chainDesc_.SwapEffect << " swap_flags=" << chainDesc_.Flags
        << " foreground=" << foreground_ << " exclusive_fullscreen=" << fullscreen_
        << " active=" << active_ << " foreground_ms=" << elapsedMs_
        << " prepare_hr=0x" << std::hex << static_cast<ULONG>(prepareHr_)
        << " sample_hr=0x" << static_cast<ULONG>(sampleHr_) << " present_hr=0x" << static_cast<ULONG>(presentHr_.load())
        << " pixels=" << pixels_[0] << ',' << pixels_[1] << ',' << pixels_[2]
        << " texture_upload_hr=0x" << static_cast<ULONG>(patternUploadHr_)
        << " texture_srv_hr=0x" << static_cast<ULONG>(patternViewHr_)
        << " texture_pipeline_hr=0x" << static_cast<ULONG>(patternPipelineHr_)
        << " texture_pixels=";
    for (UINT i = 0; i < PatternSamples; ++i) out << (i ? "," : "") << pixels_[3 + i];
    out << std::dec
        << " texture_draws=" << patternDraws_ << " texture_draw_failures=" << patternDrawFailures_
        << " texture_checks=" << patternChecks_ << " texture_matches=" << patternMatches_
        << " texture_mismatches=" << patternMismatches_ << " texture_checks_not_run=" << patternNotRun_
        << " clear_submissions=" << draws_ << " exclusive_submissions=" << fullscreenDraws_
        << " sample_checks=" << samples_ << " pixel_matches=" << matches_ << " pixel_mismatches=" << mismatches_
        << " sample_timeouts=" << timeouts_ << " sample_pending=" << pending_
        << " present_ok=" << committed_.load() << " present_occluded=" << occluded_.load() << " present_other=" << failed_.load()
        << " chain_mismatch=" << wrongChain_ << " buffer_mismatch=" << wrongBuffer_
        << " placement=after_menu_before_original_present onscreen_visibility=USER_VERIFICATION_REQUIRED menu_ready=UNMODIFIED\n";
}
void LegacyVisibilityProbe::Worker() noexcept {
    try {
        Report("armed_ctrl_shift_f8_toggle");
        UINT64 seen = UINT64_MAX; ULONGLONG next{}; bool keyWasDown{};
        while (!stop_.load()) {
            const bool key = (GetAsyncKeyState(VK_CONTROL) & 0x8000) &&
                (GetAsyncKeyState(VK_SHIFT) & 0x8000) && (GetAsyncKeyState(VK_F8) & 0x8000);
            if (key && !keyWasDown) {
                std::scoped_lock lock(mutex_);
                if (chainDesc_.OutputWindow && GetForegroundWindow() == chainDesc_.OutputWindow) {
                    if (active_) { active_ = false; rearm_.store(false); }
                    else rearm_.store(true);
                    ++revision_;
                }
            }
            keyWasDown = key;
            if (GetTickCount64() >= next && seen != revision_.load()) {
                seen = revision_.load(); Report("sample"); next = GetTickCount64() + 1000;
            }
            Sleep(20);
        }
        Report("stopped");
    } catch (...) { enabled_.store(false); }
}
}
