#include "LegacyTextureProbe.h"
#include <d3d11_1.h>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <vector>

namespace rwui::probe {
using Microsoft::WRL::ComPtr;
namespace {
struct Handle {
    HANDLE value{};
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
struct Mapping {
    Packet* value{};
    ~Mapping() { if (value) UnmapViewOfFile(value); }
};
struct Attributes {
    std::vector<unsigned char> storage;
    LPPROC_THREAD_ATTRIBUTE_LIST list{};
    ~Attributes() { if (list) DeleteProcThreadAttributeList(list); }
};
constexpr const char* Names[]{"device", "device1", "duplicate_nt", "open_shared",
    "keyed_mutex", "acquire", "source", "completion_query", "copy_complete",
    "staging", "map", "pixels", "release"};
static_assert(std::size(Names) == static_cast<size_t>(Step::Count));
D3D11_TEXTURE2D_DESC Description(Kind kind) {
    D3D11_TEXTURE2D_DESC d{};
    d.Width = Width; d.Height = Height;
    d.MipLevels = d.ArraySize = d.SampleDesc.Count = 1;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    if (kind != Kind::Local) d.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    if (kind == Kind::Nt) d.MiscFlags |= D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    return d;
}
bool Record(Packet& p, Step step, HRESULT hr) {
    p.results[static_cast<ULONG>(step)] = hr;
    return hr == S_OK;
}
HRESULT WaitGpu(ID3D11DeviceContext* context, ID3D11Query* query, HANDLE owner) {
    const auto deadline = GetTickCount64() + 1000;
    HRESULT hr = S_FALSE;
    do {
        hr = context->GetData(query, nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (hr != S_FALSE) return hr;
        if (WaitForSingleObject(owner, 0) != WAIT_TIMEOUT) return E_ABORT;
        Sleep(1);
    } while (GetTickCount64() < deadline);
    return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
}
HRESULT CreateDevice(LUID luid, ComPtr<ID3D11Device>& device,
    ComPtr<ID3D11DeviceContext>& context) {
    ComPtr<IDXGIFactory1> factory;
    auto hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return hr;
    for (UINT i = 0; ; ++i) {
        ComPtr<IDXGIAdapter1> adapter;
        if (factory->EnumAdapters1(i, &adapter) != S_OK) return DXGI_ERROR_NOT_FOUND;
        DXGI_ADAPTER_DESC1 d{};
        if (FAILED(adapter->GetDesc1(&d))) continue;
        if (d.AdapterLuid.HighPart != luid.HighPart || d.AdapterLuid.LowPart != luid.LowPart) continue;
        const D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_0};
        return D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 1, D3D11_SDK_VERSION,
            &device, nullptr, &context);
    }
}
}

bool ValidPacket(const Packet& p, DWORD ownerPid) noexcept {
    return p.magic == Magic && p.version == Version && p.bytes == sizeof(Packet) &&
        p.ownerPid == ownerPid && ownerPid != 0 && p.width == Width && p.height == Height &&
        (p.kind == static_cast<ULONG>(Kind::Nt) || p.kind == static_cast<ULONG>(Kind::Kmt)) &&
        p.textureHandle != 0 && p.textureHandle != UINT64_MAX;
}

UINT Pixel(UINT x, UINT y, Kind kind) noexcept {
    // Small upper-left swatches only: transparent everywhere else. The number
    // of bars identifies local (1), NT (2), KMT (3), even without a font engine.
    if (x < 8 || x >= 72 || y < 8 || y >= 8 + 8 * (1 + static_cast<UINT>(kind))) return 0;
    if (x < 24) return 0xffff0000; // opaque red
    if (x < 40) return 0xff00ff00; // opaque green
    if (x < 56) return 0xff0000ff; // opaque blue
    return 0x80808080; // premultiplied half-alpha white
}

int TextureProbeChild(HANDLE mapping, HANDLE owner) noexcept {
    try {
        Mapping m{static_cast<Packet*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Packet)))};
        if (!m.value || WaitForSingleObject(owner, 0) != WAIT_TIMEOUT ||
            !ValidPacket(*m.value, GetProcessId(owner))) return 2;
        auto& p = *m.value;
        const auto kind = static_cast<Kind>(p.kind);
        auto work = [&]() -> bool {
            ComPtr<ID3D11Device> device;
            ComPtr<ID3D11DeviceContext> context;
            if (!Record(p, Step::Device, CreateDevice({p.adapterLow, p.adapterHigh}, device, context))) return false;
            ComPtr<ID3D11Texture2D> texture;
            Handle duplicated;
            auto handle = reinterpret_cast<HANDLE>(p.textureHandle);
            if (kind == Kind::Nt) {
                ComPtr<ID3D11Device1> device1;
                if (!Record(p, Step::Interface, device.As(&device1))) return false;
                const auto ok = DuplicateHandle(owner, handle, GetCurrentProcess(),
                    &duplicated.value, 0, FALSE, DUPLICATE_SAME_ACCESS);
                if (!Record(p, Step::Duplicate, ok ? S_OK : HRESULT_FROM_WIN32(GetLastError()))) return false;
                if (!Record(p, Step::Open, device1->OpenSharedResource1(duplicated.value, IID_PPV_ARGS(&texture)))) return false;
            } else {
                // KMT handles are not kernel handles: never duplicate/close them.
                if (!Record(p, Step::Open, device->OpenSharedResource(handle, IID_PPV_ARGS(&texture)))) return false;
            }
            D3D11_TEXTURE2D_DESC actual{};
            texture->GetDesc(&actual);
            if (actual.Width != Width || actual.Height != Height || actual.Format != DXGI_FORMAT_B8G8R8A8_UNORM ||
                actual.MipLevels != 1 || actual.ArraySize != 1 || actual.SampleDesc.Count != 1)
                return Record(p, Step::Pixels, E_INVALIDARG);
            ComPtr<IDXGIKeyedMutex> keyed;
            if (!Record(p, Step::Mutex, texture.As(&keyed))) return false;
            if (!Record(p, Step::Acquire, keyed->AcquireSync(0, 0))) return false;
            auto fill = [&]() -> bool {
                auto d = Description(Kind::Local);
                std::vector<UINT> pixels(Width * Height);
                for (UINT y = 0; y < Height; ++y) for (UINT x = 0; x < Width; ++x)
                    pixels[y * Width + x] = Pixel(x, y, kind);
                D3D11_SUBRESOURCE_DATA initial{pixels.data(), Width * 4, 0};
                ComPtr<ID3D11Texture2D> source, staging;
                if (!Record(p, Step::Source, device->CreateTexture2D(&d, &initial, &source))) return false;
                d.BindFlags = 0; d.Usage = D3D11_USAGE_STAGING; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                if (!Record(p, Step::Staging, device->CreateTexture2D(&d, nullptr, &staging))) return false;
                ComPtr<ID3D11Query> query;
                D3D11_QUERY_DESC q{D3D11_QUERY_EVENT, 0};
                if (!Record(p, Step::Query, device->CreateQuery(&q, &query))) return false;
                context->CopyResource(texture.Get(), source.Get());
                context->CopyResource(staging.Get(), texture.Get());
                context->End(query.Get()); context->Flush();
                if (!Record(p, Step::CopyComplete, WaitGpu(context.Get(), query.Get(), owner))) return false;
                D3D11_MAPPED_SUBRESOURCE read{};
                if (!Record(p, Step::Map, context->Map(staging.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &read))) return false;
                bool match = true;
                for (UINT y = 0; y < Height; ++y) for (UINT x = 0; x < Width; ++x) {
                    const auto* row = reinterpret_cast<const UINT*>(static_cast<const BYTE*>(read.pData) + y * read.RowPitch);
                    match &= row[x] == Pixel(x, y, kind);
                }
                context->Unmap(staging.Get(), 0);
                return Record(p, Step::Pixels, match ? S_OK : E_FAIL);
            };
            const bool filled = fill();
            const bool released = Record(p, Step::Release, keyed->ReleaseSync(1));
            return filled && released;
        };
        const bool ok = work();
        InterlockedExchange(&p.done, ok ? 1 : -1);
        return ok ? 0 : 1;
    } catch (...) { return 3; }
}

