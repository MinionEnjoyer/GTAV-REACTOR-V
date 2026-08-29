#include "BootstrapPolicy.h"

#include <windows.h>

#include <algorithm>
#include <array>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

namespace {

constexpr wchar_t WindowClassName[] = L"ReactorV.NativeBootstrap.Status";
constexpr wchar_t PreloaderMutexName[] = L"Local\\ReactorV.Preloader.Singleton.production";
constexpr wchar_t RuntimeReadyEventPrefix[] = L"Local\\ReactorV.RuntimeReady.";
constexpr wchar_t PreloadDataReadyEventPrefix[] = L"Local\\ReactorV.PreloadDataReady.";
constexpr COLORREF TransparentKey = RGB(1, 0, 1);
constexpr int OverlayWidth = 560;
constexpr int OverlayHeight = 68;
constexpr DWORD BootstrapPollMilliseconds = 250;
constexpr ULONGLONG MaximumLifetimeMilliseconds = 300000;

struct WindowState {
    reactorv::bootstrap::StartupStage stage =
        reactorv::bootstrap::StartupStage::NativeBootstrap;
    HFONT titleFont{};
    HFONT statusFont{};
};

struct WindowCandidate {
    DWORD processId{};
    HWND window{};
    long long area{};
};

ULONGLONG FileTimeValue(const FILETIME& value) {
    ULARGE_INTEGER result{};
    result.LowPart = value.dwLowDateTime;
    result.HighPart = value.dwHighDateTime;
    return result.QuadPart;
}

std::filesystem::path ProcessExecutablePath() {
    std::wstring buffer(32768, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) {
        return {};
    }
    buffer.resize(length);
    return std::filesystem::path(buffer);
}

std::filesystem::path LocalDataDirectory() {
    std::wstring buffer(32768, L'\0');
    const DWORD length = GetEnvironmentVariableW(
        L"LOCALAPPDATA",
        buffer.data(),
        static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) {
        return {};
    }
    buffer.resize(length);
    return std::filesystem::path(buffer) / L"ReactorV";
}

void AppendBootstrapLog(const std::wstring& stage, const std::wstring& detail = {}) {
    try {
        const auto directory = LocalDataDirectory();
        if (directory.empty()) return;
        std::filesystem::create_directories(directory);
        SYSTEMTIME now{};
        GetLocalTime(&now);
        std::wofstream output(
            directory / L"reactorv-native-bootstrap.log",
            std::ios::app);
        if (!output) return;
        output << now.wYear << L'-';
        output.width(2); output.fill(L'0'); output << now.wMonth << L'-';
        output.width(2); output << now.wDay << L'T';
        output.width(2); output << now.wHour << L':';
        output.width(2); output << now.wMinute << L':';
        output.width(2); output << now.wSecond << L'.';
        output.width(3); output << now.wMilliseconds;
        output << L" stage=" << stage;
        if (!detail.empty()) output << L' ' << detail;
        output << L'\n';
    } catch (...) {
        // Diagnostics must never be able to interrupt GTA startup.
    }
}

ULONGLONG ProcessStartTime() {
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user)) {
        return 0;
    }
    return FileTimeValue(created);
}

bool IsFreshFile(const std::filesystem::path& path, const ULONGLONG processStart) {
    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes)) {
        return false;
    }
    // Accept writes up to five seconds before the recorded process start to
    // tolerate filesystem timestamp rounding, but never consume an old run.
    constexpr ULONGLONG tolerance = 5ULL * 10000000ULL;
    return FileTimeValue(attributes.ftLastWriteTime) + tolerance >= processStart;
}

std::string ReadFreshTail(
    const std::filesystem::path& path,
    const ULONGLONG processStart,
    const std::streamoff maximumBytes = 262144) {
    if (!IsFreshFile(path, processStart)) return {};
    std::ifstream input(path, std::ios::binary);
    if (!input) return {};
    input.seekg(0, std::ios::end);
    const auto position = input.tellg();
    if (position <= 0) return {};
    const auto size = static_cast<std::streamoff>(position);
    input.seekg(std::max<std::streamoff>(0, size - maximumBytes), std::ios::beg);
    std::ostringstream output;
    output << input.rdbuf();
    return output.str();
}

