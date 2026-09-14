# Startup hardening diagnosis — 2026-09-12

This is a read-only forensic record of the latest local GTA V Enhanced session. Times below are UTC and come from the named session logs.

## Scope and conclusion

The logs show two bounded, repeated preloader waits of approximately ten seconds each: one for the initializing surface and one for the passive HUD surface. They occur after the browser has already painted, because the external-GPU presenter is unavailable and the policy waits for an external presenter/fresh frame before it permits the surface to become ready.

The logs do not show a long GBAY initialization or a GBAY retry loop. They show that the game-readiness gate takes until the game becomes playable, and that menu presentation has its own bounded browser/paint wait after user input.

## Latest session timeline

Preloader log: `<LOCAL_REACTORV_DIR>\reactorv-session-20260912T140557767Z-32316.log`.

| Time | Event | Observed duration/result |
| --- | --- | --- |
| 14:05:59.759 | WebView content ready | Preloader elapsed 1.993 s; navigation took 44 ms. |
| 14:06:09.266 | External GPU browser faulted | `adapter-luid-discovery-timeout`; authoritative WebView2 remained active. The external browser was started at 14:05:59.204, so the logged start-to-fault interval is about 10.06 s. |
| 14:09:32.800 | Initializing surface wait begins | 5,000 ms deadline; WebView pixels verified at 14:09:33.049 in 162 ms. |
| 14:09:37.968 | First initializing-surface timeout | Fail closed and retry. |
| 14:09:43.219 | Second initializing-surface timeout | Surface abandoned at 14:09:43.224; requires a fresh toggle. |
| 14:09:49.488 | Runtime-ready handoff | Signaled successfully. |
| 14:11:11.276 | Passive-HUD surface wait begins | This follows the driving HUD becoming visible. |
| 14:11:16.466 | First passive-HUD timeout | Fail closed and retry. |
| 14:11:21.716 | Second passive-HUD timeout | Surface abandoned at 14:11:21.719; requires a fresh toggle. |

Thus the initial surface and passive HUD each incur two 5 s deadline windows (about 10.4 s including retry handling). The exact policy deadline is `HostSurfaceReadyDeadline = TimeSpan.FromSeconds(5)` in `src/ReactorV.Preloader/Program.cs`.

Script log: `<LOCAL_REACTORV_DIR>\reactorv-session-20260912T140939480Z-3608.log`.

| Time | Event | Observed duration/result |
| --- | --- | --- |
| 14:09:39.483 | Script construction begins | — |
| 14:09:39.651 | Browser ready | Script elapsed 170 ms. |
| 14:09:45.782 | GBAY bridge ready | ALLIN1 reports a ready menu/catalog bridge. |
| 14:09:49.483 | Story mode ready / runtime handoff | Script elapsed 10.0 s. |
| 14:09:52.551 | First playable readiness poll | Script elapsed 13.1 s. |

ALLIN1 logs show GBAY services initialized at 14:09:39.881. The builtin driving provider is selected at 14:09:39.792; the HUD becomes eligible when the player is driving at 14:11:11.264. That elapsed time is gameplay state, not a recorded HUD-loading interval.

## Native/external presenter comparison

The 10 s adapter-LUID policy is defined in `src/ReactorV.DirectX/ExternalGpuBrowserSession.cs` as `AdapterLuidDiscoveryTimeoutMilliseconds = 10000` with a 50 ms poll. The latest session logs the timeout/fallback, but does not emit an adapter-LUID-discovered or native-publication timestamp. Therefore the logs establish start-to-fallback timing, not the exact point at which a native adapter became queryable.

In the latest session, the external shadow starts at 14:05:59.204 and faults at 14:06:09.266. The RenderHook binds afterwards at local 07:06:10.374 (UTC 14:06:10.374); it records one initial receive error (`receive_error=11`) before binding, then no continued receive/import/copy/ack failures.

Earlier sessions demonstrate that external readiness can arrive in time to satisfy the host surface gate:

