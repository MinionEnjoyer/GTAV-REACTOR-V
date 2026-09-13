# Failed-presentation input cleanup and capture audit — 2026-09-10

Follow-up to [Test 4's failed live session](CLOSE-INTENT-TEST4-LIVE-REVIEW.md).
Local source/tests only: no new game installation, Test 5 package, launch or
publishing. All fourteen restored baseline hashes still match.

## Exact-ID cleanup implemented

Test 4 removed the failed presentation from the registry before its provider
commit was processed. `AbortPresentationTransfer` then recognized an absent
registry record as stale and returned, leaving its bound input epoch alive.
That made later F9 callbacks yield rather than mint a fresh opening intent.

`ProviderPresentationInputCleanup.TryRevoke` is a small pure, production-used
operation with these rules:

- Match the terminal presentation ID exactly and validate it first.
- Clear only a matching bound epoch/ID and/or matching committed fallback ID.
- Return only the matching positive epoch for runtime cancellation. The host's
  existing epoch-specific cancellation cannot consume a newer pending arm/bind.
- Pending, unbound input is deliberately not part of this operation.
- No visibility, global input mode, input lease or registry mutation; no invented
  dismissal. Duplicate callbacks are idempotent; newer identities are preserved.

Script invokes it on exact transfer aborts (even if the registry already removed
the record), cancelled pending transfers, and acknowledged host/menu dismissal.
It clears local matching state before notifying the runtime. Broad explicit
`CloseOverlay` behavior, normal commit and the existing close-release guard are
unchanged. Stale callbacks still return before changing global visibility/mode.

This repairs cleanup and enables a subsequent fresh opening request. It does
**not** fix Test 4's first missing desktop presentation or establish the cause of
the later GTA hang.

## Validation

Evidence root:
`<DIAGNOSTICS_DIR>\failed-presentation-cleanup-20260910`

- `red/before.trx`: the missing-cleanup wiring test failed before implementation.
- `managed/managed-final.trx`: **1,139/1,139 managed tests passed**, eighteen more
  than Test 4. Script Release build: zero warnings/errors.
- Direct tests execute the production cleanup helper plus the real host intent
  gate. Coverage includes already-removed registry state, stale callbacks after
  a new bind, a pending next epoch, invalid/case-different IDs, independent
  bound/fallback identities, duplicate hide/abort and invalid local epochs.
- A real registry integration test reproduces removal before the failed paint
  callback, revokes the old input, opens a fresh generation and proves the stale
  callback cannot remove or revoke the replacement. Source contracts verify the
  production call sites and absence of broad pending-input/visibility mutations.
- `desktop-probe`: **39 expected-outcome scenarios**, malformed-pixel parser
  checks and authenticated/rejected-helper checks passed. The full fixture exits
  cleanly; no game process or fixture is left running.

New Script SHA-256:
`ba7762220f7ad548181325b915175abfd953e1a4d6a4fbc9641a18ceb5c76db5`

Runtime and Preloader binary hashes remain Test 4's `061c490c...` and `5d08fec9...`.
No capture algorithm, deadline, fallback budget or acceptance threshold changed.
Existing frozen Test 4 tools/payload remain untouched. Repository deployment
scripts intentionally still pin the old candidate: no new install is authorized
by changing source alone and no new live candidate was qualified here.

## Capture audit and controlled replay

The Test 4 desktop-probe stall is at `BitBlt`; the later game hang has no
identified blocking stack. The probe's GDI call copies the narrow witness strip from the
desktop with layered windows included. The parent bounds the confirmed GDI
stage and starts DXGI only after the owned predecessor has exited, using the
unused part of the original budget. Actual mismatching pixels do not trigger a
retry. DXGI acquisition and mapped frames have release/unmap/dispose paths.
These properties bound probe handling; they are not proof that a game/graphics
hang cannot occur or that this code caused the game's later hang.

The fixture now replays all four exact failed Test 4 palettes, in addition to
the three previous palettes. Each Test 4 palette matched **8/8 through GDI and
8/8 through DXGI** on the same NVIDIA RTX 4070 Ti / DISPLAY3 HDR output, with the
same reported floating-point format, output color space and SDR-white scale 3.
The fixture uses an owned nonactivating window, not the GTA swap chain or the
exact in-game witness position. It does not reproduce GTA's rendering conditions.

This makes a basic palette-decoding failure less likely in the tested desktop
conditions; it does not rule out game-specific HDR/composition/capture behavior.
Five ordinary desktop witnesses, wrong/stale identity rejection and three
injected-GDI-timeout -> real-DXGI recoveries also passed. Measured two-backend and
startup hard-timeout scenarios took 908 ms and 906 ms; real fallback successes
took 658–687 ms. These are fixture observations, not timing guarantees under GTA.

## Remaining diagnostic gap before another live run

The frozen presentation-comparison controller collects lifecycle/module evidence
but does not arm a gameplay-hang dump. The older `Watch-Controller-Hang.ps1`
targets a pending WebView controller creation at fixed waves; after controller
completion, it does not trigger on a later gameplay freeze. Running that old
startup watcher unchanged would not cover Test 4's failure.

A subsequent game test needs a separately verified, bounded local hang capture
that remains active after menu/browser readiness, targets the exact game PID and
start identity, and captures before termination. A missing Script heartbeat alone
cannot be the trigger because pause menus can suspend script scheduling. Preserve
signed collector checks, local-only output and disk/capture limits; rehearse on
an owned unresponsive fixture before installing or asking for another game run.
No watcher or dump capture was armed in this turn. The missing-menu cause and
later GTA hang remain unresolved; offline success is not in-game acceptance.
