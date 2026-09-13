# Opening F9 / desktop-capture test — prepared 2026-09-10

**Update after live test (~10:55 UTC): consumed and restored; functional acceptance
failed. Do not rerun the desktop command.** User reports delayed/invisible initial
GBay and a flashing/disappearing speedometer. All 12 originals match after automatic
restoration. Review: `artifacts/issue-1-controller-startup/OPENING-TEST1-REVIEW.md`.
The preparation record below is historical, not an active test invitation.

Ready for a **user-launched Story Mode test**, not installed or armed. No GTA
launch, commit, push, release or online upload was performed during preparation.

Package: `<DIAGNOSTICS_DIR>\presentation-opening-20260910-test1`

Desktop controls: `<DESKTOP_DIR>\ReactorV-Presentation-Test\Run Test.cmd`
and adjacent `Restore Test.cmd` / `READ ME.txt`.

## Qualification

- 124 private-copy deployment/restore checks passed, including interrupted
  installation after each of three swaps, conflicts in each target and backup,
  rejection of old receipt versions, partial restore and idempotent restore.
- 44 collector policy checks passed.
- Frozen production external-host rehearsal passed in 98.16 seconds, including
  the 90-second shutdown watch. All eight tracked target/host/browser processes
  exited 0; no module read failures or disqualifiers. The disposable target used
  the real runtime proxy in a secondary AppDomain, not GTA or SHVDN. In-game
  first-F9 behavior is not established by this rehearsal.
- Prepared manifest schema 3, 1,126 payload files, five sealed tools. SHA-256:
  `eb28558836ef7a5bfeea51b4bc6739fa2ae139dfcaa8d85d52140446949e718a`.
- Candidate identities are the three binaries in
  [the implementation report](DESKTOP-WITNESS-HANDOFF-REFINEMENT.md).
- Final read-only preflight and independent verification of all 12 original
  installed baseline hashes passed. No new live directory or game/host/fixture
  process remains after preparation.

The updated deployment driver uses receipt schema 2 and kind
`desktop-presentation-three-file-v2`. It installs runtime and preloader under
`plugins/ReactorV`, and the script under `scripts/ReactorV`. All three originals
are hash-verified and backed up before replacement. The qualified native-off /
windowed helper remains unchanged. Original files/configuration are restored
after normal exit and observation. Conflicting files are preserved, not overwritten.

Older frozen packages and results remain unchanged, including the restored
`presentation-comparison-20260910-retry1` run. Previous desktop controls were
backed up to `presentation-opening-20260910-desktop-controls-before` under the
shared diagnostics root before repointing the three desktop files.

## Test and subsequent review

1. With GTA closed, run the desktop `Run Test.cmd`; leave the controller open.
2. Wait for READY FOR USER LAUNCH, then launch GTA Enhanced into **Story Mode**
   within five minutes. Do not use Online or Install/Repair during this test.
3. Press F9 once after loading. Report whether the first press opens GBay,
   approximate delay, stable rendering/input, and two close/reopen cycles.
   Startup splash/preloader UI remains disabled intentionally.
4. Exit GTA normally; leave the controller open through its 90-second shutdown
   watch and until RESTORED. Use Restore Test.cmd only if recovery is needed.

One run only. New evidence and three-file receipt will be under:
`<DIAGNOSTICS_DIR>\presentation-opening-20260910-test1\live`.
Use that root for the next user result, not retry1. Expected timeout-killed capture
helpers remain visible as nonzero exits in raw diagnostics; inspect them separately
from GTA/browser crashes. A technical verdict is not visual acceptance or proof
that GitHub issue #1's remote crash is resolved.
