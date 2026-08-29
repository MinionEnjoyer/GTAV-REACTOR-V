# Architecture

RageWebUI separates web rendering, compositing, and game execution so each can
stay on its required thread:

```text
React page (CEF off-screen render thread)
  │ CefSharp.PostMessage / transparent premultiplied BGRA frames
  ├──────────────────────────────┐
  ▼                              ▼
BridgeProtocol              FrameMailbox (latest frame wins)
  │ bounded request queue         │
  ▼                               ▼
Script.Tick                 DXGI Present hook
  │                         ├─ D3D11: draw directly
  ▼                         └─ D3D12: D3D11On12 wrapped back buffer
GameApiRouter → GTA natives        │
  │                                ▼
  └─ response/event → CEF      GTA swap chain
```

## Early native bootstrap

`ReactorV.Bootstrap.asi` is a separate root-level native plug-in whose job ends
at the managed-runtime handoff. `DllMain` performs only loader-safe setup and
dispatches a worker thread. That worker may show the small click-through startup
status surface and start the packaged cache warmer. It never initializes CLR,
CEF, WebView2, COM, GTA natives, MinHook, or a DirectX compositor.

The bootstrap and compositor deliberately have different ownership. The
bootstrap does not hook `Present`; only `plugins/ReactorV/RageWebUI.Native.dll`
may own the later D3D11/D3D12 overlay hook. Once the managed runtime reports
that Story Mode and the browser are ready, the native status surface exits.

## DirectX renderer

The native `RageWebUI.Native.dll` hooks `IDXGISwapChain::Present` and
`ResizeBuffers` with MinHook. It accepts CEF's transparent, premultiplied BGRA
paint frames through a narrow C ABI and uploads only the newest available
frame. A full-screen triangle composites the UI with premultiplied-alpha
blending while preserving the game's D3D11 pipeline state.

For D3D11 swap chains, the compositor draws with the game's immediate context.
For D3D12, the hook also observes `ID3D12CommandQueue::ExecuteCommandLists` to
identify the direct queue, then wraps each back buffer with D3D11On12. That lets
both APIs share the same shaders, texture upload, blend behavior, and resize
path.

Rendering in the game's swap chain is what allows the UI to survive exclusive
fullscreen. The ScriptHook adapter uses GTA's normalized cursor controls while
the menu is open, so input continues to work when Win32 mouse messages are not
available in fullscreen.

The standalone harness uses the same exported compositor ABI. Its native test
surface creates a real D3D11 or D3D12 swap chain, while the managed harness
loads the production React bundle and a fake GTA API router. It is not a mock of
the renderer; only the game API is mocked.

## Browser hosts

CEF off-screen rendering is used for DirectX because it exposes final page paint
buffers suitable for texture upload. CEF runs without its own visible window,
and maps only `https://ragewebui.local/` to the packaged `ui` directory.

The original WebView2-owned transparent desktop window remains available as the
`windowed` renderer. `auto` prefers DirectX and falls back to WebView2 if native
hook initialization fails. Both browser hosts implement the same `IOverlayHost`
contract and feed the same `BridgeBroker`, so the React SDK and game API are
renderer-independent.

## Native ABI

The ABI in `native/include/RageWebUI.Native.h` deliberately contains only C
types. It covers initialization, visibility, BGRA frame submission, input event
polling, render statistics, and test-surface lifecycle. Native ownership never
crosses the managed boundary.

## Trust boundary

- Only `https://ragewebui.local/` may navigate in either embedded browser.
- New windows and external navigation are blocked.
- The virtual host maps only to the packaged `ui` directory.
- There is no generic `native.call` endpoint. The API is an explicit allowlist.
- IDs, methods, message sizes, queue depth, values, coordinates, strings, model
  names, time, weather, and wanted levels are validated.
- GTA objects and natives execute only during `Script.Tick`, never on a browser
  or DirectX hook thread.
- The Present hook only uploads frame data and draws; it never executes game API
  requests or waits for Chromium.

This boundary is intended for local Story Mode mods. It is not an anti-cheat
boundary and must not be used in GTA Online.
