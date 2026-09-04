#include "LegacyCpuFrameBridge.h"
#include <algorithm>
#include <cstring>
#include <fstream>

namespace rwui::transport {
using Microsoft::WRL::ComPtr;
namespace {
struct Handle { HANDLE value{}; ~Handle() { if (value) CloseHandle(value); } };
bool Identity(HANDLE process, UINT64 expected) {
    FILETIME created{}, exited{}, kernel{}, user{};
    return process && WaitForSingleObject(process, 0) == WAIT_TIMEOUT &&
        GetProcessTimes(process, &created, &exited, &kernel, &user) &&
        ((UINT64(created.dwHighDateTime) << 32) | created.dwLowDateTime) == expected;
}
std::filesystem::path ProcessImage(DWORD pid) {
    Handle process{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)};
    wchar_t path[32768]{}; DWORD length = 32768;
    if (!process.value || !QueryFullProcessImageNameW(process.value, 0, path, &length)) return {};
    return std::filesystem::path(std::wstring(path, length));
}
}
UINT64 CpuFrameTimestampUs() noexcept {
    LARGE_INTEGER value{}, frequency{}; QueryPerformanceCounter(&value); QueryPerformanceFrequency(&frequency);
    return frequency.QuadPart ? UINT64(value.QuadPart / frequency.QuadPart) * 1000000ull +
        UINT64(value.QuadPart % frequency.QuadPart) * 1000000ull / frequency.QuadPart : 0;
}
bool LegacyCpuFramesEnabled(const DWORD targetPid) noexcept {
    try {
        if (ProcessImage(targetPid).filename() != L"GTA5.exe") return false;
        HMODULE module{};
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&LegacyCpuFramesEnabled), &module)) return false;
        wchar_t path[32768]{}; const auto length = GetModuleFileNameW(module, path, 32768);
        if (!length || length >= 32768) return false;
        const auto root = std::filesystem::path(path).parent_path();
        const auto marker = root / L"ReactorV.LegacyCpuFrames.enabled";
        const auto attributes = GetFileAttributesW(marker.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES &&
            !(attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) &&
            std::filesystem::is_regular_file(root / L"ReactorV.LegacyLiveTest.json");
    } catch (...) { return false; }
}
std::filesystem::path CpuFrameLogPath(DWORD targetPid, const wchar_t* role) noexcept {
    try {
        const auto image = ProcessImage(targetPid);
        if (image.filename() != L"GTA5.exe") return {}; // Harness does not write into its source/build directory.
        return image.parent_path() / L"scripts" / L"ReactorV" /
            (std::wstring(L"ReactorV.CpuFrames.") + role + L".log");
    } catch (...) { return {}; }
}
void CpuFrameTrace::Record(HRESULT hr, UINT64 generation, UINT64 bytes, UINT64 us, UINT64 dropped,
    const char* stage, bool recovered) noexcept {
    ++count_; if (hr != S_OK) ++failures_; else bytes_ += bytes;
    totalUs_ += us; maxUs_ = std::max(maxUs_, us);
    if (path_.empty() || (hr == S_OK && GetTickCount64() < nextLog_)) return;
    nextLog_ = GetTickCount64() + 1000;
    try {
        SYSTEMTIME now{}; GetLocalTime(&now);
        std::ofstream out(path_, std::ios::app);
        out << now.wYear << '-' << now.wMonth << '-' << now.wDay << 'T' << now.wHour << ':' << now.wMinute << ':' << now.wSecond
            << " pid=" << GetCurrentProcessId() << " transport=cpu_mapping_v1 diagnostic=1 generation=" << generation
            << " hr=0x" << std::hex << static_cast<ULONG>(hr) << std::dec
            << " attempts=" << count_ << " failures=" << failures_ << " bytes=" << bytes_ << " frame_bytes=" << bytes
            << " last_us=" << us << " mean_us=" << totalUs_ / count_ << " max_us=" << maxUs_
            << " rate_or_queue_drops=" << dropped << " stage=" << stage << " recovery=" << (recovered ? "retry-retired-slot" : "none")
            << " fps_limit=15 max_frame_bytes=" << CpuFrameMaximumBytes << '\n';
    } catch (...) {}
}
LegacyCpuFrameBridge::~LegacyCpuFrameBridge() { Reset(); }
void LegacyCpuFrameBridge::Reset() noexcept {
    if (pixels_) UnmapViewOfFile(pixels_); pixels_ = nullptr;
    if (mapping_) CloseHandle(mapping_); mapping_ = nullptr;
    staging_.Reset(); query_.Reset(); context_.Reset(); device_.Reset(); width_ = height_ = 0;
}
HRESULT LegacyCpuFrameBridge::Prepare(const SharedGpuFrameDescriptorV1& f, LUID adapter) {
    if (device_ && adapter.HighPart == adapter_.HighPart && adapter.LowPart == adapter_.LowPart &&
        width_ == f.width && height_ == f.height && format_ == f.pixelFormat) return S_OK;
    Reset();
    ComPtr<IDXGIFactory1> factory;
    auto hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory)); if (hr != S_OK) return hr;
    for (UINT i = 0; ; ++i) {
        ComPtr<IDXGIAdapter1> candidate; DXGI_ADAPTER_DESC1 d{};
        if (factory->EnumAdapters1(i, &candidate) != S_OK) return DXGI_ERROR_NOT_FOUND;
        if (candidate->GetDesc1(&d) != S_OK || d.AdapterLuid.HighPart != adapter.HighPart || d.AdapterLuid.LowPart != adapter.LowPart) continue;
        hr = D3D11CreateDevice(candidate.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &device_, nullptr, &context_);
        if (hr != S_OK) return hr;
        break;
    }
    D3D11_TEXTURE2D_DESC d{}; d.Width = f.width; d.Height = f.height;
    d.MipLevels = d.ArraySize = d.SampleDesc.Count = 1; d.Format = static_cast<DXGI_FORMAT>(f.pixelFormat);
    d.Usage = D3D11_USAGE_STAGING; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    hr = device_->CreateTexture2D(&d, nullptr, &staging_); if (hr != S_OK) return hr;
    const D3D11_QUERY_DESC q{D3D11_QUERY_EVENT, 0};
    hr = device_->CreateQuery(&q, &query_); if (hr != S_OK) return hr;
    const auto bytes = f.width * f.height * 4u;
    mapping_ = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bytes, nullptr);
    if (!mapping_) return HRESULT_FROM_WIN32(GetLastError());
    pixels_ = MapViewOfFile(mapping_, FILE_MAP_WRITE, 0, 0, bytes);
    if (!pixels_) return HRESULT_FROM_WIN32(GetLastError());
    width_ = f.width; height_ = f.height; format_ = f.pixelFormat; adapter_ = adapter; ++epoch_;
    return S_OK;
}
HRESULT LegacyCpuFrameBridge::Convert(const SharedGpuFrameDescriptorV1& gpu, LUID adapter,
    const std::atomic_bool& stop, SharedGpuFrameDescriptorV1& cpu) noexcept {
    cpu = {};
    stage_ = "validation";
    try {
        if (stop.load()) return E_ABORT;
        if (gpu.producerProcessId != GetCurrentProcessId() || !gpu.width || !gpu.height ||
            gpu.width > 8192 || gpu.height > 8192 ||
            UINT64(gpu.width) * gpu.height * 4 > CpuFrameMaximumBytes ||
            gpu.synchronization != SharedGpuSynchronization::D3d11KeyedMutex) return E_INVALIDARG;
        stage_ = "prepare";
        auto hr = Prepare(gpu, adapter); if (hr != S_OK) return hr;
        // The producer reads its OWN pool texture. This local import is not an
        // IPC identity override; the wire descriptor below retains the GTA peer.
        auto local = gpu; local.consumerProcessId = local.producerProcessId;
        local.consumerCreationTime = local.producerCreationTime;
        SharedGpuFrameValidationContext validation{local.producerProcessId, local.consumerProcessId,
            local.producerCreationTime, local.consumerCreationTime, local.sessionIdHigh, local.sessionIdLow};
        ImportedD3D11SharedFrame imported;
        stage_ = "local-import";
        if (ImportD3D11SharedFrame(device_.Get(), local, validation, imported, &hr) != SharedGpuD3D11ImportError::None)
            return hr == S_OK ? E_INVALIDARG : hr;
        const auto deadline = GetTickCount64() + 100;
        stage_ = "acquire";
        while (!imported.TryAcquireForPresent()) {
            if (stop.load()) return E_ABORT;
            if (GetTickCount64() >= deadline) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
            Sleep(1); // Producer worker only; never CEF paint or GTA Present.
        }
        stage_ = "readback-fence";
        context_->CopyResource(staging_.Get(), imported.Texture()); context_->End(query_.Get()); context_->Flush();
        if (failNextReadback_.exchange(false)) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        do {
            hr = context_->GetData(query_.Get(), nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
            if (hr != S_FALSE) break;
            if (stop.load()) return E_ABORT;
            if (GetTickCount64() >= deadline) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
            Sleep(1);
        } while (true);
        if (hr != S_OK) return hr;
        D3D11_MAPPED_SUBRESOURCE read{};
        stage_ = "map";
        hr = context_->Map(staging_.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &read);
        if (hr != S_OK) return hr;
        for (UINT y = 0; y < gpu.height; ++y)
            std::memcpy(static_cast<BYTE*>(pixels_) + size_t(y) * gpu.width * 4,
                static_cast<const BYTE*>(read.pData) + size_t(y) * read.RowPitch, size_t(gpu.width) * 4);
        context_->Unmap(staging_.Get(), 0);
        stage_ = "release";
        if (!imported.ReleaseAfterPresent()) return E_FAIL;
        cpu = gpu; cpu.versionMinor = CpuFrameVersionMinor;
        cpu.synchronization = SharedGpuSynchronization::CpuBgraMapping;
        cpu.slotCount = 1; cpu.slotIndex = 0; cpu.resourceEpoch = epoch_;
        cpu.sharedTextureHandle = reinterpret_cast<UINT_PTR>(mapping_);
        cpu.sharedFenceHandle = cpu.acquireValue = cpu.releaseValue = 0;
        stage_ = "ready";
        return S_OK;
    } catch (...) { return E_FAIL; }
}
CpuFrameMapping::~CpuFrameMapping() {
    if (pixels_) UnmapViewOfFile(pixels_);
    if (mapping_) CloseHandle(mapping_);
}
HRESULT CpuFrameMapping::Open(const SharedGpuFrameDescriptorV1& f, const SharedGpuFrameValidationContext& validation) noexcept {
    if (mapping_ || pixels_ || f.synchronization != SharedGpuSynchronization::CpuBgraMapping ||
        ValidateSharedGpuFrame(f, validation) != SharedGpuFrameValidationError::None ||
        validation.expectedConsumerProcessId != GetCurrentProcessId()) return E_INVALIDARG;
    if (!Identity(GetCurrentProcess(), validation.expectedConsumerCreationTime)) return E_ACCESSDENIED;
    Handle producer{OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, f.producerProcessId)};
    if (!Identity(producer.value, validation.expectedProducerCreationTime)) return E_ACCESSDENIED;
    if (!DuplicateHandle(producer.value, reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(f.sharedTextureHandle)),
        GetCurrentProcess(), &mapping_, FILE_MAP_READ, FALSE, 0)) return HRESULT_FROM_WIN32(GetLastError());
    pixels_ = MapViewOfFile(mapping_, FILE_MAP_READ, 0, 0, size_t(f.width) * f.height * 4);
    return pixels_ ? S_OK : HRESULT_FROM_WIN32(GetLastError());
}
}
