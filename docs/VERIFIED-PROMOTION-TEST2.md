# Local verified-promotion test 2 — 2026-09-10

**Live result: failed functional acceptance; baseline restored.** The user ran
this package and reported delayed GBay opening, a speedometer that only started
working after GBay opened, and unusable ESC-menu buttons. All 14 baseline hashes
were verified after automatic restoration. The package is consumed: do not rerun
its desktop shortcut. The preparation notes below describe its pre-run state;
see [the live review](VERIFIED-PROMOTION-TEST2-LIVE-REVIEW.md) for findings.

Scope: a newly frozen, reversible native-off/windowed Story Mode test of the
HUD/input, HDR capture and verified native-promotion fixes. This is not a release,
an SDK change or proof that the remote crash report is resolved. No GTA launch
is automated. The user starts the desktop controller, then GTA separately.

## Package

`<DIAGNOSTICS_DIR>\verified-promotion-20260910-test2`

Five replacement targets, four unique payload files:

| Target | Candidate SHA-256 |
| --- | --- |
| `plugins/ReactorV/RageWebUI.Runtime.dll` | `33a690aafff9c8916e61f0fe53f3bca9da052585d6ad466ee1f0e49b2fbcbb2e` |
| `plugins/ReactorV/ReactorV.Preloader.exe` | `1dac29e7b0ec13766315fce6ae33ba3f32492463312214cb7741880a2d97edab` |
| `scripts/ReactorV/RageWebUI.Script.dll` | `254bfa750d0c50ee56001365714124e7e186494a2605854b73b4087179679a30` |
| `plugins/ReactorV/RageWebUI.Core.dll` | `fff68240c5ef008e5127b5d1aac804b3e1f2bc77c0d993bf1ead0a693da41c40` |
| `scripts/ReactorV/RageWebUI.Core.dll` | `fff68240c5ef008e5127b5d1aac804b3e1f2bc77c0d993bf1ead0a693da41c40` |

There are exactly two active Core copies under scripts/plugins. Archive/backup
copies are outside those loading locations and are not deployment targets.
The two original Core files are identical, hash
`674bf7eb379fab6e95f49b724eb8010bf98aa0a909e8f32596b84ca4ffce6862`, but have
independent, relative-path-derived backup names. No original backup is shared.

## Transaction and acceptance gates

- Five-file receipt schema 3 / kind `desktop-presentation-five-file-v3`.
- Frozen preparation schema 4; older two/three-file receipts are refused.
- All five target/backups checked before restoration; 14 restored baseline
  hashes cover both Core copies as well as the existing game/native/config set.
- Payload, UI/dependencies, game identity and frozen tool hashes checked before
  a live run. The old consumed test and desktop folder are left unchanged.
- Existing isolation helper still controls the native-off/config transaction.
  No installed content/UI, Steam setting or game account is changed.
- A matching hidden-provider handoff rehearsal is required, but is not a
  visual pass. Real-browser menu/HUD desktop-pixel tests remain separate.

Private-copy deployment evidence:
`<DIAGNOSTICS_DIR>\presentation-deployment-rehearsal-verified-promotion-test2-a\result.json`

224 checks pass: interruption after each of five swaps, complete rollback,
target and backup conflicts for every file, aliased Core backup refusal,
old-receipt rejection, wrong store, partial restore and idempotent restore.
The real game installation remained unchanged throughout this rehearsal.

Package qualification completed: 44 collector/comparison checks, 1,061 managed
tests, successful package-local secondary-AppDomain handoff with clean shutdown,
and 154 real-browser assertions against the frozen payload (software and default
GPU). Final package preflight passes. All 14 installed baseline hashes match;
the package has no live receipt or armed run. Desktop controls are ready at
`<DESKTOP_DIR>\ReactorV-Test-2`. Nothing is installed or published.

`rehearsal.json` records the matching frozen preparation hash and successful
handoff; `browser-verification` contains the visual probe logs. Neither supplies
the user's still-required live GBay/HUD acceptance.

## User-run checks

1. Close GTA and run the new desktop `ReactorV-Test-2\Run Test.cmd` once.
2. Wait for `READY FOR USER LAUNCH`, then start GTA Enhanced in **Story Mode**.
   Never select Online for this mod test. Keep the controller window open.
3. Record approximately how long the first F9 GBay opening takes. Verify it is
   visible, steady, navigable, closes and reopens correctly.
4. Drive a vehicle: verify the speedometer remains visible rather than flashing
   for one frame. Note whether opening/closing GBay changes the HUD behavior.
5. Exit GTA normally. Wait for the 90-second shutdown observation and the
   `RESTORED` message before closing the controller.

No startup splash/preloader screen is expected under this unchanged isolation
recipe (`startVisible=false`, `showFirstRunSplash=false`). Its absence alone is
not a failure. Full startup/splash acceptance is outside this test.

One live run per package. A launch timeout restores without starting a host.
If restoration is blocked by a running process or file conflict, preserve the
whole frozen package, helpers, receipt and backups. Close GTA/host normally and
use the new desktop `Restore Test.cmd`; never overwrite an intervening file or
reuse the old consumed shortcut. No automatic game or unrelated process kill.

Logs stay local in the package's `live` folder. The collector's technical
qualification never substitutes for the user's GBay/HUD observations.