| Session | Shadow started | First shadow content ready | Result |
| --- | --- | --- | --- |
| 2026-09-10 | elapsed 1.177 s | elapsed 9.660 s | Initializing surface was marked ready on a fresh external frame in about 0.34 s after its wait began. |
| 2026-09-11 | elapsed 0.248 s | elapsed 11.942 s | Later initializing and passive-HUD gates were satisfied by fresh external frames in about 0.39 s and 0.35 s respectively. |
| 2026-09-12 | elapsed 1.437 s | none; fallback at elapsed 11.499 s | Both current surface gates exhausted their two 5 s retries. |

The 2026-09-11 first `content_ready` time is later than ten seconds after `shadow_started`; that event includes more than adapter discovery (browser/surface readiness). It must not be treated as a direct adapter-LUID deadline measurement. On 2026-09-12 the RenderHook binds about 1.1 s after the external-browser failure; that bounds a possible readiness race but does not prove the exact adapter-LUID availability time.

## GBAY and Chop coverage

GBAY was observed opening only after F9 at 14:10:33.875. Its first completed presentation waited 126 ms for browser preparation and 1,104 ms for provider commit. A later presentation waited 77 ms and 402 ms. No GBAY timeout, retry, or initialization failure appears in the session.

Chop was explicitly exercised four times. The first three presentations were cancelled by Escape before a recorded `menu_presentation_ready` event:

| Dispatch | Result |
| --- | --- |
| 14:10:16.563 | Cancelled by Escape at 14:10:19.018. |
| 14:10:22.400 | Cancelled by Escape at 14:10:23.743. |
| 14:10:26.303 | Cancelled by Escape at 14:10:29.546. |
| 14:12:12.343 | Completed at 14:12:12.829: 21 ms browser preparation and 464 ms provider commit. |

Consequently, only the fourth Chop attempt reproduces a successful presentation path. The first three still establish lower bounds on perceived wait before cancellation (about 2.455 s, 1.343 s, and 3.243 s); they do not establish their eventual completion time or a timeout/failure.

## Warnings and limitations

- ALLIN1 records `default_component_metadata_unavailable` for one GBAY weapon (`Unbounded DLC component count`) and a colored-smoke weapon-pack warning. Neither is followed by a retry, timeout, or blocked GBAY initialization.
- Deferred map attestation of five archives runs asynchronously after ALLIN1 starts; all five complete between 14:09:43 and 14:09:59. The logs do not show it blocking GBAY or browser readiness.
- This record does not measure total game-launch duration or attribute all gameplay time to Reactor. In particular, time until driving/HUD eligibility is a game-state observation.
- The logs lack a direct native adapter-ready/publication event for the 2026-09-12 failure. A future capture needs that telemetry to distinguish unavailable adapter discovery from an external-browser publication failure.

## Implemented hardening

- Adapter discovery uses a 10 s fast phase followed by 500 ms polling for the lifetime of the active producer. A late game device is not a permanent startup failure. Stop/Dispose invalidate discovery; missing native dependencies remain explicit terminal failures. Discovery traces distinguish deferred waiting, elapsed time, discovery, and native-query failure.
- Browser construction is reserved once and protected across stopped/restarted epochs. A cancelled construction cannot attach its result or fault the next lifecycle; a new construction waits for the prior result to be attached or disposed without blocking Stop on CEF initialization.
- Native initializer/passive-HUD requests retain only their current surface intent while the presenter starts. The existing five-second paint budget begins after renderer readiness, with a fresh surface generation and real browser/fresh-frame acknowledgements. Missing presenters fail hidden immediately. Hide, replacement and provider disconnection invalidate deferred intent. Windowed-only and synthetic WebView harness routes preserve their previous behavior.
- Bootstrap attachment now has bounded hello write/read, owned-pipe cleanup and serialized pre-attachment retry. A disconnected established connection is not reported healthy and cannot spawn a second reader/writer pair. Authentication, native-only presentation and input proof requirements were not relaxed.
- Consumer regressions exercise the production Reactor registry/API and passive-HUD leases using `allin1.gbay` and `gtav.chop-it-up`. These test the common consumer contracts, not the actual running Chop plug-in or GTA pixels.