LegacyTextureProbe::~LegacyTextureProbe() { Stop(); }
void LegacyTextureProbe::Configure(const std::filesystem::path& helper, const std::filesystem::path& log) {
    Stop(); helper_ = helper; log_ = log;
}
void LegacyTextureProbe::Start(ID3D11Device* device) {
    if (worker_.joinable() || !device || helper_.empty() || log_.empty()) return;
    stop_.store(false); active_.store(true);
    worker_ = std::thread([this, device = ComPtr<ID3D11Device>(device)] { Run(device); });
}
void LegacyTextureProbe::Stop() noexcept {
    stop_.store(true);
    if (worker_.joinable()) worker_.join();
    std::scoped_lock lock(viewMutex_);
    view_.Reset(); keyed_.Reset(); active_.store(false);
}
void LegacyTextureProbe::Log(const char* mode, const char* step, HRESULT hr) {
    SYSTEMTIME now{}; GetLocalTime(&now);
    std::ofstream out(log_, std::ios::app);
    out << std::setfill('0') << std::setw(4) << now.wYear << '-' << std::setw(2) << now.wMonth << '-'
        << std::setw(2) << now.wDay << 'T' << std::setw(2) << now.wHour << ':' << std::setw(2) << now.wMinute
        << ':' << std::setw(2) << now.wSecond << '.' << std::setw(3) << now.wMilliseconds
        << " pid=" << GetCurrentProcessId() << " mode=" << mode << " step=" << step
        << " hr=0x" << std::hex << static_cast<ULONG>(hr) << std::dec
        << " result=" << (hr == S_OK ? "PASS" : hr == E_PENDING ? "NOT_RUN" : "FAIL") << '\n';
}