BOOL CALLBACK FindGameWindowCallback(HWND window, LPARAM parameter) {
    auto& candidate = *reinterpret_cast<WindowCandidate*>(parameter);
    if (!IsWindowVisible(window) || IsIconic(window)) return TRUE;
    DWORD processId{};
    GetWindowThreadProcessId(window, &processId);
    if (processId != candidate.processId) return TRUE;
    RECT bounds{};
    if (!GetWindowRect(window, &bounds)) return TRUE;
    const long long width = bounds.right - bounds.left;
    const long long height = bounds.bottom - bounds.top;
    const long long area = width * height;
    if (width >= 320 && height >= 240 && area > candidate.area) {
        candidate.window = window;
        candidate.area = area;
    }
    return TRUE;
}

HWND FindGameWindow() {
    WindowCandidate candidate{GetCurrentProcessId(), nullptr, 0};
    EnumWindows(FindGameWindowCallback, reinterpret_cast<LPARAM>(&candidate));
    return candidate.window;
}

bool IsGameForeground(const HWND gameWindow) {
    const HWND foreground = GetForegroundWindow();
    if (foreground == nullptr || gameWindow == nullptr) return false;
    DWORD processId{};
    GetWindowThreadProcessId(foreground, &processId);
    return processId == GetCurrentProcessId();
}

void PositionStatusWindow(const HWND statusWindow, const HWND gameWindow) {
    RECT bounds{};
    if (!GetWindowRect(gameWindow, &bounds)) return;
    const int x = std::max<int>(bounds.left + 8, bounds.right - OverlayWidth - 24);
    const int y = bounds.top + 24;
    SetWindowPos(
        statusWindow,
        HWND_TOPMOST,
        x,
        y,
        OverlayWidth,
        OverlayHeight,
        SWP_NOACTIVATE | SWP_SHOWWINDOW);
}

LRESULT CALLBACK StatusWindowProcedure(
    const HWND window,
    const UINT message,
    const WPARAM wParam,
    const LPARAM lParam) {
    auto* state = reinterpret_cast<WindowState*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
        return TRUE;
    }
    if (message == WM_ERASEBKGND) return 1;
    if (message == WM_PAINT && state != nullptr) {
        PAINTSTRUCT paint{};
        const HDC device = BeginPaint(window, &paint);
        RECT client{};
        GetClientRect(window, &client);
        const HBRUSH keyBrush = CreateSolidBrush(TransparentKey);
        FillRect(device, &client, keyBrush);
        DeleteObject(keyBrush);
        SetBkMode(device, TRANSPARENT);

        RECT titleBounds{0, 0, OverlayWidth - 2, 27};
        SelectObject(device, state->titleFont);
        SetTextColor(device, RGB(25, 25, 25));
        OffsetRect(&titleBounds, 1, 1);
        DrawTextW(device, L"REACTOR", -1, &titleBounds, DT_RIGHT | DT_SINGLELINE | DT_VCENTER);
        OffsetRect(&titleBounds, -1, -1);
        SetTextColor(device, RGB(60, 220, 122));
        DrawTextW(device, L"REACTOR", -1, &titleBounds, DT_RIGHT | DT_SINGLELINE | DT_VCENTER);

        RECT statusBounds{0, 29, OverlayWidth - 2, OverlayHeight};
        SelectObject(device, state->statusFont);
        const wchar_t* status = reactorv::bootstrap::StartupStageText(state->stage);
        SetTextColor(device, RGB(20, 20, 20));
        OffsetRect(&statusBounds, 1, 1);
        DrawTextW(device, status, -1, &statusBounds, DT_RIGHT | DT_SINGLELINE | DT_VCENTER);
        OffsetRect(&statusBounds, -1, -1);
        SetTextColor(device, RGB(238, 248, 244));
        DrawTextW(device, status, -1, &statusBounds, DT_RIGHT | DT_SINGLELINE | DT_VCENTER);
        EndPaint(window, &paint);
        return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

HWND CreateStatusWindow(WindowState& state) {
    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = StatusWindowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = WindowClassName;
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    RegisterClassExW(&windowClass);

    state.titleFont = CreateFontW(
        -18, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, NONANTIALIASED_QUALITY,
        DEFAULT_PITCH, L"Segoe UI");
    state.statusFont = CreateFontW(
        -15, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, NONANTIALIASED_QUALITY,
        DEFAULT_PITCH, L"Segoe UI");

    const HWND window = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW |
            WS_EX_NOACTIVATE | WS_EX_TOPMOST,
        WindowClassName,
        L"REACTOR V startup",
        WS_POPUP,
        0,
        0,
        OverlayWidth,
        OverlayHeight,
        nullptr,
        nullptr,
        windowClass.hInstance,
        &state);
    if (window != nullptr) {
        SetLayeredWindowAttributes(window, TransparentKey, 255, LWA_COLORKEY | LWA_ALPHA);
    }
    return window;
}

