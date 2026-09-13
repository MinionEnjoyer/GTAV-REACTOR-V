# Test 2 live review — 2026-09-10

## Outcome and preservation

Failed functional acceptance. The user reported delayed GBay opening, the
speedometer becoming usable only after opening GBay, and unusable buttons in
the game's ESC menu. A clean process exit is not a functional pass.

Evidence is preserved under
`<DIAGNOSTICS_DIR>\verified-promotion-20260910-test2\live`.
The runtime log is
`<LOCAL_REACTORV_DIR>\reactorv-session-20260910T120747724Z-39896.log`.
The host log is `host-logs\reactorv-session-20260910T120630034Z-9260.log`.
Times below are UTC.

GTA PID 39896 exited at 12:11:37.857 with code 0; host PID 9260 exited at
12:11:37.894 with code 0. After the shutdown observer completed, the installation
receipt reported `Restored` and all 14 baseline hashes matched. Do not reuse this
single-run package or modify its frozen payload/tools/evidence.

Collector `qualified` is false. Its only false check is
`observedProbeHelpersExitedZero`; `disqualifiers` is empty. This technical result
is separate from the user's functional failure. Do not suppress this check or
reinterpret clean game/host exits as acceptance.

## Confirmed findings

### Initial desktop presentation fails; browser initialization is fast

Browser content became ready at 12:06:30.513, approximately 373 ms after browser
initialization began. The delay reported here is not explained by WebView2
initialization taking minutes.

| Attempt | Desktop check completion | Result |
| --- | --- | --- |
| GBay 1 | 12:08:14.693 | DXGI 0/8 marker matches; hidden |
| GBay 2 | 12:08:16.202 | DXGI 0/8; hidden |
| HUD first attempt | 12:08:43.832 | DXGI 0/8; hidden |
| HUD retry | 12:08:45.688 | DXGI 0/8; hidden |
| GBay 3 | 12:10:39.288 | GDI 8/8; input committed |
| HUD after GBay close | 12:10:40.504 | GDI 8/8 |

All nine native promotions in this session reported `Applied`, not `Recovered`.
The game HWND and bounds remained stable, and GTA was recorded as foreground.
The owner was already attached during each initial reveal, so later success is
not evidence that owner attachment happened for the first time.

Initial DXGI samples are game-scene colors, not merely distorted marker colors.
Capture reported FP16 scRGB, HDR output and SDR-white scale 3. The earlier HDR
normalization fix must not be credited with solving these initial failures.
Later success used GDI; that correlation does not establish why the desktop
presentation changed. No root cause for that transition is established yet.

### HUD exhaustion explains the dependency on GBay

`PassiveHudPresentationGate` permits two attempts per active interval. Both
initial attempts failed, and the script entered `Exhausted` at 12:08:45.699.
`UpdatePassiveHud` resets the gate during menu requests/preemption. After the
successful GBay menu closed, the HUD obtained a fresh attempt at 12:10:40.183
and desktop verification passed.

There is also a misleading state boundary: the script recorded `Presented`
before desktop verification finished. `KeepCompositionQualifiedPresentationVisible`
publishes native visibility before desktop proof so the window remains
closeable; the HUD currently interprets this visibility as presentation success.
Closing authority and verified presentation readiness need distinct signals.
Input authorization must remain gated by desktop verification.

### The managed input lease was released

The successful menu entered `Interactive` at 12:10:39.309, began disarming at
12:10:40.166, and reached `Hidden` with neutral input at 12:10:40.368.
The provider pointer shield dropped at 12:10:40.172. No later lease acquisition
was observed. These logs do not support claiming that the managed menu lease
remained permanently held.

## ESC-menu investigation: not yet a proven cause

The passive HUD uses a full-size external window. Its styles include
`WS_EX_TRANSPARENT` and it returns `HTTRANSPARENT`, but that is not proof of
cross-process mouse passthrough. Microsoft documents the latter's underlying
window search as limited to the same thread:
[WM_NCHITTEST](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest).
GTA and the external host run on different threads/processes. A real
cross-process input test is missing; source assertions cannot substitute for it.
Do not blindly add layered-window styles to the DirectComposition host.

The pause check itself is not an obviously wrong API: upstream SHVDN's
`Game.IsPaused` calls `IS_PAUSE_MENU_ACTIVE`
([Game.cs](https://raw.githubusercontent.com/scripthookvdotnet/scripthookvdotnet/main/source/scripting_v3/GTA/Game.cs)).

There are script heartbeat gaps and wall-clock/game-time divergence consistent
with paused or stalled game-script processing. That is a hypothesis, not proof
of the precise input failure. A script-side pause check cannot hide the external
window while the script does not tick. React clears stale HUD content after
one second, but clearing its pixels does not itself hide the native host HWND.
The native host currently has no corresponding `hud.frame` freshness handling.

## Next regression gates before another live candidate

1. Distinguish requested/native visibility from desktop-verified presentation
   throughout the real script/host boundary, including failed initial reveals.
2. Exercise vehicle/HUD activation before any GBay opening, failed reveal and
   retry exhaustion, menu preemption, and independent fresh HUD activation.
   Do not add unlimited retries or weaken pixel verification to obtain a pass.
3. Exercise paused/stalled producer behavior on the host: stale passive content
   must not leave a full-size input-obstructing HWND. An independent host-side
   freshness lease is a candidate mitigation, not proof of click-through.
4. Validate actual cross-process mouse delivery with passive HUD, hidden HUD,
   menu open/close and ESC transitions. Keep OS delivery separate from managed
   input-lease correctness; direct `SendMessage` is not an actual input test.
5. Reproduce the cold-game desktop visibility failure and retain capture backend,
   sampled pixels, game foreground/owner/bounds and promotion evidence. Passing
   the owned browser fixture alone is insufficient.

This review changes no binaries, game settings or installed files. No new test
was deployed and no GitHub write or release was performed.
