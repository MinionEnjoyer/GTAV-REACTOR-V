#include "BootstrapPolicy.h"
#include "IncrementalLogSignals.h"
#include "LegacyStartupShadowProbe.h"
#include "LegacyStartupShadowRunner.h"
#include "ScriptProbeAbi.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace {

#ifndef REACTORV_ENABLE_LEGACY_STARTUP_SHADOW
#define REACTORV_ENABLE_LEGACY_STARTUP_SHADOW 0
#endif

constexpr bool LegacyStartupShadowResearchEnabled =
    REACTORV_ENABLE_LEGACY_STARTUP_SHADOW != 0;
const unsigned char BootstrapModulePinAnchor{};

constexpr wchar_t WindowClassName[] = L"ReactorV.NativeBootstrap.Status";
constexpr wchar_t PreloaderMutexPrefix[] = L"Local\\ReactorV.Preloader.Singleton.";
constexpr wchar_t RuntimeReadyEventPrefix[] = L"Local\\ReactorV.RuntimeReady.";
constexpr wchar_t PreloadDataReadyEventPrefix[] = L"Local\\ReactorV.PreloadDataReady.";
constexpr wchar_t BootstrapHostToggleEventPrefix[] = L"Local\\ReactorV.BootstrapHostToggle.";
constexpr wchar_t BootstrapHostAboutToggleEventPrefix[] =
    L"Local\\ReactorV.BootstrapHostAboutToggle.";
constexpr wchar_t BootstrapHostVerifyToggleEventPrefix[] =
    L"Local\\ReactorV.BootstrapHostVerifyToggle.";
constexpr wchar_t BootstrapHostVerifyActiveEventPrefix[] =
    L"Local\\ReactorV.BootstrapHostVerifyActive.";
constexpr wchar_t BootstrapHostInitializerPromotionEventPrefix[] =
    L"Local\\ReactorV.BootstrapHostInitializerPromotion.";
constexpr wchar_t BootstrapHostCloseEventPrefix[] = L"Local\\ReactorV.BootstrapHostClose.";
constexpr wchar_t F9OwnershipReleasedEventPrefix[] =
    L"Local\\ReactorV.F9OwnershipReleased.";
constexpr COLORREF TransparentKey = RGB(1, 0, 1);
constexpr int OverlayWidth = 560;
constexpr int OverlayHeight = 68;

struct WindowState {
    reactorv::bootstrap::StartupStage stage =
        reactorv::bootstrap::StartupStage::NativeBootstrap;
    HFONT titleFont{};
    HFONT statusFont{};
};

void AppendBootstrapLog(const std::wstring& stage, const std::wstring& detail);

// Reuse the existing status copy/fonts in a tiny BGRA texture. Unlike the
// desktop HWND this can be composited into Legacy exclusive fullscreen.
// The publisher runs only on the bootstrap worker, on stage changes, and has
// no menu/input ownership. Enhanced retains its existing status path.
class NativeStatusPublisher {
    using Submit = std::int32_t(__cdecl*)(const void*, std::int32_t, std::int32_t, std::int32_t, std::uint64_t);
    HMODULE module_{};
    Submit submit_{};
    std::filesystem::path modulePath_;
    bool enabled_{}, published_{};
    int lastStage_{-1};
    std::uint64_t generation_{};
public:
    explicit NativeStatusPublisher(const std::filesystem::path& executable) {
        const auto directory = executable.parent_path() / L"plugins" / L"ReactorV";
        std::error_code error;
        enabled_ = executable.filename() == L"GTA5.exe" &&
            std::filesystem::exists(directory / L"ReactorV.LegacyCpuFrames.enabled", error) && !error;
        modulePath_ = directory / L"RageWebUI.Native.dll";
    }
    ~NativeStatusPublisher() {
        if (submit_) submit_(nullptr, 0, 0, 0, 0);
        if (module_) FreeLibrary(module_);
    }
    bool Publish(const WindowState& state) noexcept {
        if (!enabled_) return false;
        if (!module_ && GetModuleHandleExW(0, modulePath_.c_str(), &module_))
            submit_ = reinterpret_cast<Submit>(GetProcAddress(module_, "RWUI_SubmitStartupStatusFrame"));
        if (!submit_) return false;
        const auto stage = static_cast<int>(state.stage);
        if (published_ && stage == lastStage_) return true;
        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = OverlayWidth;
        info.bmiHeader.biHeight = -OverlayHeight;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;
        void* raw{};
        HDC dc = CreateCompatibleDC(nullptr);
        if (!dc) return false;
        HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &raw, nullptr, 0);
        if (!bitmap || !raw) { if (bitmap) DeleteObject(bitmap); DeleteDC(dc); return false; }
        auto oldBitmap = SelectObject(dc, bitmap);
        auto oldFont = SelectObject(dc, state.titleFont);
        ZeroMemory(raw, OverlayWidth * OverlayHeight * 4);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, RGB(60, 220, 122));
        RECT title{0, 0, OverlayWidth - 2, 27};
        DrawTextW(dc, L"REACTOR", -1, &title, DT_RIGHT | DT_SINGLELINE | DT_VCENTER);
        SelectObject(dc, state.statusFont);
        SetTextColor(dc, RGB(238, 248, 244));
        RECT status{0, 29, OverlayWidth - 2, OverlayHeight};
        DrawTextW(dc, reactorv::bootstrap::StartupStageText(state.stage), -1, &status,
            DT_RIGHT | DT_SINGLELINE | DT_VCENTER);
        // GDI's batched writes must finish before reading DIB pixels. Alpha is
        // derived from coverage; RGB remains premultiplied on transparent black.
        GdiFlush();
        auto* pixels = static_cast<std::uint8_t*>(raw);
        for (int i = 0; i < OverlayWidth * OverlayHeight; ++i) {
            auto* pixel = pixels + i * 4;
            pixel[3] = std::max({pixel[0], pixel[1], pixel[2]});
        }
        const bool accepted = submit_(raw, OverlayWidth, OverlayHeight, OverlayWidth * 4, ++generation_) != 0;
        SelectObject(dc, oldFont);
        SelectObject(dc, oldBitmap);
        DeleteObject(bitmap);
        DeleteDC(dc);
        if (accepted) {
            published_ = true; lastStage_ = stage;
            AppendBootstrapLog(L"native_startup_status_submitted",
                L"stage=" + std::to_wstring(stage) + L" pixels=560x68 corner=top-right input=none");
        }
        return accepted;
    }
};

struct WindowCandidate {
    DWORD processId{};
    HWND window{};
    long long area{};
};

using ScriptHookKeyboardHandler = void(*)(
    DWORD,
    WORD,
    BYTE,
    BOOL,
    BOOL,
    BOOL,
    BOOL);
using KeyboardHandlerRegister = void(*)(ScriptHookKeyboardHandler);
using KeyboardHandlerUnregister = void(*)(ScriptHookKeyboardHandler);
struct ScriptHookInputApi {
    KeyboardHandlerRegister keyboardRegister{};
    KeyboardHandlerUnregister keyboardUnregister{};
};
constexpr std::uint8_t ProbeLoadingAvailableBit =
    reactorv::scriptprobe::LoadingAvailableBit;
constexpr std::uint8_t ProbeLoadingBit = reactorv::scriptprobe::LoadingBit;
constexpr std::uint8_t ProbePlayerPlayingAvailableBit =
    reactorv::scriptprobe::PlayerPlayingAvailableBit;
constexpr std::uint8_t ProbePlayerPlayingBit =
    reactorv::scriptprobe::PlayerPlayingBit;
constexpr std::uint8_t ProbeFrontendReadyAvailableBit =
    reactorv::scriptprobe::FrontendReadyAvailableBit;
constexpr std::uint8_t ProbeFrontendReadyBit =
    reactorv::scriptprobe::FrontendReadyBit;
constexpr std::uint8_t ProbeLandingMenuAvailableBit =
    reactorv::scriptprobe::LandingMenuAvailableBit;
constexpr std::uint8_t ProbeLandingMenuActiveBit =
    reactorv::scriptprobe::LandingMenuActiveBit;

ScriptHookInputApi scriptHookInputApi{};
std::atomic<bool> scriptHookKeyboardRegistered{false};
std::atomic<bool> scriptHookKeyboardRoutingEnabled{false};
std::atomic<bool> scriptHookKeyboardDispatchObserved{false};
std::atomic<unsigned int> scriptHookKeyboardCallbacksInFlight{0};
std::atomic<std::uint64_t> lastF9RouteClaimedAtMilliseconds{0};
std::atomic<HANDLE> bootstrapAboutToggleEvent{nullptr};
std::atomic<HANDLE> bootstrapInitializerToggleEvent{nullptr};
std::atomic<HANDLE> bootstrapVerifyToggleEvent{nullptr};
std::atomic<std::uint8_t> bootstrapFallbackStage{
    static_cast<std::uint8_t>(
        reactorv::bootstrap::StartupStage::NativeBootstrap)};
std::atomic<unsigned long> keyboardRouteSequence{0};
std::atomic<std::uint8_t> keyboardLastRoute{0};
std::atomic<std::uint8_t> keyboardLastProbeState{0};
std::atomic<std::uint8_t> keyboardLastProbeStatus{0};
std::atomic<std::uint8_t> scriptProbeCachedBits{0};
std::atomic<std::uint8_t> scriptProbeCachedStatus{
    static_cast<std::uint8_t>(
        reactorv::scriptprobe::SnapshotStatus::Unavailable)};
std::atomic<std::uint64_t> scriptProbeCachedSampledAt{0};
std::atomic<bool> scriptProbeExecutionObserved{false};
reactorv::scriptprobe::ReadSnapshotFunction scriptProbeReadSnapshot{};

template <typename Function>
Function ResolveScriptHookExport(HMODULE module, const char* decoratedName) noexcept {
    if (module == nullptr) return nullptr;
    return reinterpret_cast<Function>(GetProcAddress(module, decoratedName));
}

bool CurrentProcessIsForeground() noexcept {
    const HWND foreground = GetForegroundWindow();
    if (foreground == nullptr) return false;
    DWORD processId{};
    GetWindowThreadProcessId(foreground, &processId);
    return processId == GetCurrentProcessId();
}

