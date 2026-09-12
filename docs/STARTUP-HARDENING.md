# Startup hardening — 0.2.4 live-session review

## Scope

This release focuses on startup hardening only. It does not claim the older experimental desktop or HUD feature set.

The original startup defect had two related symptoms: a fixed ten-second cutoff could expire before the graphics adapter became available, and a late adapter could then starve the first usable presentation. The selected fix replaces that brittle path with a readiness gate, an owned bounded hello, deferred fresh-generation presentation, and a restart lease. The intent is to wait for a valid owner and fresh frame without allowing an unbounded or stale startup handoff.

## Evidence boundary

The installer recorded six managed startup files as installed before this session. The local run used the diagnostic baseline; the consumer UI and Chop content changed after that installation, so this is a startup/host review rather than a full package-integrity assertion.

The live session did not exercise the speedometer before the first GBay request. A 15-second delayed-adapter case was verified only in a private fixture, not in this live run. The Los Santos Customs blue-tint report and the original reporter's crash are not established as fixed. This startup change has no new live Legacy acceptance.

## Last live session (UTC)

| Time | Observed outcome |
| --- | --- |
| 14:49:43.547 | Startup host began. |
| 14:49:44.183 | Embedded web content became ready (636 ms after host start). |
| 14:49:53.610 | The game adapter was discovered after 9.712 s; the late-adapter path did not starve startup. |
| 14:49:54.274 | First accelerated paint arrived. |
| 14:49:54.446 | Accelerated bootstrap became ready (10.900 s after host start). |
| 14:49:54.456 | First submitted frame was acknowledged. |
| 14:53:52.641–14:53:52.662 | Story readiness and runtime handoff completed. |
| 14:53:53.200–14:53:54.126 | GBay opened from the physical F9 intent, became interactive after a 170 ms browser-prepare wait plus 138 ms provider-commit wait, then closed cleanly; its input lease returned to Hidden. |
| 14:54:36.420–14:54:36.467 | First passive HUD request was presented and verified in 48 ms. Subsequent observed HUD presentations completed in 164 ms, 89 ms, 67 ms, and 108 ms. |
| 15:02:12.948–15:02:14.829 | Chop dispatched, became interactive after a 79 ms browser-prepare wait plus 159 ms provider-commit wait, and closed through Back/Escape routing; its input lease returned to Hidden. |
| 15:03:51.849–15:03:51.936 | The game exited with code 0; the host detached input, disposed its web view, stopped the accelerated presenter, and stopped with code 0. |

The first passive HUD presentation followed the first GBay request. This session records both working but does **not** prove passive-HUD readiness before GBay or freedom from that ordering dependency.

## Warnings and failures

No game-side error, exception, fatal, failure, warning, timeout, or degraded-state marker was recorded. One initial accelerated bootstrap probe was rejected before the successful ready/submit/ack sequence; this was a bounded transient, not a failure. One later stale-generation frame was ignored, then replaced by an acknowledged fresh frame.

The native hook rendered 23,048 frames with all native failure counters at zero. The session did not invoke adapter recovery; adapter discovery succeeded normally, so recovery behavior remains unexercised rather than failed.

## Release qualification

The release is rebuilt from the published 0.2.3 baseline with the focused startup changes, not by distributing the local diagnostic files. The native implementation and consumer UI/input contracts retain that baseline; older experimental desktop/HUD changes are not promoted. Runtime, assembly, package and render-hook resource versions identify 0.2.4.

Both edition packages must pass the existing full Release gates: native CTest, managed and web regressions, rendering/browser cold starts, GBay lifecycle stress, packaged preloader/host readiness, legal/content boundaries and package integrity. Additional checks exercise a 15-second delayed adapter on D3D11/D3D12 and real .NET Framework 4.8 named pipes (delayed ACK, absent ACK, partial frame and disposed peer). New consumer tests use the public registry and GBay/Chop extension IDs; they do not load those external plug-ins or prove GTA pixels.

Final source-commit, package hashes, test totals and qualifications are reported in the [0.2.4 release notes](https://github.com/MinionEnjoyer/GTAV-REACTOR-V/releases/tag/v0.2.4). Offline package gates do not erase the live-evidence limits above.
