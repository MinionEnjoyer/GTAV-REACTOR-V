import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const bridgeHarness = vi.hoisted(() => {
  const listeners = new Map<string, Set<(payload: unknown) => void>>()
  return {
    bridge: {
      isNative: true,
      on(eventName: string, listener: (payload: unknown) => void) {
        const eventListeners = listeners.get(eventName) ?? new Set<(payload: unknown) => void>()
        eventListeners.add(listener)
        listeners.set(eventName, eventListeners)
        return () => eventListeners.delete(listener)
      },
    },
    emit(eventName: string, payload: unknown) {
      for (const listener of listeners.get(eventName) ?? []) listener(payload)
    },
    reset() {
      listeners.clear()
    },
  }
})

vi.mock('./bridge', () => ({ bridge: bridgeHarness.bridge }))

import { installWindowedInputForwarding } from './windowedInput'
import {
  activateProviderInput,
  prepareProviderInput,
  revokeProviderInput,
} from './providerInputGate'

class TestMouseEvent {
  constructor(
    public readonly type: string,
    public readonly init: Record<string, unknown> = {},
  ) {}
}

class TestWheelEvent extends TestMouseEvent {
  readonly deltaY: number

  constructor(type: string, init: Record<string, unknown> = {}) {
    super(type, init)
    this.deltaY = typeof init.deltaY === 'number' ? init.deltaY : 0
  }
}

class TestElement {
  readonly dataset: Record<string, string> = {}
  readonly style: Record<string, string> = {}
  parentElement: TestElement | null = null
  readonly children: TestElement[] = []
  readonly selectors = new Set<string>()
  clicks = 0
  readonly dispatchedEvents: string[] = []
  onDispatch: ((event: TestMouseEvent) => void) | null = null
  focused = false
  removed = false
  scrollHeight = 0
  clientHeight = 0
  scrollWidth = 0
  clientWidth = 0

  constructor(
    readonly interactive = false,
    readonly role?: 'tab',
  ) {}

  append(child: TestElement) {
    child.parentElement = this
    this.children.push(child)
  }

  remove() {
    this.removed = true
  }

  focus() {
    this.focused = true
  }

  click() {
    this.clicks += 1
  }

  dispatchEvent(event: TestMouseEvent) {
    this.dispatchedEvents.push(event.type)
    this.onDispatch?.(event)
    return true
  }

  scrollBy(_options: unknown) {}

  closest<T>(selector: string): T | null {
    let candidate: TestElement | null = this
    while (candidate) {
      if (candidate.selectors.has(selector) ||
        (selector.includes('button') && candidate.interactive) ||
        (selector.includes('[role="tab"]') && candidate.role === 'tab')) return candidate as T
      candidate = candidate.parentElement
    }
    return null
  }
}

class TestButtonElement extends TestElement {}

