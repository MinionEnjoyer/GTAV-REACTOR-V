# Reactor V Diagnostics

A portable, local-only diagnostic for GTA V Story Mode and bundled Reactor V
release references. It automatically selects a release only when every bounded
identity anchor matches; otherwise it reports unrecognized/build-drift rather
than treating an arbitrary DLL version as a pass.
It collects evidence; it does not automatically diagnose a corrupted execution
pointer, repair your game, install dependencies, disable antivirus, or upload files.

## Before starting

Extract the entire ZIP outside GTA, keeping the EXE, DLL, and `manifests` folder
together. Double-click **ReactorV.Diagnostics.exe**. Windows x64 and .NET Framework
4.8 or later are required to run the diagnostic itself. No developer SDK, compiler,
PowerShell execution-policy change, administrator rights, or installation is required.
This build is unsigned; do not bypass a Windows security warning you do not understand.

Choose the actual game folder and correct edition. Reports must be outside the
game. Do not use GTA Online. The diagnostic does not launch GTA or select Story
Mode for you. Close GTA and any launcher doing installation/repair before isolation.

## Recommended order

1. **Check + dependencies**. Compares installed Reactor package files with hashes
   generated from the exact bundled edition ZIP. Select a specific bundled
   release when comparing an intentionally drifted installation. A modified JSON/config
   file may be intentional. Contextual ASI/graphics-proxy files are observations,
   not assertions of conflict. Required native-library load tests run in a
   time-limited helper, not inside GTA or the collector.
2. **Record next launch**. Close GTA, click Record, and wait for **Armed** before
   launching Story Mode yourself. The default wait is 120 seconds and recording
   is 180 seconds. Increase recording time if loading takes longer. Stop cancels
   collection; it does not close GTA. PID, process start time, module samples,
   window/DPI observations, exit code and new log content are kept together.
3. **Only when asked to compare native on/off:** Preview native-off, inspect the
   actions, then Apply native-off and confirm. Record another run, then **Restore
   originals** using the ORIGINAL `isolation-state.json`. Repeat the native-on
   run to distinguish a reproducible difference from a lucky launch.

Native-off moves ONLY these four files into an external backup:

- `ReactorV.RenderHook.asi`
- `ReactorV.Bootstrap.asi`
- `ReactorV.ScriptProbe.asi`
- `plugins/ReactorV/RageWebUI.Native.dll`

Expect missing or degraded Reactor UI in native-off mode. This tests crash
behavior, not menu acceptance. On-disk absence and sampled module-name absence
have separate limits; neither proves a renamed/manually mapped module never ran.
Keep the original isolation run folder until restoration is complete. Changed
destination files are never overwritten. Interrupted moves can be restored from
the journal. Do not run repair/reinstall or start GTA during file moves; the
diagnostic checks repeatedly but cannot lock out an unrelated launcher.

## Dependency checks

The report distinguishes missing/mismatched files, unknown or blocked checks,
and actual isolated load results. It checks x64/.NET Framework, native PE
architecture and imports, MSVC/UCRT resolution, CEF package integrity and native
loading, and WebView2 runtime availability through the verified Microsoft loader.
ScriptHookV, ScriptHookVDotNet and common ASI-loader prerequisites are inspected
without executing third-party plugins. Their versions/presence are evidence,
not a claim that an arbitrary edition or game build is supported. WebView2
preview-channel availability is distinguished from production Evergreen evidence.
Having the Edge browser installed alone is not WebView2 runtime proof. A native
load test does not create a full browser, exercise the GPU in GTA, or prove
compatibility with every mod. Modified/unlisted native sidecars can make the load
probe refuse to run rather than execute an unknown dependency.

Optional Windows event collection is limited to relevant GTA/Reactor events
from the preceding 24 hours in preflight, with a separate exact-session window
when recording. Missing permission/channel data is reported as
unavailable, not clean. No matching events does not rule out antivirus interference.

