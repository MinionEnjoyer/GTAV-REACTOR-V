# Local close-intent test 4 — 2026-09-10

Fresh, single-use **native-off/windowed Story Mode** package, not a release.
Preparation and offline checks do not install into GTA or launch the game.
Consumed Test 3, its frozen tools, receipts and partial live evidence are retained.

**Consumed live test: failed; original installation restored. Do not rerun.**
The user reports Escape worked, GBay would not open, and GTA later froze and
closed. Windows recorded Application Hang event 1002 for this game's PID.
The controller completed its shutdown watch and restoration; all fourteen
baseline hashes were independently verified. See
[the Test 4 live review](CLOSE-INTENT-TEST4-LIVE-REVIEW.md). The qualification and
run instructions below describe the historical pre-run state.

Package: `<DIAGNOSTICS_DIR>\close-intent-20260910-test4`

Desktop controls: `<DESKTOP_DIR>\ReactorV-Test-4`

## Scope and identity

The only changed production binary versus Test 3 is Script. It includes the
[bounded stale-intent correction and regression coverage](CLOSE-INTENT-REGRESSION.md).
It does not establish that the pause-menu symptom is fixed.

| File | SHA-256 |
| --- | --- |
| Runtime | `061c490c8d65bb06b5ee77c8d2dcd7e2e982349ea2df5063d41bf2cfc91db07e` |
| Preloader | `5d08fec9024e5e0bb81f0cbeb2b9ed1c3c9fa6c981f4fc0c42ff74d224704b10` |
| Script | `e10bf71f9415eff630c11c82f0f6281c0bd915681bc78f361e3fadd9722d6d25` |
| Core (two independent targets) | `708c4b28d53e7a2a68808265409d55dd2114d572789d9fec3ef621fe777d6279` |

The five deployment targets and fourteen-file baseline/restore contract remain
unchanged. Script/Core go under `scripts/ReactorV`; Runtime/Preloader/Core under
`plugins/ReactorV`. The two Core copies have independent backups. Native-off
isolation intentionally sets `startVisible=false` and `showFirstRunSplash=false`;
**no startup splash or preloader screen is expected** in this test.

## Offline evidence

- `close-intent-regression-20260910/managed/managed.trx`: 1,121 managed passes,
  including 23 added regression cases; two reproductions failed before the fix.
  Script Release builds with zero warnings/errors.
- `presentation-deployment-rehearsal-close-intent-test4/result.json`: 224 checks
  against private copies, including injected interruption/conflict/restore cases.
  Live installation remains unchanged.
- Comparison evidence rules: 44 checks pass; missing/failed helper exits remain
  disqualifiers. No collector deadline or evidence standard was relaxed.
- This package's `browser-verification`: 84 software and 84 GPU checks against
  its own payload, including desktop witnesses, hide/reopen and layer preservation.
- 1,126 payload/helper files and five tools are sealed in `prepared.json`, schema 4.
  No new UI, browser dependency, game executable or baseline identity is accepted.
- The hidden secondary-AppDomain provider handoff and full 90-second shutdown
  watch passed (`rehearsal-20260910T133145Z`, completed 13:33:25 UTC), with no
  disqualifiers. `rehearsal.json` matches preparation hash
  `b4dc46bdd96ee9dcb5f100067f2afb7434de4252431157c398f38880175ea3ca`.
  Final frozen Preflight passed with the fourteen-file baseline unchanged.
  Neither check proves live menu visibility, actual GTA Escape behavior or HUD
  acceptance. This package has not been run in GTA or installed during preparation.

Prior actual separate-process pointer/Escape pass-through evidence applies to
the unchanged Runtime/Preloader, not to GTA's pause-menu frontend. Test 3's
attachment-deadline/unclassified-helper flags and incomplete shutdown capture
are not retroactively changed by this new package.

## One user-run session

1. Close GTA. Run `ReactorV-Test-4\Run Test.cmd` once and wait for `READY FOR USER
   LAUNCH`. Start Enhanced into **Story Mode**, never Online, within five minutes.
   Keep the controller window open throughout the game and shutdown watch.
2. Before touching GBay, open GTA's ESC menu. Test Escape to return. Reopen it
   and test the mouse Back control where available. Record the baseline. If a
   vehicle is nearby, check the speedometer before GBay; otherwise mark HUD untested.
3. Press F9, note opening delay and visibility, then close GBay with F9. Open
   GTA's ESC menu again and repeat Escape/Back. Record the first result before
   switching pause-menu tabs as a workaround. Check GBay reopening/navigation.
4. If practical, check the HUD after GBay closes. Do not treat an untested HUD as
   passed or hide an original failure with repeated retries.
5. Exit GTA normally. Leave the controller open for its 90-second shutdown watch
   and wait for `RESTORED`. Report behavior and any capture/restoration errors.

If Back gets stuck, report whether it worked before GBay and whether clicking
another tab then Escape recovers it. Escape route logs record only managed event
state, not proof of native event delivery. If no game window can be closed
normally, ask for assistance rather than force-killing processes.

Use `Restore Test.cmd` only if restoration needs attention, after GTA and its
host have stopped. Keep receipts/backups. Conflicting files are never blindly
overwritten. One live run per package; do not rerun a consumed Run command.

No automatic GTA launch, Steam change, force-kill, upload, commit, push or release.
Ordinary game/mod cache and log writes are outside the binary/config rollback.
