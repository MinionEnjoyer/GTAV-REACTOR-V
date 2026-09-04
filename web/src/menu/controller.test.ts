import { describe, expect, it, vi } from 'vitest'
import type { MenuDescriptor, RoutedMenuDescriptor } from '../gta/types'
import { adaptMenusToRoutes } from './adapter'
import { MenuController } from './controller'

const wireMenus: MenuDescriptor[] = [
  {
    extensionId: 'fixture', id: 'main', label: 'Main', description: '', icon: '', order: 1,
    nodes: [
      { id: 'heading', kind: 'status', label: 'Status', description: '', enabled: true, visible: true, value: 'Ready', tone: 'success' },
      { id: 'enabled', kind: 'toggle', label: 'Enabled', description: '', enabled: true, visible: true, actionId: 'settings.enabled', value: false },
      {
        id: 'catalog', kind: 'grid', label: 'Catalog', description: '', enabled: true, visible: true, columns: 3,
        nodes: [{ id: 'buy', kind: 'action', label: 'Buy', description: '', enabled: true, visible: true, actionId: 'catalog.buy' }],
      },
      { id: 'settings', kind: 'submenu', label: 'Settings', description: '', enabled: true, visible: true, menuId: 'settings' },
    ],
  },
  {
    extensionId: 'fixture', id: 'settings', label: 'Settings', description: '', icon: '', order: 2,
    nodes: [{ id: 'gain', kind: 'range', label: 'Gain', description: '', enabled: true, visible: true, actionId: 'settings.gain', value: 1, minimum: -1, maximum: 1, step: 0.5 }],
  },
]

