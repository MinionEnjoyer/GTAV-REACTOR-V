# Default-owner close intent regression — 2026-09-10

Test 3 proved a state-ordering defect, not the cause of GTA's stuck pause-menu
Back state. See [the retained live review](LAYERED-INPUT-TEST3-LIVE-REVIEW.md).

ALLIN1's physical F9 poll closed GBay first. Reactor's managed F9 event arrived
nine milliseconds later, after presentation state was cleared but while the
input lease was still Disarming. It armed a new unbound opening epoch. Escape
then called `CloseOverlay` on that epoch even though no menu was requested.
The host was already hidden and the lease subsequently Hidden. A managed
KeyDown callback does not prove Windows/GTA frontend key delivery or suppression.

## Bounded correction

- Default-owner managed F9 yields during the existing Arming, Interactive and
  Disarming lease phases, including when presentation/intent state is cleared.
  There is no new cooldown or timestamp-guessed close token. A fresh accepted
  typed owner dispatch retains its existing physically-held-key authority.
- Unbound intents expire on Tick, before KeyDown routing and before binding.
  This retains the existing inclusive deadline: only `now > deadline` expires
  a valid deadline. A bound presentation is not expired by this unbound guard.
- Escape with only an unbound intent cancels its authority without changing
  visibility or inventing a dismissal. Bound/fallback and genuinely pending
  presentation close paths are retained. Normal Escape/Back routing is retained
  when no intent exists. This is not an OS Escape suppression/forwarding change.
- Escape routing telemetry is capped at four entries per second and explicitly
  identifies its evidence as managed KeyDown, not native frontend delivery.
  It includes lease, epochs, visibility, mode, observed pause state and game time.

No force-enable-all-controls, forced pause-menu close, native key injection,
host input policy change, deadline relaxation or new retry budget is included.

## Regression evidence

Evidence root:
`<DIAGNOSTICS_DIR>\close-intent-regression-20260910`

The two initial source-contract reproductions failed before the correction
(`red/before.trx`). After the change, **1,121/1,121 managed tests passed**
(`managed/managed.trx`), and the Release Script build completed with zero
warnings/errors. Twenty-three added test cases cover the recorded event order,
all lease phases, both close orderings, held-key repeats, legitimate reopen,
unchanged dispatch/generic toggles, deadline boundaries and long scheduling
gaps, all intent/presentation Escape combinations, and source wiring that keeps
empty-intent cancellation/expiry away from visibility/dismissal mutations.

These include deterministic policy/gate replays and source-contract checks;
they do not execute GTA's frontend or prove the in-game pause regression fixed.

Candidate Script SHA-256:
`e10bf71f9415eff630c11c82f0f6281c0bd915681bc78f361e3fadd9722d6d25`

Runtime, Preloader and Core outputs match Test 3 byte-for-byte. Earlier native,
browser and actual cross-process input evidence applies only to those unchanged
components. A fresh package must still verify deployment, payload identity and
external-host handoff before live use. Test 3 is consumed and remains untouched.

## Next live checks

Test GTA Escape/Back before any GBay interaction, then repeat immediately after
F9 open and F9 close. Record a failure before using the alternate-tab workaround.
Check GBay reopen and, if a vehicle is available, HUD before/after GBay. Native-off
isolation intentionally has no startup splash. Keep the collector running until
its shutdown watch finishes and restoration is verified. Treat functional
reports and technical capture qualification as separate results.
