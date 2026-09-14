# Issue #1: bounded controller startup and diagnostic stages

## Scope and current conclusion

**Latest finding (2026-09-10):** the exact 2/8 and 0/8 failed marker scores are
reproduced offline by HDR-clipped DXGI capture, with no GTA involved. FP16 capture
and OS white-level normalization now restore all three recorded identities to
8/8. 1,028 managed tests plus expanded desktop fixtures pass. Not installed; this
corrects the witness, not proven in-game visibility or the BitBlt stall. See
[HDR desktop witness correction](HDR-DESKTOP-WITNESS-FIX.md).

**Latest offline follow-up (2026-09-10):** HUD content validation and bounded
visibility reconciliation implemented; removed menu input authorization without
desktop proof. 1,010 managed tests and 59 compiled PNG checks passed, plus full
desktop/collector fixtures. Not installed; initial compositor visibility/delay
remains unresolved. Core also changed, so a future test must cover both consumed
Core copies, not reuse the old three-file deployment. See
[HUD/input safety details](HUD-PAINT-AND-INPUT-SAFETY.md).

**Latest live outcome (~2026-09-10 10:55 UTC): functional acceptance failed.**
Opening test1 still had delayed/invisible GBay and a disappearing speedometer.
GDI stalls are now isolated to BitBlt; DXGI returned insufficient desktop identity
matches, yet explicit-F9 fallback enabled input. Separately, whole-frame pixel
coverage rejects sparse passive HUDs and exhausts their reveal retries. All three
original binaries/configuration were restored and all 12 baseline hashes verified.
No active test or new fix installed. See
`artifacts/issue-1-controller-startup/OPENING-TEST1-REVIEW.md`.

**Fresh test prepared (2026-09-10 10:43 UTC):** The three-file opening-F9/capture
candidate passed 124 private deployment/restore checks and a 98.16-second frozen
external-host rehearsal. Desktop controls now point to the new one-use package;
the game installation remains original and no live test is armed. See
[test preparation and next evidence root](PRESENTATION-OPENING-TEST.md).

**Latest offline work (2026-09-10):** Physical F9-at-dispatch handling and bounded
GDI-timeout-to-DXGI recovery are implemented; 986 managed tests and two complete
desktop/protocol fixture runs passed. Not installed. Live startup acceptance and
the remote crash remain unresolved. See [refinement report](DESKTOP-WITNESS-HANDOFF-REFINEMENT.md).

**Latest live result (2026-09-10):** GBay became openable after a delay; two desktop
probe children reached gdi-capture quickly but hit their 900 ms deadline. A third
verified 8/8 pixels in 189 ms, followed by logged GBay navigation. The first
presentation also lacked an armed F9 intent and expired at its provider gate;
source/log ordering supports a separate first-open intent issue. The splash is
disabled in the test config, and no native preloader UI is expected in this route.
Helper classification worked. Both binaries were automatically restored, all 12
original hashes verified. No active test. Adam subsequently confirmed it seemed
steady once open; initialization delay and full acceptance remain unresolved.
See `artifacts/issue-1-controller-startup/PRESENTATION-RETRY1-REVIEW.md`.

**Reset after accidental Online launch (2026-09-10):** The first two-file live
run was aborted at the user's request; it is not Story Mode acceptance evidence.
Both original binaries and all 12 baseline files were restored and verified.
The same desktop controls now point to fresh `presentation-comparison-20260910-retry1`,
whose offline rehearsal passed. No new live test is installed or armed. See
`artifacts/issue-1-controller-startup/PRESENTATION-TEST-RESET.md` for exact paths.

**Two-file game test prepared, not installed (2026-09-10):** A frozen package now
contains the tested runtime/preloader pair, exact dependencies, authenticated
helper observer and new reversible two-file installer. It passed 85 private-copy
deployment/restore checks and a complete external-host attachment/shutdown
rehearsal. Final game preflight still matches all 12 original hashes. Desktop
controls are in `ReactorV-Presentation-Test`; no live session is armed. See
`artifacts/issue-1-controller-startup/PRESENTATION-TEST-PREPARED.md` for exact
package/receipt paths and limitations. In-game flicker is not yet accepted as fixed.

**Latest offline refinement (2026-09-10):** The desktop witness now crops to its
exact identity-sample rectangle, and records child stage/timing/lifecycle data.
The observer authenticates same-EXE probe helpers separately from competing
hosts; unknown roles and incomplete/nonzero helper exits remain explicit failures.
Release build, 975 managed tests, 44 comparison checks, production-child desktop
pixel tests and real helper-classification fixtures pass. Five monitor-sized
desktop targets verified 8/8 samples in 129–159 ms; wrong identity was rejected.
This is not proof the in-game flicker is fixed. All 12 restored game hashes match;
no new installation or test is armed. Details and candidate hashes:
`artifacts/issue-1-controller-startup/DESKTOP-PROBE-REFINEMENT.md`.

