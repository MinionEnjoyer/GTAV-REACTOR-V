# App-local DXGI compatibility — 0.2.3

## Problem and change

Early loading of Reactor's native graphics dependencies could select Windows'
DXGI without initializing an installed game-root ReShade proxy. Disabling the
Reactor hook restored ReShade in a local test but also removed native adapter
discovery/rendering, leaving a delayed or invisible desktop fallback.

The render-hook worker now prepares the exact app-local `dxgi.dll` before native
loading. The edition/Story policy still runs first, and loading remains outside
DllMain. No DLL search directories, environment variables or global loader
policies are changed.

- No app-local proxy: the original native-loading path is unchanged.
- Already loaded exact proxy: retain its module and verify its path/exports.
- Recognized installed ReShade64: inspect its version resources with the file
  held against write/replacement, load the exact file with system-only dependency
  search, verify the loaded path and factory exports, retain the reference.
- Unknown/unreadable/invalid proxy or missing exports: the early native renderer
  stays inactive with a specific log reason. Reactor does not remove the proxy
  or bypass it by silently continuing with system DXGI.

Version metadata identifies compatibility, not trust/authenticity. The feature
does not download, configure or bundle ReShade. Module references are retained
even on later native failure because third-party hooks may already be published.
The initial log precedes proxy execution; there is no unsafe forced cancellation
of third-party DLL initialization.

`ReactorV.RenderHook.log` records `dxgi_compatibility_start` and
`dxgi_compatibility` with decision, Win32 error, selected/loaded path, version,
and first DXGI module before/after. Paths in these logs can be personal; review
before sharing. The ASI also now carries the release file/product version.

## Tests and live evidence

The loader regression uses 16 isolated Windows processes, including a failing
old-sequence negative control, new/preloaded/system-first cases, malformed,
unknown, wrong-architecture and locked files, failed initialization, missing
exports, path isolation and retained references. It calls genuine static DXGI
imports. Synthetic proxy fixtures are never included in runtime packages.

The optional `tools/RenderHookIsolation/Test-DxgiCompatibility.ps1` stages a copy
of an explicitly selected real proxy and runs loader, Enhanced D3D12 external
GPU-frame and Legacy flip-render tests. It requires a fresh output directory,
records input hashes/logs/exits, and puts the authenticated test producer beside
the native DLL under `plugins/ReactorV`. It never changes a game installation.

The single-hook candidate passed a local GTA Enhanced 1.0.1158.13 session:

- ReShade 6.7.3 (CoreFX variant), RenoDX and 71 custom shaders initialized.
- Native rendering continued: over 17,000 logged render events, with no recorded
  receive/import/copy/ACK failures or producer-image rejects.
- Two GBay presentations became ready in roughly 0.3 seconds each.
- Game and host exited with code 0; the user reported the test working.

That test retained an existing diagnostic baseline for the other runtime files;
it is evidence for this focused hook change, not live validation of every binary
in the rebuilt release package. Final release packages undergo the normal
offline package gates. Live Legacy, other PCs, every UI/input edge case, and the
original issue reporter's crash are not established by this test.

One RenoDX shader-replacement error and other injector warnings were retained in
the local review despite the successful session. This patch does not claim to
resolve unrelated shader or graphics-mod errors. First-run splash settings and
consumer content are not changed. No diagnostic logs/dumps, personal paths,
consumer assets, ReShade DLLs or CoreFX/RenoDX files are shipped.

References: [Microsoft LoadLibraryExW](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-loadlibraryexw),
[Windows DLL search order](https://learn.microsoft.com/en-us/windows/win32/dlls/dynamic-link-library-search-order).
