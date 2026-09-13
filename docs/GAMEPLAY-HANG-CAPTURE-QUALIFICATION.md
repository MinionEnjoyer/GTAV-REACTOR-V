# Gameplay-hang capture: standalone qualification

Date: 2026-09-10. Scope: the missing post-startup capture identified in
`CLOSE-INTENT-TEST4-LIVE-REVIEW.md` and `FAILED-PRESENTATION-CLEANUP.md`.

## Outcome

The new `tools/GameplayHangCapture` observer and owned fixture passed **100/100
checks across nine process scenarios**. The unchanged managed suite passed
**1,139/1,139** tests. All **14/14** tracked installed file hashes still match the
restored Test 4 baseline. No game launch, installation change, GitHub write or
upload occurred. No game, collector or fixture remained after qualification.

This qualifies the standalone diagnostic, not its future live-test integration
or a fix for the missing GBay menu / subsequent GTA hang.

## Evidence

Local evidence root:

```text
<DIAGNOSTICS_DIR>\gameplay-hang-qualification-20260910-r3
```

- `tests.json`: 100 expected-outcome checks, including exact identity/readiness,
  free-space boundary, process cases and rejected malformed dump copies.
- `source-identity.json`: SHA-256 of the watcher, common module, fixture, test
  harness and existing collector helper, verified unchanged after execution.
- `hang/result.json`: original target PID **39884**, capture complete, target
  still alive, ProcDump exit **1**, elapsed observation **9.28 seconds**.
- `hang/gameplay-hang.dmp`: **17,076,546 bytes**, original PID verified,
  **7 threads with stack and context data**, **48 modules**, no full-memory flag.
  SHA-256 `5960ab8b7bbd1308235cfc499c69410b0f42c570e95b371cc02daaf8fc66ed36`.
- `managed/managed.trx`: passing managed regression suite.
- `installation-baseline-check.json`: expected and actual hashes of 14 files.

The real hung-window case completed a snapshot capture while the original
process survived and later exited normally. Responsive UI with no later script
heartbeat and a one-second stall produced no dump. A hang before readiness never
armed the collector. Explicit stop and deadline cancelled collection while the
target remained alive. Wrong start ticks, wrong executable hash and a simulated
other-architecture collector name were rejected before attachment/output.

Negative parser checks reject bad signature, full-memory flag, invalid directory,
wrong PID, truncated header, missing thread stream and out-of-bounds context.
This is structural dump verification, not a debugger/symbolication result.

## Fixes made during qualification

The first rehearsal successfully wrote a dump but our parser rejected repeated
all-zero `UnusedStream` directory slots. The parser now skips only those empty
slots while retaining bounds and duplicate-data-stream checks. The original
failed run remains under `gameplay-hang-qualification-20260910-r1`; the subsequent
81-check run remains under `...-r2`. Neither was relabeled as the final run.

Final review expanded collector overlap checks to the `procdump`, `procdump64`
and `procdump64a` names because documented cancellation is target-wide. A harmless
owned fixture named `procdump.exe` verified rejection without running another
actual collector. Successful capture is also reconciled after stop/exit/deadline
to avoid losing a completed dump to a polling-order race.

## Still required before a game retest

No new live candidate was installed or launched, and no Test 5 package was
created. Frozen Test 4 tools and payload remain unchanged. Follow the integration
checklist in `tools/GameplayHangCapture/README.md`: freeze the observer with the
intended input-cleanup binaries, qualify its controller-owned lifetime and
heartbeat handling, preserve local results through closeout, and verify graceful
collector exit before restoration. Do not equate an observer PID with an armed
hang monitor or a playable log signal with visible/usable GBay.

The detector cannot recover Test 4's missing dump. It can miss fast crashes,
pre-readiness failures and freezes whose windows still pump messages. Live
capture overhead and the usefulness of the bounded custom dump remain unproven.
The underlying game-freeze and game-specific presentation causes remain open.
