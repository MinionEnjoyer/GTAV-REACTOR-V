# Issue #1: reversible startup isolation

This diagnostic kit follows the reporter's `reporter diagnostic archive` attachment (<REPORTER_ATTACHMENT_ID>). The 19:14 logged session showed corrected 150% DPI captures and successful sampled rendering, then a GTA access-violation exit. Its PID differs from the attached 19:21 dump. That dump faults on an unresolvable execute address, with no reliable caller establishing component ownership. The previous DPI fix must not be presented as resolving this remaining crash.

## Controlled variants

| Variant | Temporarily disabled | Kept active | Required evidence |
| --- | --- | --- | --- |
| Managed providers off | Managed Reactor script and ALLIN1 DLL | Reactor native bootstrap/renderhook/scriptprobe/compositor, preloader, SHVDN and other scripts | Exact layout, native components observed, SHVDN reaches script loading, no Reactor/ALLIN1 activation in captured logs |
| Reactor native off | Three Reactor ASIs and native compositor DLL | Managed Reactor/ALLIN1; windowed renderer fallback | Exact layout, no disabled component observed, SHVDN observed, windowed startup and first managed tick exit |

Changing the renderer setting alone is insufficient: native ASIs can arm before the managed configuration is read. Native-off therefore physically parks all four specified native components. It selects the existing WebView2 window fallback and restores the exact original JSON bytes afterward. This tests a different presentation route, so the result narrows a path, not an individual function or culpable library.

If managed-off works and native-off fails, managed startup or its interactions becomes the next lead. If managed-off fails and native-off works, the early native path or interactions becomes the next lead. Both failing leaves common/external paths possible. Both working suggests timing/interaction or intermittency; it does not prove either path safe. Only compare runs whose isolation evidence is sufficient, and do not treat one successful launch as a fix.

## Implementation

`tools/Issue1Isolation` is a separate net48 x64 WinForms helper. This step does not change production Reactor binaries. It requires the four exact issue1 diagnostic-v1 binary hashes and GTA Enhanced 1.0.1158.13. Backups/receipts live outside GTA, SHA-256 checked before applying/restoring. Duplicate components, unsafe paths, changed originals and corrupted backups fail closed. No arbitrary script from an uploaded archive is executed.

The GUI watches one user-launched game PID. It samples module names, records its exit, checks the prepared file layout, and exports bounded per-session evidence only when the user saves a ZIP. Shared logs are baseline-differenced; Reactor session logs are filtered by PID linkage and session time. No browser profile, configuration, backup binary, or dump is exported. Preloader shutdown can still be unwinding when the game exit is observed, so the last preloader lines may not be included. Periodic module snapshots cannot prove a module was never briefly loaded.

Offline qualification includes synthetic prepare/restore and negative-evidence tests, a D:-to-C: backup/restore roundtrip, a rendered GUI layout check, and the real managed loader/windowed renderer running in a secondary AppDomain from a disposable copy without the native compositor. The loader logs CEF deferral to `reactorv-bootstrap.log`; WebView2 initialization logs to `reactorv-runtime.log`. The probe checks both, rather than incorrectly expecting both stages in the runtime log.

No game launch, live-game file installation, GitHub publication, or issue reply is part of this work. The helper's live process observer and the two in-game modes still require local acceptance. Current source remains separate from the previously staged production diagnostic changes.

## Proposed issue reply — draft, not posted

Thanks, the ZIP helped. The DPI capture fix is working in the logged session, but GTA still exits with an access violation. The dump is from a different launch, so I don't want to claim it identifies the crashing component.

I've prepared a reversible helper to compare two startup paths: first with managed Reactor/ALLIN1 disabled, then with Reactor's early native components disabled while the managed side uses its windowed fallback. It backs up the changed files outside GTA, restores the originals, and saves a separate result ZIP for each observed launch. You won't need to run more PowerShell commands.

Please use the included README, restore between tests, and leave the other mods/settings unchanged. Missing Reactor/ALLIN1 menus in the first test are expected. Tell me whether each test reached Story Mode and attach the two result ZIPs after reviewing them. This is still an isolation test, not a confirmed fix.

**Maintainer gate:** perform local in-game acceptance before sending the kit, and approve the wording before posting.
