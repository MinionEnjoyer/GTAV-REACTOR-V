# Overlay input and cold-presentation investigation — 2026-09-10

Continuation of [the HUD lease fixes](HUD-HOST-LEASE-AND-PRESENTATION.md).
No game installation, game launch, commit, push or release.

## Cross-process fixture

`tools/OverlayInputValidation` starts a small receiver window and a separately
owned process with the production `OverlayWindow` and real composition WebView.
The receiver logs actual `WM_LBUTTONDOWN` and ESC delivery. It does not generate
or forward input. Buttons outside the overlay region select the baseline,
current enabled host, or an experimental disabled host. A synthetic 42 MPH card
is rendered over the receiver, and browser captures are saved per mode.

The fixture uses the production verified-topmost helper. An initial exploratory
run using raw SetWindowPos lacked the topmost flag, so it is not input-occlusion
evidence. The corrected run reports `Recovered` then `Applied` and confirmed
`WS_EX_TOPMOST`, preserving HWND and bounds.

Evidence:
`<DIAGNOSTICS_DIR>\overlay-input-20260910-c`

- Current host: enabled, style `0x16010000`, exstyle `0x082100A8`.
- Disabled candidate: disabled, style `0x1E010000`, same exstyle `0x082100A8`.
- Both render the HUD visibly. Disabling native input does not by itself stop
  this fixture's composition browser from drawing.
- The Computer Use tool refused coordinate clicks through **both** overlays,
  reporting the overlay rather than the receiver as its hit target. The current
  host retry after receiver activation produced the same refusal. No guard was
  bypassed. Those refused calls are **not** delivered clicks or proof of what
  a physical click would do. In the earlier unobstructed baseline, one mouse
  down and one ESC event were received normally.
- The user reported no counters increasing during the manual disabled-candidate
  check. The log recorded neither event. This is not an accepted candidate.
  On the following turn, bringing the receiver to foreground and delivering ESC
  did increment its counter. Mouse delivery remained unproven in that phase;
  the manual report does not establish that keyboard delivery fails with focus.

The input-disabled option remains a **failed fixture experiment**, not the
production fix. The subsequent layered-window fix is described below.
Microsoft documents the relevant distinctions:

- [WM_NCHITTEST](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest):
  HTTRANSPARENT's underlying-window search is limited to the same thread.
- [EnableWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enablewindow):
  disabled windows cannot receive ordinary mouse/keyboard input or activation.
- [WindowFromPoint](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-windowfrompoint):
  hidden/disabled windows are excluded by that API. An automation tool's
  separate overlap guard is not interchangeable with this API or real input.

## Cold desktop visibility diagnostics

The failed game run had monitor-sized bounds for early failures and later
successes. That alone does not establish exclusive fullscreen or flip mode.
The saved graphics XML predates the run and cannot establish its live mode.

`DesktopWindowState.Capture` now supplies a read-only snapshot immediately before
and after the desktop witness, correlated with its transfer generation. It
reports validity, visibility, enabled/minimized state, style bits, client bounds,
foreground relation, owner relation and relative z-order of the scoped game and
overlay windows. It does not read window titles or unrelated process names,
perform GDI capture, wait on DWM, change z-order or synthesize input.

These serial Win32 queries are explicitly non-atomic diagnostic context, **not
pixel evidence**. The existing desktop-witness thresholds/deadlines are unchanged.
They can help distinguish z-order/visibility changes from presentation failure;
they do not prove a GPU/driver root cause or resolve the cold GBay failure.

Validation evidence:
`<DIAGNOSTICS_DIR>\input-window-state-final-20260910`

- 1,097 managed tests.
- 56 native HUD/IPC/window-state checks, including invalid/visible/hidden/closed
  HWND snapshot behavior. Browser-rendering regression results from the previous
  turn are not represented as a new run with this telemetry build.

## Follow-up: real cross-process input delivery

The receiver now records read-only native hit targets at two fixed points in
its pad, scoped foreground/focus/capture relations, and receiver enabled state.
These snapshots are expressly not event-delivery proof. They do not inject or
forward input, read unrelated window titles, or change focus.

