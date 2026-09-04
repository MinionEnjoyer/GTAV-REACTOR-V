import { useCallback, useEffect, useLayoutEffect, useRef, useState, type CSSProperties } from 'react'
import { bridge } from '../gta/bridge'
import { isProviderInputActive } from '../gta/providerInputGate'
import { ReactorVApi } from '../gta/reactor'
import type {
  InputActionEvent,
  MenuDescriptor,
  MenuInvocation,
  MenuItem,
  MenuSearchItem,
} from '../gta/types'
import { adaptMenusToRoutes } from './adapter'
import { MenuAudioFeedback, type MenuAudioCue, type MenuAudioSource } from './audioFeedback'
import { MenuController, type MenuControllerSnapshot } from './controller'
import { handleMenuContextMenu, performMenuBack } from './backInput'
import { GbaySurface } from './GbaySurface'
import { gbayCardEdgePageAction, invokeGbaySemanticInput, moveGbayCardFocus } from './gbayInput'
import { SearchKeyboard } from './SearchKeyboardSurface'
import {
  activateSearchKeyboardKey,
  commitSearchKeyboardSession,
  createSearchKeyboardSession,
  focusSearchKeyboardKey,
  moveSearchKeyboardSelection,
  updateSearchKeyboardDraft,
  type SearchKeyboardKey,
  type SearchKeyboardSession,
} from './searchKeyboard'
import {
  classifyGbayRoute,
  gbayWeaponPreviewNode,
  gbayAccountState,
  isGbayCustomizationOption,
  isGbayWeaponPreviewAction,
  isAllin1Presentation,
  isGbayNavigationItem,
  projectAllin1GbayMenu,
  type GbayAccountState,
} from './gbay'
import {
  changedMenuIdsInLoadedTree,
  coalesceGbayStateChange,
  mergeChangedMenuDescriptors,
  parseGbayStateChangedEvent,
  preserveGbayViewState,
  type PendingGbayStateChange,
} from './gbayStateRefresh'
import {
  acknowledgePaintedMenuPresentation,
  createMenuInvocationKey,
  loadPresentedMenuTree,
  loadPresentedMenuTreeCached,
  menuInvocationIdentity,
  menuResultPresentationDirective,
  runMenuPresentationAcknowledgementOnce,
  type MenuPresentation,
} from './presentation'

const api = new ReactorVApi(bridge)

interface MenuSurfaceProps {
  presentation: MenuPresentation
  interactive?: boolean
  restoreSnapshot?: MenuControllerSnapshot | null
  onSnapshot?(presentationId: string, snapshot: MenuControllerSnapshot): void
  onClose(): void | Promise<void>
  onReady(presentationId: string): boolean | Promise<boolean>
}

interface ConfirmationPrompt {
  title: string
  message: string
}

export function GbayConfirmationDialog({
  confirmation,
  onRespond,
}: {
  confirmation: ConfirmationPrompt
  onRespond(confirmed: boolean): void
}) {
  return (
    <div className="menu-confirmation gbay-confirmation" role="dialog" aria-modal="true" aria-labelledby="gbay-confirmation-title">
      <div className="gbay-confirmation-card">
        <header className="gbay-confirmation-header">
          <span className="gbay-confirmation-mark" aria-hidden="true">!</span>
          <span><small>GBAY SECURE ACTION</small><strong>ALLIN1 confirmation</strong></span>
        </header>
        <section className="gbay-confirmation-copy">
          <small>Confirmation required</small>
          <h2 id="gbay-confirmation-title">{confirmation.title}</h2>
          <p>{confirmation.message}</p>
        </section>
        <span className="confirmation-actions gbay-confirmation-actions">
          <button type="button" onClick={() => onRespond(false)}>Cancel</button>
          <button type="button" className="primary" autoFocus onClick={() => onRespond(true)}>Confirm</button>
        </span>
      </div>
    </div>
  )
}

class MenuActionCancelled extends Error {
  constructor() {
    super('Action cancelled.')
    this.name = 'MenuActionCancelled'
  }
}

function errorMessage(error: unknown): string {
  return error instanceof Error && error.message ? error.message : 'The menu action could not be completed.'
}

function itemValue(item: MenuItem): string {
  switch (item.type) {
    case 'toggle': return item.value ? 'On' : 'Off'
    case 'choice': return item.options.find((option) => option.value === item.value)?.label ?? item.value
    case 'range': return `${item.value}${item.unit ?? ''}`
    case 'keybind': return item.value
    case 'pagination': return `${item.page} / ${item.pageCount}`
    case 'status': return item.value ?? ''
    default: return ''
  }
}