**Latest live comparison (2026-09-10):** External host reached content-ready in
0.48 seconds and the real GTA proxy attached, but Adam reports delayed, flickering
GBay: **not functionally accepted**. First two desktop-presentation probes hit
their hard deadline; third verified samples. Raw technical verdict also flags a
second preloader, strongly consistent with the same-EXE desktop-probe child that
our observer did not classify separately. Its exact identity was not retained;
the raw result is preserved. All eight tracked processes exited 0, and installation
restored at 09:18:24 UTC with all 12 baseline hashes matching. No new test active.
See local `artifacts/issue-1-controller-startup/EXTERNAL-HOST-LIVE-REVIEW.md` for
limitations and next presentation/observer work. Earlier preparation notes follow.

**Next comparison prepared, not installed (2026-09-09):** Desktop external-host
test controls are ready after 28 diagnostic checks, 50 private-copy deployment/
restore checks, and a successful real-proxy/secondary-AppDomain rehearsal with
natural target-exit host shutdown. All original game files remain restored.
The user must start the desktop controller before launching GTA; no game launch
or installation occurred during preparation. The observer flags in-process
fallback rather than silently counting it as external-host success. See local
`artifacts/issue-1-controller-startup/EXTERNAL-HOST-COMPARISON-PREPARED.md` and
`tools/ExternalHostComparison/README.md`. Visible GBay acceptance is still pending.

**Latest offline qualification (2026-09-09):** Two fresh-profile runs of the
existing persistent external preloader host passed startup/content/shutdown
checks with the same instrumented composition runtime and copied installed UI.
Content-ready was reached about 0.45 / 0.42 seconds after host-session start;
all 14 observed process instances exited 0. No Steam overlay, Reactor native
compositor, or CEF DLL was observed in their module samples; both runs completed
a 90-second post-host-exit watch. This supports a controlled external-host
comparison, not causal proof or visible/in-game GBay acceptance. The initial
collector attempt was excluded for missing browser linkage, corrected and
retested before these two passes. See local
`artifacts/issue-1-controller-startup/EXTERNAL-BROWSER-OFFLINE-RESULT.md`.
All 12 installed baseline hashes and 1,110 installed UI files remain unchanged;
installation is restored, renderer `auto`, no live test active or new fix shipped.

**Offline symbol follow-up:** Matching Windows symbols resolve the repeated
browser wait to `NtAlpcConnectPort` for `\Sessions\1\Windows\DwmApiPort`, not an
ordinary frame wait. Both local browser crashes read exactly the unloaded
`dxgi!CreateDXGIFactory` entry point while Steam's overlay inspects a hook target.
This strengthens the stale-function-pointer/module-unload-race hypothesis for
the browser exception, without explaining the earlier DWM delay or the remote
GTA crash. The initial DWM stack is no longer present on the browser main thread
at the exception. See local `TEST6-SYMBOL-FOLLOWUP.md` under the controller-startup
artifacts for evidence and the proposed hook-absence comparison; no new runtime
fix or live test was performed. Installation remains restored.

**Latest local result (test 6, 2026-09-09):** Desktop collectors completed and
captured the startup wait. Two browser snapshots approximately 60 seconds apart
show its main thread in the same DWM/ALPC connection stack. The browser later
crashed inside Steam's overlay DLL at RVA `+0x8cf26`, reading an address within
an unloaded DXGI range, matching the previous local browser fault. GTA itself
exited 0. The initial wait and later browser exception are not yet proven to
share a cause; neither establishes the remote reporter's GTA crash cause.
Both installation layers are restored and all 12 baseline hashes match; no test
active. See the local `artifacts/issue-1-controller-startup/TEST6-REVIEW.md` for
private evidence and analysis limitations. Subsequent sections preserve history.

Local native-off/windowed tests load Story Mode but do not make GBay ready. The last old-build stage was WebView2 environment readiness; the previous trace did not distinguish DirectComposition setup from controller-request completion.

Adam confirmed, with a screenshot, that GTA Enhanced's per-game Steam Overlay toggle was **off before test 3**. Disabling that setting did not restore GBay. Steam's DLL was nevertheless observed in GTA and the positively linked Reactor browser. This is a valid setting-off test, not a DLL-absent test; DLL presence is not proof the setting was enabled or the DLL caused the stall. The remote reporter's GTA crash remains a separate unresolved diagnosis.

This change adds targeted instrumentation and safe failure handling. It is **not proof that GBay now works in-game**, and is not a crash fix qualification.

## Startup stages

Each controller attempt logs attempt number, owner thread, overlay/parent HWNDs, and elapsed time:

