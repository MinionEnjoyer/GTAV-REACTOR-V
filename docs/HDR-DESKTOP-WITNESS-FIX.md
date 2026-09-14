# HDR desktop witness correction — 2026-09-10

Status: reproduced and corrected offline on this PC. Not installed, packaged,
committed or published. No GTA launch or display-setting changes.

## Finding

The first two failed live menu identities were replayed as opaque colors in an
owned, nonactivating desktop fixture. No GTA or WebView was required to reproduce
the exact **2/8 then 0/8** DXGI matches. GDI read the same colors correctly at 8/8.

| Recorded menu identity | Legacy DXGI / GDI | Corrected DXGI / GDI |
| --- | --- | --- |
| `894AFD87511DE5E7` | 2/8 / 8/8 | 8/8 / 8/8 |
| `F83A38AAB229AF24` | 0/8 / 8/8 | 8/8 / 8/8 |
| `5AB00CBF19D04392` | 2/8 / 8/8 | 8/8 / 8/8 |

The third live opening used GDI and passed; its legacy-DXGI result above is an
offline replay, not a claim about that game's capture.

The older primary-color fixture obscured this defect: red/green/blue and white
already sit at channel extremes. Only orange changed (`FFA500` to `FFFF00`),
giving a seemingly adequate 7/8 score. The actual marker uses midtone channels;
legacy DXGI brightened/clipped many of those into the wrong color.

The selected output reports NVIDIA GeForce RTX 4070 Ti / `\\.\DISPLAY3`,
HDR `RgbFullG2084NoneP2020`, and SDR-white scale **3** (240 nits). Preserving FP16
and reversing that OS-reported SDR white boost restores every observed marker
channel exactly in these fixture runs. This is a demonstrated false-negative in
our desktop witness. The identical live scores strongly implicate it there, but
old logs did not retain observed RGB, so they cannot retrospectively prove what
was visible to Adam.

## Implementation

- Prefer `IDXGIOutput5::DuplicateOutput1` with FP16 and BGRA8 formats, retaining
  FP16 when supplied. The older API converts to BGRA and can lose HDR information.
  [Microsoft API contract](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_5/nf-dxgi1_5-idxgioutput5-duplicateoutput1).
- For HDR FP16, convert linear scRGB back to SDR using the actual OS SDR reference
  white, then the sRGB transfer function. No fitted exposure, tone-map heuristic,
  channel-tolerance increase or fallback input authorization. Windows documents
  scRGB's 80-nit reference and the linear SDR-white adjustment.
  [Microsoft Advanced Color guidance](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range).
- Resolve the DXGI device name through active display paths; query target LUID/id
  using `DISPLAYCONFIG_SDR_WHITE_LEVEL`, whose value divided by 1000 is the scale.
  [Microsoft structure definition](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_sdr_white_level).
- BGRA SDR keeps its existing path. FP16 on an identified SDR output uses scale
  one. Unknown FP16 color space, unavailable/ambiguous white level, unsupported
  formats, clipped HDR negotiation, or a topology/color/white-level change during
  acquisition fail closed. Partial constructor failures release owned resources.
- CPU readback/conversion covers the requested sample strip rather than the whole
  game rectangle. The duplication/staging surface remains output-sized.
- Optional bounded telemetry records the eight expected/observed RGB values and
  physical coordinates, plus output color space, capture format and white scale.
  The parser permits only bounded numeric RGB or missing entries; telemetry never
  overrides acceptance. No screenshot is saved.
- Unchanged production requirements: eight readable identity cells, at least six
  matching, tolerance 56, original nominal 900 ms deadline, process-isolated
  helpers, GDI-stall fallback sequencing and strict desktop-proof-before-input.

## Evidence and tests

- Before-correction evidence (retained):
  `<DIAGNOSTICS_DIR>\desktop-pixel-evidence-20260910-a`
  (primary colors), `desktop-pixel-evidence-20260910-b` (recorded identities).
- Corrected runs:
  `<DIAGNOSTICS_DIR>\desktop-hdr-normalization-20260910-a`
  and `desktop-hdr-normalization-20260910-b`.
- Release build clean; **1,028 managed tests pass**. New tests cover all 256
  SDR channel values at five white levels (1,280 round trips), finite/range
  handling, interop layouts and production wiring/safety boundaries.
- Compiled desktop fixture: 13 pixel-telemetry parser checks, twelve existing
  protocol/deadline cases, five GDI witnesses, forced DXGI, three simulated
  GDI-stall-to-production-DXGI recoveries, three recorded palettes through both
  backends, wrong/stale identity rejection and two collector role checks.
  Corrected controlled color cases now require 8/8 in the fixture, without
  changing the production 6/8 requirement.
- In final `-b`, three stall fallbacks matched 8/8 in **661–670 ms** total.
  Deliberately wrong black identity matched 0/8, stale recorded identity 1/8;
  both were rejected.
- The 59 compiled HUD PNG regressions also pass against the updated Runtime
  (`hud-paint-hdr-followup-20260910-a`, maximum analyzer time 91.34 ms).
  Final read-only audit: all 12 original installed baseline hashes match,
  no GTA/host/fixture process running; CRLF-aware diff check clean.

## Remaining boundary

Follow-up: `VERIFIED-WINDOW-PROMOTION-REGRESSION.md` adds a real-browser cold
presentation test and bounded native z-order readback/recovery. This is another
offline regression fix, not live game acceptance.

This does not explain why GDI stalls inside BitBlt against GTA, nor establish
that a promoted external HWND appears reliably above the game's presentation.
Do not dismiss the user's invisible-menu observation based on this reproduction.
It makes the next desktop witness trustworthy on this HDR setup; it is not live
GBay/startup/HUD acceptance or resolution of the remote crash report.

No new live test is armed. The consumed desktop shortcut and sealed three-file
package stay untouched. Any future authorized test requires fresh reversible
deployment covering both consumed Core copies as well as Runtime, Preloader and
Script, per `HUD-PAINT-AND-INPUT-SAFETY.md`.
