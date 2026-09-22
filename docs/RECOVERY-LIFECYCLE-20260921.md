# Recovery lifecycle hardening

This change implements the five recovery improvements discussed after the
startup, delayed-menu, input, and issue #1 investigations. It preserves the
existing Legacy and Enhanced rendering paths. Offline validation does not prove
that the remote user's crash is fixed.

## Bootstrap reconnection

The runtime retires a broken pipe and its workers before attaching another.
Reconnect runs off the GTA thread with capped backoff. Each transport owns a
separate stop signal, and queued output is stamped with its transport generation.
Offline output and retired input/visibility frames cannot be replayed into a new
connection. Either reader EOF or writer failure starts recovery. Disposal stops
retries. Reconnection does not reapply the initial visible setting.

A local content epoch changes on every new transport even if the server's
content generation is unchanged. Runtime-ready requests translate that local
epoch to the server's generation; a fast reconnect cannot bypass Script's
browser-ready and paint invalidation logic.

## Presentation identities

CEF frame IDs use one monotonic allocator across browser replacement. Browser
epochs fence retired callbacks. A submission lease spans the native copy;
retirement waits up to one second for active copies before deferring cleanup.
No replacement can start while old submissions or deferred cleanup remain.
If a driver call never returns, its resources stay owned until it returns rather
than being freed under that call; the host receives unavailability immediately.
Generation exhaustion is terminal rather than
wrapping or reusing an ID. Host-surface receivers in WebView2 and the shared web
bridge reject older generations and same-generation changes of mode, including
attempts to revive a closed surface. Both browser presenters use the web guard.

Menu recovery replaces the registry's presentation token. A late ACK for the old
token cannot authorize the new attempt. Input remains with the game until the
replacement receives fresh presentation proof.

## Lifecycle-driven menu recovery

Script retains the active typed menu context, services recovery from its normal
lifecycle, and pauses it while GTA's pause frontend is active. Neither telemetry
publication nor a telemetry getter triggers recovery. Browser loss and a direct
ready-to-new-ready transition invalidate paint gates and the runtime-ready lease.
A delayed initial paint gets one retry; exhaustion follows the existing exact
presentation abort path. Explicit close/dismissal immediately cancels recovery.

## Ownership and renderer recovery

Overlay resources are released by both FormClosed and Dispose, once. Timers,
browser handlers, queued work, receipts, input state, and retained window
references are cleared. Window creation racing disposal exits on the owning STA.
Late queued preloader callbacks cannot mutate a stopped host.

After a previously acknowledged GPU session fails, renderer/adapter recovery
allows two attempts. Adapter reacquisition uses a ten-second deadline per
attempt. Exhaustion disables the failed GPU path; it cannot authorize stale
pixels or input. Initial adapter discovery remains deferred for late GTA startup.
An initializer that loses its presenter requests a fresh surface generation and
uses the existing bounded paint deadline. Repeated unavailable edges do not
restart that deadline, and a close cancels the pending recovery.

## Verification

Managed and browser regression suites cover registry token replacement, stale
surface rejection, reconnect gates, epoch allocation, and lifecycle policies.
Windows console fixtures exercise real pipe/handshake recovery and actual Form
disposal/collection without injecting into GTA.

Final offline results:

- 1,190 managed tests passed.
- 263 browser tests passed; TypeScript and Vite production build passed.
- Preloader and Script Release builds passed with zero warnings/errors,
  including their Core, Runtime, and DirectX dependencies.
- `tools/BootstrapReconnectValidation`: real runtime autonomous reconnect,
  unchanged server generation, translated readiness ACK, and shutdown passed.
- `tools/WindowLifetimeValidation`: twelve closed/direct-disposed windows were
  collected; dispose-before-start left no retained window or UI thread.

Existing unrelated local changes were preserved. No game installation or GitHub
publication was performed in this implementation pass.

Before release, run one Legacy and one Enhanced session covering delayed startup,
menu close/reopen, pause/resume, active-menu renderer recovery, passive HUD expiry,
and shutdown. A GPU/driver reset under GTA is still an in-game acceptance item.