bool TryResolveScriptProbeReader() noexcept {
    if (scriptProbeReadSnapshot != nullptr) return true;
    const HMODULE module =
        GetModuleHandleW(reactorv::scriptprobe::ModuleName);
    if (module == nullptr) return false;
    scriptProbeReadSnapshot =
        reinterpret_cast<reactorv::scriptprobe::ReadSnapshotFunction>(
            GetProcAddress(
                module,
                reactorv::scriptprobe::ReadSnapshotExportName));
    return scriptProbeReadSnapshot != nullptr;
}

bool RefreshScriptProbeSnapshot() noexcept {
    if (!TryResolveScriptProbeReader()) return false;
    reactorv::scriptprobe::Snapshot snapshot{};
    snapshot.structSize = sizeof(snapshot);
    if (!scriptProbeReadSnapshot(&snapshot) ||
        snapshot.abiVersion != reactorv::scriptprobe::SnapshotAbiVersion ||
        snapshot.sampledAtTickMilliseconds == 0) {
        scriptProbeCachedBits.store(0, std::memory_order_release);
        scriptProbeCachedStatus.store(
            static_cast<std::uint8_t>(
                reactorv::scriptprobe::SnapshotStatus::Unavailable),
            std::memory_order_release);
        scriptProbeCachedSampledAt.store(0, std::memory_order_release);
        return false;
    }
    const bool ready =
        snapshot.status == reactorv::scriptprobe::SnapshotStatus::Ready &&
        snapshot.bits != 0;
    if (snapshot.status != reactorv::scriptprobe::SnapshotStatus::Unavailable) {
        scriptProbeExecutionObserved.store(true, std::memory_order_release);
    }
    scriptProbeCachedBits.store(
        ready ? snapshot.bits : 0,
        std::memory_order_release);
    scriptProbeCachedStatus.store(
        static_cast<std::uint8_t>(snapshot.status),
        std::memory_order_release);
    scriptProbeCachedSampledAt.store(
        snapshot.sampledAtTickMilliseconds,
        std::memory_order_release);
    return ready;
}

reactorv::bootstrap::BootstrapGameStateProbe DecodeBootstrapGameStateProbe(
    const std::uint8_t bits) noexcept {
    return {
        (bits & ProbeLoadingAvailableBit) != 0,
        (bits & ProbeLoadingBit) != 0,
        (bits & ProbePlayerPlayingAvailableBit) != 0,
        (bits & ProbePlayerPlayingBit) != 0,
        (bits & ProbeFrontendReadyAvailableBit) != 0,
        (bits & ProbeFrontendReadyBit) != 0,
        (bits & ProbeLandingMenuAvailableBit) != 0,
        (bits & ProbeLandingMenuActiveBit) != 0,
    };
}

reactorv::bootstrap::BootstrapF9DispatchDecision ResolveCurrentBootstrapF9Dispatch(
    const reactorv::bootstrap::StartupStage fallbackStage,
    const bool aboutDestinationAvailable,
    const bool initializerDestinationAvailable,
    const bool verifyingDestinationAvailable,
    std::uint8_t& probeBits,
    std::uint8_t& probeStatus) noexcept {
    const auto sampledAt =
        scriptProbeCachedSampledAt.load(std::memory_order_acquire);
    const bool scriptFiberObserved =
        scriptProbeExecutionObserved.load(std::memory_order_acquire);
    if (sampledAt == 0) {
        probeBits = 0;
        probeStatus = 0;
        return reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            fallbackStage,
            {},
            false,
            scriptFiberObserved,
            aboutDestinationAvailable,
            initializerDestinationAvailable,
            verifyingDestinationAvailable);
    }
    if (!reactorv::bootstrap::IsBootstrapGameStateProbeFresh(
            sampledAt,
            GetTickCount64())) {
        probeBits = 0;
        probeStatus = 2;
        return reactorv::bootstrap::ResolveBootstrapF9Dispatch(
            fallbackStage,
            {},
            false,
            scriptFiberObserved,
            aboutDestinationAvailable,
            initializerDestinationAvailable,
            verifyingDestinationAvailable);
    }

    probeBits = scriptProbeCachedBits.load(std::memory_order_acquire);
    const auto snapshotStatus = static_cast<reactorv::scriptprobe::SnapshotStatus>(
        scriptProbeCachedStatus.load(std::memory_order_acquire));
    const bool freshProbeAvailable =
        snapshotStatus == reactorv::scriptprobe::SnapshotStatus::Ready &&
        probeBits != 0;
    probeStatus = freshProbeAvailable
        ? 1
        : snapshotStatus == reactorv::scriptprobe::SnapshotStatus::NativeFailure
            ? 3
            : 4;
    return reactorv::bootstrap::ResolveBootstrapF9Dispatch(
        fallbackStage,
        DecodeBootstrapGameStateProbe(probeBits),
        freshProbeAvailable,
        scriptFiberObserved,
        aboutDestinationAvailable,
        initializerDestinationAvailable,
        verifyingDestinationAvailable);
}

const wchar_t* ProbeStatusText(const std::uint8_t status) noexcept {
    switch (status) {
        case 1:
            return L"script-fiber-fresh";
        case 2:
            return L"script-fiber-stale";
        case 3:
            return L"script-fiber-native-failure";
        case 4:
            return L"script-fiber-observed";
        case 0:
        default:
            return L"unpublished";
    }
}

const wchar_t* LegacyShadowDiscoveryStatusText(
    const reactorv::bootstrap::LegacyShadowDiscoveryStatus status) noexcept {
    using Status = reactorv::bootstrap::LegacyShadowDiscoveryStatus;
    switch (status) {
        case Status::Ready: return L"ready";
        case Status::UnsupportedExecutable: return L"unsupported-executable";
        case Status::InvalidImage: return L"invalid-image";
        case Status::VersionUnavailable: return L"version-unavailable";
        case Status::UnsupportedBuild: return L"unsupported-build";
        case Status::SignatureMissing: return L"signature-missing";
        case Status::SignatureAmbiguous: return L"signature-ambiguous";
        case Status::SignatureReadFault: return L"signature-read-fault";
        case Status::UnstableEvidence: return L"unstable-evidence";
        case Status::InvalidTarget: return L"invalid-target";
        case Status::GateClosed: return L"gate-closed";
        case Status::Uninitialized:
        default: return L"uninitialized";
    }
}

const wchar_t* LegacyShadowPollStatusText(
    const reactorv::bootstrap::LegacyShadowPollStatus status) noexcept {
    using Status = reactorv::bootstrap::LegacyShadowPollStatus;
    switch (status) {
        case Status::ReadFault: return L"read-fault";
        case Status::UnsupportedRawValue: return L"unsupported-raw-value";
        case Status::Debouncing: return L"debouncing";
        case Status::StableButUnarmed: return L"stable-but-unarmed";
        case Status::Grounded: return L"grounded";
        case Status::NotReady:
        default: return L"not-ready";
    }
}

const wchar_t* LegacyPatternStatusText(
    const reactorv::bootstrap::LegacyPatternScanStatus status) noexcept {
    using Status = reactorv::bootstrap::LegacyPatternScanStatus;
    switch (status) {
        case Status::Missing: return L"missing";
        case Status::Unique: return L"unique";
        case Status::Ambiguous: return L"ambiguous";
        case Status::Invalid:
        default: return L"invalid";
    }
}

const wchar_t* LegacyInstructionStatusText(
    const reactorv::bootstrap::LegacyShadowInstructionStatus status) noexcept {
    using Status = reactorv::bootstrap::LegacyShadowInstructionStatus;
    switch (status) {
        case Status::SignatureRevalidationMismatch:
            return L"signature-revalidation-mismatch";
        case Status::RangeInvalid: return L"range-invalid";
        case Status::ReadFault: return L"read-fault";
        case Status::OpcodeMismatch: return L"opcode-mismatch";
        case Status::OpcodeMatched: return L"opcode-matched";
        case Status::NotChecked:
        default: return L"not-checked";
    }
}

const wchar_t* LegacyRipDecodeStatusText(
    const reactorv::bootstrap::LegacyRipDecodeStatus status) noexcept {
    using Status = reactorv::bootstrap::LegacyRipDecodeStatus;
    switch (status) {
        case Status::Success: return L"success";
        case Status::InstructionOutOfBounds:
            return L"instruction-out-of-bounds";
        case Status::DisplacementOutOfBounds:
            return L"displacement-out-of-bounds";
        case Status::TargetOutOfBounds: return L"target-out-of-bounds";
        case Status::InvalidArguments:
        default: return L"invalid-arguments";
    }
}

const wchar_t* LegacyTargetValidationStatusText(
    const reactorv::bootstrap::LegacyTargetValidationStatus status) noexcept {
    using Status = reactorv::bootstrap::LegacyTargetValidationStatus;
    switch (status) {
        case Status::Accepted: return L"accepted";
        case Status::ImageBoundsRejected: return L"image-bounds-rejected";
        case Status::AlignmentRejected: return L"alignment-rejected";
        case Status::RegionQueryFailed: return L"region-query-failed";
        case Status::RegionUnavailable: return L"region-unavailable";
        case Status::ProtectionRejected: return L"protection-rejected";
        case Status::TypeRejected: return L"type-rejected";
        case Status::AllocationBaseRejected:
            return L"allocation-base-rejected";
        case Status::RegionAddressOverflow:
            return L"region-address-overflow";
        case Status::TargetOutsideRegion: return L"target-outside-region";
        case Status::NotChecked:
        default: return L"not-checked";
    }
}

const wchar_t* LegacyDataSectionStatusText(
    const reactorv::bootstrap::LegacyDataSectionStatus status) noexcept {
    using Status = reactorv::bootstrap::LegacyDataSectionStatus;
    switch (status) {
        case Status::Accepted: return L"accepted";
        case Status::InvalidArguments: return L"invalid-arguments";
        case Status::HeaderTableRejected: return L"header-table-rejected";
        case Status::HeaderReadFault: return L"header-read-fault";
        case Status::MalformedSectionRange:
            return L"malformed-section-range";
        case Status::Missing: return L"missing";
        case Status::Ambiguous: return L"ambiguous";
        case Status::TargetCrossesBoundary:
            return L"target-crosses-boundary";
        case Status::NameRejected: return L"name-rejected";
        case Status::CharacteristicsRejected:
            return L"characteristics-rejected";
        case Status::NotChecked:
        default: return L"not-checked";
    }
}

