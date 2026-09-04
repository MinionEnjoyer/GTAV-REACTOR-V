#pragma once

#include <cstdint>
#include "../include/RageWebUI.Native.h"

namespace reactorv::renderhook {

// RageWebUI.Native owns the renderer implementation. The root-level ASI uses
// these deliberately small, edition-specific ABIs to arm DXGI interception
// before an external browser producer has a GTA HWND. Neither route calls
// RWUI_Initialize: presentation stays compositor-only and input remains owned
// by the external producer/host.
inline constexpr char EnhancedEarlyArmExportName[] =
    "RWUI_ArmEnhancedHook";
inline constexpr char BindEnhancedTargetExportName[] =
    "RWUI_BindEnhancedTarget";
inline constexpr char EnhancedDiagnosticsExportName[] =
    "RWUI_GetEnhancedHookDiagnostics";
inline constexpr char LegacyEarlyArmExportName[] = "RWUI_ArmLegacyHook";
inline constexpr char BindLegacyTargetExportName[] =
    "RWUI_BindLegacyTarget";
inline constexpr char LegacyDiagnosticsExportName[] =
    "RWUI_GetLegacyHookDiagnostics";
inline constexpr char SharedTextureConsumerDiagnosticsExportName[] =
    "RWUI_GetSharedTextureConsumerDiagnostics";

// Compatibility name retained for the Enhanced-only packaging/tests that
// predate the Legacy compositor route.
inline constexpr auto& EarlyArmExportName = EnhancedEarlyArmExportName;

using EarlyArmFunction = std::int32_t(__cdecl*)();
using BindEnhancedTargetFunction = std::int32_t(__cdecl*)(void*);
using EnhancedDiagnosticsFunction = std::int32_t(__cdecl*)(
    RwuiEnhancedHookDiagnostics*);
using BindLegacyTargetFunction = std::int32_t(__cdecl*)(void*);
using LegacyDiagnosticsFunction = std::int32_t(__cdecl*)(
    RwuiLegacyHookDiagnostics*);
using SharedTextureConsumerDiagnosticsFunction = std::int32_t(__cdecl*)(
    RwuiSharedTextureConsumerDiagnostics*);

} // namespace reactorv::renderhook
