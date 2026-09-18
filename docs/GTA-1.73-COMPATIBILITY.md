# GTA V Enhanced Title Update 1.73 compatibility

Reactor V 0.2.6 is the compatibility release for the September 15, 2026 GTA V
Enhanced update. It carries the full Legacy and Enhanced feature set forward;
it does not replace the renderer with a reduced or desktop-only mode.

## Qualified executable identities

| Profile | Executable version | SHA-256 |
| --- | --- | --- |
| Current Steam Enhanced TU 1.73 | `1.0.1158.16` | `69DA07FF67D05E9DED11289E597E8B8DC5855B0A429C085F37D148DC267CB2C5` |
| Previous Steam Enhanced | `1.0.1158.13` | `0C52864D4521D9C9D441348AA1156958792DDE8825D0297C851753F167336401` |
| Current Steam Legacy | `1.0.3889.0` | `677E4E355CFBDB13273B1D992407E3C261B3A108DC4DD5C8A0C4C1DA651802E5` |

The 0.2.6 Enhanced package targets the current TU 1.73 identity. The installer
recognizes the previous Enhanced package/profile so an older, exact package can
still be reinstalled on its matching game build. It never installs a package
whose marker version or hash differs from the detected executable.

These hashes qualify the Steam executables captured for this release. Epic and
Rockstar Launcher executables must be recorded and tested separately; matching
version text alone is not treated as proof that their binaries are identical.
Unknown hashes fail before extraction or game-directory changes.

## Update scope

The Steam update changed `GTA5_Enhanced.exe`, `GTA5_Enhanced_BE.exe`,
`rpf.cache`, `title.rgl`, `update/update.rpf`, `update/update2.rpf`, selected DLC
archives, and a radio archive. A changed game executable invalidates Reactor's
old release identity even when Rockstar describes the title update only as
general fixes.

Reactor's native process policy remains independent from the package profile:
only exact `GTA5.exe` and `GTA5_Enhanced.exe` names are supported. The
`GTA5_Enhanced_BE.exe` host is explicitly rejected, and Enhanced still requires
the exact `-nobattleye` Story Mode launch argument. The installer and runtime do
not disable, alter, or bypass BattlEye.

## Acceptance before release

Static and desktop checks:

- Native policy tests pass, including BattlEye-host rejection.
- Managed source-contract tests pass for current and previous build profiles.
- The 0.2.6 marker names the TU 1.73 build ID, version, and exact hash.
- Installer preflight accepts the local TU 1.73 executable and remains
  fail-closed for an altered or unknown executable.
- Dependency and package checks complete without changing consumer-owned ALLIN1
  UI assets, settings, or extension catalogs.

Live Story Mode checks:

- GTA reaches Story Mode without a Reactor-related crash.
- GBay opens on first request, closes, and reopens without flicker or input loss.
- Passive speedometer and Chop surfaces initialize and recover normally.
- Pause-menu controls still close and restore game input after Reactor menus.
- Los Santos Customs does not produce a persistent blue tint.
- Shutdown logs contain no native callback lifetime, renderer teardown, or
  browser-host failure.

Legacy behavior is unchanged by this title update unless a future Legacy
executable update produces a new version or hash.
