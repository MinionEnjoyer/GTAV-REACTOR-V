# Local input/HUD test 3 — 2026-09-10

Fresh, reversible **native-off/windowed Story Mode** package. Not a release.
No game launch or live installation is part of preparation or rehearsal.
The consumed Test 2 package, its raw results and its frozen tools are unchanged.

**Consumed live test: partial functional pass; baseline restored. Do not rerun.**
GBay opened promptly, but the user could not initially close GTA's pause menu.
Clicking another pause-menu tab then pressing Escape recovered it. HUD was not
tested because no vehicle was available. The controller ended before completing
its shutdown observation/restoration; the frozen Restore action subsequently
restored all 14 baseline hashes. See [the live review](LAYERED-INPUT-TEST3-LIVE-REVIEW.md).
The preparation/qualification notes below describe the pre-run state.

## Candidate

Package: `<DIAGNOSTICS_DIR>\layered-input-20260910-test3`

Desktop controls: `<DESKTOP_DIR>\ReactorV-Test-3`

| File | SHA-256 |
| --- | --- |
| Runtime | `061c490c8d65bb06b5ee77c8d2dcd7e2e982349ea2df5063d41bf2cfc91db07e` |
| Preloader | `5d08fec9024e5e0bb81f0cbeb2b9ed1c3c9fa6c981f4fc0c42ff74d224704b10` |
| Script | `8932ec71489fc5bb4ff4d0d194e4f3a87765d79d3c14a5fa0e3047cb56dd5150` |
| Core (two independent targets) | `708c4b28d53e7a2a68808265409d55dd2114d572789d9fec3ef621fe777d6279` |

Five targets: Runtime/Preloader/Core under `plugins/ReactorV`, Script/Core under
`scripts/ReactorV`. Receipt schema 3 / preparation schema 4 are retained. The
fixed-build driver and controller now pin these exact bytes. They do not accept
the old candidate hashes. Original baseline hashes and recovery conflict checks
are unchanged; the two Core files have separate backups.

Includes:

- [Layered cross-process input fix](OVERLAY-INPUT-INVESTIGATION.md): real native
  click-through without stealing foreground or forwarding Windows mouse input.
- [HUD host lease and presentation receipts](HUD-HOST-LEASE-AND-PRESENTATION.md):
  strict timestamp/generation validation, native-window expiry and independent
  vehicle-activation recovery, with matching script/preloader/Core contracts.
- Read-only native window state immediately around the existing desktop witness,
  plus verified layer configuration telemetry. No pixel threshold, deadline or
  retry-budget relaxation.

The first-opening delay remains unresolved. The next game run is evidence, not
confirmation that the offline input fix solves all presentation problems.

## Offline qualification

- Existing matched-build evidence in `layered-input-20260910`: 1,098 managed,
  64 native/IPC/input-configuration and 168 browser checks.
- Actual separate-process input in `overlay-input-20260910-e`: production host
  passes clicks through both clear and visible HUD pixels, plus Escape. A later
  production hide/reopen check again passed a visible-card click plus Escape.
  The receiver and its owned overlay were then closed normally.
- `presentation-deployment-rehearsal-layered-test3/result.json`: 224 private-copy
  deployment, interruption, conflict and restore checks; live baseline unchanged.
- 44 collector/comparison checks pass; nonzero or missing helper exits remain
  failures rather than being reclassified as success.
- Frozen package `browser-verification`: 84 software and 84 GPU checks against
  **its own payload**, including actual desktop witnesses, hide/reopen,
  stale/hidden rejection and layered-style preservation at three raster scales.
- `rehearsal.json` must record a successful package-local secondary-AppDomain
  provider handoff and clean external-host shutdown with the matching preparation
  hash. This is deliberately hidden and is not game/visual acceptance.

Preparation copies 1,126 qualified payload/helper files and freezes five tool
files. It preserves the installed UI/dependencies, baseline and game identity
checks. Final package preflight must pass before the user-run test.

## User-run sequence

1. Close GTA, run `ReactorV-Test-3\Run Test.cmd` **once**, and wait for
   `READY FOR USER LAUNCH`. Launch Enhanced into **Story Mode**, never Online,
   within five minutes. Keep the controller open.
2. **Before opening GBay**, drive a vehicle and check whether the speedometer
   appears/updates. Open ESC and test its mouse buttons, then return to the game.
3. Time the first F9 opening. Check GBay visibility, stability, navigation,
   closing and reopening. Check ESC mouse controls again after GBay closes.
4. Note HUD behavior before/after GBay. If absent, record that before trying a
   fresh vehicle exit/re-entry; do not hide the original failure with retries.
5. Exit GTA normally. Wait for the 90-second shutdown observation and `RESTORED`.

**No startup splash/preloader screen is expected.** The unchanged isolation
recipe uses `startVisible=false` and `showFirstRunSplash=false`. Its absence is
not a failure of this test. Normal splash acceptance remains separate.

One live run per package. Automatic restoration checks all 14 baseline files
after processes stop. If a process or changed file prevents restoration, retain
the entire package and backups, close GTA/host normally, then use the new
`Restore Test.cmd`. No process force-kill, Steam setting change, game launch,
upload, commit, push or release is performed by preparation. Ordinary game/mod
cache and log writes are not part of the binary/config rollback.

Requested report: HUD before GBay; ESC mouse before/after; first F9 delay;
navigation/close/reopen; crash or restoration errors. Collector qualification
does not replace those functional observations.
