import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { bridge, gta } from './gta/bridge'
import {
  browserCapabilities,
  browserRoleFromHostEvent,
  browserRoleFromLocation,
  canAcknowledgeHostSurface,
  type ReactorBrowserRole,
} from './gta/browserRole'
import {
  activateProviderInput,
  prepareProviderInput,
  revokeProviderInput,
} from './gta/providerInputGate'
import { ReactorVApi } from './gta/reactor'
import {
  waitForBootstrapHostSurfacePaint,
  waitForHostSurfacePaint,
} from './hostSurfacePaint'
import type { DependencyStatus, GameState, OverlaySnapshot, RuntimeStatus, StartupStatus } from './gta/types'
import { MenuSurface } from './menu/MenuSurface'
import {
  commitAcceptedPresentation,
  resolveAtomicPresentationLayers,
  selectReplacementRestoreSnapshot,
} from './menu/atomicPresentationHandoff'
import type { MenuControllerSnapshot } from './menu/controller'
import { PaintIdentityMarker } from './menu/PaintIdentityMarker'
import { ReactorAboutSurface } from './menu/ReactorAboutSurface'
import { StartupTransitionSurface } from './menu/StartupTransitionSurface'
import { parseMenuDismissal, parseMenuPresentation, type MenuPresentation } from './menu/presentation'
import { resolveVisiblePaintIdentity } from './paintIdentity'
import {
  createStartupFallbackStatus,
  parseStartupStatus,
  selectCurrentStartupStatus,
} from './startup'
import {
  formatDetectedGtaTarget,
  hostSurfaceSupersedesPresentation,
  parseHostProvider,
  parseHostSurface,
  resolveInitialHostSurface,
  resolvePresentationHandoff,
  resolveSurfaceView,
  shouldRetainBootstrapFrame,
  shouldRetireBootstrapAfterAcceptance,
  type HostSurfaceMode,
} from './surface'

const api = new ReactorVApi(bridge)
const initialHostSurface = resolveInitialHostSurface(
  typeof window === 'undefined' ? '' : window.location.search,
)
const initialBrowserRole = browserRoleFromLocation(
  typeof window === 'undefined' ? '' : window.location.search,
)

