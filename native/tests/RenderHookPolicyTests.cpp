#include "RenderHookContract.h"
#include "RenderHookPolicy.h"

#include <cstdlib>
#include <iostream>
#include <string_view>

namespace {

void Require(const bool condition, const char* message) {
    if (!condition) {
        std::cerr << message << '\n';
        std::exit(1);
    }
}

} // namespace

int main() {
    using reactorv::renderhook::NativeModuleDisposition;
    using reactorv::renderhook::RenderHookEdition;

    Require(
        reactorv::renderhook::IsEnhancedGameExecutable(
            L"D:\\Games\\Grand Theft Auto V Enhanced\\GTA5_Enhanced.exe"),
        "The exact Enhanced executable must activate the render hook.");
    Require(
        reactorv::renderhook::IsEnhancedGameExecutable(
            L"D:\\Games\\GTA5_ENHANCED.EXE"),
        "The executable gate must follow Windows case-insensitive semantics.");
    Require(
        !reactorv::renderhook::IsEnhancedGameExecutable(
            L"D:\\Games\\GTA5.exe"),
        "Legacy must not activate the Enhanced-specific gate.");
    Require(
        !reactorv::renderhook::IsEnhancedGameExecutable(
            L"D:\\Games\\GTA5_Enhanced_copy.exe"),
        "A similarly named process must not activate the injected renderer.");
    Require(
        reactorv::renderhook::IsLegacyGameExecutable(
            L"D:\\Games\\Grand Theft Auto V\\GTA5.exe") &&
        reactorv::renderhook::IsLegacyGameExecutable(
            L"D:\\Games\\GTA5.EXE"),
        "The exact Legacy executable must activate case-insensitively.");
    Require(
        !reactorv::renderhook::IsLegacyGameExecutable(
            L"D:\\Games\\GTA5_Enhanced.exe") &&
        !reactorv::renderhook::IsLegacyGameExecutable(
            L"D:\\Games\\GTA5_copy.exe"),
        "The Legacy gate must reject Enhanced and similarly named images.");
    Require(
        reactorv::renderhook::DetectRenderHookEdition(
            L"D:\\Games\\GTA5.exe") == RenderHookEdition::Legacy &&
        reactorv::renderhook::DetectRenderHookEdition(
            L"D:\\Games\\GTA5_Enhanced.exe") ==
            RenderHookEdition::Enhanced &&
        reactorv::renderhook::DetectRenderHookEdition(
            L"D:\\Games\\PlayGTAV.exe") ==
            RenderHookEdition::Unsupported,
        "Edition detection must fail closed to the two exact GTA images.");

    Require(
        reactorv::renderhook::IsStoryModdingPolicySatisfied(
            L"\"D:\\Games\\Grand Theft Auto V Enhanced\\GTA5_Enhanced.exe\" -nobattleye"),
        "The explicit Story/offline switch must satisfy the renderer policy.");
    Require(
        reactorv::renderhook::IsStoryModdingPolicySatisfied(
            L"GTA5_Enhanced.exe \"-NoBattlEye\""),
        "The Story/offline switch must follow Windows case-insensitive and quoted argument semantics.");
    Require(
        !reactorv::renderhook::IsStoryModdingPolicySatisfied(
            L"GTA5_Enhanced.exe"),
        "The renderer must remain inactive when the Story/offline switch is absent.");
    Require(
        !reactorv::renderhook::IsStoryModdingPolicySatisfied(
            L"GTA5_Enhanced.exe -nobattleye-disabled"),
        "A prefix match must not bypass the exact Story/offline policy.");
    Require(
        !reactorv::renderhook::IsStoryModdingPolicySatisfied(
            L"\"C:\\Games\\-nobattleye\""),
        "The executable argument itself must not satisfy the launch policy.");
    Require(
        !reactorv::renderhook::IsStoryModdingPolicySatisfied(
            L"GTA5_Enhanced.exe \"note -nobattleye\""),
        "Text embedded inside another argument must not satisfy the launch policy.");

    const auto paths = reactorv::renderhook::ResolveRenderHookPaths(
        L"C:\\Program Files\\Rockstar Games\\GTA V Enhanced\\GTA5_Enhanced.exe");
    Require(
        paths.gameRoot ==
            L"C:\\Program Files\\Rockstar Games\\GTA V Enhanced",
        "Game root must be resolved from the executable, including spaces.");
    Require(
        paths.nativeModule ==
            L"C:\\Program Files\\Rockstar Games\\GTA V Enhanced\\plugins\\ReactorV\\RageWebUI.Native.dll",
        "The native renderer must be loaded from the managed ReactorV plugin directory.");
    Require(
        paths.diagnosticsFile ==
            L"C:\\Program Files\\Rockstar Games\\GTA V Enhanced\\scripts\\ReactorV\\ReactorV.RenderHook.log",
        "Renderer diagnostics must stay under scripts/ReactorV.");

    Require(
        reactorv::renderhook::ResolveNativeModuleDisposition(false, 1) ==
            NativeModuleDisposition::ReleaseFailOpen,
        "A missing early-arm export must release the native module.");
    Require(
        reactorv::renderhook::ResolveNativeModuleDisposition(true, 0) ==
            NativeModuleDisposition::ReleaseFailOpen,
        "A rejected early arm must unload and leave GTA running.");
    Require(
        reactorv::renderhook::ResolveNativeModuleDisposition(true, -1) ==
            NativeModuleDisposition::ReleaseFailOpen,
        "An explicit native failure must unload and leave GTA running.");
    Require(
        reactorv::renderhook::ResolveNativeModuleDisposition(true, 1) ==
            NativeModuleDisposition::RetainArmed,
        "A successful arm must retain the native module for hook lifetime.");
    Require(
        std::string_view(reactorv::renderhook::EarlyArmExportName) ==
            "RWUI_ArmEnhancedHook",
        "The root loader and native renderer must share one exact export name.");
    Require(
        std::string_view(
            reactorv::renderhook::BindEnhancedTargetExportName) ==
            "RWUI_BindEnhancedTarget" &&
        std::string_view(
            reactorv::renderhook::EnhancedDiagnosticsExportName) ==
            "RWUI_GetEnhancedHookDiagnostics",
        "The root loader must use the compositor-only bind and diagnostics ABI.");
    Require(
        std::string_view(
            reactorv::renderhook::LegacyEarlyArmExportName) ==
            "RWUI_ArmLegacyHook" &&
        std::string_view(
            reactorv::renderhook::BindLegacyTargetExportName) ==
            "RWUI_BindLegacyTarget" &&
        std::string_view(
            reactorv::renderhook::LegacyDiagnosticsExportName) ==
            "RWUI_GetLegacyHookDiagnostics",
        "Legacy must use its own compositor-only D3D11 ABI.");
    Require(
        std::string_view(
            reactorv::renderhook::
                SharedTextureConsumerDiagnosticsExportName) ==
            "RWUI_GetSharedTextureConsumerDiagnostics",
        "The root hook must resolve the typed shared-texture consumer diagnostics ABI.");

    const reactorv::renderhook::RenderTargetWindowCandidate sga{
        77, 77, L"sgaWindow", true, false, false, 2560, 1440};
    Require(
        reactorv::renderhook::IsEligibleEnhancedRenderTarget(sga),
        "Enhanced must accept its exact current-process sgaWindow.");
    auto grc = sga;
    grc.className = L"grcWindow";
    Require(
        reactorv::renderhook::IsEligibleEnhancedRenderTarget(grc),
        "The compatibility target gate must accept exact grcWindow.");
    Require(
        reactorv::renderhook::IsEligibleLegacyRenderTarget(grc),
        "Legacy must accept its exact current-process top-level grcWindow.");
    Require(
        !reactorv::renderhook::IsEligibleLegacyRenderTarget(sga),
        "Legacy must never select Enhanced's sgaWindow.");
    auto socialClub = sga;
    socialClub.className = L"SocialClub";
    Require(
        !reactorv::renderhook::IsEligibleEnhancedRenderTarget(socialClub),
        "A large unrelated launcher/tool window must never be selected.");
    auto wrongProcess = sga;
    wrongProcess.processId = 78;
    Require(
        !reactorv::renderhook::IsEligibleEnhancedRenderTarget(wrongProcess),
        "An exact class in another process must never be selected.");
    wrongProcess.className = L"grcWindow";
    Require(
        !reactorv::renderhook::IsEligibleLegacyRenderTarget(wrongProcess),
        "Legacy must reject grcWindow candidates from another process.");
    auto tool = sga;
    tool.toolWindow = true;
    Require(
        !reactorv::renderhook::IsEligibleEnhancedRenderTarget(tool),
        "Tool windows must never be selected as the GTA render target.");
    tool.className = L"grcWindow";
    Require(
        !reactorv::renderhook::IsEligibleLegacyRenderTarget(tool),
        "Legacy must reject tool windows.");
    auto child = sga;
    child.topLevel = false;
    Require(
        !reactorv::renderhook::IsEligibleEnhancedRenderTarget(child),
        "Child windows must never be selected as the GTA render target.");
    auto emptyClient = sga;
    emptyClient.clientWidth = 0;
    Require(
        !reactorv::renderhook::IsEligibleEnhancedRenderTarget(emptyClient),
        "A zero-area window must remain pending rather than bind.");

    return 0;
}
