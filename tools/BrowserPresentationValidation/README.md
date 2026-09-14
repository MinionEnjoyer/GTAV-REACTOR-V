# Real-browser presentation regression

This exercises the compiled Runtime composition host, PNG analyzer, verified
native window promotion and independent desktop-capture child. It does **not**
just connect a provider or accept a private browser capture as visible output.

Build Preloader and this fixture with `dotnet build -c Release`, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/BrowserPresentationValidation/Test-BrowserPresentation.ps1 -OutputDirectory <DIAGNOSTICS_DIR>\browser-regression-new
```

Use a fresh output directory. The default is two independent cold profiles for
each of software and default GPU rendering (four child processes). Tests run
sequentially so their windows cannot cover each other. A small non-activating,
click-through synthetic window appears briefly; do not cover it during the run.
Do not run alongside GTA or a Reactor host. No game launch, install, account
connection, display-setting change or network page is involved.

Each ordinary child performs 84 assertions across menu/HUD × 100/125/150% browser scales:

- Exact marker identity and concrete browser content, captured off-screen.
- The same HWND and physical bounds after verified native promotion.
- Actual marker pixels through both GDI and DXGI; on HDR the latter uses the
  production FP16/SDR-white normalization, without changing acceptance thresholds.
- Stale previous identity and hidden-window rejection through DXGI.
- Reopened surfaces after the initial cold promotion.

It preserves synthetic PNGs, eight sampled RGB values, controller/promotion
telemetry and build identities. It never saves a screenshot of the surrounding
desktop. Browser profiles are disposable test data; share `results.txt` and the
top-level `binaries.txt`, not profiles.

For a diagnostic negative control, invoke the exe directly with the runtime
directory, a fresh output directory and `raw-software` or `raw-gpu` (use
`Start-Process -WindowStyle Hidden`). Those modes bypass verified promotion and
are expected to fail on affected setups. A raw pass on another machine does not
prove this regression never occurs. Never count an expected failure as a
production pass.

## Full-screen flip-model target

Add `-FlipBackdrop` to run the same browser checks over an owned, continuously
rendering, borderless D3D11 flip-discard window at the primary screen's physical
resolution. It uses two buffers, an opaque SDR backbuffer and vsync; it neither
changes display settings nor requests exclusive fullscreen. This is a closer
composition boundary than the small desktop case, **not an emulator of GTA's
D3D12 renderer, a measured independent-flip/MPO mode, or in-game proof**.

The target must actually be foreground and continue presenting during both
desktop witnesses. Those thirteen additional assertions bring each child to
97 checks. There is no fallback that waives focus or accepts browser-only pixels.
Launch from a user-controlled PowerShell window if background launch activation
is refused. A failed foreground prerequisite is a fixture setup failure, not a
Reactor rendering failure or a qualified test. No focus-lock bypass, simulated
input, driver setting or global fullscreen optimization change is used.

The first two automated runs on 2026-09-10 failed this foreground prerequisite;
the second recorded 21 presented frames but foreground belonged to neither test
window. The small-window software/GPU regression still passed 84/84 each after
the extension. Full-screen qualification remains pending a focused run.

Scope limits: this is not the complete OverlayWindow state machine, real GBay
provider traffic, preloader splash timing, input forwarding, D3D game injection
or an in-game acceptance test. Foreground/ingress and input-ownership invariants
remain covered separately. Actual SDR-display acceptance needs an SDR machine;
running color conversion unit tests does not substitute for that hardware test.
