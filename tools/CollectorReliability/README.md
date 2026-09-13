# Collector health and lifetime qualification

Local diagnostic infrastructure for issue #1. Not a product/runtime patch and
not evidence that the GTA/GBay stall is fixed. Nothing here launches GTA, uploads
data, changes Steam/graphics settings, installs a service/task, or elevates.

## Current qualification

**Desktop handoff passed:** the user-launched run on 2026-09-09 at 19:01 UTC
completed both 120-second probes, 236 heartbeat updates each. Windows PowerShell
events confirm the launcher stopped at 19:01:42 UTC and both collectors stopped
normally at 19:03:40 UTC, after writing `probe-complete`. Both manifest/process
identities and script hashes match; neither collector is still running. See
`artifacts/issue-1-controller-startup/COLLECTOR-HANDOFF-REVIEW.md` for evidence.
This qualifies the bounded offline desktop path, not an in-game capture or fix.

On 2026-09-09, Windows PowerShell 5.1 offline tests passed **39 checks**, including
8 owned-process exit checks. Both actual collector entry points completed their
offline-only loops. Startup failure, abrupt exit, handled failure, stale polling,
wrong run/process identity, malformed output, duplicate writers, and cancellation
were exercised. No runtime install receipt or dump event file was produced.

Final repeat report: `%LOCALAPPDATA%\ReactorV-Diagnostics\collector-reliability-tests-20260909-final\test-result.json`.
All owned fixtures exited; all 12 restored game-file hashes and the unchanged
ReShade DXGI hash were independently verified afterward. No game test is active.

Independent lifetime qualification is **blocked in the current command host**.
Its immediate job flags were `0x2800` (kill-on-close and explicit breakaway allowed),
but a child requested with breakaway remained job-bound. The launcher terminated
only its own **never-resumed** child and refused readiness. It did not fall back
to WMI, Task Scheduler, another parent, elevation, or altering job limits.
This is evidence about the current launch context, **not proof of what killed
the two historical observers**. A no-job requirement is deliberately conservative:
it does not attempt to decide whether an unknown ancestor job is harmless.

## One offline desktop check

Double-click `Run-OfflineCheck.cmd` from Explorer. It runs only two 120-second
heartbeat probes using the real collector entry points. It does not arm their
game/dump modes. Each output directory is new and outside the repository/cloud
sync; each probe has a finite deadline. Local scripts use process-scoped
`RemoteSigned`; no user/machine execution-policy setting is changed and managed
policy is not overridden.

The launcher exits after independently checking both worker process identities,
job membership, and advancing heartbeats. The console can be closed. Wait two
minutes, then inspect the newest `collector-handoff-*` directory under
`%LOCALAPPDATA%\ReactorV-Diagnostics`. Both records must show `probe-complete`,
the same run identity as `launch.json`, the exact launched PID/start identity,
and update times near their two-minute deadline **after the launcher exited**.
Terminal probe completion is never game readiness. A stale `waiting` record is
failure, not success. The September 9 desktop run passed this bounded check;
longer unattended operation and actual game capture remain separate qualifications.

To inspect while these probes are running:

```powershell
.\Test-CollectorPair.ps1 -OutputDirectory '<exact output directory>' -Offline
```

Without `-Offline`, the gate requires both actual collectors to be in fresh,
advancing, ready-for-game waiting states. An offline manifest cannot pass that
gate. Preparing a game test requires authorization and the existing qualified
deployment/restore workflow.

## Authorized live diagnostic preparation (test 6)

**Desktop path correction (2026-09-09):** the first user launch failed before
installation because the previously staged ProcDump was physically in the
packaged app's private AppData cache. The new live-test root is
`%USERPROFILE%\ReactorV-Diagnostics`, outside AppData and OneDrive. ProcDump is
`tools\procdump-12.01\procdump64.exe` under that root; both launch and hang capture
resolve it through the same function and retain the pinned SHA/signature checks.
New run outputs and `isolation-backups` also use this shared root. Old AppData
records/backups are retained unchanged, and legacy read/probe paths remain valid.
Always pass the exact manifest **storeDirectory** as `-StoreDirectory` when
restoring a new run; new runtime receipts reject a mismatched store.

