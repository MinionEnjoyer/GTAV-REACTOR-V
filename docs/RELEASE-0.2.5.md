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

Release qualification covered 36 native tests, 1,170 managed tests, 261 web
tests, real D3D11/D3D12 test surfaces, browser/host lifecycle fixtures,
consumer-file preservation, and 61 diagnostic-checker tests. Enhanced completed
all 19 package suites. Legacy passed the same pre-package regressions, but its
long combined run was interrupted twice by foreground-window timing in the
synthetic bootstrap fixture. The same unchanged packaged bootstrap scenario
passed in isolation; the exact already-tested Legacy staging tree was then
finalized and its package/checker identity verified without repeating the long
suite. This is a qualification limitation, not evidence of GTA compatibility.

There is no new GTA gameplay acceptance for the integrated 0.2.5 binaries during
this consolidation. In particular, the original issue #1 crash, Los Santos
Customs tint, passive HUD before opening a menu, and every pause-menu interaction
must not be described as conclusively fixed by offline test results. Earlier
live-session documents refer to their own explicitly identified builds.

## Release evidence

Runtime binaries were built from `9b04d8ed72e78e3b01f32f0afd0c35dfb5fd9bf2`.
The release tag additionally contains generated checker references, diagnostic
packaging privacy hardening, and this documentation.

- Enhanced runtime: `caafddbfe2eb5158209870c39372f830af58ecbbc94b41aa21c313530d654eb0`
- Legacy runtime: `7d0195fa8b9f9e26e07b3718545fd13f549aac758bcc8dc2364a54911ed0ee49`
- Installer: `8cea469a77c1954ce9df05d8e1d7e3c45aad87762bd4af02036eb202e8d3b3cd`
- Diagnostics: `dc68f0ff4569a40dac1a6c36dfc1470017b62f3eeb6d5932b389fb2f2a0d139c`

The packaged checker recognized both 0.2.5 archives exactly (74 Enhanced and
75 Legacy files), completed both isolated dependency probes with exit code 0,
rejected a modified identity anchor, and reported an explicit 0.2.4 comparison
as a mismatch. No GTA process was launched by these package checks.
