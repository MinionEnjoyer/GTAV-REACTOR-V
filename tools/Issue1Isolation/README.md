# Reactor V — issue #1 isolation test

This is a case-specific diagnostic helper, **not a release or a confirmed crash fix**. It separates Reactor's early native components from its managed Reactor/ALLIN1 startup. It does not disable unrelated mods or prove which component caused the original crash.

## Before starting

- Windows x64, .NET Framework 4.8, GTA V Enhanced **1.0.1158.13**, and your existing Reactor V 0.2.2 installation are required.
- Extract this kit outside the GTA folder. Keep its EXE, EXE.config and Newtonsoft.Json.dll together.
- The nested `ReactorV-0.2.2-issue1-diagnostic-v1-patch.zip` is the same diagnostic patch already supplied for this issue, not a newer fix. If you restored the release files after the previous test, back up those release files outside GTA and reapply the patch's `plugins` and `scripts` folders first. It replaces exactly four binaries and preserves your configuration. Read the patch's own README.
- Close GTA and `ReactorV.Preloader.exe` before preparing or restoring. The helper checks the diagnostic binary hashes and will refuse a different build. Do not bypass a failed check.
- Keep the same launch route and other mods/settings as in the failing Story Mode test. Do not use Install/Repair, update files, or change the configuration during a prepared test. This helper does not launch GTA, alter launch/anti-cheat settings, or support GTA Online.

## Run the two tests

1. Open `ReactorV.Issue1Isolation.exe` and **Choose GTA folder** (the folder containing `GTA5_Enhanced.exe`). Start with **1 · Managed providers OFF** and confirm the listed changes. This temporarily parks `RageWebUI.Script.dll` and the single `ALLIN1.dll` outside GTA. Reactor's native components, preloader, ScriptHookVDotNet, and other scripts remain enabled. Missing Reactor/ALLIN1 menus are expected.
2. Keep the helper open. Launch one Story Mode session using your usual route. Note whether Story Mode loads or it crashes. If it loads, check briefly at the same point that previously failed, then exit GTA normally. Let the helper report that the observed process exited.
3. Choose your observed outcome, click **Save result ZIP**, then **Restore originals** once GTA and the preloader are closed. Keep the ZIP even if it says **INCONCLUSIVE / changed**; this is useful evidence, not a passing test.
4. Repeat with **2 · Reactor native OFF**, using a fresh launch. This parks the three Reactor ASIs and `RageWebUI.Native.dll`, leaves managed Reactor/ALLIN1 enabled, and temporarily selects the `windowed` renderer in `ReactorV.json`. It tests the existing WebView2 window fallback; presentation may differ from the normal overlay. Save this second result ZIP and **Restore originals** again.
5. Review the two result ZIPs before sharing them in the issue. Please say which test reached Story Mode and which crashed. Do not combine either result with a dump from a different launch.

There is no automatic launch, upload, repair, or restore when closing the helper. Restoring returns to the exact pre-test **diagnostic** files/configuration; it does not uninstall the diagnostic patch or restore the earlier release backup.

## Backups and recovery

Verified backups and a restore receipt remain under `%LOCALAPPDATA%\ReactorV-Issue1-Isolation`, outside the game. The helper displays the individual backup directory. Do not move/delete it during testing. Tests preserve original configuration bytes and unrelated files.

If the helper closes unexpectedly, close GTA/preloader, reopen the helper, choose the same GTA folder, and click **Restore originals**. A closed helper cannot reconstruct that launch's capture; restore and prepare a fresh test. If a file was changed or repaired during testing, restore refuses to overwrite it. Keep the backups and report the error instead of deleting files or using Install/Repair to clear it.

Duplicate/nonstandard Reactor components and junction/symlink paths are deliberately rejected. Stale native backup copies inside GTA can also trigger this check. Report the exact message rather than bypassing validation.

## What the result contains

Each ZIP contains one observed process's summary, module names, relative installed-file SHA-256 identities, the selected mode/outcome, and bounded relevant log excerpts. Reactor session logs are associated by process identifiers and session time. Shared ScriptHook logs are collected as changed bytes from this test's baseline. A maximum of 8 MiB per source log is read.

No memory dump, configuration, backed-up binaries, browser profile, or environment-variable list is included. Logs can still contain usernames, local paths, or mod details; review before sharing. Memory dumps are not collected automatically.

Evidence checks distinguish a confirmed isolation setup from an inconclusive run. Module sampling can miss very short-lived loads, and a process exit code cannot prove Story Mode loaded. A successful isolation test narrows the investigation; it is not proof of the root cause or general compatibility.

## Verification scope

The helper has offline fixture tests for preparation, byte-exact restoration, interrupted preparation, changed/corrupt backups, duplicate components, unsafe paths, log slicing, process-ID matching, and isolation evidence rules. Cross-volume parking/restoration is tested separately. The actual managed loader and WebView2 window fallback are tested in a secondary AppDomain with the native compositor physically absent from a disposable runtime copy.

These checks do **not** constitute an in-game test of either isolation mode. No GTA launch is part of the offline validation.

Developer commands (not needed by the reporter):

```powershell
dotnet build tools/Issue1Isolation/Issue1Isolation.csproj -c Release
# Use a new output path for each invocation:
ReactorV.Issue1Isolation.exe --self-test <new-report.json>
ReactorV.Issue1Isolation.exe --windowed-probe <installed-plugins-ReactorV-folder> <new-output-directory>
ReactorV.Issue1Isolation.exe --render-ui <new-preview.png>
```