The physical paths of the copied ProcDump, qualified helper, Newtonsoft dependency
and candidate runtime were checked with `GetFinalPathNameByHandle`; none of those
current paths is redirected. Shared-root regression: **43 checks passed**, plus
14 hang-trigger checks. A clone-capture rehearsal on an owned disposable process
also passed from the new path (valid dump, completion message, target survived
and exited naturally). Preflight passes and all 12 installed baseline hashes still
match. No successful desktop live preparation is claimed yet. See
`artifacts/issue-1-controller-startup/TEST6-DESKTOP-PATH-FIX.md`.

After the offline desktop handoff passed, Adam authorized preparing the next
in-game diagnostic. `Run-GameCapture.cmd` invokes `Start-GameCapture.ps1` from the
user's desktop. It does not launch GTA. Keep GTA/preloader closed until **READY**.

The launcher verifies the unchanged qualified helper/candidate/game baseline,
ProcDump identity/signature and free space. A named, local preparation mutex
prevents concurrent launchers. It first starts the hang observer independently
and requires its loop to advance. Only then does it start the existing session
driver with `PrepareAndCapture`, which owns the runtime backup/swap and qualified
native-off/windowed isolation recipe. Both collectors must pass the live pair
gate before the launcher reports ready. All output uses a new shared local
`%USERPROFILE%\ReactorV-Diagnostics\controller-hang-test6-*` directory. No script/helper/runtime identity allowlist
was weakened and no product binary changed for this launcher.

On setup failure, the launcher requests both workers to stop cooperatively. It
attempts the existing combined restoration only after all owned workers have
exited and GTA/preloader are closed. If workers remain or restoration conflicts,
the manifest says `needs-attention`; it does not kill processes or race a restore.
Successful gameplay still needs explicit restoration afterward; it is not
performed automatically on normal collector completion.

Run promptly after READY: enter Story Mode, try GBay, stay in game for at least
two minutes, exit normally, then allow three minutes for post-exit observation.
Report actual menu/crash behavior. Do not upload the whole output folder: memory
snapshots may contain private data and remain local. The retained text result ZIP
uses the helper's explicit export allowlist and excludes the dump files.

`Start-GameCapture.ps1 -PreflightOnly` passed locally without changes. An actual
command-host start was correctly refused **before any collector resumed or
installation changed**; its manifest is at
`%LOCALAPPDATA%\ReactorV-Diagnostics\test6-launch-refusal-20260909\launch.json`.
This negative test is not a successful live preparation. Desktop execution and
the eventual GTA capture remain pending until the user runs the launcher.

## Health contract

- The polling loop itself advances `sequence` and `updatedUtc`; no independent
  timer can hide a blocked loop. Each signal includes role, run GUID, exact PID,
  process-start ticks and executable. Writes use atomic replacement.
- A health check verifies the live process identity and a five-second freshness
  bound. The pair gate samples twice and requires progress from both collectors,
  verifies the launch manifest/script hashes and rejects job-bound processes.
- Old status messages such as `armed`/`ready` are explicitly **not** health proof.
- All terminal states fail readiness. During a bounded synchronous dump capture,
  heartbeat freshness can lapse; this is busy/stale, not a new-game-ready signal.
  The checker does not kill/restart collectors or target processes.
- Cancellation is cooperative through `<role>-stop-<runId>.request` in that run's
  output folder. Stopping observation does **not** restore a game installation.
- Output is confined to a shared or legacy local diagnostics subdirectory, rejects ancestor
  reparse points, and refuses a second heartbeat owner or old output reuse.

## Tests and limitations

Run `tests\Run-Tests.ps1` with Windows PowerShell 5.1. It starts only bounded own
fixtures and the collectors' offline branches, and retains their process handles
through exit. Fixture files/logs are retained locally. A parent/job-exit test runs
only when independent launch is actually permitted; otherwise the result is
`blocked-by-host-job`, never a passing lifetime test. Its restrictive-job test
creates a new job solely inside a disposable fixture parent.

`ProcessLifetime.StartDetached` uses hidden/no-console, non-inheriting, initially
suspended process creation. It honors the current job's breakaway permission and
verifies the child is outside **all** jobs before resuming. It never kills an
already-running worker or game. Not being in a job protects against that specific
job ownership mechanism; it is no guarantee against logout, reboot, security
software, crashes, explicit termination, or power loss. A fresh pre-launch health
check remains required even after the offline survival check passes.

Microsoft references:

- [Job objects and child lifetime](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
- [Process creation flags](https://learn.microsoft.com/en-us/windows/win32/procthread/process-creation-flags)
- [QueryInformationJobObject and immediate-job semantics](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-queryinformationjobobject)
