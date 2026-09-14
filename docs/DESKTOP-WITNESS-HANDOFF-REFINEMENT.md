# Desktop witness and opening-F9 refinement — 2026-09-10

Status: implemented and validated offline, **not installed or accepted in GTA**.
Adam clarified the last live run: "it seemed steady, it just took a while to
initialize." This is visual confirmation after opening, not full startup or
remote-crash acceptance. The previous native-disabled test intentionally had
`startVisible=false` and `showFirstRunSplash=false`; those settings are unchanged.

## Changes

- An accepted, non-startup opening from the registered default F9 owner can
  capture trusted physical F9-down state at dispatch, before delayed SHVDN
  KeyDown sees a pending presentation and yields. Story readiness, managed F9
  ownership, game foreground, debounce, no superseded presentation and no
  existing presentation/input intent are all required. Binding remains exact-ID,
  process-scoped, expiring and one-shot. Close/replacement ownership is unchanged.
  Released taps are not inferred from timestamps or extension-supplied claims.
- Desktop probing retains the original nominal 900 ms budget. A confirmed GDI
  wait gets an early cutoff at 350 ms, or 150 ms after late GDI entry if later,
  capped by the original deadline. Slow startup/parsing and the child's existing
  DXGI exception path retain the full budget. Only after the timed-out owned
  helper has exited can a second, DXGI-only helper use the remaining budget
  (minimum 200 ms). No concurrent helpers or retry of a completed wrong-identity
  result. Existing bounded process-reap cleanup can add scheduling overhead;
  this is not a hard real-time 900 ms wall-clock guarantee.
- DXGI ignores pointer-only acquisitions before reading a fresh desktop frame,
  releases those frames, and checks texture dimensions/format before copying.
  Microsoft documents `LastPresentTime=0` when no desktop image update occurred:
  [DXGI_OUTDUPL_FRAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info).
- Allowlisted telemetry now distinguishes desktop DC acquisition, compatible DC
  and bitmap creation, selection, BitBlt, bitmap conversion and cleanup. Each
  attempt retains PID, timing, stage, exit and termination status; fallback logs
  its predecessor PID, backend and remaining/original budgets.

The existing proof threshold is unchanged: all eight identity samples readable,
at least six matching within the existing color tolerance. A private browser
screenshot is still not desktop proof. No input-authority gate or timeout was
expanded, and no compositor device is recreated by the fallback.

## Verification

- Preloader/runtime and Script Release builds: zero warnings/errors.
- Managed suite: **986 passed** (including both opening orders, one-shot binding,
  all 256 dispatch-gate combinations and source wiring contracts).
- Final offline fixture runs `desktop-probe-fallback-20260910-d` and `-e` both
  passed under `<DIAGNOSTICS_DIR>`.
- Per run: twelve protocol/deadline cases; five GDI witnesses at 8/8; forced
  production DXGI; wrong-identity rejection for both backends; unknown backend
  rejection; three injected GDI stalls followed by the production DXGI capture;
  authenticated helper classification and competing-role rejection.
- Final six real timeout-to-DXGI recoveries: **646–677 ms total**, 7/8 matches.
  DXGI consistently had one sample outside tolerance; its cause was not measured.
  This satisfies the existing 6/8 threshold, not an 8/8 claim. Both backends
  rejected deliberately wrong identity at 0/8. A test initially required 8/8 for
  the newly added real-DXGI check and was aligned to the unchanged production
  quorum; the original five GDI tests still require 8/8.
- Both-helper hangs failed closed at 905/913 ms including exit overhead. Slow
  healthy startup and late GDI entry were not cut off at the 350 ms phase boundary.
- Earlier failed fixture runs `-a` (0/8 DXGI) and `-b` (7/8, rejected by the initial
  stricter fixture assertion) are retained. The original in-game GDI stall's
  exact native call/driver cause remains unproven; do not equate these results
  with a fix for GitHub issue #1's remote crash.
- Diff whitespace check passed using the repository's CRLF-aware settings.
  All 12 installed baseline hashes independently match the restored retry receipt.

## Candidate identity / next live-test boundary

| Build output | SHA-256 |
| --- | --- |
| RageWebUI.Runtime.dll | 43d0333b2fe06b6e69e689bfc1e1347dae791dee3a4886fe9e2e16d3024eea82 |
| ReactorV.Preloader.exe | f7ffff36da068828bb1742f2fd6435d8c5a6dad406c18036b563b4e01e0858a7 |
| RageWebUI.Script.dll | 3b0255dda8bad2e522f1ac47583a8ba13994685322369d2ecf017627854bc3fc |

Live installation, old frozen packages, rollback receipts and desktop shortcuts
were not changed. Nothing was committed, pushed, released or launched in GTA.
No new live test is prepared. The consumed desktop shortcut must not be rerun.

A subsequent authorized **Story Mode** test needs a fresh package and reversible
deployment/restore qualification for all **three** changed binaries, including
the script. Existing two-file deployment drivers intentionally describe the
older candidate and must not silently be reused. Observe first press, initialization
delay, close/reopen, and steady rendering separately. Expected timeout-killed
helpers must stay visible in diagnostics, not be relabelled clean exits.
