# Enhanced render hook

This document records the boundary for Reactor V's GTA V Enhanced D3D12
in-frame renderer. It distinguishes evidence already produced by the repository
from work that still requires the new root hook and a live Enhanced run.

## Why Enhanced needs an in-frame hook

The bootstrap WebView2 host is an external HWND/DirectComposition visual. It can
prepare React and show a useful fallback in windowed presentation, but DWM
composition is a different frame boundary from GTA's D3D12 swap chain. The
external surface therefore cannot be the exclusive-fullscreen contract.

`ReactorV.RenderHook.asi` is the experimental source/build native owner for that
contract and is explicitly excluded from player ZIPs pending live acceptance. It is
separate from the lifecycle-only `ReactorV.Bootstrap.asi`, has no ScriptHook or
CLR dependency, and is expected to start from a loader-safe worker after GTA
loads it as a root plug-in. It will consume browser frames prepared out of
process and composite them immediately before the game's real present call.

## D3D12 ownership rule

The queue paired with an Enhanced swap chain must be captured at swap-chain
creation. For D3D12, the `pDevice` argument passed to
`IDXGIFactory::CreateSwapChain` and
`IDXGIFactory2::CreateSwapChainForHwnd` is the direct command queue. Reactor
records that queue only after validating its type, then associates it with the
successfully returned swap chain and target GTA HWND.

This rule replaces "most recently observed direct queue" as the authority.
`ExecuteCommandLists` observation may help diagnose a missed early creation
call, but it must never become render authority for a swap chain.

The Enhanced callback set is:

- `IDXGIFactory::CreateSwapChain`
- `IDXGIFactory2::CreateSwapChainForHwnd`
- `IDXGISwapChain::Present`
- `IDXGISwapChain1::Present1`
- `IDXGISwapChain::ResizeBuffers`
- `IDXGISwapChain3::ResizeBuffers1`

All callbacks are bounded, preserve the original call, and reject swap chains
that do not match the registered GTA window and D3D12 device/queue pair.

## Frame path

```text
CEF OSR host (outside GTA)
  -> complete premultiplied-BGRA frame + generation
  -> bounded latest-frame transport
  -> ReactorV.RenderHook.asi / RageWebUI.Native.dll
  -> D3D11On12 wrapped current back buffer
  -> release + flush
  -> original Present/Present1
```

The transport publishes only complete generations. A slow browser overwrites
an unconsumed older frame rather than blocking GTA or building a queue. The
render callback never waits for the host and reuses the last uploaded texture
when the page has not changed.

## Tested boundary

The following is already exercised outside GTA by Reactor's current harness:

- A real D3D12 device, direct queue, flip-model swap chain, and current back
  buffer can be created.
- The existing compositor can use D3D11On12 to draw a CEF-produced transparent
  frame and record submitted/rendered frame statistics.
- The D3D11 backend remains covered by the corresponding Legacy harness.

A separate local Enhanced capture fixture has also observed GTA's real direct
queue through `IDXGIFactory2::CreateSwapChainForHwnd`. That is evidence for the
queue-capture rule, not a Reactor V release test.

The current direct-render harness calls the compositor directly. It does **not**
prove:

- `ReactorV.RenderHook.asi` loads in GTA V Enhanced.
- Factory, `Present1`, or `ResizeBuffers1` detours execute correctly.
- An external frame crosses the staged transport without tearing.
- The overlay survives Enhanced fullscreen, Alt+Tab, display-mode changes,
  device removal, or swap-chain recreation.
- Input ownership and browser/provider handoff work through the in-frame path.
- Hook teardown is safe while a Present callback is in flight.

Those items remain blocked until the injected-hook harness and a live Enhanced
Story Mode acceptance run pass. Documentation and version metadata must not
describe the root hook as shipped before those gates pass.

## Staged validation sequence

1. Build the x64 root hook and make unsupported executable/API gates fail open.
2. Run an injected D3D12 harness that installs the real hooks before creating
   the factory, queue, and swap chain.
3. Prove exact factory queue association, both present methods, both resize
   methods, deterministic pixel output, generation drops, and clean shutdown.
4. Connect the out-of-process CEF producer through the bounded frame transport
   and prove that a delayed or terminated producer cannot stall Present.
5. Install on a clean Enhanced Story Mode fixture and test borderless and
   exclusive fullscreen, F9/Escape, mouse input, Alt+Tab, resolution changes,
   device recovery, close/reopen, and process shutdown.
6. Only then enable the Enhanced in-frame route by default. Any failed gate
   leaves GTA running and Reactor on a non-interactive fallback or unavailable
   state with a precise diagnostic.

## Primary Windows references

- Microsoft documents that D3D12 swap-chain creation receives a direct command
  queue: [IDXGIFactory2::CreateSwapChainForHwnd](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforhwnd)
  and [IDXGIFactory::CreateSwapChain](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgifactory-createswapchain).
- [Direct3D 11 on 12](https://learn.microsoft.com/en-us/windows/win32/direct3d12/direct3d-11-on-12)
  describes creating the interop device with the same D3D12 queue, wrapping
  back buffers, acquiring/releasing them, flushing, and presenting.
- [ID3D11On12Device::CreateWrappedResource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11on12/nf-d3d11on12-id3d11on12device-createwrappedresource)
  defines the input/output state contract for wrapped D3D12 resources.
- [IDXGISwapChain1::Present1](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgiswapchain1-present1)
  is a separate presentation boundary that an Enhanced hook must cover.
- [IDXGISwapChain3](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nn-dxgi1_4-idxgiswapchain3)
  includes `ResizeBuffers1`, the D3D12-aware resize path.
- WebView2's
  [composition-controller API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.createcorewebview2compositioncontrollerasync)
  and the [DirectComposition architecture](https://learn.microsoft.com/en-us/windows/win32/directcomp/architecture-and-components)
  explain why the external bootstrap visual belongs to the desktop composition
  path rather than GTA's swap-chain callback.
