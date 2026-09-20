# Reactor V 0.2.7 — installer-only ALLIN1 catalogue preservation

## Scope

0.2.7 is an updater-kit-only release. It packages the corrected updater and
ownership module; it does not publish replacement Enhanced or Legacy runtime
archives, native DLLs, render hooks, browser content, or diagnostics.

The kit preserves ALLIN1 preview artwork and only these catalogue index paths:

- `default-weapons/index.json`, `default-vehicles/index.json`,
  `default-gear/index.json`
- `generated-weapons/index.json`, `generated-vehicles/index.json`,
  `generated-gear/index.json`

All other non-PNG files beneath the protected ALLIN1 artwork root remain
rejected. Index files remain bounded to 512 KiB and reparse points remain
rejected.

## Runtime compatibility remains 0.2.6

Use this updater only with the previously qualified 0.2.6 edition archives and
their published SHA-256 sidecars. The updater continues to require exact
executable identity before any game-file change:

- Enhanced: `GTA5_Enhanced.exe` 1.0.1158.16,
  `69DA07FF67D05E9DED11289E597E8B8DC5855B0A429C085F37D148DC267CB2C5`
- Legacy: `GTA5.exe` 1.0.3889.0,
  `677E4E355CFBDB13273B1D992407E3C261B3A108DC4DD5C8A0C4C1DA651802E5`

This release does not broaden storefront support, alter native game gates, or
bypass BattlEye. It makes no new live-game qualification claim.

## Required publication assets

Publish only `ReactorV-0.2.7-installer.zip` and its SHA-256 sidecar. Do not
relabel, re-upload, or claim new hashes for the 0.2.6 runtime or diagnostics
assets. Record the exact installer hash after building from the reviewed commit.