describe('menu adapter and controller', () => {
  it('adapts exact host menus into navigable list/grid routes without changing the wire descriptors', () => {
    const original = structuredClone(wireMenus)
    const routed = adaptMenusToRoutes(wireMenus, 'main')

    expect(routed.homeRouteId).toBe('main')
    expect(routed.routes?.find((route) => route.id === 'main/catalog')).toMatchObject({
      menuId: 'main', layout: 'grid', columns: 3,
    })
    expect(routed.routes?.find((route) => route.id === 'settings')?.menuId).toBe('settings')
    expect(wireMenus).toEqual(original)
  })

  it('preserves the selected tab as initial focus while routing nested tab content', async () => {
    const tabbed: MenuDescriptor = {
      extensionId: 'fixture', id: 'tabs', label: 'Tabs', description: '', icon: '', order: 1,
      nodes: [{
        id: 'sections', kind: 'tabs', label: 'Sections', description: '', enabled: true, visible: true,
        selectedId: 'owned', tabs: [
          { id: 'browse', label: 'Browse', nodes: [{ id: 'browseAction', kind: 'action', label: 'Browse', description: '', enabled: true, visible: true, actionId: 'browse.open' }] },
          { id: 'owned', label: 'Owned', nodes: [{ id: 'ownedAction', kind: 'action', label: 'Owned', description: '', enabled: true, visible: true, actionId: 'owned.open' }] },
        ],
      }],
    }
    const controller = new MenuController(adaptMenusToRoutes([tabbed], 'tabs'))
    controller.push('tabs/sections')

    expect(controller.focusedItem?.id).toBe('owned')
    await controller.activate()
    expect(controller.currentRoute.id).toBe('tabs/sections/owned')
    expect(controller.moveTab(1)).toBe(true)
    expect(controller.currentRoute.id).toBe('tabs/sections/browse')
    expect(controller.moveTab(-1)).toBe(true)
    expect(controller.currentRoute.id).toBe('tabs/sections/owned')
  })

  it('skips passive rows, adjusts values, and emits host-ready node invocations', async () => {
    const invoke = vi.fn()
    const controller = new MenuController(adaptMenusToRoutes(wireMenus, 'main'), { invoke })

    expect(controller.focusedItem?.id).toBe('enabled')
    await controller.activate({ confirmed: true })

    expect(controller.focusedItem).toMatchObject({ id: 'enabled', value: true })
    expect(invoke).toHaveBeenCalledWith({
      extensionId: 'fixture', menuId: 'main', nodeId: 'enabled', interaction: 'set-value', value: true, confirmed: true,
    })
  })

  it('supports push, replace, back, home, and focus restoration', () => {
    const controller = new MenuController(adaptMenusToRoutes(wireMenus, 'main'))
    controller.push('main/catalog')
    expect(controller.currentRoute.id).toBe('main/catalog')
    expect(controller.focusedItem?.id).toBe('buy')
    expect(controller.back()).toBe(true)
    expect(controller.currentRoute.id).toBe('main')
    expect(controller.focus('settings')).toBe(true)
    controller.replace('settings')
    expect(controller.currentRoute.id).toBe('settings')
    controller.home()
    expect(controller.snapshot.stack).toEqual(['main'])
    expect(controller.focusedItem?.id).toBe('settings')
  })

  it('restores a valid route and focus after an authoritative descriptor refresh', () => {
    const first = new MenuController(adaptMenusToRoutes(wireMenus, 'main'))
    first.push('main/catalog')
    first.focus('buy')

    const refreshed = new MenuController(adaptMenusToRoutes(structuredClone(wireMenus), 'main'))
    refreshed.restore(first.snapshot)

    expect(refreshed.snapshot.stack).toEqual(['main', 'main/catalog'])
    expect(refreshed.currentRoute.id).toBe('main/catalog')
    expect(refreshed.focusedItem?.id).toBe('buy')
  })

  it('replaces descriptors in place while retaining route focus and invocation wiring', async () => {
    const invoke = vi.fn()
    const controller = new MenuController(adaptMenusToRoutes(wireMenus, 'main'), { invoke })
    controller.push('main/catalog')
    controller.focus('buy')
    const replacement = structuredClone(wireMenus)
    replacement[0].nodes = replacement[0].nodes.map((node) =>
      node.id === 'catalog' ? { ...node, label: 'Updated catalog' } : node)

    controller.replaceMenu(adaptMenusToRoutes(replacement, 'main'))
    expect(controller.snapshot.stack).toEqual(['main', 'main/catalog'])
    expect(controller.focusedItem?.id).toBe('buy')
    await controller.activate()
    expect(invoke).toHaveBeenCalledWith(expect.objectContaining({ nodeId: 'buy' }))
  })

  it('falls back to home when a refreshed descriptor removes the active route', () => {
    const first = new MenuController(adaptMenusToRoutes(wireMenus, 'main'))
    first.push('main/catalog')
    const replacement = structuredClone(wireMenus)
    replacement[0].nodes = replacement[0].nodes.filter((node) => node.id !== 'catalog')

    const refreshed = new MenuController(adaptMenusToRoutes(replacement, 'main'))
    refreshed.restore(first.snapshot)

    expect(refreshed.snapshot.stack).toEqual(['main'])
    expect(refreshed.currentRoute.id).toBe('main')
  })

  it('drops surviving descendants after a refreshed descriptor removes an intermediate route', () => {
    const base: RoutedMenuDescriptor = {
      extensionId: 'fixture', id: 'nested', title: 'Nested', homeRouteId: 'home',
      routes: [
        { id: 'home', title: 'Home', items: [] },
        { id: 'parent', title: 'Parent', parentId: 'home', items: [] },
        { id: 'child', title: 'Child', parentId: 'parent', items: [] },
      ],
    }
    const first = new MenuController(base)
    first.push('parent')
    first.push('child')

    const refreshed = new MenuController({
      ...base,
      routes: base.routes?.filter((route) => route.id !== 'parent'),
    })
    refreshed.restore(first.snapshot)

    expect(refreshed.snapshot.stack).toEqual(['home'])
    expect(refreshed.currentRoute.id).toBe('home')
  })

  it('passes typed action parameters without exposing the action binding to the caller', async () => {
    const invoke = vi.fn()
    const controller = new MenuController(adaptMenusToRoutes(wireMenus, 'main'), { invoke })
    controller.push('main/catalog')
    await controller.activate({ parameters: { listingId: 'bus-42' }, confirmed: true })

    expect(invoke).toHaveBeenCalledWith({
      extensionId: 'fixture', menuId: 'main', nodeId: 'buy', interaction: 'activate',
      parameters: { listingId: 'bus-42' }, confirmed: true,
    })
  })

  it('does not echo host-bound node parameters into browser invocations', async () => {
    const invoke = vi.fn()
    const boundMenu: MenuDescriptor = {
      extensionId: 'fixture', id: 'bound', label: 'Bound', description: '', icon: '', order: 1,
      nodes: [{
        id: 'purchase', kind: 'action', label: 'Purchase', description: '', enabled: true, visible: true,
        actionId: 'catalog.purchase', boundParameters: { listingId: 'host-owned-42' },
      }],
    }
    const controller = new MenuController(adaptMenusToRoutes([boundMenu], 'bound'), { invoke })

    await controller.activate()

    expect(invoke).toHaveBeenCalledWith({
      extensionId: 'fixture', menuId: 'bound', nodeId: 'purchase', interaction: 'activate',
    })
  })

  it('clamps range adjustment and reports the owning host menu rather than a browser route', async () => {
    const invoke = vi.fn()
    const controller = new MenuController(adaptMenusToRoutes(wireMenus, 'main'), { invoke })
    controller.push('settings')
    await controller.adjust(1)
    await controller.adjust(1)

    expect(controller.focusedItem).toMatchObject({ value: 1 })
    expect(invoke).toHaveBeenLastCalledWith({
      extensionId: 'fixture', menuId: 'settings', nodeId: 'gain', interaction: 'adjust', value: 1,
    })
  })

  it('supports disabled-option-safe choice adjustment in a directly authored routed menu', async () => {
    const menu: RoutedMenuDescriptor = {
      id: 'choice', extensionId: 'fixture', title: 'Choice', homeRouteId: 'choice', routes: [{
        id: 'choice', menuId: 'choice', title: 'Choice', items: [{
          id: 'mode', type: 'choice', label: 'Mode', value: 'a', wrap: true,
          options: [{ value: 'a', label: 'A' }, { value: 'b', label: 'B', disabled: true }, { value: 'c', label: 'C' }],
        }],
      }],
    }
    const controller = new MenuController(menu)
    await controller.adjust(1)
    expect(controller.focusedItem).toMatchObject({ value: 'c' })
  })

  it('rolls back optimistic values when the host rejects or cancels an invocation', async () => {
    const controller = new MenuController(adaptMenusToRoutes(wireMenus, 'main'), {
      invoke: async () => { throw new Error('cancelled') },
    })

    await expect(controller.activate()).rejects.toThrow('cancelled')
    expect(controller.focusedItem).toMatchObject({ id: 'enabled', value: false })

    controller.push('settings')
    await expect(controller.adjust(-1)).rejects.toThrow('cancelled')
    expect(controller.focusedItem).toMatchObject({ id: 'gain', value: 1 })
  })

  it('rejects a routed menu without its declared home route', () => {
    expect(() => new MenuController({ id: 'bad', extensionId: 'fixture', title: 'Bad', homeRouteId: 'missing', routes: [] }))
      .toThrow("does not contain home route 'missing'")
  })
})
