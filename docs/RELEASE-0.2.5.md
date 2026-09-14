# Reactor V 0.2.5 consolidation

This release consolidates the preserved local work, portable diagnostic checker,
and catalog-index installer fix onto the 0.2.4 mainline. The original experimental
checkout remains available locally; its old 0.2.2 version labels are not the
version of the integrated release.

## Integrated areas

- Bounded bootstrap attachment, deferred adapter discovery, surface generations,
  browser creation ownership, and the established app-local DXGI compatibility.
- Desktop/DPI/HDR capture and verified window presentation, passive-HUD ownership
  and paint readiness, and close/cancellation/input ownership handling.
- Sampled native callback diagnostics with bounded storage and off-callback log
  draining; diagnostic collection must not block rendering callbacks.
- Consumer-safe updater support for ALLIN1's three generated catalog indexes.
- A release-matched, local-only diagnostic checker. Reference manifests are
  generated from the exact edition archives and verified checksums, retaining
  historical release references instead of assuming every install is current.

## Acceptance boundary

Release qualification covers automated native/managed/web regressions, real
D3D11/D3D12 test surfaces, browser/host lifecycle fixtures, package identity,
consumer-file preservation, and the checker. Package success is not proof of
compatibility with every game mod or graphics configuration.

There is no new GTA gameplay acceptance for the integrated 0.2.5 binaries during
this consolidation. In particular, the original issue #1 crash, Los Santos
Customs tint, passive HUD before opening a menu, and every pause-menu interaction
must not be described as conclusively fixed by offline test results. Earlier
live-session documents refer to their own explicitly identified builds.

Final source identity, automated test totals, package hashes, and any qualification
limitations are recorded in the 0.2.5 GitHub release notes.
