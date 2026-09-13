# Test 4 live review — 2026-09-10

**Failed functional test. Consumed; do not rerun. Baseline restored.**

User report: GTA's Escape menu worked; GBay could not open; after approximately
a minute the game froze and crashed/closed. Because GBay never became usable,
this does not validate the original successful-GBay-close -> pause-menu sequence.
HUD is untested (logs show vehicle handle zero / driver unavailable).

## Evidence and recovery

Frozen package and raw collector output:
`<DIAGNOSTICS_DIR>\close-intent-20260910-test4`

`review-evidence` preserves hash-verified copies of the exact game-side Reactor
session, ScriptHookV/.NET, ASI loader, ALLIN1 client logs and persisted graphics
settings. Its exported `application-events.evtx` contains Application Hang event
1002 / record 19193 / report `54a1328b-cbac-4494-a724-c1b3883e446a` for PID 5240
(`0x1478`). Host logs and collector files remain in `live` unchanged.

The original Run controller completed its shutdown watch and automatic restore.
Receipt phase is Restored. Independent comparison with frozen `baseline.json`
verified all **14/14 files**, zero mismatches. No manual installation, new test,
game launch, process kill, commit, push or release occurred in this review.

The final collector result exists this time: elapsed 325.4 seconds, qualified
false. It attached to the exact host, observed one target/host/browser root with
complete main process module coverage and no native/CEF or Steam overlay in the
host/browser. Game exit 1 and an observed desktop-probe exit -1 fail the zero-exit
checks. Preserve these failures; empty runtime disqualifier arrays do not mean
the result passed. Only three of eight short-lived probes have independent
module/identity coverage, as recorded; others are supported by host logs only.

## Timeline (UTC)

| Time | Observation |
| --- | --- |
| 13:35:09 | Game PID 5240, external host PID 4892; controller PID 84936. |
| 13:36:26.390 | Managed provider connected to the exact external host. |
| 13:36:40–48 | Escape callbacks use normal routing; Hidden lease, zero pending/bound epochs, no requested or actual overlay. |
| 13:36:49.995–50.000 | F9 epoch 1 arms and binds to the first exact GBay presentation. |
| 13:36:50.157 | Browser preparation finishes in 157 ms. |
| 13:36:51.164 | First desktop witness fails; host hides the menu. |
| 13:36:55–58 | Three more requested presentations prepare in 72–95 ms, but each desktop witness fails. |
| 13:36:58.376 | Last desktop visibility failure; input remains disabled and host subsequently hidden. |
| 13:38:24.899 | Last managed script heartbeat; readiness remains playable/browser ready. |
| 13:38:59.434 | Windows Application Hang event: game stopped interacting and was closed. |
| 13:38:59.604 | Host observes game exit code 1. |
| 13:38:59.644 | External host stops with exit code 0. |

There are about 86 seconds of managed heartbeat activity after the last desktop
failure, then about 35 seconds between the final heartbeat and game termination.
This does not establish the precise onset of the hang or its blocking stack.

## What failed, and what is not established

### Initial GBay opening was dispatched, not blocked by F9 routing

The first epoch armed/bound successfully in both script and host logs. A fresh
browser image passed the offscreen pixel check. All four attempts then stalled
in the GDI desktop probe (`gdi-bitblt`), with owned helper termination after about
360–367 ms. Each fallback DXGI desktop-duplication probe completed normally but
matched **0/8** expected witness pixels; observed samples look like game pixels,
not the expected markers. Each menu was hidden with `desktop-pixels-missing`.

Native HWND snapshots still said visible/topmost-above-game, with the same
recorded 2560x1440 bounds, styles and owner relation as successful Test 3.
Those flags are not pixel proof. Test 3's GDI witness matched 8/8; Test 4 did not.
The first-opening failure is therefore in desktop presentation/capture, after
successful F9 dispatch and browser preparation. These observations do not yet
distinguish actual desktop-composition failure from a capture-path limitation.
The user's invisible-menu report corroborates that no usable menu appeared.

The fallback reports an HDR DXGI output. A System Display event merely suggests
Auto HDR; no driver-reset or resource-exhaustion event was found in the inspected
13:34–13:40:30 window. Neither fact proves a driver/HDR cause. Persisted settings
have an August timestamp and cannot prove the exact live swap-chain mode.

### Failed presentation leaves a stale bound script intent

Only epoch 1 is armed/bound; later F9 events yield despite a Hidden lease.
Source inspection shows that the host-hidden visibility path publishes the
dismissal without clearing the matching bound input fields. When the commit
path sees the registry record already gone, `AbortPresentationTransfer` cancels
the pending commit, then returns on active-presentation mismatch without revoking
that bound intent. Successful commit or `CloseOverlay` would clear it, but neither
occurs in this failed-opening sequence. The unbound-expiry guard cannot clear a
bound epoch. This is a cleanup regression target, not evidence for the later hang.

Any correction must revoke only the exact failed presentation's authorization;
a stale failure must not cancel a newer epoch, hide a replacement or invent a
user dismissal. Do not simply call broad `CloseOverlay` on an identity mismatch.

### Hang root cause remains unknown

Windows classifies the termination as Application Hang (`Top level window is
idle`), not an access-violation event for this run. The external host/browser
processes exited normally. No new game dump was found in the inspected local
CrashDumps, WER queue/archive, top-level Temp or game-root locations; the existing
`GTA5_Enhanced.exe.42016.dmp` is from September 7 and is not this session.
No browser dump was collected. Do not assign this hang to a driver, Reactor,
another mod or the stale-intent fix without a relevant stack/evidence.

## Next bounded work

The exact-ID cleanup follow-up is now implemented and offline-tested; see
[cleanup and capture audit](FAILED-PRESENTATION-CLEANUP.md). It has not been
installed in GTA, and neither first-opening visibility nor the game hang is
claimed fixed. Original evidence and the consumed package remain unchanged.

Before another live test, investigate why the unchanged desktop
capture/presentation components repeatedly stall/fail in this game session.
Preserve fail-closed visibility/input rules. A future hang capture
needs a bounded local dump/stack trigger before termination; do not ask for an
identical blind rerun or broaden into unrelated game/mod changes.
