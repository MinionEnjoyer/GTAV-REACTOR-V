# Release checklist

Every runtime release ships a matching diagnostic checker. Do not reuse the
previous checker ZIP with a new runtime: its reference hashes describe different
binaries. Retain older reference manifests so the new checker can identify older
installations without calling them corrupt merely because they are not current.

## Installer-only releases

An installer-only release is permitted only when it changes no runtime archive,
native binary, managed runtime assembly, browser content, or diagnostic checker.
State that boundary prominently, retain the exact existing runtime archive names
and executable SHA-256 gates, and publish only the versioned installer ZIP and
its checksum. Do not relabel old runtime or diagnostic assets as a new runtime
release. Run both `tools/test-install-ownership.ps1` and
`tools/test-installer-package.ps1` before publishing.

1. Consolidate and review changes on main. Update runtime, native resource,
   contract, web package, checker, and active documentation versions together.
2. Commit the source before building. Run `build-package.ps1` separately for
   Enhanced and Legacy with the official external ScriptHookV SDK and all Release
   gates enabled. Do not run these builds concurrently: they share staging.
3. Run `tools/prepare-release-diagnostics.ps1` with the release version, both
   final runtime archives and their SHA-256 sidecars, and a new output directory.
   This generates release references and builds/tests the same-version checker.
   Both editions must be present. Do not manually invent reference hashes.
4. Test automatic reference selection, an explicit older release, a mismatched
   installation, dependency probing, and the checker GUI. Run installer ownership
   regression tests and package the installer with its README and ownership module.
5. Commit generated references and release evidence. Record the exact runtime
   build commit if the final tag additionally contains generated manifests or
   documentation. Never claim those runtime binaries were built from another SHA.
6. Verify the authorized publishing account and remote. Push main, tag the
   reviewed version, and publish both runtime ZIPs, the installer ZIP, and
   `ReactorV-Diagnostics-<version>.zip`, each with its SHA-256 sidecar. Verify all
   uploaded asset hashes against the local files before completing publication.

Keep the historical edition-specific `live-test` archive names while the
installer contract uses them. These are full packages with executable guards.
Describe offline qualification separately from actual GTA gameplay acceptance;
passing a harness does not resolve a user's crash by itself.

The diagnostic ZIP is optional for end users and must be extracted outside GTA.
It is not a runtime dependency, automatic repair, security bypass, or upload tool.
