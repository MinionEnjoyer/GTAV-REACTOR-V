# Gameplay-observed Test 5: offline qualified, ready for one user run

**Consumed live test; originals restored. Do not rerun.** The user reports delayed
availability. Four opening attempts failed desktop verification before a fifth
succeeded. A separate controller status-file sharing error and incomplete
controller shutdown report prevent qualifying this run. The frozen restore
driver restored all fourteen baseline identities. See
[the live review](GAMEPLAY-TEST5-LIVE-REVIEW.md).

The preparation and run instructions below are historical pre-run information.

Package: `<DIAGNOSTICS_DIR>\gameplay-observed-20260910-test5`

Controls: `<DESKTOP_DIR>\ReactorV-Test-5`

Preparation SHA-256:
`47a31b7dde41279607aca165d25574f4fe7a0a5ed1afc99cdf7cb08dc1c47b1c`.

## Implemented in repository source

- The presentation controller starts a separate gameplay observer against its
  retained target PID/start identity. Waiting for Story readiness is distinguished
  from ProcDump's actual arming acknowledgement. Menu closure or presentation
  failure does not stop observation.
- `GameplayHang.Session.ps1` checks observer and target identities, heartbeat
  freshness/sequence, terminal result consistency and completed dump identity.
  Failures remain sticky disqualifiers, separate from visible-menu acceptance.
- A parent-written observer ownership record exists before the first child
  heartbeat. Automatic/manual restoration refuses active observers/collectors;
  closeout requests cooperative stop and never force-kills GTA or a collector.
- The offline ProviderFixture writes its rehearsal-only readiness signal after
  actual provider attachment. The watcher allows this exact prepared fixture in
  rehearsal, without generalizing the live game's pinned path/hash.
- The five-file deployment pins the input-cleanup Script build:
  `ba7762220f7ad548181325b915175abfd953e1a4d6a4fbc9641a18ceb5c76db5`.
  Runtime, Preloader and both Core targets remain unchanged from Test 4.
- Fresh packages use manifest schema 5 and seal all eight tools, the payload,
  baseline and four qualification evidence files. Package creation requires
  matching current-source capture, session and deployment qualifications.
  Historical frozen tools, payloads and receipts were not modified.

## Completed qualification

- `gameplay-session-test5-r2/result.json`: **40** observer-session checks,
  including stale/wrong identities, missing results, contradictory arming/exit
  evidence, scope violations, restore-before-heartbeat protection and PID reuse.
- Comparison evidence rules: **44** checks passed.
- `gameplay-test5-managed/managed.trx`: **1,139** managed tests passed.
- Changed PowerShell source parsed successfully; whitespace check passed.
- `gameplay-hang-test5-qualification-r1/tests.json`: **100** capture checks on
  the current observer source, including a real deliberate hang and natural
  fixture exit. The completed custom dump retained the original PID and usable
  stack/context/module records. No GTA process was targeted.
- `presentation-deployment-rehearsal-gameplay-test5-r1/result.json`: **224**
  five-file deployment/rollback checks on private copies, including interrupted
  swaps, intervening files, damaged backups and independent Core restoration.
- Frozen package `browser-verification/result.json`: **84 software + 84 GPU**
  presentation checks against its own payload. Not an in-game input test.
- Frozen package `rehearsal-20260910T142447Z`: actual secondary-AppDomain provider
  attachment and observer arming, target/collector/host/browser clean exits, and
  the full **90-second** shutdown watch. Overall elapsed **98.23 seconds**,
  no disqualifiers; completed **2026-09-10 14:26:28 UTC**. Observer closeout is
  qualified and `safeToRestore=true`, with no dump on the responsive fixture.
- Frozen Preflight passed. All fourteen installed baseline identities remain
  unchanged; no live test directory exists. All eight tools, 1,126 payload/helper
  files and four qualification evidence files are sealed.

All evidence paths above are under `<DIAGNOSTICS_DIR>`.
The earlier standalone qualification remains historical evidence. The current
watcher was rerun separately above; older results were not relabeled or edited.

## Temporary qualification pause, resolved

The new capture rehearsal correctly refused to start because GTA Enhanced and
its installed preloader were running. That game was not launched by this work;
no collector was attached to it and no installation change was made. After the
user closed GTA normally, absence of the game/host and the unchanged baseline
were verified before continuing. Static/owned short-lived PowerShell tests above
did not access GTA.

## One user-run session

1. With GTA stopped, run the new desktop `Run Test.cmd` once. Keep its window
   open; wait for `READY FOR USER LAUNCH`, then start Enhanced into Story Mode
   manually within five minutes. No automatic GTA launch occurs.
2. Test ESC/Back before GBay, then F9 opening, closing and reopening/navigation.
   Repeat ESC/Back after closing GBay and check the HUD when a vehicle is available.
   Report the original failure before retries or pause-tab workarounds.
3. Look for the controller's `GAMEPLAY HANG MONITOR ARMED` acknowledgement. A
   missing acknowledgement or collector failure is not a passing test. A dump
   stays local and does not itself establish the freeze's root cause.
4. Exit GTA normally and leave the controller open through the 90-second shutdown
   watch until `RESTORED`. Use `Restore Test.cmd` only if recovery needs attention,
   after the game, host and observer have stopped. Preserve receipts/backups.

The Run action applies the exact candidate only when invoked by the user.
Preparation did not install it, publish anything, change Steam or upload dumps.

Native-off/windowed isolation is unchanged: no startup splash/preloader screen
is expected. The live checks still concern ESC/Back before and after GBay,
F9 open/close/reopen and HUD behavior, with delayed/invisible presentation and
hangs reported honestly. This work collects evidence; the GTA freeze's cause
and game-specific presentation failure remain unresolved.