1. `webview_controller_options_begin`
2. `webview_controller_composition_device_begin` / `composition_device_ready`
3. `webview_controller_request_begin` / `request_returned`
4. `webview_controller_request_completed`
5. `webview_controller_initial_bind_begin` / `initial_bind_ready`

Existing `webview_controller_ready`, navigation, and browser/page readiness stages remain separate. Starting the UI thread is still not functional menu acceptance.

A one-shot, 30-second deadline starts before controller options/DirectComposition setup. Its background callback emits `webview_controller_deadline_elapsed` with the last stage and performs **logging only**. If the async request stays pending while the owning UI loop is alive, startup returns a `TimeoutException`, hides/fails the UI through the existing failure route, and logs `webview_controller_attempt_failed`, `webview_initialization_failed`, and `webview_failed`.

If a synchronous native call blocks the owning STA, the independent callback can record where it stalled, but cannot forcibly return that call or safely close COM objects from a worker thread. This is intentionally not advertised as a hard process-wide timeout. Environment creation and later page navigation are outside this controller-specific deadline.

## Ownership and cleanup

- A controller request has one result owner. Concurrent calls on the same host are rejected.
- Host disposal cancels the managed wait. Cancellation/deadline beats a result whose continuation has not yet been accepted.
- An abandoned request is never retried on the same host. Existing retries for returned COM failures remain separate; timeout is not a COM retry.
- If an abandoned request later succeeds, it is closed on the captured owner context without publishing it as the active controller. A late fault is observed and logged.
- Disposal is rechecked after the await and after COM property setters, before publication; failures release the local controller/composition state.
- Late cleanup is best-effort while the original message loop exists. If that STA has exited, there is no unsafe worker-thread fallback or forced COM release; process teardown remains the ultimate boundary. Closing the window does not authorize moving COM work to another thread.

These restrictions follow Microsoft's [WebView2 threading model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model): controller work and callbacks belong to the owning STA/message loop.

## Verification — 2026-09-09

- Release runtime build: successful, zero warnings/errors.
- Full core test suite: **956 passed**, including 15 new behavioral/source-integration checks. Tests cover success, timeout, cancellation/disposal, queued-result races, late failure, cleanup failure, original-thread cleanup, diagnostic watchdog, and production wiring.
- Actual .NET Framework 4.8 windowed probe in a secondary AppDomain: passed with Reactor native compositor physically absent, using the new runtime and unchanged installed dependencies in a disposable staging copy.
- Probe PID 75512: request returned at about 19 ms; completed at 200 ms; composition bind completed at 212 ms. Controller-ready and successful navigation were observed. The probe used a fresh isolated profile and synthetic page, **not GTA or GBay**, so it cannot reproduce all in-game conditions.
- TRX and probe evidence: `artifacts/issue-1-controller-startup/`.
- Runtime SHA-256: `5359dc1e4cea9225288a7057dc934f985608774ae3fd55ddff78fb663c53ed60`.

No game installation or Steam setting was changed by this implementation. The game remains on the restored diagnostic-v1 files with renderer `auto`; old isolation helper/package identities are unchanged. Do not put the new runtime into the old hash-qualified test without explicitly requalifying its deployment/receipt identity. No GitHub post, commit, push, or release was performed.

## Local test installation — subsequently authorized

Adam authorized the instrumented in-game test on 2026-09-09. At 15:49 UTC this exact runtime was installed in the native-off/windowed recipe and two local observers were armed; the user will launch GTA. Deployment used the unchanged qualified helper's explicit expectedIdentity constructor with all four hashes, replacing only the qualified runtime hash. Full preflight/layout verification remains enabled. A separate runtime receipt/backup permits restoration of the previous DLL as well as the original native/config files.

The combined local driver (`artifacts/issue-1-controller-startup/Local-Controller-Test.ps1`) passed a fixture deployment/restore rehearsal and an unexpected-file conflict-refusal test before actual use. See `artifacts/issue-1-20260908/isolation-kit/LOCAL-TEST-STATE.md` for active receipts/observers and the **combined** restore command. The old driver alone would not restore the runtime DLL. No game launch or GitHub publication performed by the assistant.

## In-game result — not accepted as a GBay fix

Test 4 completed: Adam reports no GBay menu; GTA exited with code 0. DirectComposition setup and the synchronous controller request return completed quickly. At 30 seconds the async creation result was still pending, and the new timeout/failure handling executed. A late controller was eventually received and its Close completed about 118 seconds after request return; that timestamp is after Close and does not isolate creation-arrival latency. No controller-ready/navigation/menu acceptance occurred. Thus the safety/diagnostic path was exercised successfully, but the underlying delay remains unresolved.

