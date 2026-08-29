import { describe, expect, it, vi } from 'vitest'
import { GtaBridge } from './bridge'
import { DemoTransport } from './demoTransport'
import { ReactorVApi } from './reactor'

describe('DemoTransport', () => {
  it('models compact discovery, detail lookup, menus, overlay state, and v1 compatibility', async () => {
    const transport = new DemoTransport()
    const bridge = new GtaBridge(transport, false)
    const api = new ReactorVApi(bridge)

    const handshake = await api.runtime.handshake()
    const extensionIndex = await api.extensions.list()
    const extension = await api.extensions.get(extensionIndex.items[0].id)
    const menuIndex = await api.menu.list(extensionIndex.items[0].id)
    const menu = await api.menu.get(menuIndex.items[0].extensionId, menuIndex.items[0].id)
    const overlay = await api.overlay.setVisibility('visible')
    const legacy = await bridge.invoke<{ apiVersion: number }>('overlay.ready')

    expect(handshake.apiVersion).toBe(2)
    expect(extensionIndex).toMatchObject({ total: 1, items: [{ id: 'allin1.online', menuCount: 2 }] })
    expect(extension?.menuIds).toEqual(['gbay', 'weapons.customize'])
    expect(menuIndex).toMatchObject({ total: 2, truncated: false })
    expect(menu.nodes.some((node) => node.kind === 'submenu')).toBe(true)
    expect(overlay.visible).toBe(true)
    expect(legacy.apiVersion).toBe(2)
    bridge.destroy()
  })

  it('publishes lifecycle/input events through managed subscriptions', async () => {
    const transport = new DemoTransport()
    const bridge = new GtaBridge(transport, false)
    const api = new ReactorVApi(bridge)
    const listener = vi.fn()
    const subscription = await api.events.subscribe({ events: ['runtime.lifecycle', 'input.action'] }, listener)

    transport.publish('runtime.lifecycle', { phase: 'story-ready', timestamp: 1 })
    transport.publish('input.action', { action: 'menu.accept', phase: 'pressed', source: 'controller', timestamp: 2 })

    expect(listener).toHaveBeenCalledTimes(2)
    await expect(subscription.unsubscribe()).resolves.toBe(true)
    bridge.destroy()
  })
})
