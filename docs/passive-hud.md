# Passive speedometer prefab (candidate protocol v1)

The `PassiveHudContract` adds a read-only speedometer slot without opening a menu, consuming input, or changing F9 ownership. The protocol is additive; old extension handles remain binary-compatible. Consumers must feature-detect `RageWebUI.Core.PassiveHudContract` and should hide gracefully on older hosts. Use the matching Core, Script, Runtime/Preloader and React assets together. This candidate still requires Legacy and Enhanced in-game qualification before release.

Declare capability `presentation.passive-hud.v1` and register event `hud.frame` (4096-byte limit recommended). Publish via the ordinary extension handle at up to 10 Hz:

```json
{"schema":1,"visible":true,"kind":"speedometer","speed":72,"units":"KMH","gear":"3","manual":false,"notice":""}
```

`speed` must be finite and 0–9999, `units` KMH/MPH, `gear` at most 3 characters, and `notice` at most 180. There is no HTML, action binding, pointer or menu payload. Withdraw with `{"schema":1,"visible":false}`. Publishers must withdraw during pause, loading, loss of focus, Online, menus and when no driving readout is appropriate.

The first active publisher owns the single speedometer slot for one second. Other publishers cannot overwrite/clear that lease. Expiry or unregistering releases it. Host dispatch also rejects queued frames older than one second. This slot is separate from the existing multi-extension menu registry.

The game-thread host throttles browser updates, retains `game` input mode, yields to every pending/active menu, and uses a `passive-hud` host surface with the existing exact-generation paint proof. The persistent bootstrap host authors the surface generation and qualifies both WebView and external GPU presenters; no unqualified fullscreen HWND fallback is introduced. A live menu retires the passive surface only after matching paint. Provider disconnect hides the host, and the React prefab also clears on disconnect or stale data.

`web/src/hud/Speedometer.tsx` is the reusable prefab. Its root has no interactive elements and uses `pointer-events: none`; it ships no consumer branding/assets. Existing overlay API input gates remain unchanged.
