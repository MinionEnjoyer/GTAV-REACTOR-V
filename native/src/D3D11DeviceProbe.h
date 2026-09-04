#pragma once
#include "RageWebUI.Native.h"
#include <d3d11.h>
#include <dxgi.h>

namespace rwui {
// Allocation-only, bounded qualification. Never called from Present. It does
// not submit GPU work, retain game resources, or change the game's device.
RwuiD3D11DeviceDiagnostics ProbeD3D11Device(ID3D11Device* device, bool allocate = true) noexcept;
void DescribeD3D11SwapChain(
    IDXGISwapChain* swapChain, RwuiD3D11DeviceDiagnostics& result) noexcept;
}
