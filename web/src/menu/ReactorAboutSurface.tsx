import { useCallback, useEffect, useRef, useState } from 'react'
import { bridge } from '../gta/bridge'
import { ReactorVApi } from '../gta/reactor'
import type { ExtensionListResult, ExtensionSummary } from '../gta/types'
import {
  parseDetectedMods,
  readLastDetectedMods,
  writeLastDetectedMods,
  type DetectedModsStorage,
} from './detectedMods'

interface ReactorAboutSurfaceProps {
  gameLabel?: string
  loadExtensions?: () => Promise<unknown>
  detectedModsStorage?: DetectedModsStorage | null
}

type AboutTab = 'overview' | 'mods'
type CatalogState = 'idle' | 'loading' | 'refreshing' | 'ready' | 'error'
type CatalogSource = 'live' | 'bootstrap' | 'cache' | null

const aboutApi = new ReactorVApi(bridge)
const loadRegisteredExtensions = () => aboutApi.extensions.list({ timeoutMs: 2500 })

export function DetectedModsPanel({
  state,
  catalog,
  source,
  onRetry,
}: {
  state: CatalogState
  catalog: ExtensionListResult | null
  source: CatalogSource
  onRetry: () => void
}) {
  if ((state === 'idle' || state === 'loading') && !catalog) return (
    <section className="reactor-detected-mods" aria-label="Detected mods" aria-live="polite">
      <div className="reactor-mods-message"><span className="spinner" /> Reading registered mods…</div>
    </section>
  )

  if (state === 'error') return (
    <section className="reactor-detected-mods" aria-label="Detected mods" aria-live="polite">
      <div className="reactor-mods-message reactor-mods-unavailable">
        <strong>Mod catalog is still preparing</strong>
        <span>Reactor is reading the installed package manifests. No scripts are being run.</span>
        <button type="button" data-reactor-bootstrap-action="retry-detected-mods" onClick={onRetry}>Retry</button>
      </div>
    </section>
  )

  if (!catalog || catalog.items.length === 0) return (
    <section className="reactor-detected-mods" aria-label="Detected mods" aria-live="polite">
      <div className="reactor-mods-message">
        <strong>{source === 'cache'
          ? 'No mods were detected in the last runtime session'
          : source === 'bootstrap'
            ? 'No installed package manifests detected'
            : 'No registered mods detected'}</strong>
        <span>{source === 'live'
          ? 'Compatible mods appear here after they register with Reactor V.'
          : 'Reactor will verify the live registry when the managed runtime connects.'}</span>
        <button type="button" data-reactor-bootstrap-action="refresh-detected-mods" onClick={onRetry}>Refresh</button>
      </div>
    </section>
  )

  return (
    <section className="reactor-detected-mods" aria-label="Detected mods" aria-live="polite">
      <header>
        <span>{catalog.total} {source === 'live' ? 'registered' : source === 'bootstrap' ? 'detected' : 'last detected'} {catalog.total === 1 ? 'mod' : 'mods'}{state === 'refreshing' ? ' · checking…' : ''}</span>
        <button type="button" data-reactor-bootstrap-action="refresh-detected-mods" onClick={onRetry}>Refresh</button>
      </header>
      <ul>
        {catalog.items.map((extension: ExtensionSummary) => (
          <li key={extension.id}>
            <div className="reactor-mod-identity">
              <strong>{extension.name}</strong>
              <small>{extension.id}</small>
            </div>
            <span className="reactor-mod-version">v{extension.version}</span>
            <span className={`reactor-mod-status ${source === 'live' ? '' : 'cached'}`}>
              <i /> {source === 'live'
                ? 'Registered / runtime connected'
                : source === 'bootstrap'
                  ? 'Installed / awaiting runtime'
                  : 'Last detected / awaiting runtime'}
            </span>
            <small className="reactor-mod-summary">
              API v{extension.extensionApiVersion} · {extension.menuCount} menus · {extension.actionCount} actions · {extension.eventCount} events
            </small>
          </li>
        ))}
      </ul>
    </section>
  )
}

