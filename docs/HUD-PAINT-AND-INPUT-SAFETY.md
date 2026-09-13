# HUD paint and menu input safety — 2026-09-10

Implemented and validated offline following opening test1. **Not installed,
packaged, committed, published, or accepted in GTA.** The previous test remains
consumed/restored; do not reuse its desktop shortcut or three-file manifest.

## What changed

- Passive HUD PNG checks now measure painted content, excluding the entire
  identity-marker search region. Bounded discovery uses an eight-pixel step up
  to 4K, then a 128x72 content lattice. The existing percentage thresholds apply
  within those bounds. Empty, black, marker-only and stale-identity images fail.
  Whole-frame menu thresholds are unchanged. HUD scope requires `passive-hud`
  with no active provider presentation; a menu cannot inherit it from stale host
  surface metadata. Current lease, exact identity, target size/rasterization and
  independent desktop proof remain required by the reveal path.
- The script separates HUD requests from observed host visibility. A request
  has a four-second grace period; a timeout or observed host-hide releases it.
  One delayed retry is allowed after one second, then the gate waits for a fresh
  active interval. Continuous frame updates cannot cause unlimited reopen loops.
  Inactivity/expiry/pause/disconnect resets it; menu/startup preemption does not
  hide the competing surface. `Presented` means host visibility was reported,
  not visual or in-game acceptance.
- Desktop proof failure now hides the promoted window without acquiring menu
  input. Removed explicit-F9 fallback and its 2.5-second unverified input lease.
  Both final reveal and provider-input commit require desktop verification.
  Core's transfer ledger also rejects the intent-only transitions; enum value
  8 remains reserved for old receipts. Physical intent transport stays compatible,
  but the windowed renderer reports no intent-only authorization. Browser content
  readiness remains warm/attachable after presentation failure.
- New logs record HUD content bounds/counts/scope and script request/host-state
  transitions, attempt numbers and time. Full-frame counts remain available for
  comparisons to the failed run.

## Validation

- Release Preloader/Runtime/Core and Script builds: zero warnings/errors.
- Managed suite: **1,010 passed**, including new sparse-HUD, lifecycle and
  intent-without-proof regressions; no skipped tests.
- Compiled .NET Framework PNG analyzer: **59 checks passed**, synthetic text
  and transparency at 720p/1080p/1440p/4K and 100/125/150/200% scale, digit variants,
  stale/missing markers, marker-only/empty frames, unchanged full-menu coverage.
  Maximum measured analyzer time: **86.67 ms** in this offline run. Not a game
  startup benchmark. Images are generated in memory and not retained.
- Desktop fixture passed twelve protocol/deadline cases, five real GDI witnesses,
  forced DXGI, three simulated GDI-stall-to-production-DXGI recoveries,
  wrong-identity/unknown-backend rejection and both collector role checks.
- All **12 installed baseline hashes** still match the restored test1 receipt.
  No GTA launch or installation change during this work.

Evidence:

`<DIAGNOSTICS_DIR>\hud-paint-offline-20260910-a\result.txt`

`<DIAGNOSTICS_DIR>\desktop-probe-hud-safety-20260910-a`

Re-run helpers with x64 Windows PowerShell 5.1 and fresh output directories:
`tools/HudPaintValidation/Test-HudPaint.ps1` and
`tools/DesktopProbeValidation/Test-DesktopProbe.ps1` (`-OutputDirectory`).

## Still open / future deployment

This removes a demonstrated HUD false-negative and an unsafe input fallback.
It does **not** establish why GTA's first desktop witness stalls in BitBlt or why
DXGI saw 2/8 then 0/8 matches. Invisible initial GBay and opening delay remain
unaccepted until compositor visibility is resolved/tested. Do not claim the
reporter's crash is fixed. No preloader was expected under the previous
`startVisible=false`, `showFirstRunSplash=false` isolation recipe; unchanged here.

A future authorized test needs a newly frozen, reversible package and rehearsal.
Core changed too: this PC has copies at both `plugins/ReactorV/RageWebUI.Core.dll`
and `scripts/ReactorV/RageWebUI.Core.dll`. Audit and include all consumed Core
copies alongside Runtime/Preloader/Script with exact hashes and backups. The old
three-file deployment tools/manifests do not cover this candidate and must not
be reused or silently overwritten. Existing sealed evidence stays intact.
