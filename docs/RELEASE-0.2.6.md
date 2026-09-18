# Reactor V 0.2.6 — GTA V Enhanced TU 1.73 compatibility

This release carries Reactor V's complete Legacy and Enhanced runtime forward
after the September 15, 2026 GTA V update. It does not remove consumer features
or substitute a reduced desktop-only renderer.

## Changes

- Adds an exact Steam Enhanced TU 1.73 profile for `GTA5_Enhanced.exe`
  `1.0.1158.16`.
- Keeps the current Legacy `1.0.3889.0` package and installer profile intact.
- Retains the prior Enhanced `1.0.1158.13` installer profile so an older package
  can still be reinstalled only on its matching executable.
- Records a stable game-build ID in edition markers and diagnostic references.
- Explicitly rejects `GTA5_Enhanced_BE.exe` as a Story Mode bootstrap or render
  host. Reactor V does not disable, alter, or bypass BattlEye.
- Updates the release-matched diagnostic checker and installer bundle for 0.2.6.

## Compatibility boundary

The Enhanced archive is qualified against this Steam executable identity:

- Version: `1.0.1158.16`
- SHA-256: `69DA07FF67D05E9DED11289E597E8B8DC5855B0A429C085F37D148DC267CB2C5`

Epic and Rockstar Launcher executables are not assumed equivalent from version
text alone. Unknown executable hashes fail before installation changes begin.
Use Reactor V only in Story Mode with the matching edition package.

The GTA update also replaced game archives. A pre-update
`mods/update/update.rpf` can crash Story Mode before ScriptHookVDotNet or ALLIN1
scripts initialize; that is separate from Reactor V. Refresh the modded archive
from the current game base and reapply compatible mod entries rather than
carrying the old archive forward.

## Validation

The release gate covers native host policy, managed runtime contracts, web UI,
edition-specific package contents, installer identity checks, and diagnostic
manifest generation. Local TU 1.73 testing confirmed that the guarded Enhanced
runtime reaches Story Mode and initializes its consumers when the mounted game
archives also match the title update.

Known limitations and longer-term runtime-hardening work are documented in
[GTA-1.73-COMPATIBILITY.md](GTA-1.73-COMPATIBILITY.md) and
[FUTURE-RUNTIME-HARDENING.md](FUTURE-RUNTIME-HARDENING.md).

## Release artifacts

Final SHA-256 values are published beside each GitHub release asset:

- Enhanced: `23ab2e454775c4333219cc03c8b4050f4420a47a908a63e5331f286265370421`
- Legacy: `f4655cbe1b563eb497d69baa7aec7d3a15cceff09e58e7f4437892316a4e3099`
- Installer: `80b758b4bc5d4c2c9334830df11ba0b4d314f5293d1e50239bbcad281cbac1c8`
- Diagnostics: `91a4a2e633c9b17d9853ba344a141687ef64c12c4d98e3eaea7e7958b19cd7f7`