bool StartPreloader(
    const std::filesystem::path& preloaderPath,
    const std::filesystem::path& workingDirectory) {
    const HANDLE existing = OpenMutexW(SYNCHRONIZE, FALSE, PreloaderMutexName);
    if (existing != nullptr) {
        CloseHandle(existing);
        AppendBootstrapLog(L"preloader_already_running");
        return true;
    }
    if (!std::filesystem::is_regular_file(preloaderPath)) {
        AppendBootstrapLog(L"preloader_missing", preloaderPath.wstring());
        return false;
    }

    std::wstring command = reactorv::bootstrap::BuildPreloaderCommandLine(
        preloaderPath,
        GetCurrentProcessId());
    std::vector<wchar_t> mutableCommand(command.begin(), command.end());
    mutableCommand.push_back(L'\0');
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    const BOOL created = CreateProcessW(
        preloaderPath.c_str(),
        mutableCommand.data(),
        nullptr,
        nullptr,
        FALSE,
        CREATE_NO_WINDOW,
        nullptr,
        workingDirectory.c_str(),
        &startup,
        &process);
    if (!created) {
        AppendBootstrapLog(L"preloader_start_failed", L"error=" + std::to_wstring(GetLastError()));
        return false;
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    AppendBootstrapLog(L"preloader_started", L"pid=" + std::to_wstring(process.dwProcessId));
    return true;
}

HANDLE CreateRuntimeReadyEvent() {
    const std::wstring name = RuntimeReadyEventPrefix + std::to_wstring(GetCurrentProcessId());
    return CreateEventW(nullptr, TRUE, FALSE, name.c_str());
}

HANDLE CreatePreloadDataReadyEvent() {
    const std::wstring name = PreloadDataReadyEventPrefix + std::to_wstring(GetCurrentProcessId());
    return CreateEventW(nullptr, TRUE, FALSE, name.c_str());
}

bool RuntimeReadyEventIsSet(const HANDLE event) {
    return event != nullptr && WaitForSingleObject(event, 0) == WAIT_OBJECT_0;
}

void PumpMessages() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}

