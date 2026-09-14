# Offline desktop witness validation

Build `src/ReactorV.Preloader/ReactorV.Preloader.csproj` in Release, then run
`Test-DesktopProbe.ps1 -OutputDirectory <fresh-private-output-directory>` using
x64 Windows PowerShell 5.1. The script refuses an existing output directory or
a running GTA/preloader. It never installs into GTA, launches the game, changes
settings, uploads results, or overwrites prior evidence.

The test briefly shows a small owned nonactivating checkerboard. Production
capture reads only its sample strip within a monitor-sized target; no screenshot
is written. Five matching captures and a wrong-identity rejection are required.
Protocol fixtures also cover success, insufficient quorum, malformed JSON,
nonzero exit and a deliberate stall. **The runtime's existing timeout kills only
that disposable owned stalled child.** The test observer itself never kills.

Two same-executable process fixtures exercise real OS PID/path/hash/creation-time
classification: a direct desktop-probe helper must be authenticated and recorded;
an unexpected child role must be rejected. Policy tests with additional identity
and evidence failures live in `../ExternalHostComparison/Test-Comparison.ps1`.

The fixture also exercises forced production DXGI and three simulated GDI stalls
followed by the production DXGI fallback. All three recorded opening-test marker
palettes must match 8/8 in both backends; stale/wrong identities must be rejected.
On HDR outputs the production probe preserves FP16 and normalizes with the
OS-reported SDR white level. The eight observed RGB values, capture format and
white level are recorded, never a full screenshot. Protocol checks reject
malformed/unbounded pixel telemetry. See `docs/HDR-DESKTOP-WITNESS-FIX.md`.

This is not WebView/GBay rendering acceptance, game input testing, exclusive-fullscreen
acceptance, or proof of the original crash/flicker cause.