`<DIAGNOSTICS_DIR>\overlay-input-20260910-d`:

- Fresh unobstructed baseline: actual mouse 1 / ESC 1.
- Disabled, non-layered candidate: receiver remained enabled and foreground;
  the physical-point queries returned the disabled overlay HWND at both points.
  Disabling the form was not sufficient to establish cross-process passthrough.
- Layered + transparent candidate: both point queries returned the receiver.
  Actual Computer Use mouse events reached it through the transparent background
  and the visible MPH card (mouse 2 / ESC 1). HUD pixels remained visibly present.
- Hide/reopen: HUD disappeared/reappeared correctly, and another actual card
  click plus ESC reached the receiver (reset counters mouse 1 / ESC 1).
- No input guard bypass, synthetic Win32 forwarding, game launch or installation.

Microsoft's [layered-window hit-testing documentation](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows)
specifies mouse passthrough for **WS_EX_LAYERED + WS_EX_TRANSPARENT**. Plain
WS_EX_TRANSPARENT plus HTTRANSPARENT had not provided this cross-process
guarantee. This explains the isolated input-occlusion defect; it is not proof of
the cause of the delayed in-game GBay presentation.

### Production implementation (local source only)

`OverlayWindow.CreateParams` adds WS_EX_LAYERED alongside the existing
transparent, no-activate and no-redirection flags. `LayeredWindowInput.Initialize`
runs on every handle creation, requires the safe style combination, calls
SetLayeredWindowAttributes with alpha 255 and LWA_ALPHA only, and verifies those
attributes. Failure throws before browser presentation rather than silently
continuing with unverified configuration. No color key, opacity reduction, CPU
bitmap copy, native mouse forwarding or foreground repair is introduced.

Bootstrap/provider logical input leases still never remove native passthrough.
The existing typed DOM input path and exact presentation/input authorization
gates are unchanged. Desktop pixel thresholds, retry budgets and deadlines are
also unchanged. New telemetry labels style initialization as native configuration,
not proof of input or pixels.

### Final verification

`<DIAGNOSTICS_DIR>\layered-input-20260910`:

- `managed`: **1,098** tests passed (including the added source contract).
- `native-and-pipe`: **64** actual HWND/lease/IPC/configuration checks passed.
  Added cases cover invalid/unlayered host rejection, native layer attributes,
  all four bootstrap/provider lease combinations and handle recreation.
- `browser`: **84 software + 84 GPU = 168** checks passed. The real production
  composition host and layer initializer render changing menu/HUD markers at
  1.0/1.25/1.5 raster scales, survive hide/show and promotion, pass independent
  desktop witnesses, and reject hidden/stale pixels. This uses a fixture form;
  it is not a GTA session or a simulated claim of input ownership.

`<DIAGNOSTICS_DIR>\overlay-input-20260910-e`:

- Fresh production `OverlayWindow`, real composition browser, separate receiver
  process. **Current host** does not apply a test style override on this run.
- Production layer initialization is logged before browser creation.
- Actual input: background click, visible-card click and ESC all reached the
  receiver (**mouse 2 / ESC 1**), with the 42 MPH card visible and native topmost
  read back. Point queries also identify the receiver; actual events, not those
  queries, establish delivery.
- Runtime SHA-256:
  `061C490C8D65BB06B5EE77C8D2DCD7E2E982349EA2DF5063D41BF2CFC91DB07E`.
  Fixture/runtime fingerprints are saved in `receiver.txt`.

The final receiver is left open on Current host with those counters. Closing it
normally closes its owned overlay. No game installation changed: the consumed
Test2 receipt remains Restored, and all 14 baseline file hashes still match.
No commit, push or release. In-game ESC behavior, early splash visibility and
delayed GBay startup still require a new qualified game test; the input fix
does not declare them resolved.

Follow-up before Test 3: the same production receiver was hidden/reopened and
again received an actual visible-card click plus ESC (reset mouse 1 / ESC 1).
It was then closed normally, including its owned overlay process, before
running the frozen package's browser tests. The log remains in the same `-e`
evidence directory. See [Test 3 preparation](LAYERED-INPUT-TEST3.md).
