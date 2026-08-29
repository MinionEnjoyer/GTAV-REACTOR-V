# REACTOR V

**REACTOR** stands for **Real-time Embedded Application Component Toolkit &
Overlay Runtime**. REACTOR V is a React/HTML overlay framework for GTA V Story
Mode. Version 0.2.0 renders the browser into GTA's swap chain, so the same menu
works in windowed, borderless, and fullscreen modes:

- Direct3D 11 for GTA V Legacy.
- Direct3D 12 for GTA V Enhanced.
- A WebView2 desktop-window fallback for systems where the DirectX hook cannot
  initialize.

The included first-run splash is a lightweight example of the framework. Its
centered logo and live dependency checks render directly over gameplay, while
the typed bridge remains available for complete interactive interfaces.
Reactor preloads that interface hidden on its own UI thread, then reveals one
complete frame only after both the browser content and Story Mode are ready.
This behavior is built into Reactor itself, so it is identical for direct GTA
launches and launches started through ALLIN1.

`ReactorV.Bootstrap.asi` starts at the game's native plug-in stage. It owns only
the lightweight startup status surface and cache-warmer launch; it does not
load the CLR, create a browser, call GTA natives, or install graphics hooks.
The managed Reactor runtime takes ownership after Story Mode and the browser
are both ready.

Do not use this mod in GTA Online.

## Requirements

- 64-bit GTA V Legacy or Enhanced, Story Mode.
- A current edition-compatible Script Hook V.
- ScriptHookVDotNet v3. Enhanced installations need a matched build that
  exposes the SHVDN v3 API.
- .NET Framework 4.8.
- Microsoft Edge WebView2 Runtime only when using the `windowed` renderer or
  when `auto` falls back to it. The DirectX renderer's CEF runtime is included.

The DirectX compositor currently supports D3D11 and D3D12. GTA V Enhanced's
Vulkan renderer is not supported; select DirectX 12 in the game's graphics
settings.

## Build an installable ZIP

Building requires Node.js/pnpm, CMake 3.24+, and a Windows x64 C++ compiler.
From this directory:

```powershell
corepack enable
./build-package.ps1
```

The builder runs the native unit test, React tests, .NET tests, and six-second
D3D11 and D3D12 harness smoke tests before creating:

```text
artifacts/ReactorV-0.2.0.zip
artifacts/ReactorV-0.2.0.zip.sha256
```

Use `-SkipTests` for a compile/package-only build, or `-SkipHarness` to retain
unit tests while skipping the interactive graphics smoke tests.

## Install

1. Install Script Hook V and ScriptHookVDotNet for your GTA edition.
2. Extract the release ZIP into the directory containing `GTA5.exe` or
   `GTA5_Enhanced.exe`. The early bootstrap will be at
   `ReactorV.Bootstrap.asi`, and the managed assembly will be at
   `scripts/ReactorV/RageWebUI.Script.dll`.
3. Launch Story Mode in fullscreen, borderless, or windowed mode.
4. Press **F10** to open or close the menu. **Escape** also closes it.

Configuration lives at `scripts/ReactorV/ReactorV.json`:

```json
{
  "toggleKey": "F10",
  "startVisible": false,
  "renderer": "auto",
  "directXFrameRate": 60,
  "enableDevTools": true,
  "telemetryIntervalMilliseconds": 100
}
```

`renderer` accepts `auto`, `directx`, or `windowed`. `auto` tries the in-process
DirectX renderer first and falls back to the desktop WebView2 renderer.
`directXFrameRate` is clamped to 15-60. Set `enableDevTools` to `false` for a
public release.

## Run the DirectX harness

The packaged harness opens the real example React overlay against a fake GTA
API, without launching GTA:

```powershell
cd scripts/ReactorV
./RageWebUI.Harness.exe --api d3d11
./RageWebUI.Harness.exe --api d3d12
```

Add `--smoke` for an automated six-second verification, or use `--width`,
`--height`, `--duration`, and `--ui` to customize the run. See
[DIRECTX-HARNESS.md](docs/DIRECTX-HARNESS.md) for the pass criteria and controls.

## Work on the React UI

```powershell
cd web
pnpm install --frozen-lockfile
pnpm dev
```

Opening the Vite URL in a normal browser activates `DemoTransport`, so every
screen and action can be developed without GTA. In the DirectX renderer the SDK
automatically uses `CefSharp.PostMessage`; in the windowed renderer it uses
`window.chrome.webview`.

The public browser SDK is in `web/src/gta/bridge.ts`. Protocol v2 also exposes
runtime discovery, extension actions, declarative menus, subscriptions,
lifecycle, semantic input, cancellation, confirmation, and idempotency:

```ts
import { bridge, gta } from './gta/bridge'

await gta.vehicle.spawn('sultan')
await gta.player.teleport({ x: -75.3, y: -818.9, z: 326.2 })

const unsubscribe = bridge.on('game.state', (state) => {
  console.log(state.player.health)
})
```

Plain HTML pages can load the production `ui/ragewebui.js` module and use
`window.rageWebUI.gta` without React or a bundler.

See [API.md](docs/API.md) for every browser method and event,
[EXTENSIONS.md](docs/EXTENSIONS.md) for integrating another managed gameplay
project, and
[ARCHITECTURE.md](docs/ARCHITECTURE.md) for rendering, thread ownership, and
security boundaries.

## Project layout

```text
ReactorV/
├─ native/                    early bootstrap, D3D11/D3D12 compositor, and tests
├─ src/ReactorV.Core/         protocol validation and bounded request queue
├─ src/ReactorV.DirectX/      CEF off-screen browser and native interop
├─ src/ReactorV.Runtime/      renderer host and WebView2 fallback
├─ src/ReactorV.Script/       ScriptHook game API and renderer selection
├─ src/ReactorV.Harness/      standalone renderer/React integration harness
├─ examples/                  compiled managed-extension examples
├─ web/                       React example, typed SDK, and browser mock
├─ tests/                     protocol and queue tests
├─ docs/                      API, architecture, and harness guides
└─ build-package.ps1          verified release builder
```
