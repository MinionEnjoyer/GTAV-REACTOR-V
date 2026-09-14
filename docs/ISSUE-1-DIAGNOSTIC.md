# Issue 1: DPI correction and startup diagnostics

Diagnostic candidate `0.2.2-issue1-diagnostic-v1`, based on main
`64b98166e782d0393f24561daf3b603333522e94`. Not a published release and not a
confirmed fix for the reporter's execute access violation.

## Capture correction

The published 0.2.2 composition host with WebView2 152.0.4191.66 produces
3441x1440 capture pixels for raw 3440x1440 bounds at 150% rasterization.
At 175% it produces 3441x1441. Odd raw dimensions also round at integer
scales (1919x1079 becomes 1920x1080 at 200%).

All three identity-gated capture boundaries now accept exact dimensions or the
specific expansion `ceil(ceil(raw / scale) * scale)`. Non-exact acceptance is
limited to scale 1 through 4 and at most three additional physical pixels per
edge. Undersized, unrelated, unknown-scale, or scale-changed captures fail.
Scale values are logged invariantly, including on German Windows.

The lease, controller generation, paint identity, concrete pixels, foreground,
and presentation checks remain in place. Bootstrap proof is tied to the raw
target size and captured scale; a scale change invalidates its reuse. Desktop
witness sample coordinates use the raw target bounds rather than stretching
the extra capture edge. This change is not permission to skip in-game proof.

## New diagnostics

Managed `reactorv-runtime.log` and its existing per-process session log contain:

- `construction_begin` now includes the script assembly MVID.
- `diagnostic_first_tick_enter` and `diagnostic_first_tick_exit` bracket the
  first completed script tick. Exit is deliberately not emitted in a finally.
- `diagnostic_readiness_boundary` precedes each loading/player/native check
  for the first 16 polls, then at most one detailed poll every five seconds.
  `exit-playable` / `exit-not-playable` mean the readiness check returned.
- `diagnostic_tick_heartbeat` records cached readiness/handoff state every
  five seconds on a completed tick, without extra GTA calls for formatting.

Native `scripts/ReactorV/ReactorV.NativeLifecycle.log` contains:

- `diagnostic_present[_1]_*` (actual Present1 spelling: `diagnostic_present1_*`):
  enter, forward into the original DXGI function, and return HRESULT. Present
  flags are in `detail`. The first eight outermost calls are sampled, then at
  most one every five seconds. Device-failure results are always enqueued.
- `diagnostic_presentation_state`: mode bit 0 visible, bit 1 local owner;
  `detail` is the external presentation epoch. `diagnostic_transition_draw`
  reports whether the transition's attempted draw succeeded.
- `diagnostic_resize[1]_*`, `diagnostic_backbuffers_retire_*`, and
  `diagnostic_resize_epoch_complete`: resize/retirement boundaries and results.
  Resize-enter detail packs width in high 32 bits and height in low 32 bits;
  retirement/epoch records use the preparation epoch instead.
- `diagnostic_prepare_enter` / `diagnostic_prepare_ready`: sampled preparation
  start and successful readiness; ready result is render API 11 or 12. A failed
  preparation does not emit ready and is not by itself evidence of a crash.
- `diagnostic_d3d12_interop_generation` and
  `diagnostic_d3d12_backbuffer_generation` identify the prepared resources.
- `diagnostic_gpu_copy_enter` / `diagnostic_gpu_copy_submitted`: sampled texture
  submission with generation in detail, plus device-removal HRESULT. A removed
  device's frame is not published as a successful transfer. Submission with
  HRESULT 0 is not proof of GPU completion or visible rendering.
- `diagnostic_worker_heartbeat` and `diagnostic_device_status`: five-second
  worker health and device-removal snapshots (device query skipped if busy or
  not yet initialized).

Callback records only enter a 128-record fixed buffer. Contention/full capacity
drops records and increments `diagnostic_records_dropped`. The preparation
worker drains it outside rendering/resource locks using the existing bounded
1 MiB log plus one rotated file. Each record includes sequence, original
`callback_tick_ms`, `callback_tid`, result, and pointer identity. The outer
UTC timestamp and thread are the later writer, not necessarily the callback.
No pointer from a diagnostic event is dereferenced by the writer.

There is no new crash handler, exception suppression, recovery-after-AV logic,
driver setting change, or debug-layer injection. The final buffered records can
be lost when the process crashes; missing lines do not prove a callback never
ran. Use the matching dump and process/session identity, not just the last line.

## Local validation and candidate use

`tools/DpiCaptureProbe` exercises the real composition host without GTA.
The managed tests cover the bounded rasterization rule, stale/invalid scale,
and wiring of identity and diagnostic guards. Native tests cover bounded
buffer behavior, concurrent coherence, drop accounting, rate limits, and
worker-only file writes, alongside the existing GPU/resize/lifetime suites.

The candidate uses optimized native `RelWithDebInfo` and managed Release with
portable symbols. Preserve the exact PDBs and binary SHA-256 manifest locally.
Do not label it the fully qualified release or replace an existing release ZIP.
No full package/harness qualification or actual GTA acceptance is implied.

The staged patch requires an existing 0.2.2 Enhanced installation. It replaces
only Runtime, Preloader, native renderer, and managed Script binaries, keeping
CEF, ScriptHook, UI, configuration, and other mods untouched. When authorized
to install, stop GTA and its preloader first, back up those exact four files
outside the game folder, and copy only the patch payload. Restore the four
backups for rollback. This diagnostic does not run an installer automatically.

For a later test report, retain that session's managed/preloader and native
logs (including `.1` after rotation), and any crash dump. Do not collect or
share the WebView2 browser profile/cache. A successful local host/native test
does not establish that the reporter's GTA crash is fixed.
