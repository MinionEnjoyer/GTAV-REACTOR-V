# DirectX harness

`RageWebUI.Harness.exe` validates the complete browser-to-swap-chain path
without GTA. It creates a native test window and real swap chain, loads the
production React bundle through CEF, routes JavaScript calls to a fake GTA API,
and reports compositor statistics once per second.

## Source-build staging

The harness is a release gate, not a player runtime. `build-package.ps1` stages
it temporarily, runs it against the exact candidate runtime/UI files, and
removes it before creating the ZIP. After a local Release build:

```powershell
cd src/ReactorV.Harness/bin/Release
./RageWebUI.Harness.exe --api d3d11
./RageWebUI.Harness.exe --api d3d12
```

The example overlay is interactive: use the mouse to open Player, Vehicle, and
World pages and invoke their fake actions. Close the native window to stop.

For an automated run:

```powershell
./RageWebUI.Harness.exe --api d3d11 --smoke
./RageWebUI.Harness.exe --api d3d12 --smoke
```

A smoke run passes only when it detects the requested graphics API, receives at
least one CEF frame, renders at least one Present frame, and handles the React
app's initial API requests. It exits with code 0 on success.

## Options

| Option | Meaning |
|---|---|
| `--api d3d11\|d3d12` | Select the test swap-chain API. Default: `d3d11`. |
| `--width N` | Set client width. Default: `1280`. |
| `--height N` | Set client height. Default: `720`. |
| `--duration N` | Exit after N seconds. |
| `--smoke` | Run for six seconds and evaluate pass criteria. |
| `--ui PATH` | Load a different built web directory. |

## Development build

`build-package.ps1` compiles and stages everything needed by the harness before
running both smoke tests, but never ships `RageWebUI.Harness.exe` in the public
player ZIP. To launch the already-built executable directly, make
sure `RageWebUI.Native.dll` and an `ui` directory containing `index.html` sit
beside `src/ReactorV.Harness/bin/Release/RageWebUI.Harness.exe`.

The harness proves CEF paint, input forwarding, bridge traffic, texture upload,
resize handling, and D3D11/D3D12 presentation. It does not prove compatibility
with a particular GTA or ScriptHook build, so an in-game Story Mode pass remains
part of release qualification.
