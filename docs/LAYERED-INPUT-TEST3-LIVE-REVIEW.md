# Test 3 live review — 2026-09-10

## User observations

- GBay worked right away.
- No vehicle was available near the spawn, so speedometer acceptance is untested.
- GBay was closed before opening GTA's ESC menu. Initially the pause menu could
  not be closed. Clicking another pause-menu tab then pressing Escape recovered
  it. The user then closed GTA normally.

This is a partial pass, not a clean functional acceptance and not a crash report.
Successful tab clicking excludes a complete mouse-input lock at that point.
It does not establish whether the pause-menu fault originates in Reactor,
another mod, game frontend focus, or the specific sequence of physical inputs.

## Correlated evidence (UTC)

Package: `<DIAGNOSTICS_DIR>\layered-input-20260910-test3`

Game PID 25536; external host PID 88856. Script log:
`<LOCAL_REACTORV_DIR>\reactorv-session-20260910T131610374Z-25536.log`.
Host evidence is the frozen package's `live/host-logs` directory.

| Time | Observation |
| --- | --- |
| 13:12:22.201 | External browser content ready, initialization about 292 ms. |
| 13:16:10.553 | Managed provider attaches to the exact host in about 28 ms. |
| 13:16:31.526 | GBay presentation dispatched. |
| 13:16:32.186 | First desktop witness passes 8/8 on the layered host. |
| 13:16:32.200 | Provider ready: 163 ms browser prepare + 507 ms commit wait. |
| 13:16:32.860 | ALLIN1's physical F9 poll observes a close edge. |
| 13:16:32.864 | Script requests overlay hide. |
| 13:16:32.872 | Host pointer shield is false. |
| 13:16:32.873 | Reactor's delayed F9 handler arms a new unbound intent, epoch 2. |
| 13:16:32.882 | Host is actually hidden and detached from the game owner. |
| 13:16:33.067 | Managed input lease reaches Hidden; no further acquisition logged. |
| 13:16:34.152 | Escape cancels epoch 2 via `escape-user-intent-fallback`, despite hidden GBay. |
| 13:16:46.719–13:17:17.053 | Game HWND temporarily is not foreground; unrelated foreground identity was not collected. |
| 13:17:19.965 | Script heartbeat resumes after a roughly 46-second logging gap. |
| 13:17:28.821 | Host records game exit code 0. |
| 13:17:28.851 | External host stops with exit code 0. |

No HUD presentation was attempted; ALLIN1 reports `driver_unavailable` and
vehicle handle zero. No `game_pause_state_changed` entry captures the problematic
interval. The logging gap alone cannot distinguish paused script scheduling
from another delay, and does not establish a deadlock or crash.

## Source inspection and unresolved lead

The managed suppression path runs only for a non-Hidden menu input lease.
The last lease transition before the reported pause sequence is Hidden, and
the host had already physically hidden. These observations do not support
calling this a continuously visible overlay or a persistently held Reactor
input lease.

The delayed F9-close ordering is a real stale-intent lead: ALLIN1 closes from
physical polling first, then Reactor sees hidden state when the managed key
event arrives and treats that edge as a new opening intent. The next Escape
unnecessarily calls `CloseOverlay` again. `CloseOverlay` cancels intent and
browser presentation state; this handler does **not** directly suppress that
Windows Escape event or reset GTA's pause menu. Therefore the log is not causal
proof that the stale intent caused the stuck frontend Back state.

Next focused regression: physical F9 close -> delayed managed F9 -> GTA pause
opening/Back, with explicit empty/pending intent expiration and no reacquisition
after a closed presentation. Preserve typed input and close-grace protections;
do not force-enable all controls or forcibly close GTA's pause menu to hide the
symptom. A normal pause-menu baseline before any GBay interaction is still
missing. No speculative production input changes were made in this review.

## Collector limitations and recovery

Raw observation was disqualified at the 180-second provider-attachment deadline;
the provider later attached normally when the game's scripts started. It also
flagged PID 30472 as an unclassified preloader after it had exited too quickly
for independent identity collection. The host's own logs identify that PID as
its successful desktop-witness child (exit 0). That corroboration is not a
retroactive independent module/identity attestation; no competing host is
established by this flag alone. Preserve the original flags.

The observer PID 79460 was no longer running, its status stopped updating at
13:17:36.555, and no final `live/result.json` existed. The receipt remained
Applied. Do not claim a completed 90-second shutdown watch or a technically
qualified live capture. The reason the controller ended is not known from
these files.

After verifying GTA and the host were stopped, the exact frozen controller's
Restore action was used. Receipt is now Restored (13:19:05 UTC), and all 14
baseline hashes were verified. Payload, original backups, partial logs and
historical observations are retained. No game launch, new installation, commit,
push or release occurred during this review. Test 3 is consumed; do not rerun
its desktop Run control.
