# Live GTA acceptance

The live acceptance runner replaces repeated manual F9/click diagnostics with
one instrumented pass against the installed Reactor V and ALLIN1 builds. It
does not launch or close GTA and performs no gameplay or package mutations.

Close GTA, then arm the runner from the Reactor V repository:

```powershell
.\tools\arm-live-acceptance.ps1
```

This is a developer acceptance utility built from the source repository. It is
intentionally excluded from the public player ZIP.

The default arm window is 20 minutes. The run fails if no **fresh** GTA process
starts after arming, if that process never exposes a real GTA client window, or
if GTA was already running. A stale process or old log can never produce PASS.

Launch GTA normally and keep it in the foreground. The runner validates the
frontend About surface automatically, then asks you to select Story Mode. From
there it automatically exercises:

- early F9 routing and a painted ALLIN1 preloader;
- managed-provider handoff and GBAY `presentationReady`;
- all nine top-level GBAY sections through native WebView2 composition input;
- exact native-bound provider, root-menu, menu, route, and section identity for
  every top-level click;
- a ready, non-empty meaningful payload observation for every section,
  including visible/content/actionable/status counts;
- absence of GTA pause-menu leakage;
- two F9 About open/close cycles and two GBAY open/close cycles;
- ordered native pointer-down/pointer-up pairing at the requested tab;
- foreground HWND/PID ownership throughout interactive input.

The only manual actions are launching GTA and selecting Story Mode. F9, the
section navigation clicks, and close/reopen are driven by the runner. GTA must stay
foreground so Windows input cannot be delivered to another application.

Pointer forwarding, DOM/UI Automation state, WebView2 `CapturePreview`,
screenshots, and pixel deltas are retained as separate evidence classes. A
browser self-capture proves that Chromium rendered the requested generation,
but it cannot prove that Windows displayed those pixels over GTA. Every named
route therefore also requires an independently classified desktop-composition
capture. Neither evidence can substitute for the bounded semantic menu-state
observation used to validate provider, menu, route, identity, and payload.

Each screenshot capture is independently bounded to two seconds and records its
start, completion, duration, source, attempts, pixel metrics, and whether the
source is allowed to prove desktop visibility. Named Reactor routes retain a
correlated WebView2 self-capture for renderer diagnostics, then separately use
a bounded `CAPTUREBLT` desktop-composition copy. The desktop copy is bracketed
by matching browser controller, surface-generation, and presentation identities.
Both require two consecutive, stable, route-specific pixel classifications. A late capture can no longer
turn a requested preloader screenshot into later GBAY evidence, and a correctly
rendered but invisible WebView frame can no longer pass.

Every run writes a machine-readable receipt and state screenshots beneath
`%LOCALAPPDATA%\ReactorV\Acceptance\Runs`. The receipt records SHA-256 hashes
of the exact installed bootstrap/script/preloader/runtime/UI files, every
observed typed route and painted surface generation, an authoritative
`FrontendAbout -> StoryInitializing -> ProviderReady -> MenuPendingPaint ->
MenuInteractive -> Closing -> Closed` lifecycle keyed by surface generation and
presentation id, startup/shutdown timestamps, the foreground HWND/PID and input
edge timelines, each section's provider/menu/route identity and payload counts,
the paired pointer edges, and repeated menu cycles. A failure
preserves all screenshots and completed evidence and names the first unmet
boundary. The arm marker is one-shot and is removed when the run completes,
fails, times out, or GTA exits.

The normal UI run leaves GTA open. Its receipt therefore labels process shutdown
as `not_exercised`; a menu reaching `Closed` is only a completed surface
lifecycle. A dedicated shutdown run must separately observe the quit request,
script abort, ScriptHook uninitialization, GTA window destruction, GTA process
exit, and WebView process exit before shutdown can be marked complete.

Use an explicit receipt location when sharing a run:

```powershell
.\tools\arm-live-acceptance.ps1 -Receipt C:\Temp\reactor-live-receipt.json
```

The developer executable can also be invoked directly:

```powershell
.\src\ReactorV.Harness\bin\Release\RageWebUI.Harness.exe `
  --scenario live-acceptance
```