export function MenuSurface({
  presentation,
  interactive = true,
  restoreSnapshot = null,
  onSnapshot,
  onClose,
  onReady,
}: MenuSurfaceProps) {
  const surfaceRef = useRef<HTMLElement | null>(null)
  const controllerRef = useRef<MenuController | null>(null)
  const descriptorsRef = useRef<MenuDescriptor[]>([])
  const busyRef = useRef(false)
  const confirmedInvocationKeysRef = useRef(new Map<string, string>())
  const confirmationResolverRef = useRef<((approved: boolean) => void) | null>(null)
  const [snapshot, setSnapshot] = useState<MenuControllerSnapshot | null>(null)
  const [account, setAccount] = useState<GbayAccountState | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [confirmation, setConfirmation] = useState<ConfirmationPrompt | null>(null)
  const [searchKeyboard, setSearchKeyboardState] = useState<SearchKeyboardSession | null>(null)
  const searchKeyboardRef = useRef<SearchKeyboardSession | null>(null)
  const [reloadRevision, setReloadRevision] = useState(0)
  const [loadedPresentationId, setLoadedPresentationId] = useState<string | null>(null)
  const allin1Presentation = isAllin1Presentation(presentation)
  const readyPresentationRef = useRef<string | null>(null)
  const readyPresentationAttemptsRef = useRef(new Set<string>())
  const readyCallbackRef = useRef(onReady)
  const mountedRef = useRef(true)
  const acceptanceObservationRef = useRef<string | null>(null)
  const audioFeedbackRef = useRef<MenuAudioFeedback | null>(null)
  const lastWeaponPreviewRef = useRef<string | null>(null)
  const weaponPreviewRouteRef = useRef<{
    routeId: string
    menuId: string
    stopNodeId: string
  } | null>(null)
  const interactiveRef = useRef(interactive)
  const presentationIdRef = useRef(presentation.presentationId)
  interactiveRef.current = interactive
  presentationIdRef.current = presentation.presentationId
  readyCallbackRef.current = onReady
  if (!audioFeedbackRef.current) {
    audioFeedbackRef.current = new MenuAudioFeedback((cue) =>
      api.ui.playMenuCue(cue, { timeoutMs: 1_000 }))
  }

  const acceptsInput = useCallback(() => interactiveRef.current &&
    (!bridge.isNative || isProviderInputActive(presentationIdRef.current)), [])

  const playAudio = useCallback((cue: MenuAudioCue, source: MenuAudioSource = 'semantic') => {
    if (!acceptsInput()) return
    audioFeedbackRef.current?.play(cue, source)
  }, [acceptsInput])

  const setSearchKeyboard = useCallback((session: SearchKeyboardSession | null) => {
    searchKeyboardRef.current = session
    setSearchKeyboardState(session)
  }, [])

  const resolveConfirmation = useCallback((approved: boolean) => {
    const resolve = confirmationResolverRef.current
    confirmationResolverRef.current = null
    setConfirmation(null)
    resolve?.(approved)
  }, [])

  const respondConfirmation = useCallback((approved: boolean, source: MenuAudioSource) => {
    playAudio(approved ? 'select' : 'back', source)
    resolveConfirmation(approved)
  }, [playAudio, resolveConfirmation])

  const requestConfirmation = useCallback((title: string) => new Promise<boolean>((resolve) => {
    confirmationResolverRef.current?.(false)
    confirmationResolverRef.current = resolve
    setConfirmation({
      title,
      message: 'This action changes your current game or saved mod state. Continue?',
    })
  }), [])

  useEffect(() => () => {
    confirmationResolverRef.current?.(false)
    confirmationResolverRef.current = null
  }, [])

  useEffect(() => {
    mountedRef.current = true
    return () => { mountedRef.current = false }
  }, [])

  useEffect(() => {
    let active = true
    const previousSnapshot = controllerRef.current?.snapshot ?? restoreSnapshot ?? undefined
    const retainPaintedSurface = reloadRevision > 0 &&
      controllerRef.current !== null &&
      loadedPresentationId === presentation.presentationId
    if (!retainPaintedSurface) {
      setLoading(true)
      setLoadedPresentationId(null)
      controllerRef.current = null
    }
    setError(null)
    setNotice(null)
    setSearchKeyboard(null)

    const menuRevision = presentation.context.menuRevision
    const loadMenus = reloadRevision === 0
      ? loadPresentedMenuTreeCached(
          api.menu.get,
          presentation.extensionId,
          presentation.menuId,
          typeof menuRevision === 'string' || typeof menuRevision === 'number' ? menuRevision : null,
        )
      : loadPresentedMenuTree(api.menu.get, presentation.extensionId, presentation.menuId)

    void loadMenus
      .then((menus) => {
        if (!active) return
        descriptorsRef.current = structuredClone(menus)
        const adapted = adaptMenusToRoutes(menus, presentation.menuId)
        const routed = allin1Presentation ? projectAllin1GbayMenu(adapted) : adapted
        if (allin1Presentation) setAccount(gbayAccountState(routed))
        let controller: MenuController
        controller = new MenuController(routed, {
          onChange: (next) => {
            if (active) {
              setSnapshot(next)
              onSnapshot?.(presentation.presentationId, next)
            }
          },
          invoke: async (invocation) => {
            const invokedLabel = controller.currentRoute.items.find((item) => item.id === invocation.nodeId)?.label
            let result = await api.menu.invoke(invocation)
            if (result.confirmationRequired) {
              if (!await requestConfirmation(invokedLabel ?? 'Confirm action')) throw new MenuActionCancelled()
              const identity = menuInvocationIdentity(presentation.presentationId, invocation)
              const idempotencyKey = confirmedInvocationKeysRef.current.get(identity) ??
                createMenuInvocationKey(presentation.presentationId, invocation.nodeId)
              confirmedInvocationKeysRef.current.set(identity, idempotencyKey)
              try {
                result = await api.menu.invoke({ ...invocation, confirmed: true, idempotencyKey })
                // Any typed host response is definitive. A thrown transport
                // failure retains the key so a retry cannot double-apply.
                confirmedInvocationKeysRef.current.delete(identity)
              } catch (error) {
                throw error
              }
            }
            if (!result.succeeded) {
              throw new Error(result.error?.message ?? 'The host rejected the menu action.')
            }
            const directive = menuResultPresentationDirective(result)
            if (directive === 'close') {
              await onClose()
              return result
            }
            if (directive === 'refresh') {
              // Action-result refreshes are immediate and authoritative. The
              // reload effect retains the painted surface and restores its
              // route/focus, so ALLIN1 does not wait for the next background
              // state event or flash a loading screen after a player action.
              setReloadRevision((revision) => revision + 1)
              return result
            }
            if (active) setNotice(result.replayed ? 'Already applied.' : 'Applied.')
            return result
          },
        })
        if (previousSnapshot) controller.restore(previousSnapshot)
        controllerRef.current = controller
        const nextSnapshot = controller.snapshot
        setSnapshot(nextSnapshot)
        onSnapshot?.(presentation.presentationId, nextSnapshot)
        setLoadedPresentationId(presentation.presentationId)
      })
      .catch((loadError) => {
        if (active) {
          setError(errorMessage(loadError))
          playAudio('error')
        }
      })
      .finally(() => {
        if (active) setLoading(false)
      })

    return () => {
      active = false
    }
  }, [allin1Presentation, onClose, onSnapshot, playAudio, presentation, reloadRevision,
    requestConfirmation, restoreSnapshot, setSearchKeyboard])

  useEffect(() => {
    if (!interactive || !allin1Presentation || loading || error ||
      loadedPresentationId !== presentation.presentationId ||
      !controllerRef.current || descriptorsRef.current.length === 0) return

    let active = true
    let pending: PendingGbayStateChange | null = null
    let appliedRevision = 0
    let draining = false
    let retryTimer: number | null = null
    let inFlight: PendingGbayStateChange | null = null
    let subscription: Awaited<ReturnType<typeof api.events.subscribe>> | null = null
    const expectedPresentationId = presentation.presentationId

    const drain = async () => {
      if (draining) return
      draining = true
      try {
        while (active && pending) {
          const change = pending
          pending = null
          inFlight = change
          const menuIds = changedMenuIdsInLoadedTree(
            descriptorsRef.current, change.menus)
          if (menuIds.length === 0) {
            appliedRevision = Math.max(appliedRevision, change.revision)
            inFlight = null
            continue
          }
          const replacements = await Promise.all(menuIds.map((menuId) =>
            api.menu.get(presentation.extensionId, menuId)))
          if (!active || presentationIdRef.current !== expectedPresentationId)
            return
          const descriptors = mergeChangedMenuDescriptors(
            descriptorsRef.current, replacements, presentation.extensionId)
          const adapted = adaptMenusToRoutes(descriptors, presentation.menuId)
          const projected = projectAllin1GbayMenu(adapted)
          const controller = controllerRef.current
          if (!controller) return
          const preserved = preserveGbayViewState(
            projected, controller.snapshot)
          descriptorsRef.current = descriptors
          controller.replaceMenu(preserved)
          setAccount(gbayAccountState(preserved))
          appliedRevision = Math.max(appliedRevision, change.revision)
          inFlight = null
        }
      } catch (refreshError) {
        if (active && inFlight) {
          pending = coalesceGbayStateChange(
            pending, inFlight, appliedRevision)
          retryTimer = window.setTimeout(() => {
            retryTimer = null
            if (active) void drain()
          }, 1_000)
        }
        globalThis.console?.warn?.(
          'REACTOR V could not apply an ALLIN1 state update.',
          refreshError,
        )
      } finally {
        inFlight = null
        draining = false
        if (active && pending && retryTimer === null) void drain()
      }
    }

    const onStateChanged = (_eventName: string, payload: unknown) => {
      const change = parseGbayStateChangedEvent(payload)
      if (!change) {
        globalThis.console?.warn?.(
          'REACTOR V ignored an invalid ALLIN1 state-change event.')
        return
      }
      pending = coalesceGbayStateChange(
        pending, change, appliedRevision)
      if (pending) void drain()
    }

    void api.events.subscribe({
      events: ['allin1.gbay.state.changed'],
      replayLatest: false,
    }, onStateChanged).then((created) => {
      if (!active) {
        created.disposeLocal()
        void created.unsubscribe().catch(() => {})
        return
      }
      subscription = created
    }).catch((subscriptionError) => {
      if (active) globalThis.console?.warn?.(
        'REACTOR V could not subscribe to ALLIN1 state updates.',
        subscriptionError,
      )
    })

    return () => {
      active = false
      pending = null
      if (retryTimer !== null) window.clearTimeout(retryTimer)
      subscription?.disposeLocal()
      if (subscription) void subscription.unsubscribe().catch(() => {})
    }
  }, [allin1Presentation, error, interactive, loadedPresentationId, loading,
    presentation.extensionId, presentation.menuId, presentation.presentationId])

  useLayoutEffect(() => {
    if (loading || !snapshot || error ||
      loadedPresentationId !== presentation.presentationId ||
      readyPresentationRef.current === presentation.presentationId) return
    const presentationId = presentation.presentationId
    // Two menu trees can coexist during an atomic replacement. Always prove
    // this presentation's own staged surface, never the previously committed
    // surface returned first by a document-wide query.
    const surface = surfaceRef.current
    if (!surface) return
    const bounds = surface.getBoundingClientRect()
    if (bounds.width < 1 || bounds.height < 1) return
    const images = Array.from(surface.querySelectorAll('img'))
    const decoded = Promise.all(images.map((image) => image.complete
      ? image.decode?.().catch(() => {}) ?? Promise.resolve()
      : new Promise<void>((resolve) => {
        image.addEventListener('load', () => resolve(), { once: true })
        image.addEventListener('error', () => resolve(), { once: true })
      })))
    const fontsReady = document.fonts?.ready?.catch(() => {}) ?? Promise.resolve()
    const readyAttempt = runMenuPresentationAcknowledgementOnce(
      readyPresentationAttemptsRef.current,
      presentationId,
      () => acknowledgePaintedMenuPresentation(
        Promise.all([decoded, fontsReady]),
        window.requestAnimationFrame.bind(window),
        () => mountedRef.current && presentationIdRef.current === presentationId
          ? readyCallbackRef.current(presentationId)
          : false,
      ),
    )
    if (!readyAttempt) return
    void readyAttempt.then((accepted) => {
      if (mountedRef.current &&
        presentationIdRef.current === presentationId && accepted) {
        readyPresentationRef.current = presentationId
      }
    }).catch((readyError) => {
      // The native host remains fail-closed. Its retry envelope is contained
      // in this one attempt; a newly issued presentation receives a new token.
      globalThis.console?.warn?.(
        `REACTOR V could not acknowledge menu presentation ${presentationId}.`,
        readyError,
      )
    })
  }, [error, loadedPresentationId, loading, onReady, presentation.presentationId, snapshot])

  useEffect(() => {
    if (!bridge.isNative || !allin1Presentation || !snapshot ||
      loadedPresentationId !== presentation.presentationId) return
    const visibleItems = snapshot.route.items.filter((item) => item.visible !== false)
    const contentItems = visibleItems.filter((item) => !isGbayNavigationItem(item))
    const actionableItems = contentItems.filter((item) =>
      item.enabled !== false && item.type !== 'status' && item.type !== 'progress' &&
      item.type !== 'media' && item.type !== 'separator')
    const statusItems = contentItems.filter((item) => item.type === 'status')
    const payloadStatus = error
      ? 'error' as const
      : loading
        ? 'loading' as const
        : contentItems.length === 0
          ? 'empty' as const
          : 'ready' as const
    const observation = {
      presentationId: presentation.presentationId,
      providerId: presentation.extensionId,
      rootMenuId: presentation.menuId,
      menuId: snapshot.menuId,
      routeId: snapshot.route.id,
      sectionId: classifyGbayRoute(snapshot.route),
      payloadStatus,
      itemCount: visibleItems.length,
      contentItemCount: contentItems.length,
      actionableItemCount: actionableItems.length,
      statusItemCount: statusItems.length,
    }
    const signature = JSON.stringify(observation)
    if (acceptanceObservationRef.current === signature) return
    acceptanceObservationRef.current = signature
    try {
      bridge.reportLiveAcceptanceMenuState(observation)
    } catch (observationError) {
      globalThis.console?.warn?.(
        'REACTOR V could not publish its live acceptance menu state.',
        observationError,
      )
    }
  }, [allin1Presentation, error, loadedPresentationId, loading, presentation, snapshot])

  const perform = useCallback(async (
    operation: (controller: MenuController) => Promise<MenuInvocation | undefined> | void,
    cue?: MenuAudioCue,
    source: MenuAudioSource = 'semantic',
  ) => {
    const controller = controllerRef.current
    if (!controller || busyRef.current) return
    if (cue) playAudio(cue, source)
    busyRef.current = true
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      await operation(controller)
    } catch (operationError) {
      if (operationError instanceof MenuActionCancelled) {
        setNotice('Cancelled.')
        return
      }
      setError(errorMessage(operationError))
      playAudio('error')
      // Controller changes are optimistic. Reload the host descriptor after a
      // rejection or cancelled confirmation so the UI cannot drift from GTA.
      setReloadRevision((revision) => revision + 1)
    } finally {
      busyRef.current = false
      setBusy(false)
    }
  }, [playAudio])

  const cancelSearchKeyboard = useCallback((source: MenuAudioSource = 'semantic') => {
    if (!searchKeyboardRef.current) return
    setSearchKeyboard(null)
    playAudio('back', source)
  }, [playAudio, setSearchKeyboard])

  const applySearchKeyboard = useCallback((source: MenuAudioSource = 'semantic') => {
    const session = searchKeyboardRef.current
    if (!session) return
    setSearchKeyboard(null)
    void perform((controller) => commitSearchKeyboardSession(controller, session), 'select', source)
  }, [perform, setSearchKeyboard])

  const moveSearchKeyboard = useCallback((
    horizontal: -1 | 0 | 1,
    vertical: -1 | 0 | 1,
    source: MenuAudioSource = 'semantic',
  ) => {
    const session = searchKeyboardRef.current
    if (!session) return
    const next = moveSearchKeyboardSelection(session, horizontal, vertical)
    setSearchKeyboard(next)
    playAudio('navigate', source)
  }, [playAudio, setSearchKeyboard])

  const focusSearchKeyboard = useCallback((keyId: string) => {
    const session = searchKeyboardRef.current
    if (!session) return
    const next = focusSearchKeyboardKey(session, keyId)
    if (next.row === session.row && next.column === session.column) return
    setSearchKeyboard(next)
    playAudio('navigate', 'pointer')
  }, [playAudio, setSearchKeyboard])

  const activateSearchKey = useCallback((
    key?: SearchKeyboardKey,
    source: MenuAudioSource = 'semantic',
  ) => {
    const session = searchKeyboardRef.current
    if (!session) return
    const result = activateSearchKeyboardKey(session, key)
    if (result.intent === 'cancel') {
      cancelSearchKeyboard(source)
      return
    }
    if (result.intent === 'apply') {
      applySearchKeyboard(source)
      return
    }
    setSearchKeyboard(result.session)
    playAudio('select', source)
  }, [applySearchKeyboard, cancelSearchKeyboard, playAudio, setSearchKeyboard])

  const updateSearchDraft = useCallback((value: string) => {
    const session = searchKeyboardRef.current
    if (session) setSearchKeyboard(updateSearchKeyboardDraft(session, value))
  }, [setSearchKeyboard])

  const openControllerSearch = useCallback((
    controller: MenuController,
    item: MenuSearchItem,
  ) => {
    if (busyRef.current) return
    if (controller.focusedItem?.id !== item.id && !controller.focus(item.id)) return
    setSearchKeyboard(createSearchKeyboardSession(controller.currentRoute.id, item))
  }, [setSearchKeyboard])

  const goBack = useCallback((source: MenuAudioSource = 'semantic') => {
    if (confirmationResolverRef.current) {
      respondConfirmation(false, source)
      return
    }
    if (searchKeyboardRef.current) {
      cancelSearchKeyboard(source)
      return
    }
    const active = document.activeElement as HTMLElement | null
    performMenuBack(controllerRef.current, active, onClose)
    playAudio('back', source)
  }, [cancelSearchKeyboard, onClose, playAudio, respondConfirmation])

  useEffect(() => {
    const onContextMenu = (event: MouseEvent) => {
      if (!acceptsInput()) return
      handleMenuContextMenu(event, bridge.isNative, () => goBack('pointer'))
    }
    // Capture at the window boundary so Chromium cannot open a native context
    // menu or forward a secondary-button side effect after Reactor handles it.
    window.addEventListener('contextmenu', onContextMenu, true)
    return () => window.removeEventListener('contextmenu', onContextMenu, true)
  }, [acceptsInput, goBack])

  useEffect(() => {
    const onWheel = (event: WheelEvent) => {
      if (!acceptsInput()) return
      if (!allin1Presentation || busyRef.current || event.deltaY === 0) return
      const target = event.target instanceof Element ? event.target : null
      if (!target?.closest('.gbay-catalog')) return
      if (target.closest('.gbay-workbench-scrollbox')) return
      event.preventDefault()
      const action = event.deltaY > 0 ? 'next-page' : 'previous-page'
      void perform(async (current) => {
        const result = await invokeGbaySemanticInput(current, action)
        return result.invocation
      }, 'navigate', 'pointer')
    }
    window.addEventListener('wheel', onWheel, { passive: false })
    return () => window.removeEventListener('wheel', onWheel)
  }, [acceptsInput, allin1Presentation, perform])

  const focusItem = useCallback((item: MenuItem, source: MenuAudioSource = 'pointer') => {
    const controller = controllerRef.current
    if (!controller || controller.focusedItem?.id === item.id || !controller.focus(item.id)) return false
    playAudio('navigate', source)
    return true
  }, [playAudio])

  const moveFocus = useCallback((controller: MenuController, delta: number, source: MenuAudioSource) => {
    const previous = controller.focusedItem?.id
    const next = controller.moveFocus(delta)
    if (next && next.id !== previous) playAudio('navigate', source)
    return next
  }, [playAudio])

  const moveTab = useCallback((controller: MenuController, delta: number, source: MenuAudioSource) => {
    if (!controller.moveTab(delta)) return false
    playAudio('navigate', source)
    return true
  }, [playAudio])

  useEffect(() => {
    // Keep the transport listener mounted while this presentation is staged.
    // `acceptsInput` is the synchronous exact-presentation gate; rebuilding
    // the subscription after `interactive` flips would leave one passive-
    // effect interval where native input is authorized but no listener exists.
    const unsubscribe = api.events.onInput((input: InputActionEvent) => {
      if (!acceptsInput()) return
      if (input.phase !== 'pressed' && input.phase !== 'repeated') return
      if (confirmationResolverRef.current) {
        if (input.action === 'back') goBack('semantic')
        else if (input.action === 'accept') respondConfirmation(true, 'semantic')
        return
      }
      if (searchKeyboardRef.current) {
        switch (input.action) {
          case 'navigate-up': moveSearchKeyboard(0, -1); break
          case 'navigate-down': moveSearchKeyboard(0, 1); break
          case 'navigate-left': moveSearchKeyboard(-1, 0); break
          case 'navigate-right': moveSearchKeyboard(1, 0); break
          case 'accept': activateSearchKey(); break
          case 'back': cancelSearchKeyboard(); break
        }
        return
      }
      const controller = controllerRef.current
      if (!controller) {
        if (input.action === 'back') goBack('semantic')
        return
      }
      switch (input.action) {
        case 'navigate-up':
          if (allin1Presentation && moveGbayCardFocus(controller, 0, -1)) playAudio('navigate')
          else moveFocus(controller, -1, 'semantic')
          break
        case 'navigate-down':
          if (allin1Presentation && moveGbayCardFocus(controller, 0, 1)) playAudio('navigate')
          else moveFocus(controller, 1, 'semantic')
          break
        case 'navigate-left':
          if (allin1Presentation && gbayCardEdgePageAction(controller, -1)) {
            void perform(async (current) => {
              const result = await invokeGbaySemanticInput(current, 'previous-page')
              return result.invocation
            }, 'navigate')
          } else if (allin1Presentation && moveGbayCardFocus(controller, -1, 0)) playAudio('navigate')
          else void perform((current) => current.adjust(-1), 'navigate')
          break
        case 'navigate-right':
          if (allin1Presentation && gbayCardEdgePageAction(controller, 1)) {
            void perform(async (current) => {
              const result = await invokeGbaySemanticInput(current, 'next-page')
              return result.invocation
            }, 'navigate')
          } else if (allin1Presentation && moveGbayCardFocus(controller, 1, 0)) playAudio('navigate')
          else void perform((current) => current.adjust(1), 'navigate')
          break
        case 'accept': {
          const focused = controller.focusedItem
          if (focused?.type === 'search') {
            openControllerSearch(controller, focused)
            playAudio('select')
          } else {
            void perform((current) => current.activate(), 'select')
          }
          break
        }
        case 'back': goBack('semantic'); break
        case 'previous-tab': if (!moveTab(controller, -1, 'semantic')) moveFocus(controller, -1, 'semantic'); break
        case 'next-tab': if (!moveTab(controller, 1, 'semantic')) moveFocus(controller, 1, 'semantic'); break
        case 'previous-page':
        case 'next-page':
        case 'previous-category':
        case 'next-category':
        case 'filter-next':
        case 'favorite':
          if (!allin1Presentation) break
          void perform(async (current) => {
            const result = await invokeGbaySemanticInput(current, input.action, {
              focusSearch: () => document.querySelector<HTMLInputElement>('.gbay-search input')?.focus(),
            })
            if (result.handled) return result.invocation
            if (input.action === 'previous-page' || input.action === 'previous-category') {
              if (!current.moveTab(-1)) current.moveFocus(-1)
            } else if (input.action === 'next-page' || input.action === 'next-category') {
              if (!current.moveTab(1)) current.moveFocus(1)
            }
            return undefined
          }, input.action === 'favorite' ? 'select' : 'navigate')
          break
        case 'search':
          if (!allin1Presentation || busyRef.current) break
          // Controller entry remains entirely inside the provider UI. Apply
          // still routes through the existing typed search node.
          void invokeGbaySemanticInput(controller, input.action, {
            openSearch: (item) => openControllerSearch(controller, item),
          })
          playAudio('select')
          break
      }
    })
    return unsubscribe
  }, [acceptsInput, activateSearchKey, allin1Presentation, cancelSearchKeyboard, goBack, moveFocus,
    moveSearchKeyboard, moveTab, openControllerSearch, perform, playAudio, respondConfirmation])

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (!acceptsInput()) return
      const target = event.target as HTMLElement | null
      const editing = target?.tagName === 'INPUT' || target?.tagName === 'TEXTAREA' || target?.tagName === 'SELECT'
      // Native key mappings also arrive as input.action. Handling the same
      // physical press here would invoke actions twice. Native DOM keys are
      // reserved for the focused editor; browser demo keeps full keyboard nav.
      if (bridge.isNative) {
        if (editing && event.key === 'Escape') {
          target?.blur()
          event.preventDefault()
        }
        return
      }
      if (confirmationResolverRef.current) {
        if (event.key === 'Enter' || event.key === ' ') respondConfirmation(true, 'keyboard')
        else if (event.key === 'Escape' || event.key === 'Backspace') respondConfirmation(false, 'keyboard')
        else return
        event.preventDefault()
        event.stopPropagation()
        return
      }
      if (editing && event.key !== 'Escape') return
      const controller = controllerRef.current
      if (!controller) return
      let handled = true
      switch (event.key) {
        case 'ArrowUp': moveFocus(controller, -1, 'keyboard'); break
        case 'ArrowDown': moveFocus(controller, 1, 'keyboard'); break
        case 'ArrowLeft': void perform((current) => current.adjust(-1), 'navigate', 'keyboard'); break
        case 'ArrowRight': void perform((current) => current.adjust(1), 'navigate', 'keyboard'); break
        case 'Enter':
        case ' ': void perform((current) => current.activate(), 'select', 'keyboard'); break
        case 'Escape':
        case 'Backspace': goBack('keyboard'); break
        default: handled = false
      }
      if (handled) {
        event.preventDefault()
        event.stopPropagation()
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [acceptsInput, goBack, moveFocus, perform, respondConfirmation])

  useEffect(() => {
    // Atomic replacement keeps two MenuSurface trees mounted. Scroll only this
    // presentation's own focus target so a hidden replacement cannot move the
    // retained visible menu before its exact ready acknowledgement.
    const focused = surfaceRef.current
      ?.querySelector<HTMLElement>('[data-menu-focused="true"]')
    // Repeated controller navigation must not queue compositor animations in
    // the transparent overlay window. The nearest instant scroll is both more
    // responsive and deterministic for gamepad repeat input.
    focused?.scrollIntoView({ block: 'nearest', behavior: 'auto' })
  }, [snapshot?.focusedItemId, snapshot?.route.id])

  useEffect(() => {
    if (!allin1Presentation || !snapshot) return
    const section = classifyGbayRoute(snapshot.route)
    const previous = weaponPreviewRouteRef.current
    if (previous && section !== 'customization') {
      // Route navigation is local to Reactor, so explicitly release the
      // ALLIN1 world preview through its separately typed read action.
      void api.menu.invoke({
        extensionId: presentation.extensionId,
        menuId: previous.menuId,
        nodeId: previous.stopNodeId,
        interaction: 'activate',
      }).catch(() => undefined)
      weaponPreviewRouteRef.current = null
      lastWeaponPreviewRef.current = null
      return
    }
    if (section !== 'customization') return
    const stop = snapshot.route.items.find((item) =>
      isGbayWeaponPreviewAction(item) &&
      item.action.toLowerCase() === 'weapon.customize.preview.stop')
    weaponPreviewRouteRef.current = stop
      ? {
          routeId: snapshot.route.id,
          menuId: snapshot.menuId,
          stopNodeId: stop.id,
        }
      : null
  }, [allin1Presentation, presentation.extensionId,
    snapshot?.menuId, snapshot?.route.id, snapshot?.route.items])

  useEffect(() => {
    if (!allin1Presentation || !snapshot ||
      classifyGbayRoute(snapshot.route) !== 'customization') return
    const focused = snapshot.route.items.find((item) =>
      item.id === snapshot.focusedItemId)
    if (!focused || !isGbayCustomizationOption(focused)) {
      lastWeaponPreviewRef.current = null
      return
    }
    const preview = gbayWeaponPreviewNode(snapshot.route.items, focused)
    if (!preview) return
    const key = `${presentation.presentationId}:${preview.id}`
    if (lastWeaponPreviewRef.current === key) return
    lastWeaponPreviewRef.current = key
    // Focus changes are read-only preview intents. They bypass the mutation
    // confirmation path and never reuse the apply node's authority.
    void api.menu.invoke({
      extensionId: presentation.extensionId,
      menuId: snapshot.menuId,
      nodeId: preview.id,
      interaction: 'activate',
    }).catch(() => {
      if (lastWeaponPreviewRef.current === key)
        lastWeaponPreviewRef.current = null
    })
  }, [allin1Presentation, presentation.extensionId,
    presentation.presentationId, snapshot?.focusedItemId,
    snapshot?.menuId, snapshot?.route.id, snapshot?.route.items])

  const focusAnd = (
    item: MenuItem,
    action: (controller: MenuController) => Promise<MenuInvocation | undefined>,
    cue: MenuAudioCue = 'select',
  ) => {
    const controller = controllerRef.current
    if (!controller) return
    focusItem(item, 'pointer')
    void perform(action, cue, 'pointer')
  }

  const searchKeyboardSurface = searchKeyboard && (
    <SearchKeyboard
      session={searchKeyboard}
      onDraft={updateSearchDraft}
      onMove={(horizontal, vertical) => moveSearchKeyboard(horizontal, vertical, 'keyboard')}
      onFocusKey={focusSearchKeyboard}
      onActivate={(key) => activateSearchKey(key, 'pointer')}
      onApply={() => applySearchKeyboard('keyboard')}
      onCancel={() => cancelSearchKeyboard('pointer')}
    />
  )

  if (allin1Presentation) return (
    <>
      <GbaySurface
        surfaceRef={surfaceRef}
        snapshot={snapshot}
        account={account}
        loading={loading}
        busy={busy}
        error={error}
        notice={notice}
        onClose={() => { playAudio('back', 'pointer'); return onClose() }}
        onFocus={(item) => { focusItem(item, 'pointer') }}
        onActivate={(item) => focusAnd(item, (controller) => controller.activate())}
        onSetValue={(item, value) => focusAnd(item, (controller) => controller.setValue(value), 'navigate')}
        onRetry={() => { playAudio('select', 'pointer'); setReloadRevision((revision) => revision + 1) }}
      />
      {confirmation && <GbayConfirmationDialog confirmation={confirmation} onRespond={(confirmed) => respondConfirmation(confirmed, 'pointer')} />}
      {searchKeyboardSurface}
    </>
  )

  const renderItem = (item: MenuItem) => {
    if (item.visible === false) return null
    const focused = snapshot?.focusedItemId === item.id
    const disabled = item.enabled === false || busy
    const commonProps = {
      className: `menu-item menu-item-${item.type}${focused ? ' focused' : ''}${disabled ? ' disabled' : ''}`,
      'data-menu-focused': focused ? 'true' : 'false',
      onMouseEnter: () => focusItem(item, 'pointer'),
    }

    if (item.type === 'separator') return <div key={item.id} className="menu-separator" role="separator" />
    if (item.type === 'status') return (
      <article key={item.id} {...commonProps} role="status">
        <span><strong>{item.label}</strong><small>{item.description}</small></span>
        <span className={`menu-value tone-${item.tone ?? 'neutral'}`}>{item.value}</span>
      </article>
    )
    if (item.type === 'progress') return (
      <article key={item.id} {...commonProps}>
        <span><strong>{item.label}</strong><small>{item.description}</small></span>
        <progress value={item.value} max={item.max} aria-label={item.label} />
      </article>
    )
    if (item.type === 'media') return (
      <article key={item.id} {...commonProps}>
        {item.mediaType.startsWith('image/') || item.mediaType === 'image'
          ? <img className="menu-media" src={item.source} alt={item.alt ?? item.label} />
          : <span><strong>{item.label}</strong><small>{item.alt ?? item.mediaType}</small></span>}
      </article>
    )
    if (item.type === 'choice') return (
      <label key={item.id} {...commonProps}>
        <span><strong>{item.label}</strong><small>{item.description}</small></span>
        <select
          value={item.value}
          disabled={disabled}
          onFocus={() => focusItem(item, 'pointer')}
          onChange={(event) => focusAnd(item, (controller) => controller.setValue(event.currentTarget.value), 'navigate')}
        >
          {item.options.map((option) => <option key={option.value} value={option.value} disabled={option.disabled}>{option.label}</option>)}
        </select>
      </label>
    )
    if (item.type === 'range') return (
      <label key={`${item.id}:${item.value}`} {...commonProps}>
        <span><strong>{item.label}</strong><small>{item.description}</small></span>
        <span className="menu-range-control">
          <input
            type="range" min={item.min} max={item.max} step={item.step} defaultValue={item.value}
            disabled={disabled}
            onFocus={() => focusItem(item, 'pointer')}
            onPointerUp={(event) => focusAnd(item, (controller) => controller.setValue(Number(event.currentTarget.value)), 'navigate')}
            onKeyUp={(event) => {
              if (event.key === 'ArrowLeft' || event.key === 'ArrowRight' || event.key === 'Home' || event.key === 'End') {
                focusAnd(item, (controller) => controller.setValue(Number(event.currentTarget.value)), 'navigate')
              }
            }}
          />
          <output>{itemValue(item)}</output>
        </span>
      </label>
    )
    if (item.type === 'text' || item.type === 'search') return (
      <label key={`${item.id}:${item.value}`} {...commonProps}>
        <span><strong>{item.label}</strong><small>{item.description}</small></span>
        <input
          type={item.type === 'search' ? 'search' : item.sensitive ? 'password' : 'text'}
          defaultValue={item.value} placeholder={item.placeholder} maxLength={item.maxLength}
          disabled={disabled}
          onFocus={() => focusItem(item, 'pointer')}
          onBlur={(event) => focusAnd(item, (controller) => controller.setValue(event.currentTarget.value), 'navigate')}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              event.preventDefault()
              event.currentTarget.blur()
            }
          }}
        />
      </label>
    )
    if (item.type === 'list' || item.type === 'grid') return (
      <article key={item.id} {...commonProps}>
        <span><strong>{item.label}</strong><small>{item.description}</small></span>
        <div className={`menu-entry-set ${item.type}`}>
          {item.entries.map((entry) => (
            <button
              key={entry.id} type="button" disabled={disabled || entry.disabled}
              className={item.selectedId === entry.id ? 'selected' : ''}
              onClick={() => focusAnd(item, (controller) => controller.setValue(entry.id), 'navigate')}
            >
              {entry.image && <img src={entry.image} alt="" />}
              <span>{entry.label}</span>{entry.badge && <small>{entry.badge}</small>}
            </button>
          ))}
        </div>
      </article>
    )
    if (item.type === 'pagination') return (
      <article key={item.id} {...commonProps}>
        <span><strong>{item.label}</strong><small>{item.description}</small></span>
        <span className="menu-pagination">
          <button
            type="button" aria-label="Previous page" disabled={disabled || item.page <= 1}
            onClick={() => focusAnd(item, (controller) => controller.setValue(item.page - 1), 'navigate')}
          >‹</button>
          <output>{itemValue(item)}</output>
          <button
            type="button" aria-label="Next page" disabled={disabled || item.page >= item.pageCount}
            onClick={() => focusAnd(item, (controller) => controller.setValue(item.page + 1), 'navigate')}
          >›</button>
        </span>
      </article>
    )

    return (
      <button
        key={item.id} type="button" {...commonProps} disabled={disabled}
        onClick={() => focusAnd(item, (controller) => controller.activate())}
      >
        <span><strong>{item.label}</strong><small>{item.description}</small></span>
        <span className="menu-value">
          {itemValue(item)}
          {item.type === 'route' && <span aria-hidden="true">›</span>}
        </span>
      </button>
    )
  }

  return (
    <main
      ref={surfaceRef}
      className="menu-stage"
      data-reactor-menu-surface-root="true"
      aria-busy={loading || busy}
    >
      <section className="menu-shell" aria-label="REACTOR V menu">
        <header className="menu-header">
          <button type="button" className="menu-back" onClick={() => goBack('pointer')} aria-label="Back">‹</button>
          <span>
            <small>{presentation.extensionId}</small>
            <h1>{snapshot?.route.title ?? 'Loading menu'}</h1>
            {snapshot?.route.subtitle && <p>{snapshot.route.subtitle}</p>}
          </span>
          <button type="button" className="menu-close" onClick={() => { playAudio('back', 'pointer'); void onClose() }} aria-label="Close">×</button>
        </header>

        <div className={`menu-content layout-${snapshot?.route.layout ?? 'list'}`} style={{ '--menu-columns': snapshot?.route.columns ?? 1 } as CSSProperties}>
          {loading && <div className="menu-message"><span className="spinner" /> Loading menu…</div>}
          {!loading && error && !snapshot && <div className="menu-message error"><strong>Menu unavailable</strong><span>{error}</span><button type="button" onClick={() => { playAudio('select', 'pointer'); setReloadRevision((revision) => revision + 1) }}>Try again</button></div>}
          {!loading && snapshot?.route.items.filter((item) => item.visible !== false).map(renderItem)}
        </div>

        <footer className="menu-footer">
          <span>{error ?? notice ?? (busy ? 'Applying…' : 'Ready')}</span>
          <span>↑↓ navigate · ←→ adjust · Enter select · Esc back</span>
        </footer>
      </section>

      {confirmation && (
        <div className="menu-confirmation" role="dialog" aria-modal="true" aria-labelledby="menu-confirmation-title">
          <div>
            <small>Confirmation required</small>
            <h2 id="menu-confirmation-title">{confirmation.title}</h2>
            <p>{confirmation.message}</p>
            <span className="confirmation-actions">
              <button type="button" onClick={() => respondConfirmation(false, 'pointer')}>Cancel</button>
              <button type="button" className="primary" autoFocus onClick={() => respondConfirmation(true, 'pointer')}>Continue</button>
            </span>
          </div>
        </div>
      )}
      {searchKeyboardSurface}
    </main>
  )
}
