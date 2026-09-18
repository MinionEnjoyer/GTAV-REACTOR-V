# Future Reactor V runtime hardening goals

These goals are intentionally outside the GTA V Enhanced Title Update 1.73
compatibility pass. They preserve the complete feature set on both editions and
should be implemented only with dedicated regression evidence.

- Make bootstrap attachment retryable and nonfatal when the browser or graphics
  host is temporarily unavailable during game startup.
- Keep GBay and Chop open requests durable across delayed initialization instead
  of dropping them after a presentation timeout.
- Add persistent recovery for passive HUD surfaces such as the speedometer after
  device, window, pause-menu, or frontend transitions.
- Exercise startup, first-open, close/reopen, pause input, HUD recovery, tint,
  hang, and shutdown behavior in a cross-process acceptance suite on both
  editions.
- Improve diagnostic attribution so a report distinguishes runtime startup,
  renderer presentation, consumer UI readiness, game input ownership, and an
  unrelated game or mod crash.