bool LegacyTextureProbe::Partner(Packet& packet) {
    SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
    Handle mapping{CreateFileMappingW(INVALID_HANDLE_VALUE, &security, PAGE_READWRITE, 0, sizeof(Packet), nullptr)};
    Log("partner", "create_mapping", mapping.value ? S_OK : HRESULT_FROM_WIN32(GetLastError()));
    if (!mapping.value) return false;
    Handle owner;
    const bool ownerOk = DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), GetCurrentProcess(),
        &owner.value, PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, TRUE, 0) != FALSE;
    Log("partner", "owner_process_handle", ownerOk ? S_OK : HRESULT_FROM_WIN32(GetLastError()));
    if (!ownerOk) return false;
    Mapping memory{static_cast<Packet*>(MapViewOfFile(mapping.value, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Packet)))};
    Log("partner", "map_packet", memory.value ? S_OK : HRESULT_FROM_WIN32(GetLastError()));
    if (!memory.value) return false;
    *memory.value = packet;
    SIZE_T bytes{};
    InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
    Attributes attributes; attributes.storage.resize(bytes);
    auto* list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.storage.data());
    if (!InitializeProcThreadAttributeList(list, 1, 0, &bytes)) return false;
    attributes.list = list;
    HANDLE handles[]{mapping.value, owner.value};
    if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, sizeof(handles), nullptr, nullptr)) return false;
    STARTUPINFOEXW startup{}; startup.StartupInfo.cb = sizeof(startup); startup.lpAttributeList = list;
    std::wstring command = L"\"" + helper_.wstring() + L"\" --texture-probe-child " +
        std::to_wstring(reinterpret_cast<UINT_PTR>(mapping.value)) + L" " + std::to_wstring(reinterpret_cast<UINT_PTR>(owner.value));
    PROCESS_INFORMATION process{};
    const bool created = CreateProcessW(helper_.c_str(), command.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr, helper_.parent_path().c_str(), &startup.StartupInfo, &process) != FALSE;
    Log("partner", "create_process", created ? S_OK : HRESULT_FROM_WIN32(GetLastError()));
    if (!created) return false;
    { std::ofstream out(log_, std::ios::app); out << "partner_pid=" << process.dwProcessId
        << " owner_pid=" << GetCurrentProcessId() << " texture_kind=" << packet.kind << '\n'; }
    Handle child{process.hProcess}, thread{process.hThread};
    const auto deadline = GetTickCount64() + 6000;
    while (!Cancelled() && GetTickCount64() < deadline && WaitForSingleObject(child.value, 10) == WAIT_TIMEOUT) {}
    if (WaitForSingleObject(child.value, 0) != WAIT_OBJECT_0) {
        // Only the exact diagnostic child we created. Never terminate GTA or CEF.
        TerminateProcess(child.value, ERROR_TIMEOUT);
        WaitForSingleObject(child.value, 1000);
        Log("partner", "cancelled_or_deadline", HRESULT_FROM_WIN32(ERROR_TIMEOUT));
        return false;
    }
    DWORD code{};
    GetExitCodeProcess(child.value, &code);
    packet = *memory.value;
    return code == 0 && packet.done == 1;
}

