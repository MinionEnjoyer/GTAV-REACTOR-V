# Architecture

Reactor V separates startup presentation, browser rendering, compositing, and
game execution. There are two presentation paths, and they do not have the same
fullscreen guarantees.

```text
Early/bootstrap path (implemented)

React page -> external WebView2 + DirectComposition -> desktop/DWM presentation
     |                  |
     |                  +-> About and Story-initializer surfaces
     +-> bounded pipe -> late managed provider -> Script.Tick -> GTA natives

In-frame path (edition-specific 0.2.0 preview packages)

React page -> CEF off-screen BGRA frame -> latest-frame transport
     |                                      |
     +-> bounded bridge -> Script.Tick       +-> ReactorV.RenderHook.asi
                              |                    |- Legacy: D3D11
                              v                    `- Enhanced: D3D12/D3D11On12
                         GTA natives                         |
                                                             v
                                                       GTA swap chain
```

The external bootstrap path is useful before ScriptHookVDotNet is ready and is
retained as a windowed/borderless fallback. It is still a separate desktop
surface composed by DWM; it is not evidence that pixels entered GTA's swap
chain, and it is not a reliable exclusive-fullscreen backend. The in-frame path
is the backend intended to provide that guarantee.

## Early native bootstrap

`ReactorV.Bootstrap.asi` is a separate root-level native plug-in whose job ends
at the managed-runtime handoff. `DllMain` performs only loader-safe setup and
dispatches a worker thread. That worker may show the small click-through startup
status surface and start the packaged cache warmer. It never initializes CLR,
CEF, WebView2, COM, GTA natives, MinHook, or a DirectX compositor.

The external persistent host owns one WebView2 composition controller and the
typed bootstrap/provider pipe. Browser readiness, exact browser-paint identity,
and a DirectComposition commit prove that the browser produced the requested
surface. They do not prove that an independent/exclusive-flip GTA frame contains
that external HWND. Failed desktop observation must therefore remain diagnostic
and non-interactive; it must not start a recreate/probe loop on GTA's hot path.

`ReactorV.ScriptProbe.asi` is a separate root-level companion statically linked
against the externally supplied official ScriptHookV import library. That
dependency causes Windows to initialize ScriptHook before the companion's
`DllMain`, where it registers one bounded-cadence script fiber. Only that fiber
invokes the four read-only game-state natives. It publishes a fixed-width,
versioned atomic snapshot through `ReactorVScriptProbeReadSnapshot`; Bootstrap
discovers the export without loading the module and never invokes GTA natives.
After the managed runtime handoff, the fiber parks instead of returning, as
required by ScriptHook's fiber ownership model.

## In-frame renderer ownership

`plugins/ReactorV/RageWebUI.Native.dll` contains the existing compositor, C ABI,
frame mailbox, and input queue. The managed DirectX host can load that DLL and
submit CEF's transparent premultiplied-BGRA frames. Its full-screen triangle
uses premultiplied-alpha blending and preserves the D3D11 state it changes.

The root-level `ReactorV.RenderHook.asi` is a separate version-gated owner.
Both edition preview ZIPs include it following local fullscreen acceptance;
the generic external-host/developer package still excludes it. The hook loads
the native compositor before the managed ScriptHookVDotNet provider is available
and consumes frames from an out-of-process browser host. Keeping it separate from
`ReactorV.Bootstrap.asi` means a graphics-hook failure cannot disable F9
lifecycle routing or prevent GTA from starting.

The Legacy preview additionally requires `ReactorV.LegacyCpuFrames.enabled`
beside `ReactorV.LegacyLiveTest.json`. Its private worker reads browser frames
into an authenticated, bounded CPU mapping; the game uploads complete frames
to reusable local D3D11 textures. Readback does not run in Present. UI producer
admission is capped at 15 fps, transient timeout recovery is bounded, and stale
frame acknowledgements cannot acquire input. Enhanced retains its separate GPU
transport; the Legacy marker is rejected by Enhanced package validation.

Legacy's click-through corner status has a separate small cached texture. It
does not take menu input and is hidden behind an active full menu. Neither this
status texture nor a transport ACK alone proves a menu presentation is ready:
surface identity and the current presentation acknowledgement must also match.

The hook must remain fail-open for the game:

- Unsupported executable, Vulkan, unavailable DXGI/D3D objects, hook conflict,
  missing frame host, or renderer initialization failure disables Reactor's
  in-frame presentation for that session.
- Every hooked function still invokes the original function exactly once.
- Present and resize callbacks do not wait for Chromium, pipe I/O, disk I/O, or
  GTA natives. With no complete newer frame, the compositor simply skips.
- Hook teardown stops new work before releasing swap-chain resources and never
  unloads code while a callback is in flight.

This rendering fail-open policy is distinct from Reactor's API policy. Unknown
browser methods, untrusted paths, stale presentation IDs, and unauthorized
mutations continue to fail closed.

## Edition-specific graphics backends

### GTA V Legacy: Direct3D 11

Legacy's target backend is D3D11. The compositor retrieves the game swap
chain's D3D11 device and immediate context, uploads only the newest complete
browser frame, draws into the current back buffer, and restores the pipeline
state it touched. Legacy keeps its own independently version-gated startup
lifecycle discovery; that discovery is not reused as an Enhanced memory
signature.

### GTA V Enhanced: Direct3D 12

Enhanced's target backend is D3D12. For D3D12, DXGI receives an
`ID3D12CommandQueue`, not an `ID3D12Device`, when the application creates the
swap chain. Reactor must capture that exact direct queue from
`IDXGIFactory::CreateSwapChain` or
`IDXGIFactory2::CreateSwapChainForHwnd` and bind it to the returned swap-chain
identity. Observing whichever direct queue most recently called
`ExecuteCommandLists` is not a sufficient ownership rule on a multi-queue
renderer; it may be recorded as diagnostic evidence, but it is never accepted
as authority to render into a swap chain.

The compositor creates a D3D11On12 device over the Enhanced D3D12 device and
the exact queue, wraps each swap-chain back buffer, acquires the current wrapped
resource, draws the overlay, releases it to the declared output state, flushes,
and then allows the original present call to continue. Both `Present` and
`Present1`, and both `ResizeBuffers` and `ResizeBuffers1`, belong to the hook
contract. Resize invalidates every view and wrapped-back-buffer reference before
the original resize operation runs.

Enhanced Vulkan is not supported. The Enhanced hook must report that backend
as unavailable and leave GTA untouched rather than attempting a D3D12 fallback.

See [Enhanced render hook](ENHANCED-RENDER-HOOK.md) for the implementation and
test boundary.

## Browser hosts and frame transport

CEF off-screen rendering is the browser source for the in-frame backend because
it exposes final premultiplied-BGRA paint buffers suitable for texture upload.
CEF runs in an ordinary/default application domain outside GTA's
ScriptHookVDotNet secondary application domain. A process-scoped,
latest-frame-wins transport moves only complete, generation-stamped frames to
the native hook; the Present callback never performs browser work.

WebView2 remains the external bootstrap and fallback host. It uses visual
hosting through a DirectComposition root attached to an HWND and feeds the same
typed bridge, but its pixels are presented by the desktop compositor rather
than GTA. The two browser hosts may share UI assets and protocol contracts;
they must not simultaneously claim presentation or input ownership.

## Harness boundary

The standalone D3D11/D3D12 surface harness proves browser paint, texture upload,
alpha blending, and compositor output against real test swap chains. Its legacy
direct-render mode does not prove that MinHook intercepted a game's factory,
present, or resize calls.

The staged injected-hook harness therefore installs the production hook before
creating its test factory/queue/swap chain. For Enhanced it must prove exact
factory-queue association, `Present` and `Present1`, `ResizeBuffers1`, frame
generation/drop behavior, and callback-safe shutdown. Passing either harness is
not a substitute for an in-game Legacy/Enhanced Story Mode acceptance run.

## Native ABI

The ABI in `native/include/RageWebUI.Native.h` deliberately contains only C
types. It covers initialization, visibility, frame publication, input event
polling, render statistics, and test-surface lifecycle. Native ownership never
crosses the managed boundary, and process-local pointers are never serialized
to the external browser host.

## Trust boundary

- Only `https://ragewebui.local/` may navigate in either embedded browser.
- New windows and external navigation are blocked.
- The virtual host maps only to the packaged `ui` directory.
- There is no generic `native.call` endpoint. The API is an explicit allowlist.
- IDs, methods, message sizes, queue depth, values, coordinates, strings, model
  names, time, weather, and wanted levels are validated.
- GTA objects and natives execute only during `Script.Tick`, never on a browser
  or graphics-hook thread.
- The render hook only consumes a validated complete frame and draws; it never
  executes game API requests or waits for the browser.

This boundary is intended for local Story Mode mods. It is not an anti-cheat
boundary and must not be used in GTA Online.