describe('windowed input forwarding', () => {
  let pointTarget: TestElement | null
  let createdElements: TestElement[]
  let body: TestElement
  let root: TestElement

  beforeEach(() => {
    bridgeHarness.reset()
    revokeProviderInput()
    prepareProviderInput('test-presentation')
    activateProviderInput('test-presentation')
    pointTarget = null
    createdElements = []
    body = new TestElement()
    root = new TestElement()
    root.selectors.add('#root')
    body.append(root)
    vi.stubGlobal('HTMLElement', TestElement)
    vi.stubGlobal('HTMLButtonElement', TestButtonElement)
    vi.stubGlobal('HTMLSelectElement', class extends TestElement {})
    vi.stubGlobal('MouseEvent', TestMouseEvent)
    vi.stubGlobal('WheelEvent', TestWheelEvent)
    vi.stubGlobal('window', {
      innerWidth: 1000,
      innerHeight: 500,
      scrollBy() {},
    })
    vi.stubGlobal('document', {
      body,
      getElementById: (id: string) => id === 'root' ? root : null,
      createElement: () => {
        const element = new TestElement()
        createdElements.push(element)
        return element
      },
      elementFromPoint: () => pointTarget,
    })
  })

  afterEach(() => {
    revokeProviderInput()
    bridgeHarness.reset()
    vi.unstubAllGlobals()
  })

  it('clicks once across descendants of one tab and never across neighboring tabs', () => {
    const firstTab = new TestElement(true, 'tab')
    const firstTabLabel = new TestElement()
    const firstTabIcon = new TestElement()
    firstTab.append(firstTabLabel)
    firstTab.append(firstTabIcon)

    const secondTab = new TestElement(true, 'tab')
    const secondTabLabel = new TestElement()
    secondTab.append(secondTabLabel)

    const dispose = installWindowedInputForwarding()
    const pointer = (pressed: boolean, released: boolean) => bridgeHarness.emit('input.pointer', {
      x: 0.25,
      y: 0.5,
      pressed,
      released,
      wheelDelta: 0,
    })

    pointTarget = firstTabLabel
    pointer(true, false)
    pointTarget = firstTabIcon
    pointer(false, true)

    expect(firstTab.clicks).toBe(1)
    expect(secondTab.clicks).toBe(0)

    const firstTabMouseUps = firstTab.dispatchedEvents.filter((type) => type === 'mouseup').length
    pointTarget = firstTabLabel
    pointer(true, false)
    pointTarget = new TestElement()
    pointer(false, true)

    expect(firstTab.clicks).toBe(1)
    expect(firstTab.dispatchedEvents.filter((type) => type === 'mouseup')).toHaveLength(firstTabMouseUps + 1)

    pointTarget = firstTabLabel
    pointer(true, false)
    pointTarget = secondTabLabel
    pointer(false, true)

    expect(firstTab.clicks).toBe(1)
    expect(secondTab.clicks).toBe(0)
    dispose()
  })

  it('offers forwarded wheel input to the DOM before applying scroll fallback', () => {
    const catalog = new TestElement()
    pointTarget = catalog
    const dispose = installWindowedInputForwarding()

    bridgeHarness.emit('input.pointer', {
      x: 0.25,
      y: 0.5,
      pressed: false,
      released: false,
      wheelDelta: -120,
    })

    expect(catalog.dispatchedEvents).toContain('wheel')
    dispose()
  })

  it('renders a DOM cursor and releases a held provider control on pointer reset', () => {
    const button = new TestElement(true)
    pointTarget = button
    const dispose = installWindowedInputForwarding()

    bridgeHarness.emit('input.pointer', {
      x: 0.25,
      y: 0.5,
      pressed: true,
      released: false,
      wheelDelta: 0,
    })

    const cursor = createdElements.find(
      (element) => element.dataset.reactorWindowedCursor === 'true',
    )
    expect(cursor).toBeDefined()
    expect(cursor?.style.display).toBe('block')
    expect(cursor?.style.left).toBe('250px')
    expect(cursor?.style.top).toBe('250px')
    expect(button.dispatchedEvents).toContain('mousedown')

    bridgeHarness.emit('input.pointerReset', null)

    expect(button.dispatchedEvents).toContain('mouseup')
    expect(button.clicks).toBe(0)
    expect(cursor?.style.display).toBe('none')
    dispose()
  })

  it.each(['mouseout', 'mouseup'])('clears the provider press before a reentrant %s reset listener', (eventType) => {
    const button = new TestElement(true)
    pointTarget = button
    const dispose = installWindowedInputForwarding()
    button.onDispatch = (event) => {
      if (event.type === eventType) bridgeHarness.emit('input.pointerReset', null)
    }

    bridgeHarness.emit('input.pointer', {
      x: 0.25,
      y: 0.5,
      pressed: true,
      released: false,
      wheelDelta: 0,
    })
    const cursor = createdElements.find(
      (element) => element.dataset.reactorWindowedCursor === 'true',
    )
    bridgeHarness.emit('input.pointerReset', null)

    expect(button.dispatchedEvents.filter((type) => type === 'mouseout')).toHaveLength(1)
    expect(button.dispatchedEvents.filter((type) => type === 'mouseup')).toHaveLength(1)
    expect(cursor?.style.display).toBe('none')
    dispose()
  })

  it('abandons the first provider sample when bootstrap cleanup synchronously closes its gate', () => {
    revokeProviderInput()
    const bootstrapSurface = new TestElement()
    bootstrapSurface.selectors.add('.reactor-about-surface')
    const bootstrapTabs = new TestElement()
    bootstrapTabs.selectors.add('.reactor-about-tabs')
    const bootstrapAction = new TestButtonElement(true)
    bootstrapAction.selectors.add('[data-reactor-bootstrap-action]')
    bootstrapAction.dataset.reactorBootstrapAction = 'overview'
    bootstrapTabs.append(bootstrapAction)
    bootstrapSurface.append(bootstrapTabs)
    const providerButton = new TestElement(true)
    const dispose = installWindowedInputForwarding()

    pointTarget = bootstrapAction
    bridgeHarness.emit('input.bootstrapPointer', {
      x: 0.1,
      y: 0.2,
      pressed: false,
      released: false,
      wheelDelta: 0,
    })
    const cursor = createdElements.find(
      (element) => element.dataset.reactorWindowedCursor === 'true',
    )
    bootstrapAction.onDispatch = (event) => {
      if (event.type === 'mouseout') revokeProviderInput()
    }
    prepareProviderInput('gbay')
    expect(activateProviderInput('gbay')).toBe(true)

    pointTarget = providerButton
    bridgeHarness.emit('input.pointer', {
      x: 0.75,
      y: 0.25,
      pressed: true,
      released: false,
      wheelDelta: 0,
    })

    expect(bootstrapAction.dispatchedEvents).toContain('mouseout')
    expect(providerButton.dispatchedEvents).toHaveLength(0)
    expect(cursor?.style.display).toBe('none')
    expect(cursor?.dataset.reactorWindowedCursorOwner).toBe('none')
    dispose()
  })

  it('hands the body cursor from bootstrap to GBAY without stale bootstrap traffic stealing its held provider press', () => {
    revokeProviderInput()
    const bootstrapSurface = new TestElement()
    bootstrapSurface.selectors.add('.reactor-about-surface')
    const bootstrapTabs = new TestElement()
    bootstrapTabs.selectors.add('.reactor-about-tabs')
    const bootstrapAction = new TestButtonElement(true)
    bootstrapAction.selectors.add('[data-reactor-bootstrap-action]')
    bootstrapAction.dataset.reactorBootstrapAction = 'overview'
    bootstrapTabs.append(bootstrapAction)
    bootstrapSurface.append(bootstrapTabs)
    const button = new TestElement(true)
    const dispose = installWindowedInputForwarding()
    const pointer = (x: number, y: number, pressed: boolean, released: boolean) => bridgeHarness.emit('input.pointer', {
      x,
      y,
      pressed,
      released,
      wheelDelta: 0,
    })

    pointTarget = bootstrapAction
    bridgeHarness.emit('input.bootstrapPointer', {
      x: 0.1,
      y: 0.2,
      pressed: false,
      released: false,
      wheelDelta: 0,
    })

    const cursor = createdElements.find(
      (element) => element.dataset.reactorWindowedCursor === 'true',
    )
    expect(cursor).toBeDefined()
    expect(cursor?.parentElement).toBe(body)
    expect(cursor?.parentElement).not.toBe(root)
    expect(body.children.at(-1)).toBe(cursor)
    expect(cursor?.style.position).toBe('fixed')
    expect(cursor?.style.pointerEvents).toBe('none')
    expect(cursor?.style.zIndex).toBe('2147483647')
    expect(cursor?.dataset.reactorWindowedCursorOwner).toBe('bootstrap')
    expect(bootstrapAction.dispatchedEvents).toContain('mousemove')

    // Provider reset and rejected provider traffic own neither the splash
    // cursor nor its bootstrap press state.
    bridgeHarness.emit('input.pointerReset', null)
    pointer(0.6, 0.6, true, false)
    expect(cursor?.style.display).toBe('block')
    expect(cursor?.dataset.reactorWindowedCursorOwner).toBe('bootstrap')
    expect(cursor?.style.left).toBe('100px')
    expect(cursor?.style.top).toBe('100px')

    bridgeHarness.emit('host.provider', { connected: true, sessionGeneration: 7 })
    prepareProviderInput('gbay')
    expect(activateProviderInput('gbay')).toBe(true)
    pointTarget = button
    pointer(0.75, 0.25, true, false)
    expect(button.dispatchedEvents.filter((type) => type === 'mousedown')).toHaveLength(1)
    expect(cursor?.style.display).toBe('block')
    expect(cursor?.dataset.reactorWindowedCursorOwner).toBe('provider')

    // These are late messages from the splash document.  Once GBAY holds the
    // provider lease they must not hide its cursor or release its press.
    const bootstrapEventsBeforeLateTraffic = bootstrapAction.dispatchedEvents.length
    bridgeHarness.emit('host.provider', { connected: true, sessionGeneration: 7 })
    bridgeHarness.emit('host.surface', { mode: 'not-a-surface' })
    bridgeHarness.emit('host.surface', { mode: 'none', generation: 11 })
    bridgeHarness.emit('input.bootstrapPointerReset', null)
    pointTarget = bootstrapAction
    bridgeHarness.emit('input.bootstrapPointer', {
      x: 0.2,
      y: 0.8,
      pressed: true,
      released: true,
      wheelDelta: 0,
    })

    expect(cursor?.style.display).toBe('block')
    expect(cursor?.dataset.reactorWindowedCursorOwner).toBe('provider')
    expect(cursor?.style.left).toBe('749px')
    expect(cursor?.style.top).toBe('125px')
    expect(button.dispatchedEvents.filter((type) => type === 'mouseup')).toHaveLength(0)
    expect(bootstrapAction.dispatchedEvents).toHaveLength(bootstrapEventsBeforeLateTraffic)
    expect(bootstrapAction.clicks).toBe(0)

    pointTarget = button
    pointer(0.75, 0.25, false, true)
    expect(button.dispatchedEvents.filter((type) => type === 'mouseup')).toHaveLength(1)
    expect(button.clicks).toBe(1)

    dispose()
    dispose()
    expect(cursor?.removed).toBe(true)
  })

  it.each([
    ['provider reset', () => bridgeHarness.emit('input.pointerReset', null)],
    ['provider loss', () => bridgeHarness.emit('host.provider', { connected: false, sessionGeneration: 1 })],
    ['new provider session', () => bridgeHarness.emit('host.provider', { connected: true, sessionGeneration: 1 })],
    ['bootstrap supersession', () => bridgeHarness.emit('host.surface', { mode: 'initializing', generation: 8 })],
  ])('retires a held provider cursor exactly once on %s', (_name, revoke) => {
    const button = new TestElement(true)
    pointTarget = button
    const dispose = installWindowedInputForwarding()

    bridgeHarness.emit('input.pointer', {
      x: 0.25,
      y: 0.5,
      pressed: true,
      released: false,
      wheelDelta: 0,
    })
    const cursor = createdElements.find(
      (element) => element.dataset.reactorWindowedCursor === 'true',
    )
    revoke()
    revoke()

    expect(cursor?.style.display).toBe('none')
    expect(button.dispatchedEvents.filter((type) => type === 'mouseup')).toHaveLength(1)
    expect(button.clicks).toBe(0)
    dispose()
  })

  it('drops pointer traffic while preparation is closed and resets a held press on replacement', () => {
    const button = new TestElement(true)
    pointTarget = button
    const dispose = installWindowedInputForwarding()
    const pointer = (pressed: boolean, released: boolean) => bridgeHarness.emit('input.pointer', {
      x: 0.25,
      y: 0.5,
      pressed,
      released,
      wheelDelta: 0,
    })

    pointer(true, false)
    const cursor = createdElements.find(
      (element) => element.dataset.reactorWindowedCursor === 'true',
    )
    expect(button.dispatchedEvents).toContain('mousedown')
    expect(cursor?.style.display).toBe('block')

    prepareProviderInput('replacement')

    expect(button.dispatchedEvents).toContain('mouseup')
    expect(cursor?.style.display).toBe('none')
    const eventCount = button.dispatchedEvents.length
    pointer(true, true)
    expect(button.dispatchedEvents).toHaveLength(eventCount)
    expect(button.clicks).toBe(0)

    expect(activateProviderInput('test-presentation')).toBe(false)
    expect(activateProviderInput('replacement')).toBe(true)
    pointer(true, false)
    expect(button.dispatchedEvents.at(-1)).toBe('mousedown')
    expect(cursor?.style.display).toBe('block')
    dispose()
  })

  it('revokes provider input immediately on provider loss or bootstrap supersession', () => {
    const button = new TestElement(true)
    pointTarget = button
    const dispose = installWindowedInputForwarding()
    const press = () => bridgeHarness.emit('input.pointer', {
      x: 0.25,
      y: 0.5,
      pressed: true,
      released: false,
      wheelDelta: 0,
    })

    press()
    bridgeHarness.emit('host.provider', { connected: false, sessionGeneration: 2 })
    const afterProviderLoss = button.dispatchedEvents.length
    press()
    expect(button.dispatchedEvents).toHaveLength(afterProviderLoss)

    prepareProviderInput('next')
    activateProviderInput('next')
    press()
    bridgeHarness.emit('host.surface', { mode: 'initializing', generation: 8 })
    const afterBootstrap = button.dispatchedEvents.length
    press()
    expect(button.dispatchedEvents).toHaveLength(afterBootstrap)
    dispose()
  })

  it('ignores a disconnect from an older provider session', () => {
    const button = new TestElement(true)
    pointTarget = button
    const dispose = installWindowedInputForwarding()
    const press = () => bridgeHarness.emit('input.pointer', {
      x: 0.25,
      y: 0.5,
      pressed: true,
      released: false,
      wheelDelta: 0,
    })

    bridgeHarness.emit('host.provider', { connected: true, sessionGeneration: 4 })
    prepareProviderInput('current-session')
    expect(activateProviderInput('current-session')).toBe(true)
    press()
    const beforeStaleDisconnect = button.dispatchedEvents.length

    bridgeHarness.emit('host.provider', { connected: false, sessionGeneration: 3 })
    press()
    expect(button.dispatchedEvents.length).toBeGreaterThan(beforeStaleDisconnect)

    bridgeHarness.emit('host.provider', { connected: true, sessionGeneration: 5 })
    const afterReplacementConnect = button.dispatchedEvents.length
    press()
    expect(button.dispatchedEvents).toHaveLength(afterReplacementConnect)
    dispose()
  })
})
