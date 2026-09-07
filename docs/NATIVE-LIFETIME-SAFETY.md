# Native lifetime safety — 0.2.1 preview

This is a targeted native safety update following 0.2.0, not a claim that GitHub
issue #1 is resolved. The reported execute access violation has no recovered
trustworthy caller chain. The one-pixel initializer sizing issue is unchanged.

## Changes

- Retain the native module before publishing DXGI hooks or window callbacks.
  The root loader also retains its reference when arming fails, including when
  paired with an older native module that does not pin itself.
- Stop treating a quiet callback count as proof that executable trampolines
  are safe to free. A thread can already have entered a detour but not reached
  the counter. Once published, trampolines and original-function pointers remain
  resident until process exit and are reused on rearm.
- Check disable/remove/uninitialize results. Failed or deferred cleanup retains
  its ledger and blocks rearm until cleanup can safely complete. Only hooks
  never exposed to callers are physically removed/uninitialized.
- Reserve hook-tracking capacity before creating hooks and attempt rollback
  when the arming ABI catches a C++ exception.
- Preserve a detached window's forwarding-chain metadata if restoration fails.
  Another mod may retain Reactor's WndProc after detachment; its code stays valid.

Shutdown still stops preparation/consumer workers and releases compositor and
input resources when callbacks have drained. The native DLL and published hook
stubs intentionally remain loaded. Close GTA before replacing binaries; a hot
DLL unload/reload is not an installation mechanism.

## Telemetry

`<GTA>/scripts/ReactorV/ReactorV.NativeLifecycle.log` records UTC time, PID/TID,
native source/toolchain fingerprint, configuration, compile time, module base,
hook target addresses, stage and result. It records successful initialization
and failure/deferred-cleanup events. Numeric MinHook results use the upstream
MinHook status enum; cleanup results are defined in `native/src/HookCleanup.h`.

The current log and one `.1` rotated log are each bounded to approximately
1 MiB. Failed I/O drops diagnostics. Nothing is sent over the network. No new
exception handler is installed, and no lifecycle file I/O is added to Present,
resize or input callbacks. This is last-stage evidence, not a crash dump.

For a follow-up report, collect this log alongside `ReactorV.RenderHook.log`
and the Reactor session log from the same launch. Match PID/timestamps; do not
merge separate launches into a single crash timeline. Release downloads include
ZIP SHA-256 checksums. The earlier local candidate manifest contains SHA-256
hashes of its two binaries; the source fingerprint is not a substitute for
binary hashes, and versioned rebuilds must not be confused with that candidate.

## Tests

Configure/build the native tree in Release with BUILD_TESTING enabled and run
CTest. The additional tests cover staged cleanup failures/retries, held
callbacks, permanent trampoline retention, real HWND callback forwarding after
detach plus FreeLibrary, and telemetry identity/rotation/I/O failure. Source
contracts ensure retention occurs before callback publication and file logging
does not occur in the render callbacks.

The callback test was also run against InputQueue.cpp from the unpatched base:
it exits 8 (module no longer resident). With the patched code it exits 0 and
actually forwards a message through the retained callback.

Two existing managed source-contract readers normalize CRLF to LF so assertions
are portable to a Windows checkout; the assertions themselves are unchanged.

## Rollout boundary

The original local native candidate replaced only `ReactorV.RenderHook.asi` in the GTA
root and `plugins/ReactorV/RageWebUI.Native.dll` in an existing matching 0.2.0
preview installation. That incremental candidate is not the 0.2.1 download.
0.2.1 uses the existing full edition-specific package workflow and keeps its
preview status and executable guards. Close GTA before installation; back up
the existing runtime and preserve consumer assets/settings. Do not change other
ASIs or game settings during a comparison. A normal launch without ProcDump is
the first comparison.

Both edition packages must pass the existing complete package/harness gates.
Automated success does not establish live acceptance on either edition or that
the reporter's specific crash has been fixed.

The pre-version-bump safety candidate was tested locally on Enhanced 1.0.1158.13
on 2026-09-07 (game PID 42016, 17:52–17:56 UTC). Its exact native fingerprint was
confirmed, six hooks enabled successfully, 26,186 rendered frames were recorded,
and receive/import/copy/acknowledgement error counters remained zero. Menu input
and hiding were recorded. GTA and the preloader exited with code 0, and browser
disposal completed. This did not exercise failure/deferred native cleanup or
reproduce the other user's crash, and the versioned full packages require their
own live comparison.

## Package-harness corrections

The first 0.2.1 package attempt exposed a race in the synthetic WebView presenter:
logical bootstrap retirement selected no presenter while the non-presenting GPU
shadow refreshed, briefly hiding an already-committed provider menu. The explicit
harness route now retains only visible, accepted and committed pixels matching
the current presentation and provider session. Cold reveals still require GPU
readiness; production native presentation, hide/disconnect handling and visual
thresholds are unchanged.

A second attempt lost desktop focus to Explorer during setup. Before provider
attachment the harness now rechecks focus and initializer pixels. It can reissue
the process-scoped show intent only when focus loss explains a hidden window;
an unexplained hide fails. Focus loss during provider attachment is reported as
an environment failure rather than a cascade of menu-readiness failures.
