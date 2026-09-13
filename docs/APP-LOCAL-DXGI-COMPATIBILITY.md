# App-local DXGI / ReShade startup compatibility

Status: implementation and offline qualification complete; **initial local GTA
Enhanced test reported working on 2026-09-10 and corroborated by logs**. See
[live review](DXGI-COMPATIBILITY-LIVE-REVIEW.md) for evidence and remaining limits.

## Why this change

In the blue-tint game session the early Reactor render hook and native DLL were
loaded, but the game-root CoreFX-modified ReShade `dxgi.dll` was not. Disabling
only `ReactorV.RenderHook.asi` allowed ReShade to initialize and removed the tint,
but also removed the native adapter/renderer path; the older installed desktop
fallback then delayed/flickered and did not show the passive HUD. Those two
observations do not by themselves prove every symptom has the same cause.

The new Windows-loader fixture reproduces the specific startup bypass: with
Reactor's native dependency flags and Windows-format absolute paths, native
D3D11/D3D12/DXGI imports can select system DXGI without loading the installed
app-local proxy. A negative control deliberately applies the new expectation to
the old sequence and fails. Do not replace the Windows backslashes in this
fixture with mixed separators: that changed observed loader behavior in the
initial fixture and did not reproduce the production call faithfully.

## Scope and safety

`RenderHookWorker` now prepares the app-local DXGI proxy **after** the existing
edition / `-nobattleye` Story policy and native-file-presence checks, **before**
loading the native compositor. DllMain still does not inspect or load it.

- No proxy present: unchanged native-loading path.
- Exact proxy already resident: acquire a process-lifetime reference and verify
  its path and DXGI factory exports. No duplicate initialization is requested.
- Not resident, recognized ReShade64 metadata: open the file against concurrent
  replacement/write, load this exact absolute file with system-only dependency
  search, verify path and factory exports, retain the module reference.
- Unknown, inaccessible, reparse/directory, invalid architecture, failed load,
  or missing exports: leave the early native renderer inactive with a specific
  diagnostic. Do not remove the proxy or bypass the failure with system DXGI.
- Version metadata is a compatibility discriminator, **not authentication**.
  This handles a proxy the user already installed; it is not a downloader or
  arbitrary DLL search mechanism.
- No `SetDllDirectory`, `AddDllDirectory`, PATH/CWD search, global search-policy
  change, ReShade configuration change, visibility bypass, or input-guard change.
- Loaded proxy references are retained even if subsequent native initialization
  fails; unloading code which may have installed callbacks is unsafe.

Logs include the compatibility decision, Win32 error, exact selected path,
version for newly loaded ReShade, and first DXGI module before/after. The start
record precedes execution of the proxy; this is not a promise that arbitrary
third-party DLL initialization can be safely cancelled or bounded in-process.

