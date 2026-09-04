#include "RenderHookContract.h"
#include "RenderHookPolicy.h"

#include <windows.h>

#include <filesystem>
#include <fstream>
#include <iomanip>
#include <limits>
#include <sstream>
#include <string>
#include <system_error>

namespace {

std::filesystem::path ProcessExecutablePath() {
    std::wstring buffer(32768, L'\0');
    const DWORD length = GetModuleFileNameW(
        nullptr,
        buffer.data(),
        static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return {};
    buffer.resize(length);
    return std::filesystem::path(buffer);
}

std::wstring WindowsErrorText(const DWORD error) {
    wchar_t* message{};
    const DWORD length = FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER |
            FORMAT_MESSAGE_FROM_SYSTEM |
            FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr,
        error,
        0,
        reinterpret_cast<wchar_t*>(&message),
        0,
        nullptr);
    std::wstring result = length != 0 && message != nullptr
        ? std::wstring(message, length)
        : L"unavailable";
    if (message != nullptr) LocalFree(message);
    while (!result.empty() &&
        (result.back() == L'\r' || result.back() == L'\n' ||
         result.back() == L' ' || result.back() == L'.')) {
        result.pop_back();
    }
    return result;
}

void AppendLog(
    const std::filesystem::path& logPath,
    const std::wstring& status,
    const std::wstring& detail = {}) noexcept {
    try {
        std::filesystem::create_directories(logPath.parent_path());
        constexpr std::uintmax_t maximumLogBytes = 4u * 1024u * 1024u;
        std::error_code fileError;
        if (std::filesystem::exists(logPath, fileError) && !fileError &&
            std::filesystem::file_size(logPath, fileError) > maximumLogBytes &&
            !fileError) {
            auto rotatedPath = logPath;
            rotatedPath += L".1";
            std::filesystem::remove(rotatedPath, fileError);
            fileError.clear();
            std::filesystem::rename(logPath, rotatedPath, fileError);
            // Rotation is diagnostic hygiene only. If another reader keeps the
            // file open, continue appending rather than affecting GTA startup.
        }
        SYSTEMTIME now{};
        GetLocalTime(&now);
        std::wofstream output(logPath, std::ios::app);
        if (!output) return;
        output << std::setfill(L'0')
               << std::setw(4) << now.wYear << L'-'
               << std::setw(2) << now.wMonth << L'-'
               << std::setw(2) << now.wDay << L'T'
               << std::setw(2) << now.wHour << L':'
               << std::setw(2) << now.wMinute << L':'
               << std::setw(2) << now.wSecond << L'.'
               << std::setw(3) << now.wMilliseconds
               << L" status=" << status;
        if (!detail.empty()) output << L' ' << detail;
        output << L'\n';
    } catch (...) {
        // Renderer bootstrap diagnostics must never affect GTA startup.
    }
}

std::wstring ErrorDetail(
    const wchar_t* operation,
    const DWORD error,
    const std::filesystem::path& path = {}) {
    std::wstring detail = L"operation=";
    detail += operation;
    detail += L" win32_error=" + std::to_wstring(error);
    detail += L" message=\"" + WindowsErrorText(error) + L"\"";
    if (!path.empty()) detail += L" path=\"" + path.wstring() + L"\"";
    return detail;
}

struct WindowSearch final {
    DWORD processId{};
    reactorv::renderhook::RenderHookEdition edition{
        reactorv::renderhook::RenderHookEdition::Unsupported};
    HWND selected{};
    std::uint64_t selectedScore{};
};

BOOL CALLBACK FindGameWindowCallback(
    const HWND window,
    const LPARAM parameter) noexcept {
    auto* const search = reinterpret_cast<WindowSearch*>(parameter);
    if (search == nullptr) return FALSE;
    DWORD processId{};
    GetWindowThreadProcessId(window, &processId);
    wchar_t className[64]{};
    const int classLength = GetClassNameW(
        window, className, static_cast<int>(std::size(className)));
    RECT client{};
    GetClientRect(window, &client);
    const reactorv::renderhook::RenderTargetWindowCandidate candidate{
        processId,
        search->processId,
        classLength > 0
            ? std::wstring_view(className, static_cast<std::size_t>(classLength))
            : std::wstring_view{},
        GetAncestor(window, GA_ROOT) == window,
        GetWindow(window, GW_OWNER) != nullptr,
        (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0,
        client.right - client.left,
        client.bottom - client.top,
    };
    const bool eligible =
        search->edition == reactorv::renderhook::RenderHookEdition::Enhanced
            ? reactorv::renderhook::IsEligibleEnhancedRenderTarget(candidate)
            : search->edition ==
                    reactorv::renderhook::RenderHookEdition::Legacy &&
                reactorv::renderhook::IsEligibleLegacyRenderTarget(candidate);
    if (!eligible) {
        return TRUE;
    }
    const std::uint64_t classPriority =
        search->edition == reactorv::renderhook::RenderHookEdition::Enhanced &&
            _wcsicmp(className, L"sgaWindow") == 0 ? 2u : 1u;
    const auto area = static_cast<std::uint64_t>(candidate.clientWidth) *
        static_cast<std::uint64_t>(candidate.clientHeight);
    const auto score = (classPriority << 62) |
        (area & ((1ull << 62) - 1));
    if (search->selected == nullptr || score > search->selectedScore) {
        search->selected = window;
        search->selectedScore = score;
    }
    return TRUE;
}

HWND FindGameWindow(
    const reactorv::renderhook::RenderHookEdition edition) noexcept {
    WindowSearch search{GetCurrentProcessId(), edition};
    EnumWindows(&FindGameWindowCallback, reinterpret_cast<LPARAM>(&search));
    return search.selected;
}

const wchar_t* WindowClassName(
    const reactorv::renderhook::RenderHookEdition edition,
    const std::uint32_t value) noexcept {
    if (edition == reactorv::renderhook::RenderHookEdition::Legacy) {
        return value == static_cast<std::uint32_t>(
            RwuiLegacyTargetWindowClass::GrcWindow)
            ? L"grcWindow" : L"unknown";
    }
    if (value == static_cast<std::uint32_t>(
            RwuiEnhancedTargetWindowClass::SgaWindow)) return L"sgaWindow";
    if (value == static_cast<std::uint32_t>(
            RwuiEnhancedTargetWindowClass::GrcWindow)) return L"grcWindow";
    return L"unknown";
}

const wchar_t* QueueSourceName(const std::uint32_t source) noexcept {
    switch (source) {
    case 2: return L"FactoryCreation";
    case 3: return L"ResizeBuffers1";
    default: return L"None";
    }
}

struct NormalizedDiagnostics final {
    std::uint32_t flags{};
    std::uint32_t targetWindowProcessId{};
    std::uint32_t targetWindowClass{};
    std::uint32_t captureSource{};
    std::uint32_t consumerStage{};
    std::uint32_t renderApi{};
    std::uint64_t presentationEpoch{};
    std::uint64_t renderedFrames{};
    std::uint64_t lastFrameGeneration{};
    std::uint32_t lastReceiveError{};
    std::uint32_t lastImportError{};
    std::uint32_t lastImportHresult{};
    std::uint64_t discoveryMisses{};
    std::uint64_t producerImageRejects{};
    std::uint64_t connectFailures{};
    std::uint64_t receivedFrames{};
    std::uint64_t receiveFailures{};
    std::uint64_t importedResources{};
    std::uint64_t publishedFrames{};
    std::uint64_t copyFailures{};
    std::uint64_t acknowledgementsAccepted{};
    std::uint64_t acknowledgementsRejected{};
    std::uint64_t acknowledgementFailures{};
    std::uint64_t lastReceivedGeneration{};
    std::uint64_t lastPublishedGeneration{};
    RwuiD3D11DeviceDiagnostics device{};
    RwuiD3D11CompatibilityDiagnostics compatibility{};
};

struct RendererExports final {
    reactorv::renderhook::RenderHookEdition edition{
        reactorv::renderhook::RenderHookEdition::Unsupported};
    reactorv::renderhook::EarlyArmFunction arm{};
    reactorv::renderhook::BindEnhancedTargetFunction bind{};
    reactorv::renderhook::EnhancedDiagnosticsFunction enhancedDiagnostics{};
    reactorv::renderhook::LegacyDiagnosticsFunction legacyDiagnostics{};
    reactorv::renderhook::SharedTextureConsumerDiagnosticsFunction
        consumerDiagnostics{};
};

RendererExports ResolveRendererExports(
    const HMODULE module,
    const reactorv::renderhook::RenderHookEdition edition) noexcept {
    RendererExports exports{};
    exports.edition = edition;
    const char* const armName =
        edition == reactorv::renderhook::RenderHookEdition::Enhanced
            ? reactorv::renderhook::EnhancedEarlyArmExportName
            : reactorv::renderhook::LegacyEarlyArmExportName;
    const char* const bindName =
        edition == reactorv::renderhook::RenderHookEdition::Enhanced
            ? reactorv::renderhook::BindEnhancedTargetExportName
            : reactorv::renderhook::BindLegacyTargetExportName;
    exports.arm = reinterpret_cast<reactorv::renderhook::EarlyArmFunction>(
        GetProcAddress(module, armName));
    exports.bind =
        reinterpret_cast<reactorv::renderhook::BindEnhancedTargetFunction>(
            GetProcAddress(module, bindName));
    if (edition == reactorv::renderhook::RenderHookEdition::Enhanced) {
        exports.enhancedDiagnostics = reinterpret_cast<
            reactorv::renderhook::EnhancedDiagnosticsFunction>(GetProcAddress(
                module,
                reactorv::renderhook::EnhancedDiagnosticsExportName));
    } else if (edition == reactorv::renderhook::RenderHookEdition::Legacy) {
        exports.legacyDiagnostics = reinterpret_cast<
            reactorv::renderhook::LegacyDiagnosticsFunction>(GetProcAddress(
                module,
                reactorv::renderhook::LegacyDiagnosticsExportName));
    }
    exports.consumerDiagnostics = reinterpret_cast<
        reactorv::renderhook::SharedTextureConsumerDiagnosticsFunction>(
            GetProcAddress(
                module,
                reactorv::renderhook::
                    SharedTextureConsumerDiagnosticsExportName));
    return exports;
}

bool ExportsComplete(const RendererExports& exports) noexcept {
    return exports.arm != nullptr && exports.bind != nullptr &&
        exports.consumerDiagnostics != nullptr &&
        (exports.edition == reactorv::renderhook::RenderHookEdition::Enhanced
            ? exports.enhancedDiagnostics != nullptr
            : exports.edition ==
                    reactorv::renderhook::RenderHookEdition::Legacy &&
                exports.legacyDiagnostics != nullptr);
}

bool ReadConsumerDiagnostics(
    const RendererExports& exports,
    NormalizedDiagnostics& result) noexcept {
    RwuiSharedTextureConsumerDiagnostics diagnostics{};
    diagnostics.byteSize = sizeof(diagnostics);
    if (exports.consumerDiagnostics == nullptr ||
        exports.consumerDiagnostics(&diagnostics) != 1) return false;
    result.lastReceiveError = diagnostics.lastReceiveError;
    result.lastImportError = diagnostics.lastImportError;
    result.lastImportHresult = diagnostics.lastImportHresult;
    result.discoveryMisses = diagnostics.discoveryMisses;
    result.producerImageRejects = diagnostics.producerImageRejects;
    result.connectFailures = diagnostics.connectFailures;
    result.receivedFrames = diagnostics.receivedFrames;
    result.receiveFailures = diagnostics.receiveFailures;
    result.importedResources = diagnostics.importedResources;
    result.publishedFrames = diagnostics.publishedFrames;
    result.copyFailures = diagnostics.copyFailures;
    result.acknowledgementsAccepted = diagnostics.acknowledgementsAccepted;
    result.acknowledgementsRejected = diagnostics.acknowledgementsRejected;
    result.acknowledgementFailures = diagnostics.acknowledgementFailures;
    result.lastReceivedGeneration = diagnostics.lastReceivedGeneration;
    result.lastPublishedGeneration = diagnostics.lastPublishedGeneration;
    return true;
}

bool ReadDiagnostics(
    const RendererExports& exports,
    NormalizedDiagnostics& result) noexcept {
    result = {};
    if (exports.edition == reactorv::renderhook::RenderHookEdition::Enhanced) {
        RwuiEnhancedHookDiagnostics diagnostics{};
        diagnostics.byteSize = sizeof(diagnostics);
        if (exports.enhancedDiagnostics == nullptr ||
            exports.enhancedDiagnostics(&diagnostics) != 1) return false;
        result.flags = diagnostics.flags;
        result.targetWindowProcessId = diagnostics.targetWindowProcessId;
        result.targetWindowClass = static_cast<std::uint32_t>(
            diagnostics.targetWindowClass);
        result.captureSource = diagnostics.queueBindingSource;
        result.consumerStage = diagnostics.consumerStage;
        result.renderApi = static_cast<std::uint32_t>(
            RwuiRenderApi::Direct3D12);
        result.presentationEpoch = diagnostics.presentationEpoch;
        result.renderedFrames = diagnostics.renderedFrames;
        result.lastFrameGeneration = diagnostics.lastFrameGeneration;
        return ReadConsumerDiagnostics(exports, result);
    }
    if (exports.edition == reactorv::renderhook::RenderHookEdition::Legacy) {
        RwuiLegacyHookDiagnostics diagnostics{};
        diagnostics.byteSize = sizeof(diagnostics);
        if (exports.legacyDiagnostics == nullptr ||
            exports.legacyDiagnostics(&diagnostics) != 1) return false;
        result.flags = diagnostics.flags;
        result.targetWindowProcessId = diagnostics.targetWindowProcessId;
        result.targetWindowClass = static_cast<std::uint32_t>(
            diagnostics.targetWindowClass);
        result.consumerStage = diagnostics.consumerStage;
        result.renderApi = static_cast<std::uint32_t>(diagnostics.renderApi);
        result.presentationEpoch = diagnostics.presentationEpoch;
        result.renderedFrames = diagnostics.renderedFrames;
        result.lastFrameGeneration = diagnostics.lastFrameGeneration;
        using DeviceDiagnosticsFunction = std::int32_t(__cdecl*)(RwuiD3D11DeviceDiagnostics*);
        // Resolve from the same module as the bound ABI, never an independent
        // PATH lookup. This optional additive diagnostic preserves old loaders.
        HMODULE module{};
        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(exports.legacyDiagnostics), &module)) {
            const auto readDevice = reinterpret_cast<DeviceDiagnosticsFunction>(
                GetProcAddress(module, "RWUI_GetD3D11DeviceDiagnostics"));
            result.device.byteSize = sizeof(result.device);
            if (!readDevice || readDevice(&result.device) != 1) result.device = {};
            using CompatibilityFunction = std::int32_t(__cdecl*)(RwuiD3D11CompatibilityDiagnostics*);
            const auto readCompatibility = reinterpret_cast<CompatibilityFunction>(
                GetProcAddress(module, "RWUI_GetD3D11CompatibilityDiagnostics"));
            result.compatibility.byteSize = sizeof(result.compatibility);
            if (!readCompatibility || readCompatibility(&result.compatibility) != 1)
                result.compatibility = {};
        }
        return ReadConsumerDiagnostics(exports, result);
    }
    return false;
}

