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
  clicks = 0
  readonly dispatchedEvents: string[] = []
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
    return true
  }

  scrollBy(_options: unknown) {}

  closest<T>(_selector: string): T | null {
    let candidate: TestElement | null = this
    while (candidate) {
      if (candidate.interactive || candidate.role === 'tab') return candidate as T
      candidate = candidate.parentElement
    }
    return null
  }
}

describe('windowed input forwarding', () => {
  let pointTarget: TestElement | null
  let createdElements: TestElement[]

  beforeEach(() => {
    bridgeHarness.reset()
    revokeProviderInput()
    prepareProviderInput('test-presentation')
    activateProviderInput('test-presentation')
    pointTarget = null
    createdElements = []
    const body = new TestElement()
    vi.stubGlobal('HTMLElement', TestElement)
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
