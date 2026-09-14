# HUD native-window lease and verified presentation — 2026-09-10

Follow-up to [Test 2's failed live acceptance](VERIFIED-PROMOTION-TEST2-LIVE-REVIEW.md).
Source fixes only: no game installation, GTA launch, commit, push or release.

## Changes

- Separate closeable HWND visibility from a generation-matched host-surface
  presentation receipt. The WebView host publishes the receipt only after its
  existing desktop witness passes. Both the local windowed session and the
  external server/proxy carry it to the script. Hide, replaced paint evidence,
  content loss, failed presentation, disconnect and session replacement revoke
  the receipt. Stale receipts cannot qualify a new HUD generation.
  The preloader's counter includes startup surfaces, so a dedicated mapping
  correlates its authoritative generation with the script's request. Frames are
  translated toward the host and receipts back toward the script; translation
  never refreshes timestamps. Retirement/session changes clear the mapping.
- The HUD presentation gate now waits for that receipt, rather than declaring
  `Presented` as soon as the host says it is visible. It still allows at most
  two attempts per activation. A native window disappearing before verification
  consumes the failed attempt. Leaving/re-entering the vehicle can begin a new
  activation without any GBay interaction; repeated failure is not retried forever.
- A separate 100 ms host-STA watchdog enforces the existing one-second passive
  HUD lease against the **native window**, not just React's pixels. It continues
  while bounds polling is stopped for an asynchronous reveal. If the producer
  stops, it hides the HWND, cancels pending reveal work and revokes presentation
  proof. Fresh frames from an expired generation cannot resurrect it; a new
  surface generation is required. The watchdog is also disposed on direct Form
  disposal, including startup failure paths.
- Host-bound HUD frames carry generation and UTC send time. Transport delay
  consumes the lease; stale/future/replayed/wrong-generation/invalid frames cannot
  renew it. The extension-facing HUD API and React asset format remain compatible.
  One frame-rejection trace per surface generation avoids per-frame log flooding.
- Menu preemption clears the old HUD lease without hiding the replacement menu.
  Provider disconnection also retires a passive HUD's native window.
  An expiry event carries the exact host generation to the parent preloader,
  which also retires the logical surface/native presenter for that generation.

The injected DirectX renderer retains its pre-existing visible-state criterion;
it does not use an external HWND witness. That compatibility fallback is limited
to the known injected renderer, not unknown runtimes or WebView hosts. Native
renderer acceptance has not been re-established by these offline tests.
The external-GPU bootstrap route publishes its HUD receipt only after the
existing exact-generation fresh-frame readiness gate and native visibility
decision; it does not substitute an external-HWND desktop receipt.

## What the new tests actually establish

- **1,097 managed tests** pass, including 36 new cases for frame transport,
  expiry, stale/replayed/malformed data, generation-matched state and a replay of
  the initial Test 2 HUD failures followed by a fresh vehicle activation.
- **52 native-window/IPC fixture checks** pass. The fixture runs the production
  `OverlayWindow` and its real WinForms watchdog on an off-screen HWND, with no
  browser or game ticks. It verifies the HWND actually becomes hidden and the
  reveal generation is invalidated, rather than only checking a policy boolean.
  A separate child process runs the production bootstrap server; the real
  authenticated named-pipe proxy receives injected verification receipts and
  their revocations. Injecting those receipts tests transport, not desktop pixels.
  A provider reload may restart its HUD generation counter without inheriting
  the disconnected provider's expired lease.
- **154 existing browser checks** pass (77 software, 77 default GPU), retaining
  the original paint and desktop-witness thresholds. No deadline or pixel
  acceptance threshold was loosened.
- Runtime, preloader, script and native-window fixture Release builds pass with
  no warnings/errors.

The first native-window run exposed a new transport defect: Json.NET parses ISO
UTC strings into Date tokens, and converting those back through `Value<string>`
loses UTC kind. The host now handles both token forms explicitly. A JSON
round-trip regression reproduces that path. Preserve the failed exploratory
run rather than reporting only the earlier pure-class tests.

Final evidence:
`<DIAGNOSTICS_DIR>\hud-host-lease-final-20260910`

- `managed/managed-mapped-final.trx` is the final 1,097-test run.
- `native-and-pipe-mapped-repeat` is the final 52-check run with generation
  translation, provider-reload cases and binary hashes.
- `browser-mapped-final` contains the two 77-check browser runs and binary hashes.
- Earlier results remain separate; they predate the last mapping/reload cases.

Repeatable runner: build `tools/HudHostValidation/HudHostValidation.csproj` in
Release, then run `tools/HudHostValidation/Test-HudHost.ps1` with a fresh
`-OutputDirectory` from x64 Windows PowerShell. It refuses an active GTA/host
session and never launches the game or synthesizes desktop input.

## Remaining acceptance gaps

This mitigates a stale external window during paused/stalled game scripts. It
does **not** prove ESC mouse delivery while fresh HUD frames keep arriving. A
responsive host-STA normally observes expiry within the lease plus one timer
interval; it cannot execute the hide during a blocked STA. A real cross-process
input test is still needed. Neither `HTTRANSPARENT` nor direct `SendMessage`
assertions can replace that test.

The initial cold-game desktop visibility failure remains unresolved. Successful
browser initialization or this fixture's off-screen window does not reproduce
GTA's swap chain. Keep the failed desktop samples and do not weaken verification
or claim the delayed GBay opening is fixed.

Any future candidate requires the matching preloader/runtime/script and both
Core copies, using a new reversible package. Do not reuse the consumed Test 2
package or mix this timestamp/receipt contract with older test binaries.
