#include "InputQueue.h"
namespace { rwui::InputQueue input; }
extern "C" __declspec(dllexport) BOOL ProbeAttach(HWND window) { return input.Attach(window); }
extern "C" __declspec(dllexport) void ProbeDetach() { input.Detach(); }
