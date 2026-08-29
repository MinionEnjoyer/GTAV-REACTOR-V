#pragma once

#include <cstdint>

#ifdef _WIN32
#define RWUI_CALL __cdecl
#ifdef RWUI_NATIVE_EXPORTS
#define RWUI_API extern "C" __declspec(dllexport)
#else
#define RWUI_API extern "C" __declspec(dllimport)
#endif
#else
#define RWUI_CALL
#define RWUI_API extern "C"
#endif

enum class RwuiRenderApi : std::int32_t {
    None = 0,
    Direct3D11 = 11,
    Direct3D12 = 12,
};

enum class RwuiInputType : std::int32_t {
    None = 0,
    MouseMove = 1,
    MouseDown = 2,
    MouseUp = 3,
    MouseWheel = 4,
    KeyDown = 5,
    KeyUp = 6,
    Character = 7,
    Resize = 8,
};

struct RwuiInputEvent {
    RwuiInputType type;
    std::int32_t x;
    std::int32_t y;
    std::int32_t delta;
    std::int32_t key;
    std::uint32_t modifiers;
    std::uint64_t timestamp;
};

struct RwuiRenderStats {
    RwuiRenderApi api;
    std::int32_t width;
    std::int32_t height;
    std::uint64_t submittedFrames;
    std::uint64_t renderedFrames;
    std::uint64_t droppedFrames;
    std::uint64_t lastFrameGeneration;
};

RWUI_API std::int32_t RWUI_CALL RWUI_Initialize(void* targetWindow);
RWUI_API void RWUI_CALL RWUI_Shutdown();
RWUI_API void RWUI_CALL RWUI_SetVisible(std::int32_t visible);
RWUI_API std::int32_t RWUI_CALL RWUI_SubmitFrame(
    const void* bgraPixels,
    std::int32_t width,
    std::int32_t height,
    std::int32_t stride,
    std::uint64_t generation);
RWUI_API std::int32_t RWUI_CALL RWUI_PollInput(RwuiInputEvent* inputEvent);
RWUI_API std::int32_t RWUI_CALL RWUI_GetStats(RwuiRenderStats* stats);

// Standalone harness entry points. These create a real swap chain and use the
// same compositor classes as the injected hook, without loading GTA.
RWUI_API std::int32_t RWUI_CALL RWUI_TestStart(
    RwuiRenderApi api,
    std::int32_t width,
    std::int32_t height,
    const wchar_t* title);
RWUI_API void RWUI_CALL RWUI_TestStop();
RWUI_API std::int32_t RWUI_CALL RWUI_TestIsRunning();

