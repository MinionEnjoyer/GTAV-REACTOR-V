# Verified native promotion and browser regression — 2026-09-10

## Finding

The new owned-window test reproduces a cold first-show failure without GTA:
private WebView2 PNG analysis succeeds, but SetWindowPos(HWND_TOPMOST) reports
success with WS_EX_TOPMOST still absent. Both GDI and corrected FP16 DXGI read
background pixels instead of the marker. Geometry, HWND identity, WS_VISIBLE
and DWM uncloaked state are correct. Rebinding the visual, default GPU mode,
starting on-screen, extra compositor fencing and an extra delay did not resolve
the raw case.

A single HWND_NOTOPMOST reset followed by HWND_TOPMOST changes the native
readback and restores all eight marker pixels. No hide/show, root replacement,
focus acquisition or browser restart is needed at that boundary. A separate
raster control also altered the outcome; therefore it was removed from the
final cold-browser test to avoid concealing the failure.

This demonstrates a recoverable native z-order disagreement on this PC. It does
not establish why Windows first reports success without the requested state,
or prove it explains every live GBay delay/crash report.

## Change and guardrails

`VerifiedWindowPromotion` now handles the final qualified reveal, explicit
topmost application and z-order reassertion. A cached managed topmost value no
longer suppresses native readback. Its shared decision policy:

1. Request promotion; reject a failed native call.
2. Read WS_EX_TOPMOST. Accept a matching readback without any reset.
3. On success/readback disagreement only, reset z-order once, preserving
   HWND/bounds/visibility and using SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE.
4. Re-promote and require a fresh matching readback. Otherwise fail closed.

There is at most one repair per invocation, no sleeps, loops or extended desktop
deadline. Telemetry reports `Applied`, `Recovered`, `PromotionRejected`,
`ResetRejected` or `ReadbackRejected`, HWND and native errors. Existing ingress,
paint identity, lease/generation and independent desktop-pixel checks remain.
Topmost success alone never grants input or proves GBay content is visible.

## Tests and evidence

- 33 new functional policy cases: every native-call/readback combination,
  exact callback order, bounded attempts and no success cache across requests.
- 1,061 managed tests pass, including updated reveal ordering contracts that
  still require desktop verification before committed input.
- New real-browser test: 77 assertions per process, software/default GPU,
  menu/HUD, three raster scales, cold/reopened/hidden/stale identities.
- Repaired the old DPI fixture's outdated composition-host constructor;
  all 24 capture-size cases pass again.
- Existing desktop protocol/deadline/color/collector controls pass.

Evidence in `<DIAGNOSTICS_DIR>`:

- `browser-presentation-20260910-x`: raw promotion, no raster control;
  browser paint passes, both desktop backends reject first presentation.
- `browser-verified-promotion-20260910-a` (software) and `-b` (GPU): compiled
  production helper, 77/77 each. Cold call reports `Recovered`; subsequent
  calls report `Applied`.
- `browser-verified-promotion-20260910-repeat`: the checked runner passes all
  four cold processes (308 assertions). `browser-verified-promotion-20260910-raw`
  uses the same runtime with recovery bypassed: exit 1, no topmost readback,
  0/8 marker matches on both backends. This is a negative control, not a pass.
- `desktop-regression-20260910-c`: independent existing capture controls pass.
- `dpi-regression-20260910-a`: 24/24 captures accepted.
- `hud-paint-verified-promotion-20260910-a`: 59 compiled HUD paint checks pass.

Run `tools/BrowserPresentationValidation/Test-BrowserPresentation.ps1` for
repeatable sequential cold runs with pinned binary hashes and completion checks.
The final-source rebuild evidence is in `verified-promotion-final-20260910`:
the managed TRX, four passing browser runs with a binary hash manifest, the
expected-failing raw negative control, all 24 DPI cases, the desktop controls,
and 59 compiled HUD checks (90.15 ms maximum analyzer time). Exploratory logs
remain separate so an earlier successful run cannot mask a final failure.
Do not equate offline passage with an in-game acceptance result. No game files
were modified, no GTA launch was performed and no release was published.
All 12 restored-baseline hashes still match; no game, host or fixture process
remains. The repository remains uncommitted and unpublished.
