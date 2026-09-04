import { afterEach, describe, expect, it, vi } from 'vitest'
import { GtaBridge, GtaBridgeError } from './bridge'
import type { WebViewTransport } from './types'

class FakeTransport implements WebViewTransport {
  listener?: (event: { data: unknown }) => void
  readonly sent: Record<string, unknown>[] = []
  throwOnPost = false

  postMessage(message: unknown): void {
    if (this.throwOnPost) throw new Error('transport closed')
    this.sent.push(message as Record<string, unknown>)
  }

  addEventListener(_type: 'message', listener: (event: { data: unknown }) => void): void {
    this.listener = listener
  }

  removeEventListener(): void { this.listener = undefined }
  receive(data: unknown): void { this.listener?.({ data }) }
  get last(): Record<string, unknown> { return this.sent[this.sent.length - 1] }
}

afterEach(() => {
  vi.useRealTimers()
  vi.restoreAllMocks()
})

describe('GtaBridge', () => {
  it('resolves a structurally valid matching response and sends a v2-compatible envelope', async () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)
    const result = client.invoke<{ ok: boolean }>('game.getState')

    expect(transport.last).toMatchObject({
      kind: 'request', method: 'game.getState', params: {}, protocolVersion: 2, minimumProtocolVersion: 1,
    })
    transport.receive({ kind: 'response', id: transport.last.id, result: { ok: true }, protocolVersion: 2 })

    await expect(result).resolves.toEqual({ ok: true })
    client.destroy()
  })

  it('uses a caller-selected request prefix to prevent cross-browser ID collisions', async () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true, 'gpu')
    const result = client.invoke('game.getState')
    expect(transport.last.id).toMatch(/^gpu-/)
    transport.receive({ kind: 'response', id: transport.last.id, result: true })
    await expect(result).resolves.toBe(true)
    client.destroy()
  })

  it('rejects an API error with its code', async () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)
    const result = client.invoke('vehicle.repair')
    transport.receive({ kind: 'response', id: transport.last.id, error: { code: 'no_vehicle', message: 'No vehicle.' } })

    await expect(result).rejects.toMatchObject({ code: 'no_vehicle', message: 'No vehicle.' } satisfies Partial<GtaBridgeError>)
    client.destroy()
  })

  it('ignores malformed responses until a valid response arrives', async () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)
    const result = client.invoke('game.getState')
    const id = transport.last.id

    transport.receive({ kind: 'response', id: 9, result: 'wrong id type' })
    transport.receive({ kind: 'response', id, error: { code: 4, message: 'wrong code type' } })
    transport.receive({ kind: 'response', id, result: 'valid' })

    await expect(result).resolves.toBe('valid')
    client.destroy()
  })

  it('isolates event listener failures and continues delivering to other listeners', () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {})
    const survivor = vi.fn()
    client.on('runtime.lifecycle', () => { throw new Error('bad consumer') })
    client.on('runtime.lifecycle', survivor)

    transport.receive({ kind: 'event', event: 'runtime.lifecycle', payload: { phase: 'story-ready' } })

    expect(consoleError).toHaveBeenCalledOnce()
    expect(survivor).toHaveBeenCalledWith({ phase: 'story-ready' })
    client.destroy()
  })

  it('forwards events and supports unsubscribe', () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)
    const listener = vi.fn()
    const unsubscribe = client.on('game.state', listener)

    transport.receive({ kind: 'event', event: 'game.state', payload: { gameTime: 1 } })
    unsubscribe()
    transport.receive({ kind: 'event', event: 'game.state', payload: { gameTime: 2 } })

    expect(listener).toHaveBeenCalledOnce()
    client.destroy()
  })

  it('replays only bounded lifecycle state and clears a dismissed presentation', () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)
    transport.receive({
      kind: 'event', event: 'menu.presentation',
      payload: { presentationId: 'menu-1', extensionId: 'fixture', menuId: 'main' },
    })

    const replayed = vi.fn()
    client.on('menu.presentation', replayed, true)
    expect(replayed).toHaveBeenCalledOnce()

    transport.receive({
      kind: 'event', event: 'menu.dismissed',
      payload: { presentationId: 'menu-1', reason: 'overlay-hidden' },
    })
    const afterDismissal = vi.fn()
    client.on('menu.presentation', afterDismissal, true)
    expect(afterDismissal).not.toHaveBeenCalled()
    client.destroy()
  })

  it('rejects invocation after destroy without posting', async () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)
    client.destroy()

    await expect(client.invoke('game.getState')).rejects.toMatchObject({ code: 'disposed' })
    expect(transport.sent).toHaveLength(0)
  })

  it('cancels an in-flight request from AbortSignal with a protocol-v2 cancel message', async () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)
    const controller = new AbortController()
    const result = client.invoke('game.getState', {}, { signal: controller.signal })
    const requestId = transport.last.id
    controller.abort()

    await expect(result).rejects.toMatchObject({ code: 'aborted' })
    expect(transport.last).toEqual({
      kind: 'cancel', id: requestId, protocolVersion: 2, minimumProtocolVersion: 1, reason: 'abort_signal',
    })
    client.destroy()
  })

  it('sends a cancel when the local timeout expires', async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)
    const result = client.invoke('game.getState', {}, 25)
    const requestId = transport.last.id
    const expectation = expect(result).rejects.toMatchObject({ code: 'timeout' })
    await vi.advanceTimersByTimeAsync(25)

    await expectation
    expect(transport.last).toMatchObject({ kind: 'cancel', id: requestId, reason: 'timeout', protocolVersion: 2 })
    client.destroy()
  })

  it('validates timeout, deadline, and idempotency metadata before posting', async () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)

    await expect(client.invoke('test.call', {}, 0)).rejects.toMatchObject({ code: 'invalid_timeout' })
    await expect(client.invoke('test.call', {}, { deadlineMs: 120_001 })).rejects.toMatchObject({ code: 'invalid_deadline' })
    await expect(client.invoke('test.call', {}, { idempotencyKey: 'spaces are invalid' })).rejects.toMatchObject({ code: 'invalid_idempotency_key' })
    expect(transport.sent).toHaveLength(0)
    client.destroy()
  })

  it('promotes deadline, confirmation, and idempotency metadata into the envelope', async () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)
    const pending = client.invoke('extensions.invoke', {}, { deadlineMs: 1500, confirmed: true, idempotencyKey: 'purchase:42' })

    expect(transport.last).toMatchObject({ deadlineMs: 1500, confirmed: true, idempotencyKey: 'purchase:42' })
    client.destroy()
    await expect(pending).rejects.toMatchObject({ code: 'disposed' })
  })

  it('turns synchronous transport failures into bridge errors', async () => {
    const transport = new FakeTransport()
    transport.throwOnPost = true
    const client = new GtaBridge(transport, true)

    await expect(client.invoke('game.getState')).rejects.toMatchObject({ code: 'transport_error' })
    client.destroy()
  })

  it('closes a bootstrap surface without waiting for the managed provider', () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)

    client.closeHostSurface()

    expect(transport.last).toEqual({
      kind: 'host', command: 'close', protocolVersion: 2, minimumProtocolVersion: 1,
    })
    client.destroy()
  })

  it('acknowledges only a valid committed bootstrap surface generation', () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)

    client.markHostSurfaceReady('initializing', 12)

    expect(transport.last).toEqual({
      kind: 'host', command: 'surface-ready', mode: 'initializing', generation: 12,
      protocolVersion: 2, minimumProtocolVersion: 1,
    })
    client.markHostSurfaceReady('verifying', 13)
    expect(transport.last).toEqual({
      kind: 'host', command: 'surface-ready', mode: 'verifying', generation: 13,
      protocolVersion: 2, minimumProtocolVersion: 1,
    })
    client.markHostSurfaceReady('setup-status', 14)
    expect(transport.last).toEqual({
      kind: 'host', command: 'surface-ready', mode: 'setup-status', generation: 14,
      protocolVersion: 2, minimumProtocolVersion: 1,
    })
    expect(() => client.markHostSurfaceReady('about', 0)).toThrowError(/positive integer/)
    client.destroy()
  })

  it('publishes exact accelerated provider pixels as a one-way host signal', () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)

    client.markExternalProviderSurfacePainted('gbay:home:42', 7)

    expect(transport.last).toEqual({
      kind: 'host',
      command: 'provider-surface-painted',
      presentationId: 'gbay:home:42',
      providerSessionGeneration: 7,
      protocolVersion: 2,
      minimumProtocolVersion: 1,
    })
    expect(() => client.markExternalProviderSurfacePainted('', 7)).toThrowError(/presentation/i)
    expect(() => client.markExternalProviderSurfacePainted('gbay:home:42', 0)).toThrowError(/generation/i)
    client.destroy()
  })

  it('publishes only bounded read-only live acceptance menu state', () => {
    const transport = new FakeTransport()
    const client = new GtaBridge(transport, true)
    const state = {
      presentationId: 'allin1.gbay:home:42',
      providerId: 'allin1.gbay',
      rootMenuId: 'home',
      menuId: 'garage',
      routeId: 'garage',
      sectionId: 'garage',
      payloadStatus: 'ready' as const,
      itemCount: 12,
      contentItemCount: 3,
      actionableItemCount: 2,
      statusItemCount: 1,
    }

    client.reportLiveAcceptanceMenuState(state)

    expect(transport.last).toEqual({
      kind: 'acceptance', command: 'menu-state', schemaVersion: 1, ...state,
    })
    expect(() => client.reportLiveAcceptanceMenuState({
      ...state, routeId: '../garage',
    })).toThrowError(/invalid/i)
    expect(() => client.reportLiveAcceptanceMenuState({
      ...state, contentItemCount: 1,
    })).toThrowError(/invalid/i)
    client.destroy()
  })
})
