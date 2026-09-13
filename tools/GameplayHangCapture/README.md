# Post-startup gameplay-hang capture

Local diagnostic tooling for the unresolved Enhanced Test 4 gameplay freeze.
This is **not a crash fix**, a shipped Reactor feature or a replacement for the
startup-controller watcher. It does not launch GTA or install anything.

## What it observes

`Watch-GameplayHang.ps1` requires an already-running, exact process ID and UTC
start ticks. It retains the process handle and verifies executable path, start
identity and SHA-256 before proceeding. The live branch deliberately pins this
machine's Enhanced executable/build; it is not a general end-user deployment.

The observer starts in `waiting-for-story-ready`. Only a current log line from
that PID with `source=script stage=diagnostic_tick_heartbeat` and
`story_ready=True playable=True browser_ready=True` can arm collection. Multiple
matching logs, duplicate identity/readiness fields or an old timestamp fail
closed. These signals establish the collection gate, **not successful GBay
presentation**. GBay may still be invisible when capture is armed.

Once armed, it uses the signed and SHA-pinned local ProcDump 12.01 executable:

```text
-r 1 -a -at 5 -mc 1824 -n 1 -s 5 -h <exact PID> <local dump>
```

ProcDump's documented `-h` trigger detects a hung window; `-r` requests a process
snapshot clone. We do not trigger from a stale script heartbeat: normal pause
menus can suspend script scheduling. Monitoring stays active after readiness,
menu closure and missing subsequent script heartbeats. See
[Microsoft's ProcDump reference](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump).

Arming is acknowledged by ProcDump's monitoring message, not merely a successful
`Start-Process`. A JSON heartbeat is emitted by the actual polling loop. This
allows a future test controller to distinguish waiting, monitoring, capturing,
completion and collector failure without an independent timer masking a wedge.

## Bounds, identity and privacy

- Fresh output only under `%USERPROFILE%\ReactorV-Diagnostics`, with reparse-point
  checks. No uploads, settings changes, debugger registration or EULA acceptance.
- Exact target identity checked throughout observation and before cancellation.
  A per-session mutex rejects another watcher for the same PID/start identity.
- At least 2 GiB free on this machine's C: diagnostics volume. A 512 MiB **observed
  size guard** requests cancellation; it is not a hard write quota and can
  overshoot between polls. Oversized/partial files are retained, not certified.
- One custom stack/metadata dump, never `-ma` full-memory collection. The
  completed dump can still contain sensitive data. Review before sharing.
- Default observation deadline 30 minutes (configurable 5–1,800 seconds),
  500 ms polling, 15-second monitoring-acknowledgement deadline, and the ProcDump
  collection limit above. Graceful cleanup may extend beyond the observation
  deadline. There is no forced process termination.
- Stop by creating `stop.request` in that run's output directory. The watcher
  also stops after capture or target exit. It never cancels a recycled PID.
- ProcDump's `-cancel PID` affects all its collectors for that target. Startup,
  arming and cancellation therefore check for other `procdump`, `procdump64` and
  `procdump64a` processes. Ambiguous cancellation returns `needs-attention`
  without sending a broad cancel. This is a check, not a system-wide lock against
  unrelated tools starting concurrently; do not run other collectors alongside it.

## What counts as a completed capture

The process must finish with the observed valid ProcDump exit code (0 or 1),
report `Dump 1 complete:`, and produce a validated file. `Read-GameplayDump`
checks the MDMP header, file size, requested/non-full-memory flags, stream bounds,
original target PID, thread/context/stack bounds and module entries. Empty
all-zero `UnusedStream` slots emitted by ProcDump are accepted; duplicate data
streams are rejected. See the
[Microsoft stream definition](https://learn.microsoft.com/en-us/windows/win32/api/minidumpapiset/ne-minidumpapiset-minidump_stream_type).

Completion racing with target exit, cancellation or a deadline is reconciled
before the final result so a finished dump is not mislabeled "no capture".
This is structural validation, not proof that every frame can be symbolicated
or that a particular root cause will be evident.

Each run preserves `status.json`, `result.json`, `events.jsonl`, collector
stdout/stderr and any dump. Event records include the target and collector
hashes. A result with `failure`, `needs-attention`, no arming acknowledgement or
an incomplete file is not a successful hang-capture qualification.

## Rehearsal

Use x64 Windows PowerShell 5.1. The trusted collector must already be installed
at `ReactorV-Diagnostics\tools\procdump-12.01\procdump64.exe` and its license must
already have been accepted. Nothing downloads automatically.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\GameplayHangCapture\Test-GameplayHang.ps1 -OutputDirectory "$env:USERPROFILE\ReactorV-Diagnostics\gameplay-hang-rehearsal-UNIQUE"
```

The harness compiles and runs a small, nonactivating Windows Forms fixture with
finite lifetimes. It does not run GTA or kill processes. Cases cover a real hung
window, responsive UI with no further script heartbeat, a pre-readiness hang, a
one-second stall, cancellation, deadline, wrong start identity, wrong executable
hash and an overlapping collector name. It corrupts copies of its own generated
dump to test rejection while retaining the original. Every test source is hashed
before and after the run. Generated dumps/fixtures stay outside Git.

## Remaining integration and limitations

The repository's presentation controller now owns the observer through
`GameplayHang.Session.ps1`, including exact observer/target identity, fresh
heartbeat checks, an arming acknowledgement, local closeout and restoration
guards. Its offline ProviderFixture supplies a readiness signal only after
attaching to the external runtime. The frozen Test 5 package passed its
process-level rehearsal, including actual arming, natural collector/target/host
exits and the full 90-second shutdown watch. **Test 5 has since been consumed and
restored, not accepted:** a status-file sharing error affected controller
reporting, the final shutdown report is missing, and early GBay opens failed
desktop verification. The observer itself armed and recorded a normal game exit
without a dump. See `docs/GAMEPLAY-TEST5-LIVE-REVIEW.md` for the live findings.
Frozen/consumed Test 4
artifacts remain unchanged. Any later package must preserve these requirements:

1. Freeze/hash this observer, its shared helpers and the intended input-cleanup
   candidate together. Start it against the exact game session the controller
   already owns; no executable-name-only attachment.
2. Qualify child lifetime and heartbeat monitoring through startup and gameplay.
   Report actual `monitorArmed`, not just a watcher PID. Do not stop observation
   merely because a menu closes, presentation fails or startup finishes.
3. Preserve observer/capture failure separately from game results, request a
   cooperative stop at test closeout, and confirm collector exit before restore.
   Handle `needs-attention` visibly; never kill the game as cleanup.
4. Keep dumps local and out of automatic support ZIPs pending explicit review.

The repository reader now opens bounded JSON snapshots with replacement-compatible
sharing and retries only native sharing/byte-lock conflicts within a 250 ms retry
budget. Missing data, malformed/oversized/reparse records, stale identity and
heartbeat failures remain disqualifying; historical failures are never cleared.
The updated `Test-GameplaySession.ps1` passes 61 checks, including real concurrent
atomic replacements and transient/persistent lock tests. The fix is **not** a
retroactive qualification of Test 5 and does not change its frozen tools. See
`docs/OBSERVER-JSON-SHARING-FIX.md` for evidence and the remaining presentation gap.

No capture occurs before the Story-ready gate. Fast crashes, a freeze that still
pumps window messages, an unobserved/non-hung window or a killed observer may
escape this detector. Capture overhead and usefulness still need live-game
evaluation. It cannot recover Test 4's missing dump or determine that freeze's
cause retrospectively.
