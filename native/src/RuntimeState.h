#pragma once

#include "DirectXCompositor.h"
#include "FrameMailbox.h"
#include "InputQueue.h"

#include <atomic>

namespace rwui {

extern FrameMailbox g_frameMailbox;
extern DirectXCompositor g_compositor;
extern InputQueue g_inputQueue;
extern std::atomic_bool g_visible;

bool InstallHooks(HWND targetWindow);
void RemoveHooks();
} // namespace rwui
