# Test 5 live review: delayed GBay availability

2026-09-10. User reports a noticeable startup delay and confirms that GBay and
Escape/Back appeared normal once visible. This is **not a qualified overall
test**, and the cause of the initial presentation failure remains unresolved.
The package is consumed; its original installation was restored with its frozen
driver. All fourteen baseline hashes independently match. No new fix was applied.

Evidence root:
`<DIAGNOSTICS_DIR>\gameplay-observed-20260910-test5`.
Game PID **73336**, host PID **73652**, observer PID **67088**, ProcDump PID
**30752**. The exact Script session log is preserved in `review-evidence`;
host logs and observer files remain under `live`. No dump was produced.

## Timing and presentation evidence

| UTC | Event | What it establishes |
| --- | --- | --- |
| 14:39:19.737 | External host starts | Game process was already identified. |
| 14:39:20.233 | Browser content ready | Approximately 0.50 seconds from host startup. |
| 14:40:36.521 | Game Script attaches to exact host | Overlay startup itself took 15.9 ms; Script reported browser readiness at 150 ms. |
| 14:40:40.917 | Story Mode ready | Game-side readiness, distinct from visible GBay. |
| 14:40:55–14:41:30 | Four GBay opening attempts rejected | Browser prepared each menu, but desktop pixel verification failed. |
| 14:42:59.046 | Fifth GBay attempt becomes interactive | Approximately 446 ms after that attempt's dispatch. |
| 14:44:30.773 | Passive HUD reports Presented | Telemetry, not independent user confirmation of HUD behavior. |

The first four GDI probe children stall at `gdi-bitblt` and are bounded by their
existing timeout. Each DXGI fallback finishes with 8 readable but **0 matching**
witness pixels. Captured pixel samples are brown/scene-like rather than the
requested marker palette. The fifth attempt succeeds through GDI with **8/8**
matches. Sample coordinates, window handles, reported geometry, foreground game
and overlay-above-game ordering are unchanged in the boundary logs.

This narrows the failure to desktop presentation/verification, not slow browser
content initialization. It does not prove whether GTA's composition behavior,
capture behavior or another condition changed. Do not remove the visibility guard
or attribute the failure to a driver solely from these logs.

Approximately 123.5 seconds elapsed between the first dispatch and first ready
menu, **across separate attempts and gaps**. That is not a single blocked request
or a measured automatic retry delay. The successful request took about 0.45 s.

The new Script cleanup logged `provider_presentation_input_revoked` for each of
the four rejected presentation IDs. Each subsequent F9 obtained a fresh epoch
and binding. That supports the targeted cleanup working on this path. The user's
follow-up confirms normal GBay and Escape/Back behavior once visible in this
session, not independent HUD confirmation or general regression clearance.

## Separate controller reporting failure

At **14:41:03.590**, the controller's `ReadAllText(status.json)` throws a file
sharing exception. It latches `gameplay-observer-failed`. This is a controller
status-reading defect, not evidence the capture process stopped: the observer
continues its sequence and ultimately writes a clean terminal result with
`monitorArmed=true`, game exit **0**, ProcDump exit **0**, and no dump.

The reader currently uses `File.ReadAllText`; the heartbeat writer atomically
replaces its JSON file. Their concurrent file access needs targeted regression
coverage and correction. Preserve real identity, malformed-data and stale-heartbeat
failures; do not clear historical disqualifiers or certify this live run.

## Interrupted controller closeout and recovery

Controller PID **74476** is no longer running. Its last status is **14:46:42.560**;
the observer records game exit at **14:46:43.758**. There is no final comparison
`live/result.json` or completed shutdown-watch report. The controller's reason
for exiting is not established. The test receipt still said `Applied`.

After verifying that the game, host and collector had stopped, the frozen
`Run-PresentationComparison.ps1 -Action Restore` completed the already-authorized
test rollback. Receipt phase is now **Restored**, and all **14/14** installed
baseline hashes match. Backups and original evidence remain intact. Test 5 must
not be rerun. No production source, frozen tool, receipt qualification or GitHub
release was modified to make the test look successful.

Subsequent read-only recheck during the repository reader fix: **13/14** frozen
baseline files now match. `scripts/ALLIN1.dll` differs; it was not overwritten,
and this does not rewrite the earlier restore result. See
`OBSERVER-JSON-SHARING-FIX.md` for both hashes and the next-test baseline boundary.