std::uint32_t TargetBoundFlag(
    const reactorv::renderhook::RenderHookEdition edition) noexcept {
    return edition == reactorv::renderhook::RenderHookEdition::Enhanced
        ? RWUI_ENHANCED_DIAGNOSTIC_TARGET_BOUND
        : RWUI_LEGACY_DIAGNOSTIC_TARGET_BOUND;
}

const wchar_t* EditionName(
    const reactorv::renderhook::RenderHookEdition edition) noexcept {
    return edition == reactorv::renderhook::RenderHookEdition::Enhanced
        ? L"enhanced" : L"legacy";
}

const wchar_t* RendererName(
    const reactorv::renderhook::RenderHookEdition edition) noexcept {
    return edition == reactorv::renderhook::RenderHookEdition::Enhanced
        ? L"d3d12" : L"d3d11";
}

std::wstring DiagnosticsDetail(
    const reactorv::renderhook::RenderHookEdition edition,
    const std::int32_t bindResult,
    const NormalizedDiagnostics& diagnostics) {
    std::wostringstream detail;
    detail << L"bind_result=" << bindResult
           << L" target_pid=" << diagnostics.targetWindowProcessId
           << L" target_class=" << WindowClassName(
                edition, diagnostics.targetWindowClass)
           << L" capture_source=" << QueueSourceName(
                diagnostics.captureSource)
           << L" render_api=" << diagnostics.renderApi
           << L" flags=0x" << std::hex << diagnostics.flags << std::dec
           << L" consumer_stage=" << diagnostics.consumerStage
           << L" presentation_epoch=" << diagnostics.presentationEpoch
           << L" rendered_frames=" << diagnostics.renderedFrames
           << L" last_generation=" << diagnostics.lastFrameGeneration
           << L" receive_error=" << diagnostics.lastReceiveError
           << L" import_error=" << diagnostics.lastImportError
           << L" import_hresult=0x" << std::hex
           << diagnostics.lastImportHresult << std::dec
           << L" discovery_misses=" << diagnostics.discoveryMisses
           << L" producer_image_rejects="
           << diagnostics.producerImageRejects
           << L" connect_failures=" << diagnostics.connectFailures
           << L" received_frames=" << diagnostics.receivedFrames
           << L" receive_failures=" << diagnostics.receiveFailures
           << L" imported_resources=" << diagnostics.importedResources
           << L" published_frames=" << diagnostics.publishedFrames
           << L" copy_failures=" << diagnostics.copyFailures
           << L" ack_accepted=" << diagnostics.acknowledgementsAccepted
           << L" ack_rejected=" << diagnostics.acknowledgementsRejected
           << L" ack_failures=" << diagnostics.acknowledgementFailures
           << L" last_received_generation="
           << diagnostics.lastReceivedGeneration
           << L" last_published_generation="
           << diagnostics.lastPublishedGeneration;
    if (edition == reactorv::renderhook::RenderHookEdition::Legacy &&
        diagnostics.device.majorVersion == 1) {
        const auto& d = diagnostics.device;
        detail << L" device_probe_complete=" << d.probeComplete
            << L" feature_level=0x" << std::hex << d.featureLevel
            << L" creation_flags=0x" << d.creationFlags
            << L" adapter_luid=" << d.adapterHigh << L":" << d.adapterLow
            << L" bgra_support=0x" << d.bgraSupport
            << L" rgba_support=0x" << d.rgbaSupport
            << L" device1_hr=0x" << d.device1Hresult
            << L" peer_device_hr=0x" << d.peerDeviceHresult
            << L" peer_feature=0x" << d.peerFeatureLevel
            << L" local_bgra_hr=0x" << d.localBgraHresult
            << L" known_shared_bgra_hr=0x" << d.sharedBgraHresult
            << L" known_shared_rgba_hr=0x" << d.sharedRgbaHresult
            << L" known_shared_bgra_rt_hr=0x" << d.sharedBgraRenderTargetHresult
            << L" fullscreen_hr=0x" << d.fullscreenHresult << std::dec
            << L" dxgi_fullscreen=" << d.fullscreen
            << L" swap_effect=" << d.swapEffect << L" swap_flags=" << d.swapFlags
            << L" buffer_format=" << d.backBufferFormat
            << L" buffer_size=" << d.width << L"x" << d.height
            << L" samples=" << d.sampleCount;
    }
    if (edition == reactorv::renderhook::RenderHookEdition::Legacy &&
        diagnostics.compatibility.majorVersion == 1) {
        const auto& c = diagnostics.compatibility;
        detail << L" legacy_bridge_enabled=" << c.enabled
            << L" legacy_bridge_active=" << c.active
            << L" legacy_bridge_stage=" << c.stage
            << L" legacy_bridge_hr=0x" << std::hex << c.hresult
            << L" direct_import_hr=0x" << c.directImportHresult << std::dec
            << L" bridged_frames=" << c.bridgedFrames;
    }
    return detail.str();
}