## Optional crash dumps

Default: **none**. If requested, obtain Microsoft Sysinternals ProcDump yourself,
review/accept its license yourself, select `procdump64.exe`, and explicitly consent
to `mini` or `full`. The diagnostic verifies the Microsoft signature and refuses
to interfere with an existing ProcDump collector. It does not install a global
debugger or enable Windows crash-dump settings.

Dump attachment happens after the exact game process is detected, so a very early
crash can precede attachment. The report must be checked for collector status;
starting a process does not guarantee a debugger successfully attached. The
collector is stopped gracefully, never force-killed while attached. If another
collector appears or detachment cannot be confirmed, the report tells you rather
than killing GTA or another collector. Full dumps can consume many GB and all
dump types may contain private data. Never post them publicly.

## What to share

Each run creates **READ-FIRST.txt**, **report.json**, and
**Review-before-sharing.zip** in a new folder. Review the ZIP yourself before
attaching it to the issue. It includes redacted text diagnostics only; it excludes
binary backups, restore journals, and `private-dumps`. Path redaction is best-effort
and is not a guarantee that arbitrary log text contains no personal data.

The original run folder can contain private paths and optional dump memory. Do
not share that entire folder. No automatic uploads or background monitoring occur.

## CLI / scriptable operations

Run `ReactorV.Diagnostics.exe --help` for complete options. The GUI uses the same
`DiagnosticRunner` and service API as the command line; reports use versioned JSON.

```powershell
.\ReactorV.Diagnostics.exe check --game 'D:\Games\GTA Enhanced'
.\ReactorV.Diagnostics.exe record --game 'D:\Games\GTA Enhanced' --wait 120 --seconds 300
.\ReactorV.Diagnostics.exe isolate-preview --game 'D:\Games\GTA Enhanced'
.\ReactorV.Diagnostics.exe isolate --game 'D:\Games\GTA Enhanced' --consent
.\ReactorV.Diagnostics.exe restore --game 'D:\Games\GTA Enhanced' --state 'C:\Reports\ORIGINAL-RUN\isolation-state.json' --consent
```

Exit 0 means collection completed, **not** that the game is healthy. Exit 2 means
a blocked/error result and 130 means cancellation. A capture can complete with a
timeout or an unconfirmed dump attachment; read its outcome fields.

## Developer verification and packaging

`build.ps1 -OutputDirectory <fresh directory>` builds the .NET Framework program,
builds the synthetic process fixture, runs the linked-source .NET 8 regression
suite, and creates a ZIP plus SHA256 manifest. Requires the .NET 8 SDK on the
developer machine only. Fixtures and test binaries are never packaged.

`prepare-release-diagnostics.ps1` is the mandatory per-release gate. It takes
the final Enhanced and Legacy ZIPs plus their explicit SHA-256 sidecars, rejects
wrong names, hashes, unsafe entries and unscoped payloads, then generates the
new release references and builds/tests the checker package. It retains older
references in `manifests/release-index.json`; it never reads a local modified
game as a release reference. Use a developer Python 3 executable explicitly if
`python` is not on PATH:

```powershell
.\tools\prepare-release-diagnostics.ps1 -ReleaseVersion 0.2.6 `
  -EnhancedArchive <final-enhanced.zip> -EnhancedSidecar <final-enhanced.zip.sha256> `
  -LegacyArchive <final-legacy.zip> -LegacySidecar <final-legacy.zip.sha256> `
  -PythonPath <python.exe> -OutputDirectory <fresh-output-directory>
```

Publish the resulting `ReactorV-Diagnostics-<release>.zip` and its sidecar with
the matching runtime release. No runtime/release binary or installer is changed.

References: [Microsoft ProcDump](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump),
[WebView2 distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution),
[.NET Framework version detection](https://learn.microsoft.com/en-us/dotnet/framework/install/how-to-determine-which-versions-are-installed).
