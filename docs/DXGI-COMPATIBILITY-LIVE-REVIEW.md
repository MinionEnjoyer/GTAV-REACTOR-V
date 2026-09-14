# DXGI compatibility — initial successful local test

User report on 2026-09-10: "good test, appears to be working".
This is initial GTA Enhanced acceptance on this PC, not broad release clearance
or an item-by-item confirmation of every visual/input test.

## Identity and session

- Game PID 65456; host PID 81760.
- Start 16:06:34 UTC (09:06:34 PDT), exit 16:11:08 UTC.
- One installed change: `ReactorV.RenderHook.asi`, SHA-256
  `ccff9575a9628de3913505cc7575a5defa62b006cb45e3d6ac65585007739bc7`.
- Post-test receipt verification: candidate identity correct; all 95 protected
  paths unchanged, including the user's ALLIN1 speedometer update, original
  native/runtime binaries, ReShade, RenoDX and custom shaders.
- Host explicitly recorded **game exit code 0** and its own **exit code 0**.
  Neither process remained running when inspected.

## Corroborating evidence

- 09:06:34.451: early hook logged `preloaded_reshade`, exact game-root `dxgi.dll`,
  Win32 error 0, native load allowed. Version is CoreFX-modified ReShade 6.7.3.
- Fresh ReShade log confirms initialization inside GTA5_Enhanced.exe, RenoDX
  registration, and **71 custom shaders loaded, zero skipped/disabled**.
- Native D3D12 armed at 09:06:35.620, bound the exact game `sgaWindow` via factory
  capture at 09:06:44.174, and began acknowledging external frames by 09:06:46.
- Host external GPU presentation/content readiness was established at 16:06:46,
  about 11.6 seconds after host startup, before the managed script constructed
  at 16:07:46. Do not confuse this with the user's first-menu response time.
- Two GBay presentations reached ready: preparation + provider-commit waits
  **164 + 125 ms** and **134 + 158 ms**, roughly 0.3 seconds each.
- Last logged native snapshot: **17,192 rendered frames**, 1,214 received/
  published/acknowledged external frames. These are sampled counters, not final
  totals. Logged receive/import/copy/ACK failures and producer-image rejects
  stayed zero in the new session.
- Host/script logs had no failed/faulted/timeout/exception stage records.

## Retained caveats

ReShade logged 38 warnings and one error, so this is not a claim that every mod's
log is clean. At 09:08:52.456, RenoDX failed to replace shader pipeline
`0x3025a695`. Other warnings include pipeline-layout notices, avoided loader
calls, two DirectInput device-creation warnings, and two reference-count warnings
at shutdown. No visible defect or crash was reported. Cause and ownership of
those warnings are not established by this successful test; no speculative fix
was applied to CoreFX/ReShade/RenoDX.

First-run splash remains disabled in the unchanged config. There is no separate
user confirmation of each checklist item or timing measurement of actual pixels.
The unrelated GitHub issue reporter's machine and live Legacy remain unverified.

## Evidence and disposition

Copies of host, script, ReShade, ASI-loader, render-hook and native lifecycle logs:
`<DIAGNOSTICS_DIR>\dxgi-compatibility-live-20260910-r1\after-session-65456`.
Render-hook/native logs also contain prior sessions; scope this review to the new
start/PID above. Original installer receipt and rollback backup remain beside
that directory. Leave this candidate installed; no further game changes, commit,
push or release were performed during review.