DWORD WINAPI RenderHookWorker(void*) noexcept {
    try {
        const auto executablePath = ProcessExecutablePath();
        const auto edition = reactorv::renderhook::DetectRenderHookEdition(
            executablePath);
        if (executablePath.empty() ||
            edition == reactorv::renderhook::RenderHookEdition::Unsupported) {
            return 0;
        }

        const auto paths =
            reactorv::renderhook::ResolveRenderHookPaths(executablePath);
        const wchar_t* commandLine = GetCommandLineW();
        if (commandLine == nullptr ||
            !reactorv::renderhook::IsStoryModdingPolicySatisfied(commandLine)) {
            AppendLog(
                paths.diagnosticsFile,
                L"inactive",
                L"reason=story_modding_policy_not_satisfied "
                L"required_switch=-nobattleye "
                L"action=launch_story_mode_through_a_supported_modding_entrypoint");
            return 0;
        }
        AppendLog(
            paths.diagnosticsFile,
            L"starting",
            L"edition=" + std::wstring(EditionName(edition)) +
                L" host=" + executablePath.filename().wstring() +
                L" renderer=" + RendererName(edition) +
                L" mode=early-arm input=detached");

        if (!std::filesystem::is_regular_file(paths.nativeModule)) {
            AppendLog(
                paths.diagnosticsFile,
                L"inactive",
                L"reason=native_module_missing expected=\"" +
                    paths.nativeModule.wstring() +
                    L"\" action=repair_reactorv_installation");
            return 0;
        }

        SetLastError(ERROR_SUCCESS);
        const HMODULE nativeModule = LoadLibraryExW(
            paths.nativeModule.c_str(),
            nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
                LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (nativeModule == nullptr) {
            const DWORD error = GetLastError();
            AppendLog(
                paths.diagnosticsFile,
                L"inactive",
                L"reason=native_module_load_failed " +
                    ErrorDetail(L"LoadLibraryExW", error, paths.nativeModule) +
                    L" action=verify_native_dependencies");
            return 0;
        }

        const auto exports = ResolveRendererExports(nativeModule, edition);
        if (!ExportsComplete(exports)) {
            const auto required = edition ==
                    reactorv::renderhook::RenderHookEdition::Enhanced
                ? L"RWUI_ArmEnhancedHook,RWUI_BindEnhancedTarget,"
                  L"RWUI_GetEnhancedHookDiagnostics,"
                  L"RWUI_GetSharedTextureConsumerDiagnostics"
                : L"RWUI_ArmLegacyHook,RWUI_BindLegacyTarget,"
                  L"RWUI_GetLegacyHookDiagnostics,"
                  L"RWUI_GetSharedTextureConsumerDiagnostics";
            AppendLog(
                paths.diagnosticsFile,
                L"inactive",
                L"reason=" + std::wstring(EditionName(edition)) +
                    L"_native_exports_missing required=" + required +
                    L" action=install_matching_reactorv_native_build");
            FreeLibrary(nativeModule);
            return 0;
        }

        if (edition == reactorv::renderhook::RenderHookEdition::Legacy &&
            std::filesystem::is_regular_file(paths.nativeModule.parent_path() /
                L"ReactorV.LegacyLiveTest.json")) {
            using EnableProbes = void(__cdecl*)(std::int32_t);
            const auto enable = reinterpret_cast<EnableProbes>(GetProcAddress(
                nativeModule, "RWUI_EnableD3D11DiagnosticProbes"));
            const bool cpuBrowserTest = std::filesystem::is_regular_file(
                paths.nativeModule.parent_path() / L"ReactorV.LegacyCpuFrames.enabled");
            if (enable) enable(cpuBrowserTest ? 0 : 1);
            if (cpuBrowserTest) AppendLog(paths.diagnosticsFile, L"cpu_browser_test_armed",
                L"legacy_only=1 fps_limit=15 texture_probe=disabled menu_readiness=unchanged");
            const auto probeMarker = paths.nativeModule.parent_path() / L"ReactorV.LegacyTextureProbe.enabled";
            const auto probeHelper = paths.nativeModule.parent_path() / L"ReactorV.TextureProbe.Partner.exe";
            if (!cpuBrowserTest && std::filesystem::is_regular_file(probeMarker) && std::filesystem::is_regular_file(probeHelper)) {
                using ConfigureProbe = void(__cdecl*)(const wchar_t*, const wchar_t*);
                const auto configure = reinterpret_cast<ConfigureProbe>(GetProcAddress(nativeModule, "RWUI_ConfigureLegacyTextureProbe"));
                const auto log = paths.diagnosticsFile.parent_path() / L"ReactorV.LegacyTextureProbe.log";
                if (configure) {
                    configure(probeHelper.c_str(), log.c_str());
                    AppendLog(paths.diagnosticsFile, L"texture_probe_armed", L"input_capture=unchanged menu_readiness=unchanged visibility_requires_user_confirmation");
                }
            }
        }
        const std::int32_t armResult = exports.arm();
        if (reactorv::renderhook::ResolveNativeModuleDisposition(
                true,
                armResult) ==
            reactorv::renderhook::NativeModuleDisposition::ReleaseFailOpen) {
            AppendLog(
                paths.diagnosticsFile,
                L"inactive",
                L"reason=early_arm_rejected result=" +
                    std::to_wstring(armResult) +
                    L" action=inspect_reactorv_native_diagnostics");
            FreeLibrary(nativeModule);
            return 0;
        }

        // Keep the successful LoadLibrary reference for process lifetime. The
        // installed hooks execute inside this module and unloading it while
        // GTA can still present would be unsafe. Process teardown releases it.
        AppendLog(
            paths.diagnosticsFile,
            L"armed",
            L"edition=" + std::wstring(EditionName(edition)) +
                L" renderer=" + RendererName(edition) +
                L" input=detached result=" +
                std::to_wstring(armResult));

        std::int32_t lastBindResult = std::numeric_limits<std::int32_t>::min();
        std::uint32_t lastFlags = std::numeric_limits<std::uint32_t>::max();
        std::uint32_t lastTargetProcessId =
            std::numeric_limits<std::uint32_t>::max();
        std::uint32_t lastTargetClass =
            std::numeric_limits<std::uint32_t>::max();
        std::uint32_t lastQueueSource =
            std::numeric_limits<std::uint32_t>::max();
        std::uint32_t lastConsumerStage =
            std::numeric_limits<std::uint32_t>::max();
        std::uint64_t lastPresentationEpoch =
            std::numeric_limits<std::uint64_t>::max();
        std::uint32_t lastReceiveError =
            std::numeric_limits<std::uint32_t>::max();
        std::uint32_t lastImportError =
            std::numeric_limits<std::uint32_t>::max();
        std::uint64_t lastReceiveFailures =
            std::numeric_limits<std::uint64_t>::max();
        std::uint64_t lastCopyFailures =
            std::numeric_limits<std::uint64_t>::max();
        std::uint64_t lastAcknowledgementFailures =
            std::numeric_limits<std::uint64_t>::max();
        bool lastReceivedAny = false;
        bool lastPublishedAny = false;
        HWND boundWindow{};
        bool rendererBound = false;
        for (;;) {
            const auto window = rendererBound && IsWindow(boundWindow)
                ? boundWindow
                : FindGameWindow(edition);
            auto bindResult = lastBindResult;
            if (!rendererBound || window != boundWindow) {
                bindResult = window == nullptr ? 0 : exports.bind(window);
                if (bindResult > 0) boundWindow = window;
            }
            NormalizedDiagnostics diagnostics{};
            ReadDiagnostics(exports, diagnostics);
            const auto targetClass = diagnostics.targetWindowClass;
            rendererBound =
                (diagnostics.flags & TargetBoundFlag(edition)) != 0 &&
                boundWindow != nullptr && IsWindow(boundWindow);
            if (!rendererBound &&
                (boundWindow == nullptr || !IsWindow(boundWindow))) {
                boundWindow = nullptr;
            }
            if (bindResult != lastBindResult ||
                diagnostics.flags != lastFlags ||
                diagnostics.targetWindowProcessId != lastTargetProcessId ||
                targetClass != lastTargetClass ||
                diagnostics.captureSource != lastQueueSource ||
                diagnostics.consumerStage != lastConsumerStage ||
                diagnostics.presentationEpoch != lastPresentationEpoch ||
                diagnostics.lastReceiveError != lastReceiveError ||
                diagnostics.lastImportError != lastImportError ||
                diagnostics.receiveFailures != lastReceiveFailures ||
                diagnostics.copyFailures != lastCopyFailures ||
                diagnostics.acknowledgementFailures !=
                    lastAcknowledgementFailures ||
                (diagnostics.receivedFrames != 0) != lastReceivedAny ||
                (diagnostics.publishedFrames != 0) != lastPublishedAny) {
                AppendLog(
                    paths.diagnosticsFile,
                    (diagnostics.flags &
                        TargetBoundFlag(edition)) != 0
                        ? L"bound" : L"waiting",
                    L"edition=" + std::wstring(EditionName(edition)) + L" " +
                        DiagnosticsDetail(edition, bindResult, diagnostics));
                lastBindResult = bindResult;
                lastFlags = diagnostics.flags;
                lastTargetProcessId = diagnostics.targetWindowProcessId;
                lastTargetClass = targetClass;
                lastQueueSource = diagnostics.captureSource;
                lastConsumerStage = diagnostics.consumerStage;
                lastPresentationEpoch = diagnostics.presentationEpoch;
                lastReceiveError = diagnostics.lastReceiveError;
                lastImportError = diagnostics.lastImportError;
                lastReceiveFailures = diagnostics.receiveFailures;
                lastCopyFailures = diagnostics.copyFailures;
                lastAcknowledgementFailures =
                    diagnostics.acknowledgementFailures;
                lastReceivedAny = diagnostics.receivedFrames != 0;
                lastPublishedAny = diagnostics.publishedFrames != 0;
            }
            // A bound compositor is driven by Present/resize hooks. Rebinding
            // and taking compositor diagnostics locks four times per second
            // can make the try-lock render path skip otherwise valid frames.
            // Poll quickly only while discovering/recovering the target.
            Sleep(rendererBound ? 1000 : 250);
        }
    } catch (...) {
        const auto executablePath = ProcessExecutablePath();
        if (!executablePath.empty() &&
            reactorv::renderhook::DetectRenderHookEdition(executablePath) !=
                reactorv::renderhook::RenderHookEdition::Unsupported) {
            AppendLog(
                reactorv::renderhook::ResolveRenderHookPaths(executablePath)
                    .diagnosticsFile,
                L"inactive",
                L"reason=worker_exception action=continue_without_reactor_renderer");
        }
    }
    return 0;
}

} // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;

    DisableThreadLibraryCalls(instance);
    // DllMain performs no path, filesystem, logging, or module-loader work.
    // Windows serializes DLL initialization before the new thread begins, so
    // all LoadLibrary/GetProcAddress work occurs after this callback returns.
    const HANDLE worker =
        CreateThread(nullptr, 0, RenderHookWorker, nullptr, 0, nullptr);
    if (worker != nullptr) CloseHandle(worker);
    return TRUE;
}
