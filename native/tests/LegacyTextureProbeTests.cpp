#include "LegacyTextureProbe.h"
#include <fstream>
#include <iostream>
#include <sstream>
#include <vector>
using Microsoft::WRL::ComPtr;
using namespace rwui::probe;
int failures{};
void Check(bool ok, const char* label) {
    std::cout << (ok ? "PASS: " : "FAIL: ") << label << '\n';
    if (!ok) ++failures;
}
std::string Read(const std::filesystem::path& path) {
    std::ifstream in(path); return {std::istreambuf_iterator<char>(in), {}};
}
int main() {
    Packet p{}; p.ownerPid = GetCurrentProcessId(); p.textureHandle = 123; p.kind = ULONG(Kind::Nt);
    Check(ValidPacket(p, p.ownerPid), "fixed NT packet accepted");
    Check(!ValidPacket(p, p.ownerPid + 1), "wrong owner rejected");
    ++p.width; Check(!ValidPacket(p, p.ownerPid), "unbounded dimensions rejected"); --p.width;
    p.kind = 19; Check(!ValidPacket(p, p.ownerPid), "unknown handle type rejected"); p.kind = ULONG(Kind::Kmt);
    Check(ValidPacket(p, p.ownerPid), "explicit KMT kind accepted");
    ++p.version; Check(!ValidPacket(p, p.ownerPid), "unknown version rejected"); --p.version;
    --p.bytes; Check(!ValidPacket(p, p.ownerPid), "wrong structure size rejected"); ++p.bytes;
    p.textureHandle = UINT64_MAX; Check(!ValidPacket(p, p.ownerPid), "invalid handle sentinel rejected");
    Check(TextureProbeChild(nullptr, nullptr) == 2, "helper rejects missing inherited handles");

    wchar_t executable[32768]{}, temp[32768]{};
    GetModuleFileNameW(nullptr, executable, 32768); GetTempPathW(32768, temp);
    const auto helper = std::filesystem::path(executable).parent_path() / L"ReactorV.TextureProbe.Partner.exe";
    const auto root = std::filesystem::path(temp) / (L"ReactorV-TextureProbe-Test-" +
        std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    if (!CreateDirectoryW(root.c_str(), nullptr)) return 1;
    struct Cleanup { std::filesystem::path root; ~Cleanup() {
        // This directory was created exclusively by this test above.
        std::error_code error; std::filesystem::remove_all(root, error);
    } } cleanup{root};
    ComPtr<ID3D11Device> game; ComPtr<ID3D11DeviceContext> context;
    const D3D_FEATURE_LEVEL level[]{D3D_FEATURE_LEVEL_11_0};
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        level, 1, D3D11_SDK_VERSION, &game, nullptr, &context))) return 125;
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = Width; desc.Height = Height;
    desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> target, staging;
    Check(SUCCEEDED(game->CreateTexture2D(&desc, nullptr, &target)), "test target created");
    desc.BindFlags = 0; desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    Check(SUCCEEDED(game->CreateTexture2D(&desc, nullptr, &staging)), "test readback created");
    if (!target || !staging) return 1;
    ComPtr<ID3D11RenderTargetView> rtv;
    Check(SUCCEEDED(game->CreateRenderTargetView(target.Get(), nullptr, &rtv)), "test RTV created");
    rwui::FrameMailbox mailbox; rwui::D3D11OverlayRenderer renderer(game.Get(), context.Get(), mailbox);
    Check(renderer.PrepareBackBuffer(target.Get()), "diagnostic pipeline prepared off Present");
    LegacyTextureProbe probe;
    Check(!probe.Active() && !probe.Draw(renderer, target.Get(), context.Get(), false), "disabled probe cannot draw");
    const auto log = root / L"probe.log";
    probe.Configure(helper, log); probe.SetDisplayMillisecondsForTest(350); probe.Start(game.Get());
    unsigned pixelFrames{}, colorFailures{};
    const auto deadline = GetTickCount64() + 20000;
    while (probe.Active() && GetTickCount64() < deadline) {
        const float clear[]{0, 0, 0, 0}; context->ClearRenderTargetView(rtv.Get(), clear);
        if (probe.Draw(renderer, target.Get(), context.Get(), false)) {
            // Readback belongs ONLY to this standalone test, not the hook.
            context->CopyResource(staging.Get(), target.Get());
            D3D11_MAPPED_SUBRESOURCE mapped{};
            if (SUCCEEDED(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped))) {
                const auto* row = reinterpret_cast<const UINT*>(static_cast<const BYTE*>(mapped.pData) + 11 * mapped.RowPitch);
                if (row[12] != 0xffff0000 || row[28] != 0xff00ff00 || row[44] != 0xff0000ff) ++colorFailures;
                context->Unmap(staging.Get(), 0); ++pixelFrames;
            }
        }
        Sleep(2);
    }
    const bool finished = !probe.Active(); probe.Stop();
    Check(finished, "bounded three-stage probe completed");
    const auto report = Read(log);
    for (const auto* mode : {"local", "outward_nt", "outward_kmt"}) {
        Check(report.find(std::string("mode=") + mode + " step=game_draw_submitted hr=0x0 result=PASS") != std::string::npos,
            mode);
    }
    Check(report.find("mode=outward_nt step=pixels hr=0x0 result=PASS") != std::string::npos &&
        report.find("mode=outward_kmt step=pixels hr=0x0 result=PASS") != std::string::npos,
        "real external process fills and pixel-verifies both game-owned transports");
    Check(pixelFrames > 0 && colorFailures == 0, "game renderer samples received shared pixels correctly");
    Check(report.find("exclusive_fullscreen_draws=0") != std::string::npos &&
        report.find("onscreen_visibility=USER_VERIFICATION_REQUIRED menu_ready=UNMODIFIED") != std::string::npos,
        "offscreen submission cannot claim fullscreen visibility or menu readiness");
    Check(!probe.Draw(renderer, target.Get(), context.Get(), false), "stopped probe has no visible/input authority");

    LegacyTextureProbe missing;
    const auto missingLog = root / L"missing.log";
    missing.Configure(root / L"ReactorV.TextureProbe.Partner.exe", missingLog);
    missing.SetDisplayMillisecondsForTest(1); missing.Start(game.Get());
    const auto missingDeadline = GetTickCount64() + 5000;
    while (missing.Active() && GetTickCount64() < missingDeadline) Sleep(5);
    Check(!missing.Active(), "missing helper fails without retry storm"); missing.Stop();
    const auto missingReport = Read(missingLog);
    Check(missingReport.find("step=partner_complete hr=0x80004005 result=FAIL") != std::string::npos &&
        missingReport.find("step=pixels hr=0x8000000a result=NOT_RUN") != std::string::npos,
        "unexecuted partner checks remain NOT_RUN, not success");

    LegacyTextureProbe cancel;
    cancel.Configure(helper, root / L"cancel.log"); cancel.Start(game.Get());
    const auto before = GetTickCount64(); cancel.Stop();
    Check(GetTickCount64() - before < 2000, "cancellation does not wait through display phases");
    std::cout << "readback_frames=" << pixelFrames << " color_failures=" << colorFailures << '\n';
    return failures ? 1 : 0;
}