References: [Microsoft LoadLibraryExW](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-loadlibraryexw),
[DLL search order](https://learn.microsoft.com/en-us/windows/win32/dlls/dynamic-link-library-search-order),
[upstream ReShade initialization](https://github.com/crosire/reshade/blob/main/source/dll_main.cpp).
The installed CoreFX variant was tested directly; upstream source is not proof
of that modified DLL's full behavior.

## Regression coverage

`ReactorV.AppLocalDxgi` runs 16 separate Windows processes: old behavior,
expected-failing old sequence, new sequence, system-first, proxy already loaded,
unknown proxy already loaded, absent proxy, CWD decoy, unrecognized/malformed
proxy, directory, write-locked proxy, wrong architecture, failed DllMain, missing
exports, and invalid executable path. It checks real static imports, loaded
identities, retained references, and the production gate/load ordering.
Synthetic proxy DLLs are test fixtures only; never ship them as game files.

`tools/RenderHookIsolation/Test-DxgiCompatibility.ps1` stages a **copy** of an
explicit real proxy and selected native DLL in a new evidence directory, then
runs loader, Enhanced D3D12 external-frame, and Legacy D3D11 flip-lifecycle tests.
It records input hashes, process exits and logs, and rejects reuse of an evidence
directory. A skip is not accepted as a passing real-proxy qualification.

Example (from repository root, GTA closed):

```powershell
& .\tools\RenderHookIsolation\Test-DxgiCompatibility.ps1 `
  -NativeBuildDirectory .\native\build-msvc-scriptprobe\Release `
  -ProxyPath 'D:\Programs\Steam\steamapps\common\Grand Theft Auto V Enhanced\dxgi.dll' `
  -RunDirectory '<DIAGNOSTICS_DIR>\dxgi-compatibility-NEW'
```

`-NativeLibraryPath` optionally selects an existing installed native DLL to test
without updating it. The fixture producer **must** be beside that copied native
DLL under `plugins\ReactorV`; the existing producer-image trust check requires
this. Initial manually staged runs put it elsewhere, causing ACK failures.
Those invalid-layout runs are retained, not counted as compatibility failures
or successful qualification. Consumer diagnostics now print image rejections,
discovery/connect errors, imports/publications, ACKs and import HRESULTs to make
that distinction visible. No production trust checks were relaxed.

## Evidence on 2026-09-10

- `<DIAGNOSTICS_DIR>\dxgi-compatibility-20260910-r1`:
  actual-proxy loader success; initial native-suite run; initial invalid render
  layouts; 1,139 managed tests passed, none skipped.
- Initial native suite: 35/36 passed, one LegacyD3D11FrameBridge timeout. Its
  unchanged isolated recheck passed in 0.28s. Preserve the timeout as a transient
  qualification observation, not a bug this loader patch claims to fix.
- Final full native rebuild/rerun: **36/36 passed, none skipped**, in 44.78s.
  Full output is `...\dxgi-compatibility-20260910-r2\native-suite-final.log`.
- `...\dxgi-compatibility-20260910-r2`: valid installed-layout tests with the
  current repository native DLL passed for the real proxy loader, Enhanced, and
  Legacy flip. Strict Enhanced external frame: published=1, ACK accepted=1,
  image rejects=0, copy failures=0, receive/import errors=0.
- `...\dxgi-compatibility-20260910-r3-installed-native`: all three also passed
  with the **exact currently installed native DLL**, copied for testing. This
  supports a one-file render-hook candidate rather than a broad runtime update.

Pinned installed inputs (unchanged by this work):

| Input | SHA-256 |
| --- | --- |
| ALLIN1.dll (user's speedometer update) | `2895149bd7f01860e80bad5dda8c7a80c6631fdff8c3ff9ff45a51a3b61bf845` |
| game-root CoreFX/ReShade dxgi.dll | `8e177571f2a3ce598b8e0e93cd0efc1e046b8c50c019f679560da9919e7d2c82` |
| installed RageWebUI.Native.dll | `58156102796900eee24aac45e2ca3fc8538050ffe52ab12eaa92b73989226fc5` |
| candidate ReactorV.RenderHook.asi | `ccff9575a9628de3913505cc7575a5defa62b006cb45e3d6ac65585007739bc7` |

## Local install and next live acceptance

At the user's request, only `ReactorV.RenderHook.asi` was replaced with the
qualified candidate. The earlier `corefx-early-hook-isolation-20260910-r2` was
closed using its frozen Restore helper: phase Restored, original hook hash
verified, disabled filename removed. That helper correctly reported the known
ALLIN1 difference; its historical protected baseline was not rewritten.

New receipt and original backup:
`<DIAGNOSTICS_DIR>\dxgi-compatibility-live-20260910-r1`.
Its `Manage-Test.ps1 -Action Verify` confirmed the installed candidate hash and
all 95 protected paths unchanged against a fresh baseline containing the user's
approved ALLIN1 update. `-Action Restore` restores only the hash-verified original
hook, with GTA/host stopped and unexpected-target overwrite protection. Prior
logs were copied into `before`. No game launch or collector was started.

ALLIN1, native/runtime DLLs, CoreFX presets/shaders and configuration remain fixed.
In particular `showFirstRunSplash=false` was not changed; a first-run splash is
not an expected result of this one-file test.

Initial live review now confirms ReShade **and** native adapter/renderer readiness.
The following remains the regression checklist for subsequent acceptance:
Then check Story startup, preloader, first GBay open, close/reopen, passive HUD
before GBay, Escape/pause interaction, vehicle entry/exit, and absence of tint or
flicker. Record timings and the exact installed hashes. Offline fixtures do not
prove GTA startup timing, CEF UI paint, the full CoreFX/RenoDX shader stack, HDR,
or actual player-visible correctness. No release or GitHub publication yet.
