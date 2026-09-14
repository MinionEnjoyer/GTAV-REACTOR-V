# Local experiment preservation — 2026-09-13

Historical note: this document describes the preservation branch at creation.
The subsequent reviewed integration is documented in [0.2.5 consolidation](RELEASE-0.2.5.md).

This branch preserves the local development tree based on `64b9816` (0.2.2).
It is a **work-in-progress archive**, not a runtime release or a replacement for
`main` / `v0.2.4` (`e94ae46`). The original working directory remains untouched.

## Relationship to the release

- 0.2.4 already includes the app-local DXGI/ReShade compatibility work, deferred
  adapter discovery, bounded bootstrap attachment and startup surface hardening.
- This snapshot additionally contains experimental desktop/DPI window promotion,
  passive-HUD lease/paint/input changes, close/dispatch behavior, and native
  callback tracing. Do not overlay this older tree wholesale onto 0.2.4.
- Future work should start from the current release and port small, reviewed
  changes with their tests. The local diagnostic infrastructure and forensic
  documents are supporting evidence, not claims that the reported GTA crash or
  Los Santos Customs color issue is fixed.

## Verification at preservation

The unchanged experimental managed suite passed **1,168 tests** (zero failed or
skipped), and the web suite passed **261 tests in 33 files**. These are offline
regressions, not acceptance of all experimental paths in GTA. Private logs, dump
files, builds, installed consumer assets, and local install backups are excluded
from this source snapshot.

The local deployment baseline chosen after comparison is the exact published
0.2.4 Enhanced package, preserving consumer UI/settings with the release updater.
The separate portable diagnostics tool is preserved on `codex/diagnostic-tool`.

## Publication copy hygiene

This publication copy redacts private profile/evidence paths from forensic docs
and normalizes developer-profile paths in portable scripts. The original local
source and its private evidence remain retained locally and unchanged.