std::wstring LegacyDataSectionNameText(
    const std::array<std::uint8_t, 8>& name) {
    std::wstring text;
    for (const auto value : name) {
        if (value == 0) break;
        text.push_back(value >= 0x20 && value <= 0x7E
            ? static_cast<wchar_t>(value)
            : L'?');
    }
    return text.empty() ? L"none" : text;
}

std::wstring LegacyInstructionBytesText(
    const reactorv::bootstrap::LegacyShadowTargetDiagnostics& diagnostics) {
    constexpr wchar_t HexDigits[] = L"0123456789ABCDEF";
    const auto count = std::min(
        diagnostics.instructionBytesRead,
        diagnostics.instructionBytes.size());
    if (count == 0) return L"none";

    std::wstring text;
    text.reserve(count * 3 - 1);
    for (std::size_t index = 0; index < count; ++index) {
        if (index != 0) text.push_back(L'-');
        const auto value = diagnostics.instructionBytes[index];
        text.push_back(HexDigits[(value >> 4U) & 0x0FU]);
        text.push_back(HexDigits[value & 0x0FU]);
    }
    return text;
}

const wchar_t* LegacyClassificationStatusText(
    const reactorv::bootstrap::Legacy3889ClassificationStatus status) noexcept {
    using Status = reactorv::bootstrap::Legacy3889ClassificationStatus;
    switch (status) {
        case Status::UnsupportedRawValue: return L"unsupported-raw-value";
        case Status::Debouncing: return L"debouncing";
        case Status::StableButUnarmed: return L"stable-but-unarmed";
        case Status::Grounded: return L"grounded";
        case Status::InvalidConfiguration:
        default: return L"invalid-configuration";
    }
}

const wchar_t* LegacyRawStateText(
    const reactorv::bootstrap::LegacyStartupRawState state) noexcept {
    using State = reactorv::bootstrap::LegacyStartupRawState;
    switch (state) {
        case State::Frontend: return L"frontend";
        case State::StoryTransition: return L"story-transition";
        case State::StoryActive: return L"story-active";
        case State::ShuttingDown: return L"shutting-down";
        case State::Unknown:
        default: return L"unknown";
    }
}

void OnScriptHookKeyboardMessage(
    const DWORD key,
    const WORD,
    const BYTE,
    const BOOL,
    const BOOL,
    const BOOL wasDownBefore,
    const BOOL isUpNow) noexcept {
    if (!scriptHookKeyboardRoutingEnabled.load(std::memory_order_acquire)) {
        return;
    }
    // Registration occurs well before ScriptHook starts delivering window
    // callbacks. Any real callback proves that its dispatch pump is finally
    // operational; until this boundary the worker keeps its guarded polling
    // fallback active so early F9 presses are not discarded.
    scriptHookKeyboardDispatchObserved.store(true, std::memory_order_release);
    if (key != VK_F9 || wasDownBefore || isUpNow ||
        !CurrentProcessIsForeground()) {
        return;
    }

    scriptHookKeyboardCallbacksInFlight.fetch_add(1, std::memory_order_acq_rel);
    if (!scriptHookKeyboardRoutingEnabled.load(std::memory_order_acquire)) {
        scriptHookKeyboardCallbacksInFlight.fetch_sub(1, std::memory_order_acq_rel);
        return;
    }
    if (!reactorv::bootstrap::TryClaimF9RouteEdge(
            lastF9RouteClaimedAtMilliseconds,
            GetTickCount64())) {
        scriptHookKeyboardCallbacksInFlight.fetch_sub(1, std::memory_order_acq_rel);
        return;
    }

    const auto stage = static_cast<reactorv::bootstrap::StartupStage>(
        bootstrapFallbackStage.load(std::memory_order_acquire));
    const HANDLE aboutDestination =
        bootstrapAboutToggleEvent.load(std::memory_order_acquire);
    const HANDLE initializerDestination =
        bootstrapInitializerToggleEvent.load(std::memory_order_acquire);
    const HANDLE verifyingDestination =
        bootstrapVerifyToggleEvent.load(std::memory_order_acquire);
    std::uint8_t probeBits = 0;
    std::uint8_t probeStatus = 0;
    const auto decision = ResolveCurrentBootstrapF9Dispatch(
        stage,
        aboutDestination != nullptr,
        initializerDestination != nullptr,
        verifyingDestination != nullptr,
        probeBits,
        probeStatus);
    const HANDLE destination =
        decision.route == reactorv::bootstrap::BootstrapSurfaceRoute::About
            ? aboutDestination
            : decision.route ==
                    reactorv::bootstrap::BootstrapSurfaceRoute::Initializing
                ? initializerDestination
                : verifyingDestination;
    if (decision.destinationAvailable && SetEvent(destination)) {
        keyboardLastRoute.store(
            decision.route == reactorv::bootstrap::BootstrapSurfaceRoute::About
                ? 1
                : decision.route ==
                        reactorv::bootstrap::BootstrapSurfaceRoute::Initializing
                    ? 2
                    : 3,
            std::memory_order_release);
        // The keyboard callback consumes only a cached script-fiber snapshot.
        // It never executes a GTA native itself.
        keyboardLastProbeState.store(probeBits, std::memory_order_release);
        keyboardLastProbeStatus.store(probeStatus, std::memory_order_release);
        keyboardRouteSequence.fetch_add(1, std::memory_order_acq_rel);
    }
    scriptHookKeyboardCallbacksInFlight.fetch_sub(1, std::memory_order_acq_rel);
}

bool TryRegisterScriptHookKeyboardHandler(
    const HANDLE aboutToggleEvent,
    const HANDLE initializerToggleEvent,
    const HANDLE verifyingToggleEvent,
    const reactorv::bootstrap::StartupStage fallbackStage) noexcept {
    if (scriptHookKeyboardRegistered.load(std::memory_order_acquire)) return true;

    const HMODULE scriptHook = GetModuleHandleW(L"ScriptHookV.dll");
    if (scriptHook == nullptr) return false;
    ScriptHookInputApi resolved{};
    resolved.keyboardRegister = ResolveScriptHookExport<KeyboardHandlerRegister>(
        scriptHook,
        "?keyboardHandlerRegister@@YAXP6AXKGEHHHH@Z@Z");
    resolved.keyboardUnregister = ResolveScriptHookExport<KeyboardHandlerUnregister>(
        scriptHook,
        "?keyboardHandlerUnregister@@YAXP6AXKGEHHHH@Z@Z");
    if (resolved.keyboardRegister == nullptr ||
        resolved.keyboardUnregister == nullptr) {
        return false;
    }

    scriptHookInputApi = resolved;
    bootstrapAboutToggleEvent.store(aboutToggleEvent, std::memory_order_release);
    bootstrapInitializerToggleEvent.store(
        initializerToggleEvent,
        std::memory_order_release);
    bootstrapVerifyToggleEvent.store(
        verifyingToggleEvent,
        std::memory_order_release);
    bootstrapFallbackStage.store(
        static_cast<std::uint8_t>(fallbackStage),
        std::memory_order_release);
    scriptHookKeyboardRoutingEnabled.store(true, std::memory_order_release);
    // Publish registration before calling into ScriptHook so a synchronous
    // callback can prove dispatch readiness. Registration alone deliberately
    // does not retire the worker's guarded polling fallback.
    scriptHookKeyboardRegistered.store(true, std::memory_order_release);
    scriptHookInputApi.keyboardRegister(OnScriptHookKeyboardMessage);
    return true;
}

void UnregisterScriptHookKeyboardHandler() noexcept {
    scriptHookKeyboardRoutingEnabled.store(false, std::memory_order_release);
    if (scriptHookKeyboardRegistered.exchange(false, std::memory_order_acq_rel) &&
        scriptHookInputApi.keyboardUnregister != nullptr) {
        scriptHookInputApi.keyboardUnregister(OnScriptHookKeyboardMessage);
    }
    for (unsigned int attempt = 0;
         attempt < 100 &&
         scriptHookKeyboardCallbacksInFlight.load(std::memory_order_acquire) != 0;
         ++attempt) {
        Sleep(1);
    }
    bootstrapAboutToggleEvent.store(nullptr, std::memory_order_release);
    bootstrapInitializerToggleEvent.store(nullptr, std::memory_order_release);
    bootstrapVerifyToggleEvent.store(nullptr, std::memory_order_release);
    scriptHookKeyboardDispatchObserved.store(false, std::memory_order_release);
    scriptHookInputApi = {};
}

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