DWORD WINAPI BootstrapWorker(void*) {
    // Leave the loader lock before touching files, processes, or user32.
    Sleep(100);
    try {
        const auto executable = ProcessExecutablePath();
        if (!reactorv::bootstrap::IsSupportedGameExecutable(executable)) return 0;

        const std::wstring mutexName =
            L"Local\\ReactorV.NativeBootstrap." + std::to_wstring(GetCurrentProcessId());
        const HANDLE singleton = CreateMutexW(nullptr, TRUE, mutexName.c_str());
        if (singleton == nullptr || GetLastError() == ERROR_ALREADY_EXISTS) {
            if (singleton != nullptr) CloseHandle(singleton);
            return 0;
        }

        AppendBootstrapLog(
            L"bootstrap_started",
            L"pid=" + std::to_wstring(GetCurrentProcessId()) + L" game=" + executable.wstring());
        WindowState state{};
        const HWND statusWindow = CreateStatusWindow(state);
        const HANDLE runtimeReadyEvent = CreateRuntimeReadyEvent();
        if (runtimeReadyEvent == nullptr) {
            AppendBootstrapLog(
                L"runtime_event_create_failed",
                L"error=" + std::to_wstring(GetLastError()));
        }
        const HANDLE preloadDataReadyEvent = CreatePreloadDataReadyEvent();
        if (preloadDataReadyEvent == nullptr) {
            AppendBootstrapLog(
                L"preload_data_event_create_failed",
                L"error=" + std::to_wstring(GetLastError()));
        }
        const auto preloaderPath = reactorv::bootstrap::ResolvePreloaderPath(executable);
        const bool preloaderStarted = StartPreloader(preloaderPath, preloaderPath.parent_path());
        const ULONGLONG processStart = ProcessStartTime();
        const ULONGLONG workerStart = GetTickCount64();
        auto previousStage = state.stage;

        while (GetTickCount64() - workerStart < MaximumLifetimeMilliseconds) {
            PumpMessages();
            const HWND gameWindow = FindGameWindow();
            if (statusWindow != nullptr) {
                if (gameWindow != nullptr && IsGameForeground(gameWindow)) {
                    PositionStatusWindow(statusWindow, gameWindow);
                } else {
                    ShowWindow(statusWindow, SW_HIDE);
                }
            }

            const auto gameRoot = executable.parent_path();
            const auto localData = LocalDataDirectory();
            const auto stage = reactorv::bootstrap::DetectStartupStage(
                ReadFreshTail(gameRoot / L"ScriptHookV.log", processStart),
                ReadFreshTail(gameRoot / L"ScriptHookVDotNet.log", processStart),
                ReadFreshTail(localData / L"reactorv-runtime.log", processStart),
                preloaderStarted,
                RuntimeReadyEventIsSet(preloadDataReadyEvent));
            if (stage != previousStage) {
                state.stage = stage;
                previousStage = stage;
                AppendBootstrapLog(L"stage_changed", reactorv::bootstrap::StartupStageText(stage));
                if (statusWindow != nullptr) InvalidateRect(statusWindow, nullptr, TRUE);
            }

            if (RuntimeReadyEventIsSet(runtimeReadyEvent)) {
                state.stage = reactorv::bootstrap::StartupStage::StoryModeReady;
                if (statusWindow != nullptr) {
                    InvalidateRect(statusWindow, nullptr, TRUE);
                    UpdateWindow(statusWindow);
                    for (BYTE alpha = 255; alpha > 25; alpha = static_cast<BYTE>(alpha - 25)) {
                        SetLayeredWindowAttributes(statusWindow, TransparentKey, alpha, LWA_COLORKEY | LWA_ALPHA);
                        Sleep(35);
                    }
                }
                AppendBootstrapLog(L"runtime_handoff_complete");
                break;
            }
            Sleep(BootstrapPollMilliseconds);
        }

        if (statusWindow != nullptr) DestroyWindow(statusWindow);
        if (runtimeReadyEvent != nullptr) CloseHandle(runtimeReadyEvent);
        if (preloadDataReadyEvent != nullptr) CloseHandle(preloadDataReadyEvent);
        if (state.titleFont != nullptr) DeleteObject(state.titleFont);
        if (state.statusFont != nullptr) DeleteObject(state.statusFont);
        ReleaseMutex(singleton);
        CloseHandle(singleton);
        AppendBootstrapLog(L"bootstrap_stopped");
    } catch (...) {
        AppendBootstrapLog(L"bootstrap_failed", L"unhandled_exception");
    }
    return 0;
}

} // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        const HANDLE worker = CreateThread(nullptr, 0, BootstrapWorker, nullptr, 0, nullptr);
        if (worker != nullptr) CloseHandle(worker);
    }
    return TRUE;
}
