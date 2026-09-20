# Reactor V runtime updater

Close GTA before updating. Download the matching edition runtime ZIP and its
SHA-256 file from the same Reactor V runtime release. This kit contains only the
updater; it does not include the runtime or third-party game-hook dependencies.

The 0.2.7 updater-only kit is intentionally compatible with the already
qualified 0.2.6 Enhanced and Legacy runtime archives. It fixes preservation of
ALLIN1's `default-*` and `generated-*` preview catalogue indexes; it does not
replace, rebuild, or broaden the supported native runtime. Continue to verify
the runtime archive checksum published with 0.2.6.

Check the downloaded runtime hash with `Get-FileHash -Algorithm SHA256`. Keep this
kit in a writable directory outside the game folder and run, for example:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\install-live-test-package.ps1 -Edition Enhanced -Archive 'C:\Downloads\ReactorV-0.2.6-enhanced-live-test.zip' -GameRoot 'C:\Games\GTAV Enhanced'
```

Use `-Edition Legacy` and the matching Legacy archive/game path for Legacy.
The execution-policy override applies to this process only; review scripts before
running them. The updater checks the supported executable identity and package
marker, backs up the replaced runtime, and preserves consumer UI compositions,
extension assets and settings. Backups and install reports remain in this kit's
`artifacts` directory. Do not discard them until you have tested the update.

The historical `live-test` archive names retain native edition/version guards.
They do not bypass those guards or imply support for GTA Online. This updater
does not certify live compatibility for every installed consumer mod.