Two matching browser processes overlapped during startup. An additional game-root ReShade DLL (file timestamps between tests 3/4, also loaded this session) means the whole graphics environment was not a verified single-variable comparison; it does not explain the earlier failure by itself. No new/changed browser dump was recorded through the three-minute post-exit window. See `artifacts/issue-1-20260908/isolation-kit/LOCAL-TEST-4-REVIEW.md`.

Both installation layers have now been restored; all 12 original baseline file hashes verified, renderer `auto`. Observers finished. No graphics mods, Steam settings, or browser profile were changed, and nothing was published. GBay functional acceptance remains outstanding.

## Offline profile comparison — subsequently authorized

On 2026-09-09, four isolated offline probes (fresh / copied persistent snapshot / independent copy of that snapshot / fresh) all reached controller readiness: 445.892, 231.494, 233.505, and 221.856 ms respectively. All used the same instrumented runtime, WebView2 152.0.4191.66, options, qualified probe worker, and x64 .NET Framework PowerShell host in a secondary AppDomain. The original profile's file paths, sizes, and SHA-256 values were unchanged afterward; all probe processes exited. No game launch or installed-file change occurred.

Profile state alone did not reproduce the stall. This does not exclude original-path permissions, live contention, host/injection differences, or intermittent behavior. Three trials logged successful synthetic-page navigation; the first ended at the worker's controller-ready criterion before navigation completion was observed. None is GBay functional acceptance. See `artifacts/issue-1-controller-startup/OFFLINE-PROFILE-AB-RESULT.md`. Private profile copies and integrity manifests are retained outside the repository, not for upload.

## Test 5 — authorized, installed, awaiting launch

At 2026-09-09 16:38 UTC the same candidate runtime/native-off-windowed recipe was installed again for a bounded, local process-snapshot diagnostic. No product binary changes were made. An independent collector records browser process roles/exits and takes at most six clone-based minidumps while controller completion remains unobserved. It passed linkage/trigger checks and disposable-process rehearsals. Dumps remain local outside the repository and are not included in the helper's text result ZIP. No GTA launch or publishing occurred.

This supersedes the previous restored state: the test is now active. See `artifacts/issue-1-20260908/isolation-kit/LOCAL-TEST-STATE.md` for current observer identities and the **explicit-output combined restore command**, and `artifacts/issue-1-controller-startup/TEST5-HANG-CAPTURE.md` for limitations. User gameplay and same-session analysis are still required.

## Test 5 result — restored, capture incomplete

The later session PID 82576 reproduced the 30-second controller timeout and no browser readiness. Late-controller Close completed 92.882 seconds after request return; this is not exact creation latency. Both external observer processes disappeared, leaving only armed/waiting status and no hang dumps/helper ZIP. Their termination cause is not established by the available Windows events. The surviving runtime log supports the repeated stall but does not reveal waiting stacks or qualify live isolation/module evidence.

Both installation layers were restored at 2026-09-09 18:02:38 UTC and all 12 original file hashes independently verified, renderer `auto`. No test is active now. See `artifacts/issue-1-controller-startup/TEST5-REVIEW.md`. Collector survival across handoff must be qualified before another game test; immediate post-launch liveness was insufficient. No new production fix, game launch or publication occurred during the review.

## Collector reliability follow-up — offline only

`tools/CollectorReliability` adds actual-loop heartbeats, exact process/run identity
checks, two-sample progress gating, cooperative cancellation, finite offline probe
modes in both collector scripts, and a fail-closed independent-process launcher.
Windows PowerShell 5.1 verification passed 39 checks (including 8 owned-process
exit checks). No installation or capture was armed. The restored runtime hash
was independently rechecked and remains the original `54833ddd...d8db1a6`.

Independent launch was refused in the current command host: despite immediate-job
flags `0x2800`, the suspended child remained in a job after an explicit permitted
breakaway request. Only that never-resumed child was terminated. This does not
establish the historical collector termination cause, and no alternate launch
mechanism or restriction bypass was attempted. The offline desktop check in
`tools/CollectorReliability/Run-OfflineCheck.cmd` is provided for user launch;
actual handoff survival is still unqualified. No gameplay retry or product fix
acceptance should be inferred. GTA remains restored; nothing was published.

## Desktop offline handoff result — passed

The user subsequently ran the provided offline desktop check. Both collector
entry points completed their 120-second probes with 236 heartbeat updates, exact
matching run/process identities, unchanged script hashes and `probe-complete`.
Windows PowerShell events confirm the launcher stopped at 19:01:42 UTC and both
collectors continued until 19:03:40 UTC, then stopped normally. This supersedes
the pending desktop qualification above, but does not qualify longer operation,
in-game capture, or GBay functionality. See
`artifacts/issue-1-controller-startup/COLLECTOR-HANDOFF-REVIEW.md`.

All 12 restored baseline hashes were reverified, renderer `auto`. No game test
is active and no installation or publication was performed during this review.
Another game test requires user approval and a fresh live readiness check.
