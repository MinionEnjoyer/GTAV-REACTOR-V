import { describe, expect, it, vi } from 'vitest'
import { GtaBridge } from './bridge'
import { ReactorVApi } from './reactor'
import type { WebViewTransport } from './types'

class LoopbackTransport implements WebViewTransport {
  private listener?: (event: { data: unknown }) => void
  readonly sent: Record<string, unknown>[] = []

  postMessage(message: unknown): void {
    const request = message as Record<string, unknown>
    this.sent.push(request)
    if (request.kind !== 'request') return
    const method = request.method
    const result = method === 'runtime.handshake'
      ? { apiVersion: 2, sessionId: 'test', capabilities: [], dependencies: [] }
      : method === 'extensions.list'
        ? { total: 1, items: [{ id: 'fixture', name: 'Fixture', version: '1.0.0', extensionApiVersion: 1, actionCount: 0, eventCount: 0, menuCount: 1 }] }
        : method === 'extensions.get'
          ? { id: 'fixture', name: 'Fixture', version: '1.0.0', description: '', capabilities: [], extensionApiVersion: 1, actions: [], events: [], menuIds: ['main'] }
        : method === 'menu.get'
          ? { extensionId: 'fixture', id: 'main', label: 'Main', description: '', icon: '', order: 0, nodes: [] }
          : method === 'events.subscribe'
            ? { id: 'sub-1', events: ['runtime.lifecycle'] }
            : method === 'events.unsubscribe'
              ? { removed: true }
              : { succeeded: true, confirmationRequired: false, replayed: false, value: null }
    queueMicrotask(() => this.listener?.({ data: { kind: 'response', id: request.id, result } }))
  }

  addEventListener(_type: 'message', listener: (event: { data: unknown }) => void): void { this.listener = listener }
  removeEventListener(): void { this.listener = undefined }
  publish(event: string, payload: unknown): void { this.listener?.({ data: { kind: 'event', event, payload } }) }
}

describe('ReactorVApi', () => {
  it('uses the typed runtime and exact flat-menu methods', async () => {
    const transport = new LoopbackTransport()
    const bridge = new GtaBridge(transport, true)
    const api = new ReactorVApi(bridge)

    await api.runtime.handshake({ apiVersions: [2, 1] })
    const summaries = await api.extensions.list()
    const detail = await api.extensions.get(summaries.items[0].id)
    const menu = await api.menu.get('fixture', 'main')

    expect(detail?.menuIds).toEqual(['main'])
    expect(menu).toMatchObject({ extensionId: 'fixture', id: 'main', nodes: [] })
    expect(transport.sent.map((message) => message.method)).toEqual([
      'runtime.handshake', 'extensions.list', 'extensions.get', 'menu.get',
    ])
    expect(transport.sent[3].params).toEqual({ extensionId: 'fixture', menuId: 'main' })
    bridge.destroy()
  })

  it('promotes invocation confirmation and idempotency without changing the typed payload', async () => {
    const transport = new LoopbackTransport()
    const bridge = new GtaBridge(transport, true)
    const api = new ReactorVApi(bridge)

    await api.extensions.invoke({
      extensionId: 'allin1.online', actionId: 'gbay.purchase', parameters: { listingId: 'car-42' },
      confirmed: true, idempotencyKey: 'purchase:car-42',
    })
    const request = transport.sent[0]

    expect(request).toMatchObject({ confirmed: true, idempotencyKey: 'purchase:car-42' })
    expect(request.params).toMatchObject({
      extensionId: 'allin1.online', actionId: 'gbay.purchase', confirmed: true,
      idempotencyKey: 'purchase:car-42', parameters: { listingId: 'car-42' },
    })
    bridge.destroy()
  })

  it('sends menu node identity while keeping browser routes out of the wire payload', async () => {
    const transport = new LoopbackTransport()
    const bridge = new GtaBridge(transport, true)
    const api = new ReactorVApi(bridge)

    await api.menu.invoke({
      extensionId: 'fixture', menuId: 'settings', nodeId: 'traffic', interaction: 'set-value', value: true,
    })

    expect(transport.sent[0].params).toEqual({
      extensionId: 'fixture', menuId: 'settings', nodeId: 'traffic', interaction: 'set-value', value: true,
    })
    bridge.destroy()
  })

  it('manages remote event subscriptions and local listeners together', async () => {
    const transport = new LoopbackTransport()
    const bridge = new GtaBridge(transport, true)
    const api = new ReactorVApi(bridge)
    const listener = vi.fn()
    const subscription = await api.events.subscribe({ events: ['runtime.lifecycle'] }, listener)

    transport.publish('runtime.lifecycle', { phase: 'story-ready' })
    expect(listener).toHaveBeenCalledWith('runtime.lifecycle', { phase: 'story-ready' })
    await expect(subscription.unsubscribe()).resolves.toBe(true)
    transport.publish('runtime.lifecycle', { phase: 'paused' })
    expect(listener).toHaveBeenCalledOnce()
    bridge.destroy()
  })
})
