# Reactor V 0.2.8

## Scope

0.2.8 promotes the integrated runtime recovery, bootstrap transport, overlay
lifetime, and presentation-safety work from the reviewed development source.
It is a runtime release, not an installer-only update.

## Compatibility boundary

Enhanced remains restricted to the exact supported Steam TU 1.73 executable
identity and Legacy remains restricted to its exact supported executable
identity. The release does not broaden storefront support, disable or bypass
BattlEye, or claim qualification for unknown game builds.

## Publication gate

Publish only artifacts produced by the full Release build and matching
diagnostic preparation. The build must complete native, web, managed, harness,
and edition-package validation without skipped gates. Publish both edition
archives, their SHA-256 sidecars, the installer kit, and the matching
diagnostics package only after their hashes are recorded from the final build.