void AppendLegacyShadowDiscoveryLog(
    const reactorv::bootstrap::LegacyShadowDiscoveryReceipt& receipt,
    const wchar_t* source,
    const std::uint32_t attempt = 0,
    const std::uint64_t durationMilliseconds = 0,
    const bool final = false,
    const bool exhausted = false) {
    const auto& diagnostics = receipt.targetDiagnostics;
    const auto& validation = diagnostics.validationEvidence;
    AppendBootstrapLog(
        L"legacy_startup_shadow_discovery",
        std::wstring(L"source=") + source +
            L" status=" + LegacyShadowDiscoveryStatusText(receipt.status) +
            L" pattern=" + LegacyPatternStatusText(receipt.patternStatus) +
            L" matches=" + std::to_wstring(receipt.matchCount) +
            L" match_rva=" + std::to_wstring(receipt.matchRva) +
            L" target_rva=" + std::to_wstring(receipt.targetRva) +
            L" pe_timestamp=" + std::to_wstring(receipt.peTimestamp) +
            L" image_size=" + std::to_wstring(receipt.sizeOfImage) +
            L" read_error=" + std::to_wstring(receipt.readError) +
            L" bytes_read=" + std::to_wstring(receipt.bytesRead) +
            L" instruction_status=" +
                LegacyInstructionStatusText(diagnostics.instructionStatus) +
            L" instruction_rva=" +
                std::to_wstring(diagnostics.instructionRva) +
            L" instruction_bytes=" +
                LegacyInstructionBytesText(diagnostics) +
            L" instruction_bytes_read=" +
                std::to_wstring(diagnostics.instructionBytesRead) +
            L" decode_attempted=" +
                (diagnostics.decodeAttempted ? L"True" : L"False") +
            L" decode_status=" +
                (diagnostics.decodeAttempted
                    ? LegacyRipDecodeStatusText(diagnostics.decodeStatus)
                    : L"not-attempted") +
            L" displacement=" +
                std::to_wstring(diagnostics.displacement) +
            L" candidate_target_rva=" +
                std::to_wstring(diagnostics.candidateTargetRva) +
            L" target_validation=" +
                LegacyTargetValidationStatusText(
                    diagnostics.validationStatus) +
            L" target_query_error=" +
                std::to_wstring(diagnostics.regionQueryError) +
            L" target_state=" +
                std::to_wstring(diagnostics.regionState) +
            L" target_protect=" +
                std::to_wstring(diagnostics.regionProtect) +
            L" target_type=" +
                std::to_wstring(diagnostics.regionType) +
            L" target_region_rva=" +
                std::to_wstring(diagnostics.regionBaseRva) +
            L" target_region_size=" +
                std::to_wstring(diagnostics.regionSize) +
            L" data_section_status=" +
                LegacyDataSectionStatusText(diagnostics.dataSectionStatus) +
            L" data_section_read_error=" +
                std::to_wstring(diagnostics.dataSectionReadError) +
            L" data_section_matches=" +
                std::to_wstring(diagnostics.dataSectionMatchCount) +
            L" data_section_name=" +
                LegacyDataSectionNameText(diagnostics.dataSectionName) +
            L" data_section_rva=" +
                std::to_wstring(diagnostics.dataSectionRva) +
            L" data_section_virtual_size=" +
                std::to_wstring(diagnostics.dataSectionVirtualSize) +
            L" data_section_raw_size=" +
                std::to_wstring(diagnostics.dataSectionRawSize) +
            L" data_section_characteristics=" +
                std::to_wstring(diagnostics.dataSectionCharacteristics) +
            L" image_bounds_pass=" +
                (validation.imageBoundsPass ? L"True" : L"False") +
            L" alignment_pass=" +
                (validation.alignmentPass ? L"True" : L"False") +
            L" region_query_pass=" +
                (validation.regionQueryPass ? L"True" : L"False") +
            L" region_usable_pass=" +
                (validation.regionUsablePass ? L"True" : L"False") +
            L" ordinary_protection_pass=" +
                (validation.ordinaryProtectionPass ? L"True" : L"False") +
            L" execute_read_write_observed=" +
                (validation.executeReadWriteObserved ? L"True" : L"False") +
            L" data_section_backed_protection_pass=" +
                (validation.dataSectionBackedProtectionPass
                    ? L"True"
                    : L"False") +
            L" protection_pass=" +
                (validation.protectionPass ? L"True" : L"False") +
            L" type_pass=" +
                (validation.typePass ? L"True" : L"False") +
            L" allocation_base_pass=" +
                (validation.allocationBasePass ? L"True" : L"False") +
            L" region_address_pass=" +
                (validation.regionAddressPass ? L"True" : L"False") +
            L" target_contained_pass=" +
                (validation.targetContainedPass ? L"True" : L"False") +
            L" attempt=" + std::to_wstring(attempt) +
            L" duration_ms=" + std::to_wstring(durationMilliseconds) +
            L" final=" + (final ? L"True" : L"False") +
            L" exhausted=" + (exhausted ? L"True" : L"False") +
            L" route_effect=False");
}

reactorv::bootstrap::LegacyShadowDiscoveryReceipt DiscoverLegacyStartupShadow(
    void* context,
    const std::stop_token* stopToken) noexcept {
    if (context == nullptr) return {};
    return static_cast<reactorv::bootstrap::LegacyStartupShadowProbe*>(context)
        ->Discover(stopToken);
}

bool FinalizeF9Ownership(
    const HANDLE ownershipReleasedEvent,
    bool& ownershipReleased,
    const reactorv::bootstrap::F9OwnershipExitKind exitKind) {
    const auto decision = reactorv::bootstrap::EvaluateF9OwnershipExit(
        ownershipReleasedEvent != nullptr,
        ownershipReleased,
        exitKind);
    if (!decision.signalBoundary) return ownershipReleased;

    if (!SetEvent(ownershipReleasedEvent)) {
        AppendBootstrapLog(
            L"f9_ownership_release_failed",
            L"error=" + std::to_wstring(GetLastError()));
        return false;
    }

    ownershipReleased = true;
    if (decision.abandoned) {
        const wchar_t* reason = L"worker_exception";
        if (
            exitKind ==
            reactorv::bootstrap::F9OwnershipExitKind::ProcessExitRequested) {
            reason = L"process_exit_requested";
        }
        AppendBootstrapLog(
            L"f9_ownership_abandoned",
            std::wstring(L"reason=") + reason);
    } else {
        AppendBootstrapLog(L"f9_ownership_released");
    }
    return true;
}

void SignalBootstrapHostCloseForOwnershipExit(
    const HANDLE bootstrapHostCloseEvent,
    const reactorv::bootstrap::F9OwnershipExitKind exitKind) {
    if (!reactorv::bootstrap::ShouldCloseBootstrapHostOnOwnershipExit(exitKind)) {
        return;
    }

    const wchar_t* reason =
        exitKind == reactorv::bootstrap::F9OwnershipExitKind::ProcessExitRequested
            ? L"process_exit_requested"
            : L"worker_exception";
    if (bootstrapHostCloseEvent == nullptr) {
        AppendBootstrapLog(
            L"bootstrap_host_lifecycle_close_unavailable",
            std::wstring(L"reason=") + reason);
        return;
    }
    if (!SetEvent(bootstrapHostCloseEvent)) {
        AppendBootstrapLog(
            L"bootstrap_host_lifecycle_close_failed",
            std::wstring(L"reason=") + reason +
                L" error=" + std::to_wstring(GetLastError()));
        return;
    }
    AppendBootstrapLog(
        L"bootstrap_host_lifecycle_close_signaled",
        std::wstring(L"reason=") + reason);
}

ULONGLONG ProcessStartTime() {
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user)) {
        return 0;
    }
    return FileTimeValue(created);
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

bool AttachStatusWindowOwner(const HWND statusWindow, const HWND gameWindow) {
    if (statusWindow == nullptr || gameWindow == nullptr ||
        !IsWindow(statusWindow) || !IsWindow(gameWindow)) {
        return false;
    }
    if (GetWindow(statusWindow, GW_OWNER) == gameWindow) return true;

    SetLastError(ERROR_SUCCESS);
    const auto previous = SetWindowLongPtrW(
        statusWindow,
        GWLP_HWNDPARENT,
        reinterpret_cast<LONG_PTR>(gameWindow));
    return (previous != 0 || GetLastError() == ERROR_SUCCESS) &&
        GetWindow(statusWindow, GW_OWNER) == gameWindow;
}

