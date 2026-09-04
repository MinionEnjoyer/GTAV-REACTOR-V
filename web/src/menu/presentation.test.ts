import { describe, expect, it, vi } from 'vitest'
import type { MenuDescriptor, MenuInvocationResult } from '../gta/types'
import {
  acknowledgeMenuPresentationWithRetry,
  acknowledgePaintedMenuPresentation,
  createMenuInvocationKey,
  loadPresentedMenuTree,
  loadPresentedMenuTreeCached,
  menuInvocationIdentity,
  menuResultPresentationDirective,
  parseMenuDismissal,
  parseMenuPresentation,
  runMenuPresentationAcknowledgementOnce,
} from './presentation'

const nodeBase = { description: '', enabled: true, visible: true }

function descriptor(id: string, nodes: MenuDescriptor['nodes'] = []): MenuDescriptor {
  return { extensionId: 'fixture.menu', id, label: id, description: '', icon: '', order: 0, nodes }
}

describe('menu presentation boundary', () => {
  it('accepts the host GUID presentation token and copies only valid JSON context', () => {
    expect(parseMenuPresentation({
      extensionId: 'fixture.menu',
      menuId: 'catalog',
      presentationId: 'ae41cf326d9a42d38f3a98402a781f88',
      context: { source: 'hotkey', page: 2 },
      inputMode: 'interactive-menu',
    })).toEqual({
      extensionId: 'fixture.menu',
      menuId: 'catalog',
      presentationId: 'ae41cf326d9a42d38f3a98402a781f88',
      context: { source: 'hotkey', page: 2 },
      inputMode: 'interactive-menu',
    })
  })

  it('rejects unsafe identifiers and non-JSON context before any route is fetched', () => {
    expect(parseMenuPresentation({ extensionId: '../fixture', menuId: 'catalog', presentationId: 'safe' })).toBeNull()
    expect(parseMenuPresentation({ extensionId: 'fixture', menuId: 'catalog', presentationId: 'has space' })).toBeNull()
    expect(parseMenuPresentation({
      extensionId: 'fixture', menuId: 'catalog', presentationId: 'safe', context: { callback: () => true },
    })).toBeNull()
  })

  it('accepts only typed dismissal events so stale presentations are not cleared accidentally', () => {
    expect(parseMenuDismissal({
      extensionId: 'fixture.menu', menuId: 'catalog',
      presentationId: 'ae41cf326d9a42d38f3a98402a781f88', reason: 'overlay-hidden',
    })).toEqual({
      extensionId: 'fixture.menu', menuId: 'catalog',
      presentationId: 'ae41cf326d9a42d38f3a98402a781f88', reason: 'overlay-hidden',
    })
    expect(parseMenuDismissal({
      extensionId: 'fixture.menu', menuId: 'catalog', presentationId: 'safe', reason: 'arbitrary',
    })).toBeNull()
    expect(parseMenuDismissal({
      extensionId: 'fixture.menu', menuId: 'catalog', presentationId: 'safe', reason: 'superseded',
    })?.reason).toBe('superseded')
    expect(parseMenuDismissal({
      extensionId: 'fixture.menu', menuId: 'catalog', presentationId: 'safe', reason: 'presentation-failed',
      failureStage: 'provider-paint-timeout',
    })?.reason).toBe('presentation-failed')
  })

  it('fetches only the reachable submenu tree and de-duplicates references', async () => {
    const menus = new Map<string, MenuDescriptor>([
      ['catalog', descriptor('catalog', [
        { ...nodeBase, id: 'details', kind: 'submenu', label: 'Details', menuId: 'details' },
        {
          ...nodeBase, id: 'grid', kind: 'grid', label: 'Grid', columns: 2,
          nodes: [{ ...nodeBase, id: 'detailsAgain', kind: 'submenu', label: 'Details again', menuId: 'details' }],
        },
      ])],
      ['details', descriptor('details')],
      ['unrelated', descriptor('unrelated')],
    ])
    const fetch = vi.fn(async (_extensionId: string, menuId: string) => structuredClone(menus.get(menuId)!))

    const loaded = await loadPresentedMenuTree(fetch, 'fixture.menu', 'catalog')

    expect(loaded.map((menu) => menu.id)).toEqual(['catalog', 'details'])
    expect(fetch.mock.calls.map((call) => call[1])).toEqual(['catalog', 'details'])
  })

  it('rejects a descriptor whose identity does not match the requested host route', async () => {
    await expect(loadPresentedMenuTree(
      async () => descriptor('different'),
      'fixture.menu',
      'catalog',
    )).rejects.toThrow("invalid descriptor for menu 'catalog'")
  })

  it('reuses only explicitly revisioned menu trees and retries rejected loads', async () => {
    const fetch = vi.fn(async () => descriptor('catalog'))

    await loadPresentedMenuTreeCached(fetch, 'fixture.menu', 'catalog', 'revision-1')
    await loadPresentedMenuTreeCached(fetch, 'fixture.menu', 'catalog', 'revision-1')
    await loadPresentedMenuTreeCached(fetch, 'fixture.menu', 'catalog', 'revision-2')
    await loadPresentedMenuTreeCached(fetch, 'fixture.menu', 'catalog', null)

    expect(fetch).toHaveBeenCalledTimes(3)

    const failingFetch = vi.fn()
      .mockRejectedValueOnce(new Error('temporary'))
      .mockResolvedValueOnce({ ...descriptor('retry'), extensionId: 'fixture.retry' })
    await expect(loadPresentedMenuTreeCached(
      failingFetch, 'fixture.retry', 'retry', 1,
    )).rejects.toThrow('temporary')
    await expect(loadPresentedMenuTreeCached(
      failingFetch, 'fixture.retry', 'retry', 1,
    )).resolves.toEqual([{ ...descriptor('retry'), extensionId: 'fixture.retry' }])
    expect(failingFetch).toHaveBeenCalledTimes(2)
  })

  it('keeps traversal limits isolated in the revisioned cache', async () => {
    const menus = new Map<string, MenuDescriptor>([
      ['catalog', descriptor('catalog', [
        { ...nodeBase, id: 'details', kind: 'submenu', label: 'Details', menuId: 'details' },
      ])],
      ['details', descriptor('details')],
    ])
    const fetch = vi.fn(async (_extensionId: string, menuId: string) => structuredClone(menus.get(menuId)!))

    await expect(loadPresentedMenuTreeCached(
      fetch, 'fixture.menu', 'catalog', 'bounded-revision', 64,
    )).resolves.toHaveLength(2)
    await expect(loadPresentedMenuTreeCached(
      fetch, 'fixture.menu', 'catalog', 'bounded-revision', 1,
    )).rejects.toThrow('traversal limit')
  })

  it('retries transport failures but never retries an authoritative stale acknowledgement', async () => {
    const recovered = vi.fn()
      .mockRejectedValueOnce(new Error('transport unavailable'))
      .mockResolvedValueOnce(true)
    await expect(acknowledgeMenuPresentationWithRetry(recovered, 3, 0)).resolves.toBe(true)
    expect(recovered).toHaveBeenCalledTimes(2)

    const stale = vi.fn().mockResolvedValue(false)
    await expect(acknowledgeMenuPresentationWithRetry(stale, 3, 0)).resolves.toBe(false)
    expect(stale).toHaveBeenCalledTimes(1)
  })

  it('acknowledges a provider menu only after assets and two paint frames', async () => {
    const order: string[] = []
    const acknowledge = vi.fn(async () => {
      order.push('presentation-ready')
      return true
    })
    const assetsReady = Promise.resolve().then(() => {
      order.push('assets-ready')
    })

    await expect(acknowledgePaintedMenuPresentation(
      assetsReady,
      (callback) => {
        order.push('animation-frame')
        callback(0)
        return 1
      },
      acknowledge,
      250,
    )).resolves.toBe(true)

    expect(order).toEqual([
      'assets-ready',
      'animation-frame',
      'animation-frame',
      'presentation-ready',
    ])
    expect(acknowledge).toHaveBeenCalledTimes(1)
  })

  it('starts only one ready attempt when React re-renders during the paint wait', async () => {
    const attempts = new Set<string>()
    let releasePaint!: () => void
    const paintPending = new Promise<void>((resolve) => { releasePaint = resolve })
    const firstCallback = vi.fn(async () => true)
    const refreshedCallback = vi.fn(async () => true)
    let currentCallback = firstCallback
    const presentationId = 'provider-presentation-1'

    const firstAttempt = runMenuPresentationAcknowledgementOnce(
      attempts,
      presentationId,
      async () => {
        await paintPending
        return currentCallback()
      },
    )
    currentCallback = refreshedCallback
    const rerenderAttempt = runMenuPresentationAcknowledgementOnce(
      attempts,
      presentationId,
      () => currentCallback(),
    )

    expect(firstAttempt).not.toBeNull()
    expect(rerenderAttempt).toBeNull()
    releasePaint()
    await expect(firstAttempt).resolves.toBe(true)
    expect(firstCallback).not.toHaveBeenCalled()
    expect(refreshedCallback).toHaveBeenCalledTimes(1)
  })

  it('honors close and refresh only as successful typed host-result directives', () => {
    const result = (succeeded: boolean, presentation: string): MenuInvocationResult => ({
      succeeded,
      confirmationRequired: false,
      replayed: false,
      value: { presentation },
    })

    expect(menuResultPresentationDirective(result(true, 'close'))).toBe('close')
    expect(menuResultPresentationDirective(result(true, 'refresh'))).toBe('refresh')
    expect(menuResultPresentationDirective(result(false, 'close'))).toBeNull()
    expect(menuResultPresentationDirective(result(true, 'navigate-anywhere'))).toBeNull()
    expect(menuResultPresentationDirective({
      succeeded: true, confirmationRequired: false, replayed: false, value: 'close',
    })).toBeNull()
  })

  it('creates unique bridge-safe idempotency keys for confirmed invocations', () => {
    const first = createMenuInvocationKey('ae41cf326d9a42d38f3a98402a781f88', 'purchase')
    const second = createMenuInvocationKey('ae41cf326d9a42d38f3a98402a781f88', 'purchase')
    expect(first).not.toBe(second)
    expect(first).toMatch(/^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/)
    expect(first.length).toBeLessThanOrEqual(128)
  })

  it('identifies equivalent confirmed invocations independent of parameter key order', () => {
    const base = {
      extensionId: 'fixture.menu', menuId: 'catalog', nodeId: 'purchase', interaction: 'activate' as const,
    }
    expect(menuInvocationIdentity('presentation-1', {
      ...base, parameters: { model: 'bus', quotedPrice: 100 },
    })).toBe(menuInvocationIdentity('presentation-1', {
      ...base, parameters: { quotedPrice: 100, model: 'bus' },
    }))
  })
})
