#include "ScriptProbeAbi.h"
#include "ScriptProbeState.h"

#include <windows.h>
#if defined(_MSC_VER)
#pragma warning(push)
#pragma warning(disable : 4505)
#endif
#include <main.h>
#if defined(_MSC_VER)
#pragma warning(pop)
#endif

#include <atomic>
#include <cstdint>
#include <string>

namespace {

constexpr std::uint64_t GetIsLoadingScreenActiveHash = 0x10D0A8F259E93EC9ULL;
constexpr std::uint64_t IsPlayerPlayingHash = 0x5E9564D8246B909AULL;
constexpr std::uint64_t IsFrontendReadyForControlHash = 0x3BAB9A4E4F2FF5C7ULL;
constexpr std::uint64_t LandingMenuIsActiveHash = 0x3BBBD13E5041A79EULL;
constexpr DWORD ActiveSampleDelayMilliseconds = 100;
constexpr DWORD ParkedSampleDelayMilliseconds = 1000;
constexpr wchar_t RuntimeReadyEventPrefix[] = L"Local\\ReactorV.RuntimeReady.";

std::atomic<bool> registered{false};
HANDLE runtimeReadyEvent{};

bool DecodeNativeBool(const std::uint64_t value) noexcept {
    return static_cast<std::uint32_t>(value & 0xFFFFFFFFULL) != 0;
}

bool TryInvokeBoolNative(
    const std::uint64_t hash,
    const bool hasArgument,
    const std::uint64_t argument,
    bool& result) noexcept {
#if defined(_MSC_VER)
    __try {
        nativeInit(hash);
        if (hasArgument) nativePush64(argument);
        const auto value = nativeCall();
        if (value == nullptr) return false;
        result = DecodeNativeBool(*value);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
#else
    try {
        nativeInit(hash);
        if (hasArgument) nativePush64(argument);
        const auto value = nativeCall();
        if (value == nullptr) return false;
        result = DecodeNativeBool(*value);
        return true;
    } catch (...) {
        return false;
    }
#endif
}

HANDLE ResolveRuntimeReadyEvent() noexcept {
    if (runtimeReadyEvent != nullptr) return runtimeReadyEvent;
    const std::wstring name =
        std::wstring(RuntimeReadyEventPrefix) +
        std::to_wstring(GetCurrentProcessId());
    runtimeReadyEvent = OpenEventW(SYNCHRONIZE, FALSE, name.c_str());
    return runtimeReadyEvent;
}

bool RuntimeHandoffComplete() noexcept {
    const HANDLE event = ResolveRuntimeReadyEvent();
    return event != nullptr && WaitForSingleObject(event, 0) == WAIT_OBJECT_0;
}

void SampleGameState() noexcept {
    std::uint8_t bits = 0;
    bool value = false;
    if (TryInvokeBoolNative(
            GetIsLoadingScreenActiveHash, false, 0, value)) {
        bits |= reactorv::scriptprobe::LoadingAvailableBit;
        if (value) bits |= reactorv::scriptprobe::LoadingBit;
    }
    if (TryInvokeBoolNative(IsPlayerPlayingHash, true, 0, value)) {
        bits |= reactorv::scriptprobe::PlayerPlayingAvailableBit;
        if (value) bits |= reactorv::scriptprobe::PlayerPlayingBit;
    }
    if (TryInvokeBoolNative(
            IsFrontendReadyForControlHash, false, 0, value)) {
        bits |= reactorv::scriptprobe::FrontendReadyAvailableBit;
        if (value) bits |= reactorv::scriptprobe::FrontendReadyBit;
    }
    if (TryInvokeBoolNative(LandingMenuIsActiveHash, false, 0, value)) {
        bits |= reactorv::scriptprobe::LandingMenuAvailableBit;
        if (value) bits |= reactorv::scriptprobe::LandingMenuActiveBit;
    }

    reactorv::scriptprobe::PublishSnapshot(
        bits,
        bits == 0
            ? reactorv::scriptprobe::SnapshotStatus::NativeFailure
            : reactorv::scriptprobe::SnapshotStatus::Ready,
        GetTickCount64());
}

void ScriptProbeMain() noexcept {
    // A ScriptHook script callback is a fiber entry point. It must never
    // return, including after Reactor transfers F9 ownership to the managed
    // runtime. Parking avoids corrupting ScriptHook's owning execution thread.
    for (;;) {
        if (RuntimeHandoffComplete()) {
            reactorv::scriptprobe::PublishSnapshot(
                0,
                reactorv::scriptprobe::SnapshotStatus::ParkedAfterRuntimeHandoff,
                GetTickCount64());
            scriptWait(ParkedSampleDelayMilliseconds);
            continue;
        }
        SampleGameState();
        scriptWait(ActiveSampleDelayMilliseconds);
    }
}

} // namespace

extern "C" __declspec(dllexport) int __stdcall
ReactorVScriptProbeReadSnapshot(
    reactorv::scriptprobe::Snapshot* snapshot) noexcept {
    if (snapshot == nullptr) return FALSE;
    return reactorv::scriptprobe::ReadSnapshot(*snapshot) ? TRUE : FALSE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        registered.store(true, std::memory_order_release);
        scriptRegister(instance, ScriptProbeMain);
    } else if (reason == DLL_PROCESS_DETACH) {
        if (registered.exchange(false, std::memory_order_acq_rel)) {
            scriptUnregister(instance);
        }
        if (runtimeReadyEvent != nullptr) {
            CloseHandle(runtimeReadyEvent);
            runtimeReadyEvent = nullptr;
        }
    }
    return TRUE;
}
