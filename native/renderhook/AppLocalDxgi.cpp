#include "AppLocalDxgi.h"

#include <memory>
#include <vector>

namespace reactorv::renderhook {
namespace {

std::filesystem::path ModulePath(HMODULE module) {
    if (!module) return {};
    std::wstring path(32768, L'\0');
    const auto size = GetModuleFileNameW(module, path.data(),
        static_cast<DWORD>(path.size()));
    if (!size || size >= path.size()) return {};
    path.resize(size);
    return path;
}

struct CloseFile { void operator()(void* handle) const { CloseHandle(handle); } };

bool ReadReShadeVersion(const std::filesystem::path& path, std::wstring& version) {
    DWORD ignored{};
    const auto size = GetFileVersionInfoSizeW(path.c_str(), &ignored);
    if (!size || size > 1024 * 1024) return false;
    std::vector<BYTE> data(size);
    if (!GetFileVersionInfoW(path.c_str(), 0, size, data.data())) return false;
    struct Translation { WORD language; WORD codePage; };
    Translation* translations{};
    UINT bytes{};
    if (!VerQueryValueW(data.data(), L"\\VarFileInfo\\Translation",
            reinterpret_cast<void**>(&translations), &bytes)) return false;
    for (UINT i = 0; i < bytes / sizeof(Translation); ++i) {
        wchar_t prefix[64]{};
        swprintf_s(prefix, L"\\StringFileInfo\\%04x%04x\\",
            translations[i].language, translations[i].codePage);
        const auto field = [&](const wchar_t* name) -> std::wstring {
            wchar_t* value{};
            UINT count{};
            const auto key = std::wstring(prefix) + name;
            if (!VerQueryValueW(data.data(), key.c_str(),
                    reinterpret_cast<void**>(&value), &count) || !count) return {};
            // Resource lengths include the terminator. Do not scan past them.
            if (value[count - 1] != L'\0') return {};
            return std::wstring(value, count - 1);
        };
        if (field(L"ProductName") == L"ReShade" &&
            _wcsicmp(field(L"OriginalFilename").c_str(), L"ReShade64.dll") == 0) {
            version = field(L"ProductVersion").substr(0, 200);
            for (auto& character : version) {
                if (character < L' ' || character == L'"') character = L' ';
            }
            return true;
        }
    }
    return false;
}

} // namespace

AppLocalDxgiResult PrepareAppLocalDxgi(const std::filesystem::path& executablePath) {
    AppLocalDxgiResult result{};
    result.firstDxgiBefore = ModulePath(GetModuleHandleW(L"dxgi.dll"));
    result.firstDxgiAfter = result.firstDxgiBefore;
    if (!executablePath.is_absolute()) {
        result.status = L"invalid_executable_path";
        result.error = ERROR_BAD_PATHNAME;
        return result;
    }
    result.candidate = (executablePath.parent_path() / L"dxgi.dll").make_preferred();
    const auto attributes = GetFileAttributesW(result.candidate.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        result.error = GetLastError();
        result.allowNativeLoad = result.error == ERROR_FILE_NOT_FOUND;
        result.status = result.allowNativeLoad ? L"absent" : L"inspect_failed";
        if (result.allowNativeLoad) result.error = ERROR_SUCCESS;
        return result;
    }
    if (attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) {
        result.status = L"non_regular_proxy";
        result.error = ERROR_INVALID_DATA;
        return result;
    }

    // Exact path lookup, never assume a basename identifies the proxy when both
    // the app-local proxy and Windows DXGI can be resident. Take a retained ref.
    HMODULE module{};
    if (GetModuleHandleExW(0, result.candidate.c_str(), &module)) {
        result.status = L"already_loaded";
    } else {
        // Keep the inspected file stable through version inspection and loading.
        // Version metadata is a compatibility discriminator, NOT a trust/signature
        // check: this is code the user already placed beside the game executable.
        const auto handle = CreateFileW(result.candidate.c_str(), GENERIC_READ,
            FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (handle == INVALID_HANDLE_VALUE) {
            result.status = L"proxy_open_failed";
            result.error = GetLastError();
            return result;
        }
        const std::unique_ptr<void, CloseFile> file(handle);
        BY_HANDLE_FILE_INFORMATION info{};
        if (!GetFileInformationByHandle(handle, &info) ||
            (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))) {
            result.status = L"non_regular_proxy";
            result.error = ERROR_INVALID_DATA;
            return result;
        }
        if (!ReadReShadeVersion(result.candidate, result.productVersion)) {
            result.status = L"unrecognized_proxy";
            result.error = ERROR_NOT_SUPPORTED;
            return result;
        }
        // Only this exact app-local module is selected. Do not add the entire
        // game directory, CWD, PATH or user DLL directories to dependency search.
        module = LoadLibraryExW(result.candidate.c_str(), nullptr,
            LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module) {
            result.status = L"proxy_load_failed";
            result.error = GetLastError();
            return result;
        }
        result.status = L"preloaded_reshade";
    }
    result.loadedPath = ModulePath(module);
    result.firstDxgiAfter = ModulePath(GetModuleHandleW(L"dxgi.dll"));
    std::error_code error;
    if (result.loadedPath.empty() ||
        !std::filesystem::equivalent(result.candidate, result.loadedPath, error) || error) {
        result.status = L"loaded_path_mismatch";
        result.error = ERROR_INVALID_DLL;
        return result;
    }
    if (!GetProcAddress(module, "CreateDXGIFactory") ||
        !GetProcAddress(module, "CreateDXGIFactory1") ||
        !GetProcAddress(module, "CreateDXGIFactory2")) {
        result.status = L"proxy_exports_missing";
        result.error = ERROR_PROC_NOT_FOUND;
        return result;
    }
    result.allowNativeLoad = true;
    return result;
}

} // namespace reactorv::renderhook