## Final qualification

Final private evidence: `artifacts/startup-hardening-20260912-r1/`.

| Check | Result |
| --- | --- |
| Managed Release regression suite | 1,168 passed, 0 failed, 0 skipped; baseline before edits was 1,139. TRX: `managed-final/startup-regressions.trx`. |
| Web regression suite | 253 passed across 33 files. |
| Release Preloader, Harness and Script builds | Passed, no warnings/errors; their Core/Runtime/DirectX dependencies built successfully. |
| Actual .NET Framework 4.8 named-pipe fixture | Delayed ACK, never ACK, partial frame and peer disposal all passed against production wire helpers. |
| Normal external-GPU qualification | D3D11 and D3D12 passed; 27 and 26 shared frames rendered respectively, zero CPU-submitted frames. `gpu-normal-final/`. |
| Adapter creation delayed by 15 seconds | D3D11 and D3D12 passed; deferred discovery occurred at 10,048/10,016 ms and discovery completed at 15,156/15,643 ms. Both subsequently rendered shared GPU frames and shut down cleanly. `gpu-late-15s-final/`. |
| Diff whitespace check | Passed. |

The delayed GPU fixture uses real native test swap chains and the external CEF producer without launching GTA. Its first-frame timing is measured **after** the deliberate surface-start delay, not from total process startup. The helper/gate tests do not simulate every Runtime worker race or prove the visual preloader/HUD in GTA.

An initial Core test run was aborted because a new fixture wrote to a pipe before starting its reader. The fixture sequencing was corrected; it was not treated as a production crash or a passing run. An initial delayed-GPU qualification rendered frames but looked for the deferred event in the wrong log; the assertion was corrected to the producer log and both APIs were rerun successfully. Final evidence directories above contain the accepted reruns.

Reproduction commands (from the repository root):

```powershell
dotnet test tests/ReactorV.Core.Tests/RageWebUI.Core.Tests.csproj -c Release --no-restore --blame-hang-timeout 30s --blame-hang-dump-type none
dotnet build tools/BootstrapAttachmentValidation/BootstrapAttachmentValidation.csproj -c Release
./tools/BootstrapAttachmentValidation/bin/Release/net48/ReactorV.BootstrapAttachmentValidation.exe
# Supply a fresh output directory and the staged Harness/Preloader/UI paths:
./tools/qualify-external-gpu-browser.ps1 -Harness <harness.exe> -Preloader <preloader.exe> -UiDirectory <ui-directory> -OutputRoot <fresh-output-directory> -LateAdapterDelayMilliseconds 15000
```

### Acceptance boundary

Initial offline qualification did not change the game installation. This work remains in the existing diagnostic development worktree; GitHub, the release version and the published 0.2.3 release were not changed.

On the user's subsequent request to install for testing, six managed files were installed locally with verified originals, frozen candidates and independent post-install hash verification. The receipt is `<DIAGNOSTICS_DIR>\startup-hardening-live-20260912-r1\receipt.json`; its sibling `Manage-Test.ps1` provides Verify and guarded Restore. Both Core copies, DirectX, Runtime, Preloader and Script match the candidate hashes; all 123 protected identities remained unchanged. The installed native renderer, render-hook ASI, CoreFX/ReShade, UI assets, other mods and settings were preserved. Before installation, normal and 15-second-late-adapter D3D11/D3D12 qualification also passed with the actual installed native DLL and UI in a private harness. GTA was not launched. The current `showFirstRunSplash=false` setting was preserved, so a first-run splash is not part of this test's expected result.

Next live acceptance must cover a cold Story Mode start, early GBAY and Chop requests, close/reopen and Escape cancellation, speedometer appearance without opening any menu, pause-menu input, and entering/exiting Los Santos Customs. Initialization improvement and the separate LSC blue-tint report remain subject to that in-game evidence; offline results are not a claim that every reported symptom is resolved.
