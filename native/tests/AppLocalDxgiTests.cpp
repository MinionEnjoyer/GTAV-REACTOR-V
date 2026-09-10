#include "AppLocalDxgi.h"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

namespace fs = std::filesystem;
using reactorv::renderhook::PrepareAppLocalDxgi;

namespace {
void Check(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}
fs::path ModulePath(HMODULE module) {
    if (!module) return {};
    wchar_t path[32768]{};
    Check(GetModuleFileNameW(module, path, 32768) != 0, "module path");
    return path;
}
fs::path Self() { return ModulePath(GetModuleHandleW(nullptr)); }
HMODULE LoadNativeFixture(const fs::path& root) {
    return LoadLibraryExW((root / L"plugins\\ReactorV\\import.dll").c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
}
int Child(const std::wstring& mode) {
    const auto root = Self().parent_path();
    const auto proxy = root / L"dxgi.dll";
    const auto loadedProxy = [&]() { return GetModuleHandleW(proxy.c_str()); };
    if (mode == L"old" || mode == L"old-expect-fixed") {
        std::wcout << L"baseline_before=" << ModulePath(GetModuleHandleW(L"dxgi.dll")) << std::endl;
        Check(LoadNativeFixture(root) != nullptr, "baseline native load");
        std::wcout << L"baseline_after=" << ModulePath(GetModuleHandleW(L"dxgi.dll"))
            << L" absolute_lookup=" << ModulePath(loadedProxy()) << std::endl;
        if (mode == L"old-expect-fixed") {
            Check(loadedProxy() != nullptr, "REGRESSION: native imports bypassed the installed app-local proxy");
        } else {
            Check(!loadedProxy(), "negative control: old flags should bypass proxy");
            std::wcout << L"REPRODUCED: old native load leaves app-local DXGI unloaded; selected="
                << ModulePath(GetModuleHandleW(L"dxgi.dll")) << L'\n';
        }
        return 0;
    }
    if (mode == L"invalid-path") {
        const auto result = PrepareAppLocalDxgi(L"relative.exe");
        Check(!result.allowNativeLoad && result.status == L"invalid_executable_path", "relative path rejected");
        return 0;
    }
    Check(!GetModuleHandleW(L"dxgi.dll"), "launcher must have no static graphics imports");
    HMODULE prior{};
    if (mode == L"system-first") {
        prior = LoadLibraryExW(L"dxgi.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Check(prior && !loadedProxy(), "system-first setup");
    } else if (mode == L"already" || mode == L"already-unknown") {
        prior = LoadLibraryExW(proxy.c_str(), nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Check(prior != nullptr, "preloaded proxy setup");
    }
    HANDLE lock = INVALID_HANDLE_VALUE;
    if (mode == L"locked") {
        lock = CreateFileW(proxy.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
            OPEN_EXISTING, 0, nullptr);
        Check(lock != INVALID_HANDLE_VALUE, "write-lock setup");
    }
    if (mode == L"cwd-decoy") fs::current_path(root / L"decoy");
    const auto result = PrepareAppLocalDxgi(Self());
    if (lock != INVALID_HANDLE_VALUE) CloseHandle(lock);
    std::wcout << L"case=" << mode << L" status=" << result.status
        << L" allow=" << result.allowNativeLoad << L" error=" << result.error
        << L" before=" << result.firstDxgiBefore << L" after=" << result.firstDxgiAfter
        << L" loaded=" << result.loadedPath << L" version=" << result.productVersion << L'\n';
    std::wstring expected = L"preloaded_reshade";
    if (mode == L"absent" || mode == L"cwd-decoy") expected = L"absent";
    else if (mode == L"unknown" || mode == L"malformed") expected = L"unrecognized_proxy";
    else if (mode == L"directory") expected = L"non_regular_proxy";
    else if (mode == L"locked") expected = L"proxy_open_failed";
    else if (mode == L"wrong-arch" || mode == L"load-fails") expected = L"proxy_load_failed";
    else if (mode == L"missing-exports") expected = L"proxy_exports_missing";
    else if (mode == L"already" || mode == L"already-unknown") expected = L"already_loaded";
    Check(result.status == expected, "exact result classification");
    const bool allow = expected == L"absent" || expected == L"already_loaded" ||
        expected == L"preloaded_reshade";
    Check(result.allowNativeLoad == allow, "native loading decision");
    if (!allow) {
        Check(result.error != 0, "blocked cases have diagnostic error");
        if (mode == L"missing-exports") Check(loadedProxy() != nullptr, "retain executable after partial initialization");
        else Check(!loadedProxy(), "invalid proxy was not executed successfully");
        return 0;
    }
    Check(result.error == 0, "success clears stale last-error");
    if (expected != L"absent") {
        Check(loadedProxy() && fs::equivalent(proxy, result.loadedPath), "exact app-local identity");
        if (mode == L"already" || mode == L"already-unknown") {
            Check(loadedProxy() == prior, "already-loaded module reused");
            FreeLibrary(prior);
            Check(loadedProxy() != nullptr, "helper retained own process-lifetime reference");
        }
        const auto second = PrepareAppLocalDxgi(Self());
        Check(second.allowNativeLoad && second.status == L"already_loaded", "repeat is idempotent in identity");
        Check(!LoadLibraryExW((root / L"missing-native.dll").c_str(), nullptr,
            LOAD_LIBRARY_SEARCH_SYSTEM32), "deliberate native load failure");
        Check(loadedProxy() != nullptr, "proxy retained after downstream load failure");
    }
    const auto native = LoadNativeFixture(root);
    Check(native != nullptr, "native static imports still load with restricted search flags");
    const auto imported = reinterpret_cast<HRESULT(*)()>(GetProcAddress(native, "InvokeImportedDxgi"));
    Check(imported != nullptr, "native import observer export");
    // Call through the real static import, not &CreateDXGIFactory (which can be
    // the import library's executable-local jump thunk rather than the target).
    const auto factoryResult = imported();
    std::wcout << L"native_factory_result=0x" << std::hex << factoryResult << std::dec << L'\n';
    if (mode == L"real" || mode == L"system-first" || expected == L"absent")
        Check(SUCCEEDED(factoryResult), "Windows/real proxy factory remains callable");
    else Check(factoryResult == E_NOTIMPL, "native IAT calls synthetic app-local proxy");
    if (expected != L"absent") Check(loadedProxy() != nullptr, "proxy survives native dependency loading");
    if (expected == L"absent") Check(!loadedProxy(), "no CWD or unrelated app-local library picked up");
    return 0;
}

DWORD RunChild(const fs::path& executable, const std::wstring& mode) {
    auto command = L"\"" + executable.wstring() + L"\" --child " + mode;
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
    const auto log = CreateFileW((executable.parent_path() / L"child-output.log").c_str(),
        GENERIC_WRITE, FILE_SHARE_READ, &security, CREATE_NEW, 0, nullptr);
    Check(log != INVALID_HANDLE_VALUE, "create child log");
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdOutput = log;
    startup.hStdError = log;
    PROCESS_INFORMATION process{};
    const auto created = CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW, nullptr, executable.parent_path().c_str(), &startup, &process);
    CloseHandle(log);
    Check(created, "spawn isolated loader child");
    CloseHandle(process.hThread);
    const auto wait = WaitForSingleObject(process.hProcess, 20000);
    if (wait != WAIT_OBJECT_0) {
        // Only this owned offline fixture; never kill a game or unrelated process.
        TerminateProcess(process.hProcess, 124);
        WaitForSingleObject(process.hProcess, 5000);
    }
    DWORD exitCode{};
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hProcess);
    std::ifstream output(executable.parent_path() / L"child-output.log");
    std::cout << output.rdbuf();
    Check(wait == WAIT_OBJECT_0, "loader child timed out");
    return exitCode;
}
}

int wmain(int argc, wchar_t** argv) {
    try {
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
        if (argc == 3 && std::wstring(argv[1]) == L"--child") return Child(argv[2]);
        Check(argc == 6, "expected proxy, unknown, fail-load, missing-exports, import fixtures");
        std::ifstream sourceFile(REACTORV_RENDERHOOK_SOURCE_PATH);
        const std::string source((std::istreambuf_iterator<char>(sourceFile)), {});
        const auto worker = source.find("DWORD WINAPI RenderHookWorker");
        const auto story = source.find("IsStoryModdingPolicySatisfied", worker);
        const auto prepare = source.find("PrepareAppLocalDxgi(executablePath)", worker);
        const auto nativeLoad = source.find("const HMODULE nativeModule = LoadLibraryExW", worker);
        const auto entry = source.find("BOOL WINAPI DllMain", worker);
        Check(worker != std::string::npos && story < prepare && prepare < nativeLoad &&
            nativeLoad < entry && entry != std::string::npos,
            "production worker keeps Story gate before proxy before native loading, outside DllMain");
        Check(source.find("if (!dxgi.allowNativeLoad)", prepare) < nativeLoad,
            "production worker respects blocked proxy result");
        const auto parent = Self().parent_path();
        const auto run = parent / (L"loader-test-runs/run " + std::to_wstring(GetCurrentProcessId()) +
            L"-" + std::to_wstring(GetTickCount64()));
        const std::vector<std::wstring> cases = { L"old", L"old-expect-fixed", L"normal", L"system-first",
            L"already", L"already-unknown", L"absent", L"cwd-decoy", L"unknown", L"malformed",
            L"directory", L"locked", L"wrong-arch", L"load-fails", L"missing-exports", L"invalid-path" };
        for (const auto& mode : cases) {
            const auto root = run / mode;
            fs::create_directories(root / L"plugins/ReactorV");
            fs::copy_file(Self(), root / L"Loader Test.exe");
            fs::copy_file(argv[5], root / L"plugins/ReactorV/import.dll");
            const auto proxy = root / L"dxgi.dll";
            if (mode == L"directory") fs::create_directory(proxy);
            else if (mode == L"malformed") { std::ofstream file(proxy); file << "not a DLL"; }
            else if (mode == L"cwd-decoy") {
                fs::create_directory(root / L"decoy");
                fs::copy_file(argv[1], root / L"decoy/dxgi.dll");
            } else if (mode != L"absent" && mode != L"invalid-path") {
                fs::copy_file(argv[(mode == L"unknown" || mode == L"already-unknown") ? 2 :
                    mode == L"load-fails" ? 3 : mode == L"missing-exports" ? 4 : 1], proxy);
                if (mode == L"wrong-arch") {
                    std::fstream file(proxy, std::ios::in | std::ios::out | std::ios::binary);
                    IMAGE_DOS_HEADER dos{};
                    file.read(reinterpret_cast<char*>(&dos), sizeof(dos));
                    Check(dos.e_magic == IMAGE_DOS_SIGNATURE, "fixture DOS signature");
                    file.seekp(dos.e_lfanew + sizeof(DWORD));
                    const WORD machine = IMAGE_FILE_MACHINE_I386;
                    file.write(reinterpret_cast<const char*>(&machine), sizeof(machine));
                }
            }
            const auto code = RunChild(root / L"Loader Test.exe", mode);
            const bool expectedFailure = mode == L"old-expect-fixed";
            std::wcout << L"case=" << mode << L" exit=" << code
                << (expectedFailure ? L" expected-negative-control" : L"") << std::endl;
            Check(code == (expectedFailure ? 1u : 0u), "isolated scenario result");
        }
        std::wcout << L"PASS: " << cases.size() << L" isolated Windows-loader scenarios; artifacts=" << run << L'\n';
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
}