export function ReactorAboutSurface({
  gameLabel = 'GTA V',
  loadExtensions = loadRegisteredExtensions,
  detectedModsStorage,
}: ReactorAboutSurfaceProps) {
  const [tab, setTab] = useState<AboutTab>('overview')
  const [catalogState, setCatalogState] = useState<CatalogState>('idle')
  const [catalog, setCatalog] = useState<ExtensionListResult | null>(null)
  const [catalogSource, setCatalogSource] = useState<CatalogSource>(null)
  const requestSequence = useRef(0)
  const providerConnected = useRef(!bridge.isNative)

  useEffect(() => () => { requestSequence.current += 1 }, [])

  const refreshCatalog = useCallback(() => {
    const requestId = ++requestSequence.current
    const liveRequest = providerConnected.current
    setCatalogState((previous) =>
      previous === 'idle' || previous === 'error' ? 'loading' : 'refreshing')
    void loadExtensions()
      .then((payload) => {
        if (requestSequence.current !== requestId) return
        const parsed = parseDetectedMods(payload)
        if (!parsed) {
          const cached = readLastDetectedMods(detectedModsStorage)
          setCatalog(cached?.catalog ?? null)
          setCatalogSource(cached ? 'cache' : null)
          setCatalogState(cached ? 'ready' : 'error')
          return
        }
        setCatalog(parsed)
        const authority = typeof payload === 'object' && payload !== null && !Array.isArray(payload)
          ? (payload as Record<string, unknown>).authority
          : null
        setCatalogSource(authority === 'bootstrap-preload' || !liveRequest ? 'bootstrap' : 'live')
        setCatalogState('ready')
        writeLastDetectedMods(parsed, detectedModsStorage)
      })
      .catch(() => {
        if (requestSequence.current !== requestId) return
        const cached = readLastDetectedMods(detectedModsStorage)
        setCatalog(cached?.catalog ?? null)
        setCatalogSource(cached ? 'cache' : null)
        setCatalogState(cached ? 'ready' : 'error')
      })
  }, [detectedModsStorage, loadExtensions])

  useEffect(() => {
    const cached = readLastDetectedMods(detectedModsStorage)
    if (cached) {
      setCatalog(cached.catalog)
      setCatalogSource('cache')
      setCatalogState('ready')
    }
    return bridge.on<unknown>('host.provider', (payload) => {
      if (typeof payload !== 'object' || payload === null || Array.isArray(payload) ||
        typeof (payload as Record<string, unknown>).connected !== 'boolean') return
      const connected = (payload as Record<string, unknown>).connected as boolean
      providerConnected.current = connected
      if (connected) {
        refreshCatalog()
        return
      }
      requestSequence.current += 1
      refreshCatalog()
    })
  }, [detectedModsStorage, refreshCatalog])

  useEffect(() => bridge.on<unknown>('host.extensionCatalog', () => {
    if (!providerConnected.current) refreshCatalog()
  }), [refreshCatalog])

  const selectTab = (nextTab: AboutTab) => {
    setTab(nextTab)
    if (nextTab === 'mods' && catalogState !== 'loading' && catalogState !== 'refreshing' &&
      catalogSource !== 'live') refreshCatalog()
  }

  return (
    <main className="reactor-about-stage" aria-label="About REACTOR V">
      <section className="reactor-about-surface">
        <nav className="reactor-about-tabs" aria-label="REACTOR V information" role="tablist">
          <button type="button" role="tab" data-reactor-bootstrap-action="overview" className={tab === 'overview' ? 'active' : ''} aria-selected={tab === 'overview'} onClick={() => selectTab('overview')}>Overview</button>
          <button type="button" role="tab" data-reactor-bootstrap-action="detected-mods" className={tab === 'mods' ? 'active' : ''} aria-selected={tab === 'mods'} onClick={() => selectTab('mods')}>Detected Mods</button>
        </nav>
        {tab === 'overview' ? (
          <>
            <div className="reactor-about-logo-safe-area">
              <img src="./ragewebui-logo.png" alt="REACTOR V" />
            </div>
            <div className="reactor-about-copy reactor-about-copy-panel">
              <h1>REACTOR V</h1>
              <p className="reactor-about-expansion">
                Real-time Embedded Application Component Toolkit &amp; Overlay Runtime
              </p>
              <p className="reactor-about-purpose">
                A lightweight embedded interface runtime for GTA V Story Mode.
              </p>
              <p className="reactor-about-credit">
                Created by MinionEnjoyer for {gameLabel}
              </p>
            </div>
          </>
        ) : (
          <DetectedModsPanel state={catalogState} catalog={catalog} source={catalogSource} onRetry={refreshCatalog} />
        )}
      </section>
    </main>
  )
}