HWND CreateStatusWindow(WindowState& state, const HWND gameWindow) {
    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = StatusWindowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = WindowClassName;
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    RegisterClassExW(&windowClass);

    if (state.titleFont == nullptr) {
        state.titleFont = CreateFontW(
            -18, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, NONANTIALIASED_QUALITY,
            DEFAULT_PITCH, L"Segoe UI");
    }
    if (state.statusFont == nullptr) {
        state.statusFont = CreateFontW(
            -15, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, NONANTIALIASED_QUALITY,
            DEFAULT_PITCH, L"Segoe UI");
    }

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
        gameWindow,
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
    const std::wstring mutexName =
        PreloaderMutexPrefix + std::to_wstring(GetCurrentProcessId());
    const HANDLE existing = OpenMutexW(SYNCHRONIZE, FALSE, mutexName.c_str());
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
    std::vector<wchar_t> childEnvironment;
    std::size_t removedSteamVariables = 0;
    if (const auto environment = GetEnvironmentStringsW(); environment != nullptr) {
        for (const wchar_t* entry = environment; *entry != L'\0';) {
            const std::wstring value(entry);
            entry += value.size() + 1;
            const auto separator = value.find(L'=', value.empty() || value[0] != L'=' ? 0 : 1);
            const auto name = value.substr(0, separator);
            if (name.size() >= 5 && _wcsnicmp(name.c_str(), L"Steam", 5) == 0) {
                ++removedSteamVariables;
                continue;
            }
            childEnvironment.insert(childEnvironment.end(), value.begin(), value.end());
            childEnvironment.push_back(L'\0');
        }
        FreeEnvironmentStringsW(environment);
    }
    // A Windows environment block is terminated by an additional NUL. The
    // Reactor helper needs normal user/runtime paths but must not inherit
    // Steam's game/overlay identity and become part of the tracked game tree.
    childEnvironment.push_back(L'\0');
    if (childEnvironment.size() == 1) childEnvironment.push_back(L'\0');
    const BOOL created = CreateProcessW(
        preloaderPath.c_str(),
        mutableCommand.data(),
        nullptr,
        nullptr,
        FALSE,
        CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
        childEnvironment.data(),
        workingDirectory.c_str(),
        &startup,
        &process);
    if (!created) {
        AppendBootstrapLog(L"preloader_start_failed", L"error=" + std::to_wstring(GetLastError()));
        return false;
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    AppendBootstrapLog(
        L"preloader_started",
        L"pid=" + std::to_wstring(process.dwProcessId) +
            L" steam_environment_removed=" +
            std::to_wstring(removedSteamVariables));
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

HANDLE CreateBootstrapHostToggleEvent() {
    const std::wstring name =
        BootstrapHostToggleEventPrefix + std::to_wstring(GetCurrentProcessId());
    return CreateEventW(nullptr, FALSE, FALSE, name.c_str());
}

HANDLE CreateBootstrapHostAboutToggleEvent() {
    const std::wstring name =
        BootstrapHostAboutToggleEventPrefix +
        std::to_wstring(GetCurrentProcessId());
    return CreateEventW(nullptr, FALSE, FALSE, name.c_str());
}

HANDLE CreateBootstrapHostVerifyToggleEvent() {
    const std::wstring name =
        BootstrapHostVerifyToggleEventPrefix +
        std::to_wstring(GetCurrentProcessId());
    return CreateEventW(nullptr, FALSE, FALSE, name.c_str());
}

HANDLE OpenBootstrapHostVerifyActiveEvent() {
    const std::wstring name =
        BootstrapHostVerifyActiveEventPrefix +
        std::to_wstring(GetCurrentProcessId());
    return OpenEventW(SYNCHRONIZE, FALSE, name.c_str());
}

HANDLE CreateBootstrapHostInitializerPromotionEvent() {
    const std::wstring name =
        BootstrapHostInitializerPromotionEventPrefix +
        std::to_wstring(GetCurrentProcessId());
    return CreateEventW(nullptr, FALSE, FALSE, name.c_str());
}

HANDLE CreateBootstrapHostCloseEvent() {
    const std::wstring name =
        BootstrapHostCloseEventPrefix + std::to_wstring(GetCurrentProcessId());
    return CreateEventW(nullptr, FALSE, FALSE, name.c_str());
}

HANDLE CreateF9OwnershipReleasedEvent() {
    const std::wstring name =
        F9OwnershipReleasedEventPrefix + std::to_wstring(GetCurrentProcessId());
    return CreateEventW(nullptr, TRUE, FALSE, name.c_str());
}

bool RuntimeReadyEventIsSet(const HANDLE event) {
    return event != nullptr && WaitForSingleObject(event, 0) == WAIT_OBJECT_0;
}

void AppendStartupReaderCounters(
    const reactorv::bootstrap::IncrementalLogSignals& scriptHook,
    const reactorv::bootstrap::IncrementalLogSignals& scriptHookDotNet,
    const reactorv::bootstrap::IncrementalLogSignals& runtime) {
    const auto& scriptHookCounters = scriptHook.Counters();
    const auto& scriptHookDotNetCounters = scriptHookDotNet.Counters();
    const auto& runtimeCounters = runtime.Counters();
    AppendBootstrapLog(
        L"startup_log_reader_counters",
        L"shv_metadata=" + std::to_wstring(scriptHookCounters.metadataChecks) +
            L" shv_reads=" + std::to_wstring(scriptHookCounters.contentReads) +
            L" shv_bytes=" + std::to_wstring(scriptHookCounters.bytesRead) +
            L" shvdn_metadata=" + std::to_wstring(scriptHookDotNetCounters.metadataChecks) +
            L" shvdn_reads=" + std::to_wstring(scriptHookDotNetCounters.contentReads) +
            L" shvdn_bytes=" + std::to_wstring(scriptHookDotNetCounters.bytesRead) +
            L" runtime_metadata=" + std::to_wstring(runtimeCounters.metadataChecks) +
            L" runtime_reads=" + std::to_wstring(runtimeCounters.contentReads) +
            L" runtime_bytes=" + std::to_wstring(runtimeCounters.bytesRead));
}

void PumpMessages() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}

DWORD WINAPI BootstrapWorker(void*) {
    // The ASI is process-lifetime infrastructure. Pin it before the worker
    // sleeps or touches any module-owned state so an unexpected dynamic unload
    // cannot unmap code underneath either native worker.
    HMODULE pinnedBootstrapModule{};
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&BootstrapModulePinAnchor),
            &pinnedBootstrapModule)) {
        return 0;
    }
    // Leave the loader lock before touching files, processes, or user32.
    Sleep(100);
    HANDLE f9OwnershipReleasedEvent = nullptr;
    HANDLE bootstrapHostCloseEvent = nullptr;
    bool f9OwnershipReleased = false;
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
        NativeStatusPublisher nativeStatus(executable);
        HWND gameWindow = FindGameWindow();
        bool gameWindowWasObserved = gameWindow != nullptr;
        ULONGLONG gameWindowMissingSince = 0;
        // Never create an independent top-level bootstrap popup. If GTA's
        // authoritative HWND is not available yet, defer the surface until a
        // later input/maintenance discovery can create it as an owned popup.
        HWND statusWindow = nullptr;
        if (gameWindow != nullptr) {
            statusWindow = CreateStatusWindow(state, gameWindow);
            if (statusWindow != nullptr) {
                AppendBootstrapLog(
                    L"status_window_owner_attached",
                    L"source=initial");
            }
        }
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
        const HANDLE bootstrapHostToggleEvent = CreateBootstrapHostToggleEvent();
        if (bootstrapHostToggleEvent == nullptr) {
            AppendBootstrapLog(
                L"bootstrap_host_event_create_failed",
                L"error=" + std::to_wstring(GetLastError()));
        }
        const HANDLE bootstrapHostAboutToggleEvent =
            CreateBootstrapHostAboutToggleEvent();
        if (bootstrapHostAboutToggleEvent == nullptr) {
            AppendBootstrapLog(
                L"bootstrap_host_about_event_create_failed",
                L"error=" + std::to_wstring(GetLastError()));
        }
        const HANDLE bootstrapHostVerifyToggleEvent =
            CreateBootstrapHostVerifyToggleEvent();
        if (bootstrapHostVerifyToggleEvent == nullptr) {
            AppendBootstrapLog(
                L"bootstrap_host_verify_event_create_failed",
                L"error=" + std::to_wstring(GetLastError()));
        }
        const HANDLE bootstrapHostInitializerPromotionEvent =
            CreateBootstrapHostInitializerPromotionEvent();
        if (bootstrapHostInitializerPromotionEvent == nullptr) {
            AppendBootstrapLog(
                L"bootstrap_host_initializer_promotion_event_create_failed",
                L"error=" + std::to_wstring(GetLastError()));
        }
        bootstrapHostCloseEvent = CreateBootstrapHostCloseEvent();
        if (bootstrapHostCloseEvent == nullptr) {
            AppendBootstrapLog(
                L"bootstrap_host_close_event_create_failed",
                L"error=" + std::to_wstring(GetLastError()));
        }
        // Create the shared ownership boundary before the first key sample.
        // Managed integrations may now fail closed as soon as they load.
        f9OwnershipReleasedEvent = CreateF9OwnershipReleasedEvent();
        if (f9OwnershipReleasedEvent == nullptr) {
            AppendBootstrapLog(
                L"f9_ownership_event_create_failed",
                L"error=" + std::to_wstring(GetLastError()));
        }
        const ULONGLONG processStart = ProcessStartTime();
        const auto gameRoot = executable.parent_path();
        const auto localData = LocalDataDirectory();
        const bool scriptProbeAvailable = TryResolveScriptProbeReader();
        AppendBootstrapLog(
            scriptProbeAvailable
                ? L"scripthook_game_state_probe_connected"
                : L"scripthook_game_state_probe_pending",
            L"source=companion-export native_calls=companion-script-fiber-only");
        reactorv::bootstrap::IncrementalLogSignals scriptHookSignals(
            gameRoot / L"ScriptHookV.log",
            processStart,
            {"INIT: Success",
             "CORE: Creating threads",
             "CORE: Launching main()",
             "INIT: GtaThread collection size"});
        reactorv::bootstrap::IncrementalLogSignals scriptHookDotNetSignals(
            gameRoot / L"ScriptHookVDotNet.log",
            processStart,
            {"Loading scripts from", "Started script RageWebUI.Script.RageWebUiScript."});
        reactorv::bootstrap::IncrementalLogSignals runtimeSignals(
            localData / L"reactorv-runtime.log",
            processStart,
            {"story_mode_ready"},
            reactorv::bootstrap::InitialLogContentPolicy::IgnoreExistingContent);

        // Establish all three file identities and the aggregate runtime-log
        // boundary before the preloader can append any current-session data.
        scriptHookSignals.Refresh();
        scriptHookDotNetSignals.Refresh();
        runtimeSignals.Refresh();

        const auto preloaderPath = reactorv::bootstrap::ResolvePreloaderPath(executable);
        const bool preloaderStarted = StartPreloader(preloaderPath, preloaderPath.parent_path());
        // The Legacy adapter retains its historical "shadow" name, but the
        // exact 3889 profile is now live-qualified: after the build/signature
        // gate and the debounced 23 -> 14 edge, it may queue the one-shot
        // initializer promotion below. Unsupported builds remain diagnostic
        // only and fail closed before acquiring poll ownership.
        reactorv::bootstrap::LegacyStartupShadowProbe legacyStartupShadowProbe;
        reactorv::bootstrap::LegacyStartupShadowRunner
            legacyStartupShadowRunner(
                &legacyStartupShadowProbe,
                DiscoverLegacyStartupShadow);
        const bool legacyStartupShadowRunnerStarted =
            LegacyStartupShadowResearchEnabled &&
            legacyStartupShadowRunner.Start();
        if (!LegacyStartupShadowResearchEnabled) {
            AppendBootstrapLog(
                L"legacy_startup_shadow_disabled",
                L"build_flag=False route_effect=False");
        } else if (!legacyStartupShadowRunnerStarted) {
            AppendBootstrapLog(
                L"legacy_startup_shadow_start_failed",
                L"route_effect=False");
        }
        std::size_t legacyStartupShadowLoggedAttempts{};
        bool legacyStartupShadowPollOwner = false;
        bool legacyStartupShadowOwnershipResolved = false;
        bool legacyStartupClassificationObserved = false;
        ULONGLONG legacyStartupLastSampleLogAt{};
        bool legacyStartupPollStatusObserved = false;
        reactorv::bootstrap::LegacyShadowPollStatus
            previousLegacyStartupPollStatus =
                reactorv::bootstrap::LegacyShadowPollStatus::NotReady;
        reactorv::bootstrap::Legacy3889ClassificationStatus
            previousLegacyStartupClassification =
                reactorv::bootstrap::
                    Legacy3889ClassificationStatus::InvalidConfiguration;
        HANDLE bootstrapHostVerifyActiveEvent =
            OpenBootstrapHostVerifyActiveEvent();
        const bool keyboardHandlerStarted = TryRegisterScriptHookKeyboardHandler(
            bootstrapHostAboutToggleEvent,
            bootstrapHostToggleEvent,
            bootstrapHostVerifyToggleEvent,
            state.stage);
        AppendBootstrapLog(
            keyboardHandlerStarted
                ? L"scripthook_keyboard_handler_registered"
                : L"scripthook_keyboard_handler_pending");
        auto previousStage = state.stage;
        bool previousF9Down = false;
        bool previousEscapeDown = false;
        bool previousF4Down = false;
        bool previousEnterDown = false;
        bool handoffWaitingForReleaseLogged = false;
        ULONGLONG nextMaintenanceAt = 0;
        unsigned long observedKeyboardRouteSequence = 0;
        bool keyboardHandlerUnavailableLogged = false;
        bool keyboardDispatchObservedLogged = false;
        bool scriptProbeConnectionLogged = scriptProbeAvailable;
        bool promotionIssuedForActiveVerification = false;
        // One monotonic pre-handoff latch is shared by the native Legacy edge
        // and the ScriptHook/log fallback. Whichever source wins suppresses a
        // second host generation from the other source.
        bool initializerPromotionIssuedBeforeRuntimeReady = false;
        bool legacyObjectiveInitializerPromotionPending = false;
        bool initializerPromotionSignalFailureLogged = false;
        reactorv::bootstrap::BootstrapProcessExitIntent processExitIntent{};
        // Bootstrap owns F9 for the lifetime of the process until either the
        // managed runtime explicitly accepts the handoff or GTA reaches a
        // confirmed exit boundary. A wall-clock timeout can strand a visible
        // persistent host without an input owner during long frontend runs.
        auto workerExitKind =
            reactorv::bootstrap::F9OwnershipExitKind::WorkerException;

        while (true) {
            PumpMessages();
            const ULONGLONG now = GetTickCount64();
            const bool runtimeReadyForPromotion =
                RuntimeReadyEventIsSet(runtimeReadyEvent);
            reactorv::bootstrap::LegacyShadowDiscoveryAttempt
                legacyStartupShadowAttempt{};
            while (legacyStartupShadowRunner.TryReadAttempt(
                legacyStartupShadowLoggedAttempts,
                legacyStartupShadowAttempt)) {
                AppendLegacyShadowDiscoveryLog(
                    legacyStartupShadowAttempt.receipt,
                    legacyStartupShadowAttempt.attempt == 1
                        ? L"async-initial"
                        : L"async-runtime-unpack-retry",
                    legacyStartupShadowAttempt.attempt,
                    legacyStartupShadowAttempt.durationMilliseconds,
                    legacyStartupShadowAttempt.final,
                    legacyStartupShadowAttempt.exhausted);
                ++legacyStartupShadowLoggedAttempts;
            }
            if (!legacyStartupShadowOwnershipResolved &&
                legacyStartupShadowRunnerStarted &&
                legacyStartupShadowRunner.IsCompleted()) {
                legacyStartupShadowRunner.Join();
                legacyStartupShadowOwnershipResolved = true;
                legacyStartupShadowPollOwner =
                    legacyStartupShadowRunner.IsReady();
                AppendBootstrapLog(
                    L"legacy_startup_shadow_ownership",
                    std::wstring(L"poll_owner=") +
                        (legacyStartupShadowPollOwner ? L"True" : L"False") +
                        L" route_effect=False");
            }
            if (legacyStartupShadowPollOwner &&
                legacyStartupShadowProbe.IsReady()) {
                const auto shadow = legacyStartupShadowProbe.Poll(now);
                const bool pollStatusChanged =
                    !legacyStartupPollStatusObserved ||
                    shadow.status != previousLegacyStartupPollStatus;
                legacyStartupPollStatusObserved = true;
                previousLegacyStartupPollStatus = shadow.status;
                if (shadow.status ==
                        reactorv::bootstrap::
                            LegacyShadowPollStatus::ReadFault) {
                    if (pollStatusChanged) {
                        AppendBootstrapLog(
                            L"legacy_startup_shadow_read_fault",
                            L"error=" + std::to_wstring(shadow.readError) +
                                L" bytes_read=" +
                                std::to_wstring(shadow.bytesRead) +
                                L" consecutive=" + std::to_wstring(
                                    shadow.consecutiveReadFaults) +
                                L" continuity_reset=True route_effect=False");
                    }
                } else {
                    const bool classificationChanged =
                        !legacyStartupClassificationObserved ||
                        shadow.classification.status !=
                            previousLegacyStartupClassification;
                    const bool changedSampleLogDue =
                        legacyStartupLastSampleLogAt == 0 ||
                    now - legacyStartupLastSampleLogAt >= 1000;
                    if (classificationChanged || pollStatusChanged ||
                        (shadow.rawValueChanged && changedSampleLogDue)) {
                        legacyStartupLastSampleLogAt = now;
                        legacyStartupClassificationObserved = true;
                        previousLegacyStartupClassification =
                            shadow.classification.status;
                        AppendBootstrapLog(
                            L"legacy_startup_shadow_sample",
                            L"raw=" + std::to_wstring(shadow.rawValue) +
                                L" poll_status=" +
                                LegacyShadowPollStatusText(shadow.status) +
                                L" classification=" +
                                LegacyClassificationStatusText(
                                    shadow.classification.status) +
                                L" state=" +
                                LegacyRawStateText(
                                    shadow.classification.state) +
                                L" stable_samples=" +
                                std::to_wstring(
                                    shadow.classification.consecutiveSamples) +
                                L" frontend_armed=" +
                                (shadow.classification.frontendArmed
                                    ? L"True"
                                    : L"False") +
                                L" session=" +
                                std::to_wstring(shadow.sessionGeneration) +
                                L" route_effect=False");
                    }
                    if (shadow.wouldEnterStory) {
                        legacyObjectiveInitializerPromotionPending = true;
                        AppendBootstrapLog(
                            L"legacy_startup_shadow_entering_story",
                            L"edge=" + std::to_wstring(
                                shadow.diagnosticEdgeSequence) +
                                L" source_observation=" + std::to_wstring(
                                    shadow.diagnosticSourceObservationSequence) +
                                L" session=" +
                                std::to_wstring(shadow.sessionGeneration) +
                                L" route_effect=Queued");
                    }
                }
                if (!legacyStartupShadowProbe.IsReady()) {
                    legacyStartupShadowPollOwner = false;
                    AppendBootstrapLog(
                        L"legacy_startup_shadow_deactivated",
                        L"reason=read-fault-threshold route_effect=False");
                }
            }
            RefreshScriptProbeSnapshot();
            if (!scriptProbeConnectionLogged &&
                scriptProbeReadSnapshot != nullptr) {
                scriptProbeConnectionLogged = true;
                AppendBootstrapLog(
                    L"scripthook_game_state_probe_connected",
                    L"source=companion-export native_calls=companion-script-fiber-only");
            }
            if (bootstrapHostVerifyActiveEvent == nullptr) {
                bootstrapHostVerifyActiveEvent =
                    OpenBootstrapHostVerifyActiveEvent();
            }
            const bool verificationActive =
                bootstrapHostVerifyActiveEvent != nullptr &&
                WaitForSingleObject(bootstrapHostVerifyActiveEvent, 0) ==
                    WAIT_OBJECT_0;
            std::uint8_t promotionProbeBits = 0;
            std::uint8_t promotionProbeStatus = 0;
            const auto promotion = ResolveCurrentBootstrapF9Dispatch(
                state.stage,
                bootstrapHostAboutToggleEvent != nullptr,
                bootstrapHostToggleEvent != nullptr,
                bootstrapHostVerifyToggleEvent != nullptr,
                promotionProbeBits,
                promotionProbeStatus);
            const auto promotionDecision =
                reactorv::bootstrap::EvaluateVerificationPromotion(
                    verificationActive,
                    promotionIssuedForActiveVerification,
                    promotionProbeStatus,
                    promotion);
            if (!verificationActive) {
                // The preloader owns this manual-reset acknowledgement and
                // clears it on every close or surface replacement. Reset the
                // latch only after observing that boundary so a late snapshot
                // can never resurrect a verification surface the user closed.
                promotionIssuedForActiveVerification =
                    promotionDecision.nextPromotionIssued;
            } else if (promotionDecision.shouldPromote) {
                const HANDLE destination =
                    promotion.route ==
                            reactorv::bootstrap::BootstrapSurfaceRoute::About
                        ? bootstrapHostAboutToggleEvent
                        : bootstrapHostToggleEvent;
                if (destination != nullptr && SetEvent(destination)) {
                    promotionIssuedForActiveVerification =
                        promotionDecision.nextPromotionIssued;
                    AppendBootstrapLog(
                        promotion.route ==
                                reactorv::bootstrap::BootstrapSurfaceRoute::About
                            ? L"bootstrap_host_about_toggle_signaled"
                            : L"bootstrap_host_toggle_signaled",
                        L"source=verification-promotion probe=script-fiber-fresh");
                }
            }

            const bool fallbackObjectiveStoryRoute =
                promotion.route ==
                    reactorv::bootstrap::BootstrapSurfaceRoute::Initializing &&
                promotion.destinationAvailable;
            const bool legacyObjectiveStoryRoute =
                legacyObjectiveInitializerPromotionPending;
            const bool objectiveStoryPromotionRequested =
                legacyObjectiveStoryRoute || fallbackObjectiveStoryRoute;
            if (reactorv::bootstrap::ShouldAttemptObjectiveInitializerPromotion(
                    initializerPromotionIssuedBeforeRuntimeReady,
                    preloaderStarted,
                    runtimeReadyForPromotion,
                    objectiveStoryPromotionRequested,
                    bootstrapHostInitializerPromotionEvent != nullptr)) {
                if (SetEvent(bootstrapHostInitializerPromotionEvent)) {
                    initializerPromotionIssuedBeforeRuntimeReady = true;
                    legacyObjectiveInitializerPromotionPending = false;
                    initializerPromotionSignalFailureLogged = false;
                    AppendBootstrapLog(
                        L"bootstrap_host_initializer_promotion_signaled",
                        std::wstring(L"source=") +
                            (legacyObjectiveStoryRoute
                                ? L"legacy-native-lifecycle"
                                : L"objective-story-route") +
                            L" probe=" + ProbeStatusText(promotionProbeStatus) +
                            L" stage=" +
                            reactorv::bootstrap::StartupStageText(state.stage));
                } else if (!initializerPromotionSignalFailureLogged) {
                    initializerPromotionSignalFailureLogged = true;
                    AppendBootstrapLog(
                        L"bootstrap_host_initializer_promotion_signal_failed",
                        L"error=" + std::to_wstring(GetLastError()) +
                            L" retry_pending=True");
                }
            } else if (
                initializerPromotionIssuedBeforeRuntimeReady ||
                runtimeReadyForPromotion) {
                legacyObjectiveInitializerPromotionPending = false;
            }

            if (gameWindow != nullptr && !IsWindow(gameWindow)) {
                gameWindow = nullptr;
                if (gameWindowMissingSince == 0) gameWindowMissingSince = now;
            }
            if (reactorv::bootstrap::IsBootstrapGameWindowLossConfirmed(
                    gameWindowWasObserved,
                    gameWindow != nullptr,
                    gameWindowMissingSince,
                    now)) {
                workerExitKind =
                    reactorv::bootstrap::F9OwnershipExitKind::ProcessExitRequested;
                AppendBootstrapLog(
                    L"game_exit_boundary_confirmed",
                    L"source=game-window-destroyed grace_ms=" +
                        std::to_wstring(
                            reactorv::bootstrap::BootstrapGameWindowLossGraceMilliseconds()));
                break;
            }

            // Sample the one-shot Story Mode handoff before routing this key
            // edge. ProviderConnected is intentionally irrelevant here: IPC
            // commonly attaches while GTA is still at the main menu.
            const bool runtimeReady = runtimeReadyForPromotion;
            const SHORT f9State = GetAsyncKeyState(VK_F9);
            const bool f9Down = (f9State & 0x8000) != 0;
            const bool f9PressedSinceLastPoll = (f9State & 0x0001) != 0;
            const SHORT escapeState = GetAsyncKeyState(VK_ESCAPE);
            const bool escapeDown = (escapeState & 0x8000) != 0;
            const bool escapePressedSinceLastPoll = (escapeState & 0x0001) != 0;
            const SHORT altState = GetAsyncKeyState(VK_MENU);
            const bool altDown = (altState & 0x8000) != 0;
            const bool altPressedSinceLastPoll = (altState & 0x0001) != 0;
            const SHORT f4State = GetAsyncKeyState(VK_F4);
            const bool f4Down = (f4State & 0x8000) != 0;
            const bool f4PressedSinceLastPoll = (f4State & 0x0001) != 0;
            const SHORT enterState = GetAsyncKeyState(VK_RETURN);
            const bool enterDown = (enterState & 0x8000) != 0;
            const bool enterPressedSinceLastPoll = (enterState & 0x0001) != 0;
            if ((f9PressedSinceLastPoll ||
                 (f9Down && !previousF9Down) ||
                 escapePressedSinceLastPoll ||
                 (escapeDown && !previousEscapeDown) ||
                 f4PressedSinceLastPoll ||
                 (f4Down && !previousF4Down) ||
                 enterPressedSinceLastPoll ||
                 (enterDown && !previousEnterDown)) &&
                gameWindow == nullptr) {
                // Window discovery is normally maintenance work. Resolve it
                // on an actual edge so the first short tap is not discarded.
                gameWindow = FindGameWindow();
                if (gameWindow != nullptr) {
                    gameWindowWasObserved = true;
                    gameWindowMissingSince = 0;
                    if (statusWindow == nullptr || !IsWindow(statusWindow)) {
                        statusWindow = CreateStatusWindow(state, gameWindow);
                    } else {
                        AttachStatusWindowOwner(statusWindow, gameWindow);
                    }
                }
            }
            const bool gameForeground =
                gameWindow != nullptr && IsGameForeground(gameWindow);
            const auto processExitDecision =
                reactorv::bootstrap::EvaluateBootstrapProcessExitInput(
                    processExitIntent,
                    now,
                    gameForeground,
                    altDown || altPressedSinceLastPoll,
                    f4PressedSinceLastPoll || (f4Down && !previousF4Down),
                    enterPressedSinceLastPoll ||
                        (enterDown && !previousEnterDown),
                    escapePressedSinceLastPoll ||
                        (escapeDown && !previousEscapeDown));
            if (!processExitIntent.pending &&
                processExitDecision.nextIntent.pending) {
                AppendBootstrapLog(
                    L"game_exit_intent_armed",
                    L"source=alt-f4 confirmation_window_ms=" +
                        std::to_wstring(
                            reactorv::bootstrap::BootstrapProcessExitIntentLifetimeMilliseconds()));
            } else if (processExitIntent.pending &&
                !processExitDecision.nextIntent.pending) {
                AppendBootstrapLog(
                    L"game_exit_intent_cancelled",
                    L"source=escape-or-expiry");
            }
            processExitIntent = processExitDecision.nextIntent;
            if (processExitDecision.confirmed) {
                workerExitKind =
                    reactorv::bootstrap::F9OwnershipExitKind::ProcessExitRequested;
                AppendBootstrapLog(
                    L"game_exit_boundary_confirmed",
                    L"source=alt-f4-enter");
                break;
            }
            const bool useF9PollingFallback =
                reactorv::bootstrap::ShouldUseF9PollingFallback(
                    scriptHookKeyboardRegistered.load(std::memory_order_acquire),
                    scriptHookKeyboardDispatchObserved.load(
                        std::memory_order_acquire));
            const auto f9Decision = reactorv::bootstrap::EvaluateF9Input(
                runtimeReady,
                f9Down,
                useF9PollingFallback ? f9PressedSinceLastPoll : false,
                previousF9Down,
                gameForeground);
            if (f9Decision.routeToBootstrap &&
                useF9PollingFallback &&
                reactorv::bootstrap::TryClaimF9RouteEdge(
                    lastF9RouteClaimedAtMilliseconds,
                    now)) {
                // EvaluateF9Input runs while native still owns F9. Do not
                // suppress an accepted edge if RuntimeReady changes between
                // sampling and delivery; ownership transfers only on a later
                // idle physical sample.
                std::uint8_t probeBits = 0;
                std::uint8_t probeStatus = 0;
                const auto decision = ResolveCurrentBootstrapF9Dispatch(
                    state.stage,
                    bootstrapHostAboutToggleEvent != nullptr,
                    bootstrapHostToggleEvent != nullptr,
                    bootstrapHostVerifyToggleEvent != nullptr,
                    probeBits,
                    probeStatus);
                const HANDLE destination =
                    decision.route == reactorv::bootstrap::BootstrapSurfaceRoute::About
                        ? bootstrapHostAboutToggleEvent
                        : decision.route ==
                                reactorv::bootstrap::BootstrapSurfaceRoute::Initializing
                            ? bootstrapHostToggleEvent
                            : bootstrapHostVerifyToggleEvent;
                if (decision.destinationAvailable) {
                    SetEvent(destination);
                    AppendBootstrapLog(
                        decision.route == reactorv::bootstrap::BootstrapSurfaceRoute::About
                            ? L"bootstrap_host_about_toggle_signaled"
                            : decision.route ==
                                    reactorv::bootstrap::BootstrapSurfaceRoute::Initializing
                                ? L"bootstrap_host_toggle_signaled"
                                : L"bootstrap_host_verify_toggle_signaled",
                        std::wstring(L"key=F9 source=polling-pre-dispatch probe=") +
                            ProbeStatusText(probeStatus));
                }
            }
            if (!keyboardDispatchObservedLogged &&
                scriptHookKeyboardDispatchObserved.load(
                    std::memory_order_acquire)) {
                keyboardDispatchObservedLogged = true;
                AppendBootstrapLog(
                    L"scripthook_keyboard_dispatch_observed",
                    L"source=callback polling_fallback=False");
            }
            const auto currentKeyboardRouteSequence =
                keyboardRouteSequence.load(std::memory_order_acquire);
            if (currentKeyboardRouteSequence != observedKeyboardRouteSequence) {
                observedKeyboardRouteSequence = currentKeyboardRouteSequence;
                const auto route = keyboardLastRoute.load(std::memory_order_acquire);
                const auto probe = keyboardLastProbeState.load(std::memory_order_acquire);
                const auto probeStatus =
                    keyboardLastProbeStatus.load(std::memory_order_acquire);
                const auto ProbeValue = [probe](
                    const std::uint8_t availableBit,
                    const std::uint8_t valueBit) -> const wchar_t* {
                    if ((probe & availableBit) == 0) return L"unavailable";
                    return (probe & valueBit) != 0 ? L"true" : L"false";
                };
                const std::wstring probeDetail =
                    std::wstring(L" probe=") +
                    ProbeStatusText(probeStatus) +
                    L" loading=" +
                    ProbeValue(ProbeLoadingAvailableBit, ProbeLoadingBit) +
                    L" player_playing=" +
                    ProbeValue(
                        ProbePlayerPlayingAvailableBit,
                        ProbePlayerPlayingBit) +
                    L" frontend_ready=" +
                    ProbeValue(
                        ProbeFrontendReadyAvailableBit,
                        ProbeFrontendReadyBit) +
                    L" landing_menu=" +
                    ProbeValue(
                        ProbeLandingMenuAvailableBit,
                        ProbeLandingMenuActiveBit);
                AppendBootstrapLog(
                    route == 1
                        ? L"bootstrap_host_about_toggle_signaled"
                        : route == 2
                            ? L"bootstrap_host_toggle_signaled"
                            : L"bootstrap_host_verify_toggle_signaled",
                    std::wstring(L"key=F9 source=scripthook_keyboard stage=") +
                        reactorv::bootstrap::StartupStageText(
                            static_cast<reactorv::bootstrap::StartupStage>(
                                bootstrapFallbackStage.load(
                                    std::memory_order_acquire))) +
                        probeDetail);
            }
            const bool closeRequested =
                reactorv::bootstrap::EvaluateBootstrapCloseInput(
                    escapeDown,
                    escapePressedSinceLastPoll,
                    previousEscapeDown,
                    gameForeground);
            if (closeRequested && bootstrapHostCloseEvent != nullptr) {
                SetEvent(bootstrapHostCloseEvent);
                AppendBootstrapLog(L"bootstrap_host_close_signaled", L"key=Escape");
            }
            previousF9Down = f9Down;
            previousEscapeDown = escapeDown;
            previousF4Down = f4Down;
            previousEnterDown = enterDown;

            if (runtimeReady && !f9Decision.releaseOwnership &&
                !handoffWaitingForReleaseLogged) {
                AppendBootstrapLog(
                    L"runtime_handoff_waiting_for_f9_release");
                handoffWaitingForReleaseLogged = true;
            }

            if (f9Decision.releaseOwnership) {
                UnregisterScriptHookKeyboardHandler();
                FinalizeF9Ownership(
                    f9OwnershipReleasedEvent,
                    f9OwnershipReleased,
                    reactorv::bootstrap::F9OwnershipExitKind::RuntimeHandoff);
                state.stage = reactorv::bootstrap::StartupStage::StoryModeReady;
                if (statusWindow != nullptr && IsWindow(statusWindow)) {
                    InvalidateRect(statusWindow, nullptr, TRUE);
                    UpdateWindow(statusWindow);
                    for (BYTE alpha = 255; alpha > 25;
                         alpha = static_cast<BYTE>(alpha - 25)) {
                        SetLayeredWindowAttributes(
                            statusWindow,
                            TransparentKey,
                            alpha,
                            LWA_COLORKEY | LWA_ALPHA);
                        Sleep(35);
                    }
                }
                AppendStartupReaderCounters(
                    scriptHookSignals,
                    scriptHookDotNetSignals,
                    runtimeSignals);
                AppendBootstrapLog(L"runtime_handoff_complete");
                workerExitKind =
                    reactorv::bootstrap::F9OwnershipExitKind::RuntimeHandoff;
                break;
            }

            // Window placement, file tails, and stage diagnostics are kept off
            // the frame-scale input path. Recheck them only every 250 ms.
            if (now >= nextMaintenanceAt) {
                nextMaintenanceAt = now +
                    reactorv::bootstrap::MaintenancePollIntervalMilliseconds();
                const HWND discoveredGameWindow = FindGameWindow();
                if (discoveredGameWindow != nullptr) {
                    const bool ownerChanged = discoveredGameWindow != gameWindow;
                    gameWindow = discoveredGameWindow;
                    gameWindowWasObserved = true;
                    gameWindowMissingSince = 0;
                    if (statusWindow == nullptr || !IsWindow(statusWindow)) {
                        statusWindow = CreateStatusWindow(state, gameWindow);
                        if (statusWindow != nullptr) {
                            AppendBootstrapLog(
                                L"status_window_owner_attached",
                                L"source=window-recreated");
                        }
                    } else if (ownerChanged &&
                        AttachStatusWindowOwner(statusWindow, gameWindow)) {
                        AppendBootstrapLog(
                            L"status_window_owner_attached",
                            L"source=game-window-replaced");
                    }
                } else if (gameWindow == nullptr &&
                    gameWindowWasObserved &&
                    gameWindowMissingSince == 0) {
                    gameWindowMissingSince = now;
                }
                if (!scriptHookKeyboardRegistered.load(std::memory_order_acquire)) {
                    const bool registered = TryRegisterScriptHookKeyboardHandler(
                        bootstrapHostAboutToggleEvent,
                        bootstrapHostToggleEvent,
                        bootstrapHostVerifyToggleEvent,
                        state.stage);
                    if (registered) {
                        keyboardHandlerUnavailableLogged = false;
                        AppendBootstrapLog(
                            L"scripthook_keyboard_handler_registered",
                            L"source=maintenance_retry");
                    } else if (!keyboardHandlerUnavailableLogged &&
                        scriptHookSignals.HasSignal("INIT: Success")) {
                        keyboardHandlerUnavailableLogged = true;
                        AppendBootstrapLog(
                            L"scripthook_keyboard_handler_unavailable",
                            L"fallback=no_intent_until_authoritative_stage");
                    }
                }
                const bool nativeStatusVisible = nativeStatus.Publish(state);
                if (statusWindow != nullptr && IsWindow(statusWindow)) {
                    if (!nativeStatusVisible && gameWindow != nullptr && IsGameForeground(gameWindow)) {
                        PositionStatusWindow(statusWindow, gameWindow);
                    } else {
                        ShowWindow(statusWindow, SW_HIDE);
                    }
                }

                // RuntimeReady is the explicit ownership boundary. Once it is
                // set, the native bootstrap no longer needs filesystem-derived
                // evidence and must stop touching startup logs entirely.
                if (!runtimeReady) {
                    scriptHookSignals.Refresh();
                    scriptHookDotNetSignals.Refresh();
                    runtimeSignals.Refresh();
                }
                const reactorv::bootstrap::StartupSignals startupSignals{
                    scriptHookSignals.HasSignal("INIT: Success"),
                    scriptHookSignals.HasSignal("CORE: Creating threads") ||
                        scriptHookSignals.HasSignal("CORE: Launching main()") ||
                        scriptHookSignals.HasSignal(
                            "INIT: GtaThread collection size"),
                    scriptHookDotNetSignals.HasSignal("Loading scripts from") ||
                        scriptHookDotNetSignals.HasSignal(
                            "Started script RageWebUI.Script.RageWebUiScript."),
                    runtimeSignals.HasSignal("story_mode_ready") || runtimeReady,
                };
                const auto stage = reactorv::bootstrap::DetectStartupStage(
                    startupSignals,
                    preloaderStarted,
                    RuntimeReadyEventIsSet(preloadDataReadyEvent));
                if (stage != previousStage) {
                    state.stage = stage;
                    previousStage = stage;
                    bootstrapFallbackStage.store(
                        static_cast<std::uint8_t>(stage),
                        std::memory_order_release);
                    AppendBootstrapLog(
                        L"stage_changed",
                        reactorv::bootstrap::StartupStageText(stage));
                    if (statusWindow != nullptr && IsWindow(statusWindow))
                        InvalidateRect(statusWindow, nullptr, TRUE);
                }
            }
            Sleep(reactorv::bootstrap::F9PollIntervalMilliseconds());
        }

        // Retire bootstrap pixels before abandoning the corresponding input
        // owner. Runtime handoff intentionally keeps the persistent host alive
        // for the managed provider and has already released this boundary.
        SignalBootstrapHostCloseForOwnershipExit(
            bootstrapHostCloseEvent,
            workerExitKind);
        UnregisterScriptHookKeyboardHandler();
        FinalizeF9Ownership(
            f9OwnershipReleasedEvent,
            f9OwnershipReleased,
            workerExitKind);

        if (statusWindow != nullptr && IsWindow(statusWindow))
            DestroyWindow(statusWindow);
        if (runtimeReadyEvent != nullptr) CloseHandle(runtimeReadyEvent);
        if (preloadDataReadyEvent != nullptr) CloseHandle(preloadDataReadyEvent);
        if (bootstrapHostToggleEvent != nullptr) CloseHandle(bootstrapHostToggleEvent);
        if (bootstrapHostAboutToggleEvent != nullptr)
            CloseHandle(bootstrapHostAboutToggleEvent);
        if (bootstrapHostVerifyToggleEvent != nullptr)
            CloseHandle(bootstrapHostVerifyToggleEvent);
        if (bootstrapHostVerifyActiveEvent != nullptr)
            CloseHandle(bootstrapHostVerifyActiveEvent);
        if (bootstrapHostInitializerPromotionEvent != nullptr)
            CloseHandle(bootstrapHostInitializerPromotionEvent);
        if (bootstrapHostCloseEvent != nullptr) {
            CloseHandle(bootstrapHostCloseEvent);
            bootstrapHostCloseEvent = nullptr;
        }
        if (f9OwnershipReleasedEvent != nullptr) {
            CloseHandle(f9OwnershipReleasedEvent);
            f9OwnershipReleasedEvent = nullptr;
        }
        if (state.titleFont != nullptr) DeleteObject(state.titleFont);
        if (state.statusFont != nullptr) DeleteObject(state.statusFont);
        ReleaseMutex(singleton);
        CloseHandle(singleton);
        AppendBootstrapLog(L"bootstrap_stopped");
    } catch (...) {
        // A persistent host must never remain visible after its native input
        // owner fails. Queue its typed close boundary before unregistering the
        // keyboard callback and releasing F9 to any managed consumer.
        SignalBootstrapHostCloseForOwnershipExit(
            bootstrapHostCloseEvent,
            reactorv::bootstrap::F9OwnershipExitKind::WorkerException);
        UnregisterScriptHookKeyboardHandler();
        FinalizeF9Ownership(
            f9OwnershipReleasedEvent,
            f9OwnershipReleased,
            reactorv::bootstrap::F9OwnershipExitKind::WorkerException);
        if (f9OwnershipReleasedEvent != nullptr) {
            CloseHandle(f9OwnershipReleasedEvent);
            f9OwnershipReleasedEvent = nullptr;
        }
        if (bootstrapHostCloseEvent != nullptr) {
            CloseHandle(bootstrapHostCloseEvent);
            bootstrapHostCloseEvent = nullptr;
        }
        AppendBootstrapLog(L"bootstrap_failed", L"unhandled_exception");
    }
    return 0;
}

} // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        // This ASI intentionally has no static ScriptHook dependency and does
        // no native registration under loader lock. ReactorV.ScriptProbe.asi
        // owns the official ScriptHook import and script-fiber sampling; this
        // bootstrap only consumes its exported atomic snapshot.
        const HANDLE worker = CreateThread(nullptr, 0, BootstrapWorker, nullptr, 0, nullptr);
        if (worker != nullptr) CloseHandle(worker);
    }
    return TRUE;
}