function App() {
  const [runtime, setRuntime] = useState<RuntimeStatus | null>(null)
  const [telemetry, setTelemetry] = useState<GameState | null>(null)
  const [checking, setChecking] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [presentation, setPresentation] = useState<MenuPresentation | null>(null)
  const [committedPresentation, setCommittedPresentation] = useState<MenuPresentation | null>(null)
  const [providerConnected, setProviderConnected] = useState(!bridge.isNative)
  const providerConnectedRef = useRef(!bridge.isNative)
  const [providerSessionGeneration, setProviderSessionGeneration] = useState(0)
  const [browserRole, setBrowserRole] = useState<ReactorBrowserRole>(initialBrowserRole)
  const browserAuthority = browserCapabilities(browserRole)
  const providerSessionGenerationRef = useRef(0)
  const [hostSurface, setHostSurface] = useState<HostSurfaceMode>(initialHostSurface)
  const hostSurfaceRef = useRef<HostSurfaceMode>(initialHostSurface)
  const [hostSurfaceGeneration, setHostSurfaceGeneration] = useState(0)
  const [hostGame, setHostGame] = useState<{ edition?: string; version?: string }>({})
  const [startupStatus, setStartupStatus] = useState<StartupStatus | null>(null)
  const [initializerPresentationId, setInitializerPresentationId] = useState<string | null>(null)
  const initializerPresentationIdRef = useRef<string | null>(null)
  const initializerPresentationWasStoryInitializerRef = useRef(false)
  const [acceptedPresentationId, setAcceptedPresentationId] = useState<string | null>(null)
  const [interactivePresentationId, setInteractivePresentationId] = useState<string | null>(null)
  const presentationRef = useRef<MenuPresentation | null>(null)
  const committedPresentationRef = useRef<MenuPresentation | null>(null)
  const deferredHostSurfaceNoneRef = useRef(false)
  const menuSnapshotsRef = useRef(new Map<string, MenuControllerSnapshot>())

  useLayoutEffect(() => {
    const root = document.documentElement
    const previous = root.dataset.reactorBrowserRole
    root.dataset.reactorBrowserRole = browserRole
    return () => {
      if (previous === undefined) delete root.dataset.reactorBrowserRole
      else root.dataset.reactorBrowserRole = previous
    }
  }, [browserRole])

  const rememberMenuSnapshot = useCallback((
    presentationId: string,
    snapshot: MenuControllerSnapshot,
  ) => {
    menuSnapshotsRef.current.set(presentationId, snapshot)
    // Presentations are short-lived tokens. Bound retained navigation state so
    // repeated refreshes cannot accumulate for the lifetime of the WebView.
    while (menuSnapshotsRef.current.size > 8) {
      const oldest = menuSnapshotsRef.current.keys().next().value
      if (typeof oldest !== 'string') break
      menuSnapshotsRef.current.delete(oldest)
    }
  }, [])

  useEffect(() => {
    // These two bootstrap events are transported directly by the persistent
    // host. They are deliberately the only subscriptions installed before the
    // managed GTA provider is ready.
    const unsubscribeProvider = bridge.on<unknown>('host.provider', (payload) => {
      const provider = parseHostProvider(payload)
      if (!provider) return
      // Host events are ordered, but a WebView reconnect can leave an older
      // queued notification behind. Never let that notification roll the
      // active provider session (and its subscriptions) backward.
      if (provider.sessionGeneration < providerSessionGenerationRef.current) return
      const providerSessionChanged =
        provider.sessionGeneration > providerSessionGenerationRef.current
      providerSessionGenerationRef.current = provider.sessionGeneration
      providerConnectedRef.current = provider.connected
      setProviderConnected(provider.connected)
      setProviderSessionGeneration(provider.sessionGeneration)
      if (providerSessionChanged || !provider.connected) {
        revokeProviderInput()
        presentationRef.current = null
        committedPresentationRef.current = null
        setPresentation(null)
        setCommittedPresentation(null)
        initializerPresentationIdRef.current = null
        initializerPresentationWasStoryInitializerRef.current = false
        setInitializerPresentationId(null)
        setAcceptedPresentationId(null)
        setInteractivePresentationId(null)
        setRuntime(null)
        setTelemetry(null)
        setChecking(true)
        setStartupStatus(null)
        if (deferredHostSurfaceNoneRef.current) {
          deferredHostSurfaceNoneRef.current = false
          hostSurfaceRef.current = 'none'
          setHostSurface('none')
        }
      }
    }, true)
    const unsubscribeBrowserRole = bridge.on<unknown>('host.browserRole', (payload) => {
      const nextRole = browserRoleFromHostEvent(payload)
      if (nextRole) setBrowserRole(nextRole)
    }, true)
    const unsubscribeSurface = bridge.on<unknown>('host.surface', (payload) => {
      const surface = parseHostSurface(payload)
      if (!surface) return
      // Native may publish its clean `none` ownership boundary while the
      // replacement tree is still preparing. Retain the last painted bootstrap
      // surface and its exact identity until that requested presentation is
      // accepted; otherwise the still-visible HWND would contain only the
      // opacity-zero preparation layer.
      const retainBootstrapFrame = shouldRetainBootstrapFrame(
        surface.mode,
        hostSurfaceRef.current,
        presentationRef.current !== null,
        committedPresentationRef.current !== null,
        surface.handoff,
      )
      if (!retainBootstrapFrame) {
        deferredHostSurfaceNoneRef.current = false
        hostSurfaceRef.current = surface.mode
        setHostSurface(surface.mode)
        setHostSurfaceGeneration(surface.generation ?? 0)
      }
      else {
        deferredHostSurfaceNoneRef.current = true
      }
      if (surface.edition || surface.gameVersion) {
        setHostGame((current) => ({
          edition: surface.edition ?? current.edition,
          version: surface.gameVersion ?? current.version,
        }))
      }
      // A real bootstrap surface is authoritative. Its idle `none` marker is
      // only the clean handoff boundary before a managed presentation, so it
      // must not unmount an active menu controller and discard route/focus.
      if (hostSurfaceSupersedesPresentation(surface.mode)) {
        revokeProviderInput()
        presentationRef.current = null
        committedPresentationRef.current = null
        setPresentation(null)
        setCommittedPresentation(null)
        initializerPresentationIdRef.current = null
        initializerPresentationWasStoryInitializerRef.current = false
        setInitializerPresentationId(null)
        setAcceptedPresentationId(null)
        setInteractivePresentationId(null)
      }
    }, true)
    const unsubscribeStartup = bridge.on<unknown>('startup.status', (payload) => {
      const status = parseStartupStatus(payload)
      if (!status) {
        globalThis.console?.warn?.('REACTOR V ignored an invalid startup status event.')
        return
      }
      setStartupStatus((current) => selectCurrentStartupStatus(current, status))
    }, true)
    return () => {
      unsubscribeProvider()
      unsubscribeBrowserRole()
      unsubscribeSurface()
      unsubscribeStartup()
    }
  }, [])

  useEffect(() => {
    if (hostSurface !== 'initializing') return
    setStartupStatus((current) => current && current.sessionId !== 'web-fallback'
      ? current
      : createStartupFallbackStatus(providerConnected))
  }, [hostSurface, providerConnected])

  useEffect(() => {
    if (!providerConnected) return
    let active = true
    void api.startup.getStatus({ timeoutMs: 1200 })
      .then((payload) => {
        if (!active) return
        const status = parseStartupStatus(payload)
        if (status) setStartupStatus((current) => selectCurrentStartupStatus(current, status))
        else globalThis.console?.warn?.('REACTOR V received an invalid startup status response.')
      })
      .catch(() => {
        // Older hosts do not expose startup.getStatus. Retain the bounded
        // compatibility view and let host.provider advance its three checks.
      })
    return () => {
      active = false
    }
  }, [providerConnected, providerSessionGeneration])

  const refresh = useCallback(async () => {
    if (!providerConnected) return
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
  }, [providerConnected, providerSessionGeneration])

  useEffect(() => {
    if (!bridge.isNative || !providerConnected) return

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
  }, [providerConnected, providerSessionGeneration])

  useEffect(() => {
    if (!providerConnected) return
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
  }, [providerConnected, providerSessionGeneration, refresh, runtime])

  const handleMenuEvent = useCallback((eventName: string, payload: unknown) => {
    if (!providerConnectedRef.current) return
    if (eventName === 'menu.presentation') {
      const next = parseMenuPresentation(payload)
      if (next) {
        const inputOwnershipChanged = prepareProviderInput(next.presentationId)
        presentationRef.current = next
        if (inputOwnershipChanged) setInteractivePresentationId(null)
        const initializerHandoffId = hostSurfaceRef.current !== 'none' &&
          committedPresentationRef.current === null
          ? next.presentationId
          : null
        initializerPresentationIdRef.current = initializerHandoffId
        initializerPresentationWasStoryInitializerRef.current =
          initializerHandoffId !== null && hostSurfaceRef.current === 'initializing'
        setInitializerPresentationId(initializerHandoffId)
        setAcceptedPresentationId((current) => inputOwnershipChanged ? null : current)
        setPresentation(next)
      }
      else globalThis.console?.warn?.('REACTOR V ignored an invalid menu presentation event.')
      return
    }

    const dismissal = parseMenuDismissal(payload)
    if (!dismissal) {
      globalThis.console?.warn?.('REACTOR V ignored an invalid menu dismissal event.')
      return
    }
    revokeProviderInput(dismissal.presentationId)
    setInteractivePresentationId((current) =>
      current === dismissal.presentationId ? null : current)
    if (dismissal.reason !== 'superseded') {
      const dismissedRequested =
        presentationRef.current?.presentationId === dismissal.presentationId
      const dismissedCommitted =
        committedPresentationRef.current?.presentationId === dismissal.presentationId
      const restoreInitializer = dismissal.reason === 'presentation-failed' &&
        initializerPresentationIdRef.current === dismissal.presentationId &&
        initializerPresentationWasStoryInitializerRef.current
      if (dismissedRequested) presentationRef.current = null
      if (dismissedCommitted) committedPresentationRef.current = null
      if (initializerPresentationIdRef.current === dismissal.presentationId) {
        initializerPresentationIdRef.current = null
        initializerPresentationWasStoryInitializerRef.current = false
      }
      setPresentation((current) =>
        current?.presentationId === dismissal.presentationId ? null : current)
      setCommittedPresentation((current) =>
        current?.presentationId === dismissal.presentationId ? null : current)
      setInitializerPresentationId((current) =>
        current === dismissal.presentationId ? null : current)
      setAcceptedPresentationId((current) =>
        current === dismissal.presentationId ? null : current)
      if (restoreInitializer) {
        deferredHostSurfaceNoneRef.current = false
        hostSurfaceRef.current = 'initializing'
        setHostSurface('initializing')
      }
      else if (dismissal.reason === 'overlay-hidden' &&
        (dismissedRequested || dismissedCommitted)) {
        deferredHostSurfaceNoneRef.current = false
        hostSurfaceRef.current = 'none'
        setHostSurface('none')
      }
    }
  }, [])

  // Install the local event listeners with the document itself. This closes
  // the provider-connect/render-effect gap for a CEF document receiving a
  // queued replay immediately after it finishes loading.
  useEffect(() => {
    const unsubscribePresentation = bridge.on<unknown>(
      'menu.presentation',
      (payload) => handleMenuEvent('menu.presentation', payload),
      true,
    )
    const unsubscribeDismissal = bridge.on<unknown>(
      'menu.dismissed',
      (payload) => handleMenuEvent('menu.dismissed', payload),
    )
    return () => {
      unsubscribePresentation()
      unsubscribeDismissal()
    }
  }, [handleMenuEvent])

  useEffect(() => {
    if (!providerConnected || browserRole === 'gpu-renderer') return
    const sessionGeneration = providerSessionGeneration
    let disposed = false
    const subscriptionAbort = new AbortController()
    let subscription: Awaited<ReturnType<typeof api.events.subscribe>> | undefined
    void api.events.subscribe(
      { events: ['menu.presentation', 'menu.dismissed'], replayLatest: true },
      undefined,
      { signal: subscriptionAbort.signal },
    ).then((activeSubscription) => {
      // The previous provider owns this subscription token. If its async
      // request resolves after a replacement session is current, do not send
      // an unsubscribe carrying the old token through the new provider.
      if (disposed || providerSessionGenerationRef.current !== sessionGeneration) {
        activeSubscription.disposeLocal()
        return
      }
      subscription = activeSubscription
    }).catch((subscriptionError) => {
      if (disposed || subscriptionAbort.signal.aborted) return
      globalThis.console?.warn?.('REACTOR V menu presentation subscription failed.', subscriptionError)
    })
    return () => {
      disposed = true
      subscriptionAbort.abort()
      if (subscription && providerSessionGenerationRef.current === sessionGeneration) {
        void subscription.unsubscribe().catch(() => {})
      }
      else subscription?.disposeLocal()
    }
  }, [browserRole, providerConnected, providerSessionGeneration])

  const closePresentedMenu = useCallback(async () => {
    if (!providerConnected) return
    const closingPresentationId = presentationRef.current?.presentationId ??
      committedPresentationRef.current?.presentationId
    if (closingPresentationId) {
      revokeProviderInput(closingPresentationId)
      setInteractivePresentationId((current) =>
        current === closingPresentationId ? null : current)
    }
    // The native close path atomically hides the overlay and releases capture.
    // Keep the current surface mounted until the host confirms that transition.
    try {
      await gta.closeOverlay()
      const closeStillCurrent =
        presentationRef.current?.presentationId === closingPresentationId ||
        (presentationRef.current === null &&
          committedPresentationRef.current?.presentationId === closingPresentationId)
      if (closeStillCurrent) {
        presentationRef.current = null
        committedPresentationRef.current = null
        setCommittedPresentation(null)
      }
      setPresentation((current) =>
        current?.presentationId === closingPresentationId ? null : current)
      if (initializerPresentationIdRef.current === closingPresentationId) {
        initializerPresentationIdRef.current = null
        initializerPresentationWasStoryInitializerRef.current = false
      }
      setInitializerPresentationId((current) =>
        current === closingPresentationId ? null : current)
      setAcceptedPresentationId((current) =>
        current === closingPresentationId ? null : current)
    } catch (closeError) {
      globalThis.console?.warn?.('REACTOR V could not acknowledge the overlay close.', closeError)
    }
  }, [providerConnected])

  const acknowledgePresentedMenu = useCallback(async (presentationId: string) => {
    if (!providerConnected) return false
    try {
      const acknowledgement = await api.overlay.presentationReady(presentationId)
      if (!acknowledgement.accepted) {
        globalThis.console?.warn?.(
          `REACTOR V ignored a stale presentation-ready acknowledgement for ${presentationId}.`,
        )
      }
      if (acknowledgement.accepted) {
        // A response may cross a replacement or provider reconnect. Native
        // acceptance of the old token must not retire or activate the current
        // browser presentation.
        if (presentationRef.current?.presentationId !== presentationId) {
          revokeProviderInput(presentationId)
          globalThis.console?.warn?.(
            `REACTOR V discarded an accepted but superseded presentation ${presentationId}.`,
          )
          return false
        }
        // Native acceptance retires any bootstrap identity before exposing the
        // typed menu. Mirror that boundary locally as an idempotent guard; the
        // host also publishes `host.surface=none` so its native reveal gate and
        // this browser tree cross the same FIFO ownership boundary.
        // Native retires the Story initializer after exact paint acceptance,
        // while About/verifying/setup remain the fallback owner if the provider
        // later disconnects. An explicit early native `none` boundary still
        // wins once the staged tree is ready.
        if (shouldRetireBootstrapAfterAcceptance(
          hostSurfaceRef.current,
          deferredHostSurfaceNoneRef.current,
        )) {
          deferredHostSurfaceNoneRef.current = false
          hostSurfaceRef.current = 'none'
          setHostSurface('none')
        }
        const committed = commitAcceptedPresentation(
          presentationRef.current,
          committedPresentationRef.current,
          presentationId,
        )
        committedPresentationRef.current = committed
        setCommittedPresentation(committed)
        setAcceptedPresentationId(presentationId)
      }
      return acknowledgement.accepted
    } catch (readyError) {
      globalThis.console?.warn?.('REACTOR V could not acknowledge the painted menu surface.', readyError)
      throw readyError
    }
  }, [providerConnected])

  const closeStartupTransition = useCallback(() => {
    if (!browserAuthority.bootstrapInput) return
    const closingPresentationId = presentationRef.current?.presentationId
    if (closingPresentationId) revokeProviderInput(closingPresentationId)
    setInteractivePresentationId((current) =>
      current === closingPresentationId ? null : current)
    try {
      bridge.closeHostSurface()
    } catch (closeError) {
      globalThis.console?.warn?.('REACTOR V could not close the bootstrap surface.', closeError)
    }
  }, [browserAuthority.bootstrapInput])

  const presentationHandoff = resolvePresentationHandoff(
    presentation?.presentationId ?? null,
    initializerPresentationId,
    acceptedPresentationId,
  )

  useLayoutEffect(() => {
    const presentationId = committedPresentation?.presentationId
    if (!presentationId || acceptedPresentationId !== presentationId) return
    let active = true
    // The acceptance response mutates the staged tree in this React commit.
    // Cross two subsequent browser frames before opening either semantic or
    // pointer input so native readiness can never outrun visible GBAY pixels.
    void waitForHostSurfacePaint(
      Promise.resolve(),
      window.requestAnimationFrame.bind(window),
      0,
    ).then(() => {
      if (!active || presentationRef.current?.presentationId !== presentationId ||
        acceptedPresentationId !== presentationId ||
        committedPresentationRef.current?.presentationId !== presentationId) return
      if (browserRole === 'gpu-renderer') {
        if (providerSessionGenerationRef.current !== providerSessionGeneration) return
        try {
          bridge.markExternalProviderSurfacePainted(
            presentationId,
            providerSessionGeneration,
          )
        } catch (paintReadyError) {
          globalThis.console?.warn?.(
            'REACTOR V could not publish its exact accelerated provider pixels.',
            paintReadyError,
          )
          return
        }
      }
      setInteractivePresentationId(presentationId)
    })
    return () => { active = false }
  }, [acceptedPresentationId, browserRole, committedPresentation?.presentationId,
    providerSessionGeneration])

  useLayoutEffect(() => {
    if (!browserAuthority.providerInput || !interactivePresentationId ||
      acceptedPresentationId !== interactivePresentationId ||
      presentationRef.current?.presentationId !== interactivePresentationId) return
    // This layout effect runs after the commit that removes `inert` and updates
    // MenuSurface's synchronous input ref. No browser event can interleave
    // between that DOM commit and opening the matching global pointer gate.
    activateProviderInput(interactivePresentationId)
  }, [acceptedPresentationId, browserAuthority.providerInput, interactivePresentationId])

  useEffect(() => {
    if (!browserAuthority.providerInput) revokeProviderInput()
  }, [browserAuthority.providerInput])

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (browserAuthority.bootstrapInput &&
        (hostSurface === 'verifying' || hostSurface === 'initializing' ||
        presentationHandoff.holdInitializer) && event.key === 'Escape') {
        event.preventDefault()
        event.stopPropagation()
        closeStartupTransition()
        return
      }
      if (browserAuthority.providerInput && providerConnected && !presentation &&
        hostSurface !== 'none' && event.key === 'Escape') {
        void gta.closeOverlay()
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [browserAuthority.bootstrapInput, browserAuthority.providerInput,
    closeStartupTransition, hostSurface, presentation, presentationHandoff.holdInitializer,
    providerConnected])

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

  const surfaceView = resolveSurfaceView(
    hostSurface,
    committedPresentation !== null,
  )
  const presentationInputInteractive = committedPresentation !== null &&
    browserAuthority.providerInput &&
    presentation?.presentationId === committedPresentation.presentationId &&
    interactivePresentationId === committedPresentation.presentationId &&
    acceptedPresentationId === committedPresentation.presentationId
  const visiblePaintIdentity = resolveVisiblePaintIdentity(
    surfaceView,
    hostSurfaceGeneration,
    providerSessionGeneration,
    committedPresentation?.presentationId ?? null,
  )

  useLayoutEffect(() => {
    const canAcknowledgeSurface = canAcknowledgeHostSurface(
      browserRole,
      surfaceView,
    )
    if (!bridge.isNative || !canAcknowledgeSurface || committedPresentation !== null ||
      hostSurfaceGeneration <= 0 ||
      (surfaceView !== 'about' && surfaceView !== 'verifying' &&
        surfaceView !== 'setup-status' && surfaceView !== 'initializing')) return
    let active = true
    const publishReady = async () => {
      // A staged GBAY tree can contain a large artwork catalog. Prepare those
      // assets independently so they never delay the initializer's reveal ack.
      const images = Array.from(document.images).filter((image) =>
        !image.closest('[data-reactor-presentation-preparing="true"]'))
      const decoded = Promise.all(images.map((image) => image.complete
        ? image.decode?.().catch(() => {}) ?? Promise.resolve()
        : new Promise<void>((resolve) => {
          image.addEventListener('load', () => resolve(), { once: true })
          image.addEventListener('error', () => resolve(), { once: true })
        })))
      const fontsReady = document.fonts?.ready?.catch(() => {}) ?? Promise.resolve()
      // Local artwork/fonts are normally cached. Their wait remains bounded;
      // two frame opportunities settle normal visible rendering while the
      // native full-size pixel probe remains the authoritative reveal proof
      // when a hidden WebView throttles animation frames.
      await waitForBootstrapHostSurfacePaint(
        Promise.all([decoded, fontsReady]),
        window.requestAnimationFrame.bind(window),
        250,
        100,
      )
      if (!active) return
      try {
        bridge.markHostSurfaceReady(surfaceView, hostSurfaceGeneration)
      } catch (readyError) {
        globalThis.console?.warn?.('REACTOR V could not acknowledge its bootstrap surface.', readyError)
      }
    }
    void publishReady()
    return () => { active = false }
  }, [browserAuthority.bootstrapInput, browserRole, committedPresentation,
    hostSurfaceGeneration, surfaceView])

  const atomicPresentationLayers = resolveAtomicPresentationLayers(
    presentation,
    committedPresentation,
  )
  const replacementRestoreSnapshot = selectReplacementRestoreSnapshot(
    atomicPresentationLayers.preparing,
    atomicPresentationLayers.visible,
    atomicPresentationLayers.visible
      ? menuSnapshotsRef.current.get(atomicPresentationLayers.visible.presentationId)
      : null,
  )

  // Every provider replacement loads inertly beneath the last accepted frame.
  // Its exact acknowledgement promotes the staged tree and matching paint
  // marker together in one React commit. This is the same atomic path whether
  // the retained frame is an initializer or another provider menu.
  if (atomicPresentationLayers.visible || atomicPresentationLayers.preparing) return (
    <>
      {atomicPresentationLayers.visible && (
        <div
          key={`presentation:${atomicPresentationLayers.visible.presentationId}`}
          className="menu-presentation-layer"
          inert={!presentationInputInteractive || undefined}
        >
          <PaintIdentityMarker
            identity={visiblePaintIdentity}
          />
          <MenuSurface
            key={atomicPresentationLayers.visible.presentationId}
            presentation={atomicPresentationLayers.visible}
            interactive={presentationInputInteractive}
            onSnapshot={rememberMenuSnapshot}
            onClose={closePresentedMenu}
            onReady={(presentationId) => acceptedPresentationId === presentationId}
          />
        </div>
      )}
      {atomicPresentationLayers.preparing && (
        <div
          key={`presentation:${atomicPresentationLayers.preparing.presentationId}`}
          className="menu-presentation-layer preparing"
          data-reactor-presentation-preparing="true"
          aria-hidden="true"
          inert
        >
          <MenuSurface
            key={atomicPresentationLayers.preparing.presentationId}
            presentation={atomicPresentationLayers.preparing}
            interactive={false}
            restoreSnapshot={replacementRestoreSnapshot}
            onSnapshot={rememberMenuSnapshot}
            onClose={closePresentedMenu}
            onReady={acknowledgePresentedMenu}
          />
        </div>
      )}
      {!atomicPresentationLayers.visible && presentationHandoff.holdInitializer &&
        surfaceView === 'about' && (
        <>
          <PaintIdentityMarker
            identity={visiblePaintIdentity}
          />
          <ReactorAboutSurface
            gameLabel={formatDetectedGtaTarget(
              hostGame.edition ?? runtime?.edition,
              hostGame.version,
            )}
          />
        </>
      )}
      {!atomicPresentationLayers.visible && presentationHandoff.holdInitializer &&
        surfaceView === 'verifying' && (
        <>
          <PaintIdentityMarker
            identity={visiblePaintIdentity}
          />
          <main className="game-state-verification-stage" aria-live="polite">
            <section className="game-state-verification-surface" aria-label="Verifying GTA V game state">
              <span className="spinner" aria-hidden="true" />
              <strong>Verifying game state…</strong>
              <small>Reactor V is identifying the current GTA V screen.</small>
            </section>
          </main>
        </>
      )}
      {!atomicPresentationLayers.visible && presentationHandoff.holdInitializer &&
        surfaceView === 'initializing' && (
        <>
          <StartupTransitionSurface
            status={startupStatus ?? createStartupFallbackStatus(providerConnected)}
            surfaceGeneration={hostSurfaceGeneration}
            onClose={closeStartupTransition}
          />
          <PaintIdentityMarker
            identity={visiblePaintIdentity}
          />
        </>
      )}
      {!atomicPresentationLayers.visible && presentationHandoff.holdInitializer &&
        surfaceView === 'setup-status' && (
        <>
          <PaintIdentityMarker
            identity={visiblePaintIdentity}
          />
          <main
            className="splash-stage"
            onDoubleClick={() => { if (providerConnected) void refresh() }}
          >
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

              <p className="splash-hint"><kbd>F9</kbd> toggle <span>·</span> <kbd>Esc</kbd> close</p>
            </section>
          </main>
        </>
      )}
    </>
  )

  if (surfaceView === 'about') {
    return (
      <>
        <PaintIdentityMarker
          identity={visiblePaintIdentity}
        />
        <ReactorAboutSurface
          gameLabel={formatDetectedGtaTarget(
            hostGame.edition ?? runtime?.edition,
            hostGame.version,
          )}
        />
      </>
    )
  }

  if (surfaceView === 'verifying') {
    return (
      <>
        <PaintIdentityMarker
          identity={visiblePaintIdentity}
        />
        <main className="game-state-verification-stage" aria-live="polite">
          <section className="game-state-verification-surface" aria-label="Verifying GTA V game state">
            <span className="spinner" aria-hidden="true" />
            <strong>Verifying game state…</strong>
            <small>Reactor V is identifying the current GTA V screen.</small>
          </section>
        </main>
      </>
    )
  }

  if (surfaceView === 'initializing') {
    return (
      <>
        <PaintIdentityMarker
          identity={visiblePaintIdentity}
        />
        <StartupTransitionSurface
          status={startupStatus ?? createStartupFallbackStatus(providerConnected)}
          surfaceGeneration={hostSurfaceGeneration}
          onClose={closeStartupTransition}
        />
      </>
    )
  }

  if (surfaceView === 'transparent') {
    return <main className="idle-stage" aria-hidden="true" />
  }

  return (
    <>
      <PaintIdentityMarker
        identity={visiblePaintIdentity}
      />
      <main
        className="splash-stage"
        onDoubleClick={() => { if (providerConnected) void refresh() }}
      >
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

        <p className="splash-hint"><kbd>F9</kbd> toggle <span>·</span> <kbd>Esc</kbd> close</p>
        </section>
      </main>
    </>
  )
}

export default App
