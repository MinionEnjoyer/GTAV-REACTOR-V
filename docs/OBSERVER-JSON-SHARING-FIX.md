# Observer snapshot fix and full-screen presentation investigation

2026-09-10. Repository-only changes; no live installation, game launch, commit or
publication. The consumed Test 5 package is not rerun or modified.

## Fixed: heartbeat reader sharing race

The Test 5 controller used `File.ReadAllText` while its observer atomically
replaced `status.json`. A deterministic owned-file fixture reproduces the same
sharing exception before the fix. The replacement reader:

- Opens the snapshot once with `FileShare.ReadWrite | FileShare.Delete`, preserving
  the opened generation while allowing the producer to replace the path.
- Checks the opened length and caps the actual read at 64 KiB plus one sentinel
  byte. Rejects oversized, empty, malformed UTF-8/JSON and reparse-point records.
- Retries only `IOException` sharing/byte-range lock codes 32/33, within a 250 ms
  retry budget. Persistent failures propagate; this is not an OS I/O timeout.
- Preserves BOM support, exact process/session checks, sequence monotonicity and
  the existing heartbeat-age limit. No cached healthy record, timestamp refresh,
  observer restart or clearing of already-latched errors.

API references: [FileShare](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare?view=netframework-4.8.1),
[Windows error codes](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499-).

Evidence under `<DIAGNOSTICS_DIR>`:

| Run | Result |
| --- | --- |
| `gameplay-session-reader-red-20260910-r1` | Original reader fails on the owned compatible-writer handle. |
| `gameplay-session-reader-green-20260910-r1` | 53 checks pass after the fix. |
| `gameplay-session-reader-green-20260910-r2` | 61 checks pass; includes byte locks, atomic replacement soak, malformed/oversized/BOM data, reparse rejection, sticky failures and source hash verification. |
| `reader-and-flip-regression-20260910/managed/managed.trx` | 1,139 managed tests pass. |
| `browser-reader-regression-20260910-r1` | Ordinary real-browser software and GPU cases pass 84 assertions each. |

The comparison policy suite separately passes 44 checks. The C# helper only
operates on its owned diagnostic files with finite-lived threads. It never
starts a collector or game. Failed runs and their evidence are retained.

## Still unresolved: first desktop presentation

Source review traces the existing sequence through off-screen browser paint,
retained HWND promotion, parent-position notification/commit, then the independent
desktop witness. Test 5 reports the same root/composition identities and window
geometry across failed and successful attempts. Browser readiness and Script
input cleanup do not explain the first four missing desktop witnesses.

No renderer repair, longer deadline, proof-threshold reduction, driver change,
root/device restart loop or speculative automatic retry was added. Test 5 is
still unqualified; user confirmation of normal GBay/Escape after visibility is
recorded separately in its live review.

The existing small-window fixture does not exercise a foreground full-screen
flip-model target. `BrowserPresentationValidation -FlipBackdrop` adds an owned
borderless D3D11 flip-discard backdrop, preserves both desktop witnesses and
requires foreground ownership plus continued frame presentation during probing.
Microsoft describes why full-screen flip-model composition can take different
paths from an ordinary desktop window in its
[flip-model guidance](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model).
That motivates the test; it does not identify GTA's actual scanout mode.

The new fixture builds with zero warnings/errors. Runs `browser-flip-20260910-r1`
and `-r2` stop at the foreground prerequisite: the second has 21 rendered frames,
no render exception, but neither owned window is foreground. Thus **no full-screen
presentation result is claimed**. A focused, user-launched fixture run is next;
its 97-check software/GPU cases remain unqualified. No input or focus restriction
was bypassed. Even a pass would not establish GTA/D3D12 or exclusive-fullscreen
acceptance.

## Installation and evidence boundaries

The eight frozen Test 5 tool hashes still match, and `prepared.json` remains
`47a31b7dde41279607aca165d25574f4fe7a0a5ed1afc99cdf7cb08dc1c47b1c`.
All scoped Reactor baseline binaries/config still match their restored versions.

A fresh comparison now matches **13/14** overall baseline files: installed
`scripts/ALLIN1.dll` is `ca68392bfb8307a6838816c04623a14bbb1f13a04764bcf0e62d6e7b55c786a4`,
not the frozen baseline's `1e7c598b50d57951db87f08c52e86321d32a71e17f1bd0b57b352b6874c2d5bb`.
The source/timing of this change has not been established here. It was not
overwritten or accepted by changing the old manifest. Any future live package
needs a reviewed current baseline; it must not roll back this unrelated file.
No game, host, browser fixture or ProcDump remains running at closeout.