void LegacyTextureProbe::Run(ComPtr<ID3D11Device> device) noexcept {
    try {
        Log("session", "begin_v1", S_OK);
        if (device->GetCreationFlags() & D3D11_CREATE_DEVICE_SINGLETHREADED) {
            Log("session", "singlethreaded_worker_unsafe", E_ACCESSDENIED);
            active_.store(false); return;
        }
        ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; DXGI_ADAPTER_DESC ad{};
        if (FAILED(device.As(&dxgi)) || FAILED(dxgi->GetAdapter(&adapter)) || FAILED(adapter->GetDesc(&ad))) {
            Log("session", "adapter", E_FAIL); active_.store(false); return;
        }
        { std::ofstream out(log_, std::ios::app); out << "identity pid=" << GetCurrentProcessId()
            << " feature_level=0x" << std::hex << device->GetFeatureLevel()
            << " flags=0x" << device->GetCreationFlags() << " adapter_luid=" << ad.AdapterLuid.HighPart
            << ':' << ad.AdapterLuid.LowPart << std::dec << '\n'; }
        // Decompose the previous combined NT probe: log each call independently.
        ComPtr<ID3D11Device> peer; ComPtr<ID3D11DeviceContext> peerContext;
        const auto peerHr = CreateDevice(ad.AdapterLuid, peer, peerContext);
        Log("inbound_nt", "peer_device", peerHr);
        if (peerHr == S_OK) {
            auto d = Description(Kind::Nt); ComPtr<ID3D11Texture2D> texture;
            auto hr = peer->CreateTexture2D(&d, nullptr, &texture); Log("inbound_nt", "create_texture", hr);
            if (hr == S_OK) {
                ComPtr<IDXGIResource1> resource;
                hr = texture.As(&resource); Log("inbound_nt", "resource1", hr);
                Handle handle;
                if (hr == S_OK) { hr = resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &handle.value); Log("inbound_nt", "create_handle", hr); }
                if (hr == S_OK) {
                    ComPtr<ID3D11Device1> game1; ComPtr<ID3D11Texture2D> opened;
                    hr = device.As(&game1); Log("inbound_nt", "game_device1", hr);
                    if (hr == S_OK) Log("inbound_nt", "game_open", game1->OpenSharedResource1(handle.value, IID_PPV_ARGS(&opened)));
                }
            }
        }
        for (auto kind : {Kind::Local, Kind::Nt, Kind::Kmt}) {
            if (Cancelled()) break;
            const char* mode = kind == Kind::Local ? "local" : kind == Kind::Nt ? "outward_nt" : "outward_kmt";
            auto d = Description(kind);
            std::vector<UINT> pixels(Width * Height);
            for (UINT y = 0; y < Height; ++y) for (UINT x = 0; x < Width; ++x) pixels[y * Width + x] = Pixel(x, y, kind);
            D3D11_SUBRESOURCE_DATA initial{pixels.data(), Width * 4, 0};
            ComPtr<ID3D11Texture2D> texture;
            auto hr = device->CreateTexture2D(&d, kind == Kind::Local ? &initial : nullptr, &texture);
            Log(mode, "game_create_texture", hr); if (hr != S_OK) continue;
            Handle nt; HANDLE shared{};
            if (kind != Kind::Local) {
                if (kind == Kind::Nt) {
                    ComPtr<IDXGIResource1> resource;
                    hr = texture.As(&resource); Log(mode, "game_resource1", hr);
                    if (hr != S_OK) continue;
                    hr = resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &nt.value);
                    shared = nt.value;
                } else {
                    ComPtr<IDXGIResource> resource;
                    hr = texture.As(&resource); Log(mode, "game_resource", hr);
                    if (hr != S_OK) continue;
                    hr = resource->GetSharedHandle(&shared);
                }
                Log(mode, "game_shared_handle", hr); if (hr != S_OK) continue;
                Packet p{}; p.kind = static_cast<ULONG>(kind); p.ownerPid = GetCurrentProcessId();
                p.adapterLow = ad.AdapterLuid.LowPart; p.adapterHigh = ad.AdapterLuid.HighPart;
                p.textureHandle = reinterpret_cast<UINT_PTR>(shared);
                for (auto& result : p.results) result = E_PENDING;
                const bool ok = Partner(p);
                for (ULONG i = 0; i < static_cast<ULONG>(Step::Count); ++i) Log(mode, Names[i], p.results[i]);
                Log(mode, "partner_complete", ok ? S_OK : E_FAIL); if (!ok) continue;
            }
            ComPtr<ID3D11ShaderResourceView> view;
            hr = device->CreateShaderResourceView(texture.Get(), nullptr, &view);
            Log(mode, "game_srv", hr); if (hr != S_OK) continue;
            ComPtr<IDXGIKeyedMutex> keyed;
            if (kind != Kind::Local) {
                hr = texture.As(&keyed); Log(mode, "game_mutex", hr); if (hr != S_OK) continue;
            }
            phaseDraws_.store(0); fullscreenDraws_.store(0); acquireFailure_.store(0);
            { std::scoped_lock lock(viewMutex_); view_ = view; keyed_ = keyed; }
            const auto deadline = GetTickCount64() + displayMs_;
            while (!Cancelled() && GetTickCount64() < deadline) Sleep(10);
            { std::scoped_lock lock(viewMutex_); view_.Reset(); keyed_.Reset(); }
            // A draw submission is NOT proof of on-screen visibility or a valid
            // browser frame. Keep this entirely separate from menu diagnostics.
            Log(mode, "game_draw_submitted", phaseDraws_.load() ? S_OK : E_FAIL);
            { std::ofstream out(log_, std::ios::app); out << "mode=" << mode
                << " draw_submissions=" << phaseDraws_.load() << " exclusive_fullscreen_draws=" << fullscreenDraws_.load()
                << " acquire_release_error=0x" << std::hex << acquireFailure_.load() << std::dec
                << " onscreen_visibility=USER_VERIFICATION_REQUIRED menu_ready=UNMODIFIED\n"; }
        }
        Log("session", Cancelled() ? "cancelled" : "complete_not_menu_acceptance", Cancelled() ? E_ABORT : S_OK);
    } catch (...) { try { Log("session", "exception", E_FAIL); } catch (...) {} }
    active_.store(false);
}

bool LegacyTextureProbe::Draw(D3D11OverlayRenderer& renderer, ID3D11Texture2D* backBuffer,
    ID3D11DeviceContext* context, bool fullscreen) noexcept {
    std::unique_lock lock(viewMutex_, std::try_to_lock);
    if (!lock.owns_lock() || !view_ || Cancelled()) return false;
    if (keyed_) {
        const auto hr = keyed_->AcquireSync(1, 0);
        if (hr != S_OK) { acquireFailure_.store(hr); return false; }
    }
    const bool rendered = renderer.RenderShared(backBuffer, view_.Get(), 1, false);
    if (keyed_) {
        context->Flush();
        const auto hr = keyed_->ReleaseSync(1);
        if (hr != S_OK) acquireFailure_.store(hr);
    }
    if (rendered) { ++phaseDraws_; ++totalDraws_; if (fullscreen) ++fullscreenDraws_; }
    return rendered;
}
}
