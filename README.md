<p align="center">
  <img src="web/public/ragewebui-logo.png" alt="REACTOR V" width="200" />
</p>

# REACTOR V

Build React/HTML menus and overlays for **GTA V Story Mode** on **Legacy and Enhanced**.
A shared runtime by **MinionEnjoyer**: each mod keeps ownership of its gameplay,
settings and saved data. No ALLIN1 Launcher or gameplay client required.

**0.2.5 — unsigned, edition-specific manual-download release.**
Use the package for your exact supported game version. **Not for GTA Online.**
See the release notes for validation coverage and known limits.

## What's new in 0.2.5

- Consolidates startup, desktop/DPI presentation, passive-HUD ownership, and menu/input work onto one mainline.
- Retains 0.2.4's late-adapter recovery, bounded bootstrap attachment, and fresh-frame readiness checks.
- Adds bounded native diagnostic tracing and preserves ALLIN1's generated catalog indexes during updates.
- Ships a release-matched diagnostic checker with installation identity, dependency probes, and older-release references.

[0.2.5 release notes](https://github.com/MinionEnjoyer/GTAV-REACTOR-V/releases/tag/v0.2.5) ·
[Consolidation validation and limits](docs/RELEASE-0.2.5.md) ·
[ReShade compatibility](docs/DXGI-COMPATIBILITY.md)

## Downloads

[Published builds](https://github.com/MinionEnjoyer/GTAV-REACTOR-V/releases) are on GitHub.
Choose **one** full runtime ZIP: **Legacy 1.0.3889.0** or **Enhanced 1.0.1158.13**.
The historical `live-test` filenames retain edition/version guards; they are not incremental patches.

Downloads are **unsigned**. Verify the matching SHA-256 checksum before installing.
For an existing modded installation, use the separate updater ZIP to preserve
consumer-owned menus, assets and settings. Game-hook dependencies are not bundled.
Follow the [installation guide](docs/GETTING-STARTED.md#install) before updating.

The optional diagnostic checker is a separate download. Extract it outside GTA;
it does not install dependencies, change antivirus settings, or upload reports.
Use the checker supplied with the release you are testing; newer checkers retain
older release references. See its bundled README for reference selection and limits.

## Documentation

- [Installation and configuration](docs/GETTING-STARTED.md) — requirements, controls, logs and safe updates.
- [Make a mod with Reactor](docs/EXTENSIONS.md) — managed extensions, starter examples and ownership.
- [Browser API](docs/API.md) — interface-to-game contracts.
- [Source setup](docs/GETTING-STARTED.md#build-and-verify-from-source) — building and package checks.
- [Architecture](docs/ARCHITECTURE.md) · [Graphics tests](docs/DIRECTX-HARNESS.md) · [Live acceptance](docs/LIVE-ACCEPTANCE.md).

[Support the project](https://buymeacoffee.com/minionenjoyer) ·
[License](LICENSE) · [Third-party notices](THIRD_PARTY_NOTICES.md)
