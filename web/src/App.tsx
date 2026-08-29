import { useCallback, useEffect, useMemo, useState } from 'react'
import { bridge, gta } from './gta/bridge'
import type { DependencyStatus, GameState, OverlaySnapshot, RuntimeStatus } from './gta/types'

function App() {
  const [runtime, setRuntime] = useState<RuntimeStatus | null>(null)
  const [telemetry, setTelemetry] = useState<GameState | null>(null)
  const [checking, setChecking] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setChecking(true)
    setError(null)
    try {
      const status = await gta.ready()
      setRuntime(status)
      try {
        setTelemetry(await gta.getState())
      } catch (telemetryError) {
        setTelemetry(null)
        setError(telemetryError instanceof Error ? telemetryError.message : 'Story Mode is not ready.')
      }
    } catch (bridgeError) {
      setRuntime(null)
      setTelemetry(null)
      setError(bridgeError instanceof Error ? bridgeError.message : 'Runtime verification failed.')
    } finally {
      setChecking(false)
    }
  }, [])

  useEffect(() => {
    if (!bridge.isNative) return

    let active = true
    void gta.ready()
      .then((status) => {
        if (!active) return
        setRuntime(status)
        setError(null)
      })
      .catch((bridgeError) => {
        if (!active) return
        setRuntime(null)
        setError(bridgeError instanceof Error ? bridgeError.message : 'Runtime verification failed.')
      })
      .finally(() => {
        if (active) setChecking(false)
      })

    return () => {
      active = false
    }
  }, [])

  useEffect(() => {
    const unsubscribeState = bridge.on<GameState>('game.state', (state) => {
      if (!state) return
      setTelemetry(state)
      setError(null)
      // Compatibility with older hosts that do not yet emit the atomic
      // overlay.snapshot event. Current Reactor hosts normally skip this
      // fallback and arrive with runtime + telemetry in one update.
      if (runtime === null) void refresh()
    })
    const unsubscribeSnapshot = bridge.on<OverlaySnapshot>('overlay.snapshot', (snapshot) => {
      if (!snapshot?.runtime || !snapshot?.state) return
      setRuntime(snapshot.runtime)
      setTelemetry(snapshot.state)
      setError(null)
      setChecking(false)
    })
    if (!bridge.isNative) void refresh()
    return () => {
      unsubscribeState()
      unsubscribeSnapshot()
    }
  }, [refresh, runtime])

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') void gta.closeOverlay()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [])

  const checks = useMemo<DependencyStatus[]>(() => {
    const demoBridge = runtime?.runtime === 'Browser demo'
    return [
      ...(runtime?.dependencies ?? []),
      {
        id: 'bridge',
        name: 'REACTOR V assets',
        loaded: runtime !== null && (bridge.isNative || demoBridge),
        required: true,
        detail: runtime ? `API v${runtime.apiVersion}` : 'Waiting for the UI bridge',
      },
      {
        id: 'telemetry',
        name: 'GTA V Story Mode',
        loaded: telemetry !== null,
        required: true,
        detail: telemetry ? 'Game session responding' : 'Waiting for Story Mode',
      },
    ]
  }, [runtime, telemetry])

  return (
    <main className="splash-stage" onDoubleClick={() => void refresh()}>
      <section className="startup-check" aria-label="GTA V startup verification">
        <div className="logo-safe-area">
          <img src="./ragewebui-logo.png" alt="GTA V" className="splash-logo" />
        </div>

        <ul className="verification-list" aria-live="polite">
          {checking && checks.length === 2 ? (
            <li className="checking"><span className="spinner" /> Verifying installation…</li>
          ) : checks.map((item) => (
            <li key={item.id} className={item.loaded ? 'passed' : 'failed'} title={item.detail}>
              <span className="verification-mark">{item.loaded ? '✓' : '×'}</span>
              <span>{item.name}</span>
            </li>
          ))}
          {error && <li className="failed" title={error}><span className="verification-mark">×</span><span>Status check</span></li>}
        </ul>

        <p className="splash-hint"><kbd>F10</kbd> toggle <span>·</span> <kbd>Esc</kbd> close</p>
      </section>
    </main>
  )
}

export default App
