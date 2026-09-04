import { describe, expect, it, vi } from 'vitest'
import type { RoutedMenuDescriptor } from '../gta/types'
import { MenuController } from './controller'
import { gbayCardEdgePageAction, invokeGbaySemanticInput, moveGbayCardFocus } from './gbayInput'

function fixture(invoke = vi.fn()) {
  const menu: RoutedMenuDescriptor = {
    id: 'vehicles', extensionId: 'allin1.gbay', title: 'Vehicles', homeRouteId: 'vehicles', routes: [{
      id: 'vehicles', menuId: 'vehicles', title: 'Vehicles', layout: 'grid', columns: 3,
      items: [
        { id: 'search', type: 'search', label: 'Search', value: '', action: 'vehicle.search' },
        {
          id: 'category', type: 'choice', label: 'Category', value: 'all', action: 'vehicle.category',
          options: [{ value: 'all', label: 'All' }, { value: 'sedan', label: 'Sedans' }, { value: 'sport', label: 'Sports' }],
        },
        {
          id: 'ownership', type: 'choice', label: 'Ownership', value: 'all', action: 'vehicle.ownership',
          options: [{ value: 'all', label: 'All' }, { value: 'owned', label: 'Owned' }, { value: 'available', label: 'Available' }],
        },
        { id: 'vehicle-alpha', type: 'command', label: 'Alpha', action: 'vehicle.checkout' },
        { id: 'vehicle-bravo', type: 'command', label: 'Bravo', action: 'vehicle.checkout' },
        { id: 'vehicle-charlie', type: 'command', label: 'Charlie', action: 'vehicle.checkout' },
        { id: 'vehicle-delta', type: 'command', label: 'Delta', action: 'vehicle.checkout' },
        { id: 'favorite-alpha', type: 'command', label: 'Add favorite', action: 'vehicle.favorite' },
        { id: 'favorite-bravo', type: 'command', label: 'Add favorite', action: 'vehicle.favorite' },
        { id: 'pages', type: 'pagination', label: 'Page', action: 'vehicle.page', page: 1, pageCount: 3 },
      ],
    }],
  }
  return { controller: new MenuController(menu, { invoke }), invoke }
}

describe('GBAY 0.5 semantic input parity', () => {
  it('navigates an inline Unequip action in its original card position', () => {
    const menu: RoutedMenuDescriptor = {
      id: 'weapons.customize', extensionId: 'allin1.gbay', title: 'Customize', homeRouteId: 'weapons.customize', routes: [{
        id: 'weapons.customize', title: 'Customize', items: [
          { id: 'custom-option-a', type: 'command', label: 'Scope', action: 'weapon.customize.apply' },
          { id: 'custom-option-b', type: 'command', label: 'Suppressor', enabled: false, action: 'weapon.customize.apply' },
          { id: 'custom-option-c', type: 'command', label: 'Grip', action: 'weapon.customize.apply' },
          { id: 'custom-option-b-unequip', type: 'command', label: 'Unequip Suppressor', action: 'weapon.customize.apply' },
        ],
      }],
    }
    const controller = new MenuController(menu, { invoke: vi.fn() })
    controller.focus('custom-option-a')
    expect(moveGbayCardFocus(controller, 1, 0)).toBe(true)
    expect(controller.focusedItem?.id).toBe('custom-option-b-unequip')
    expect(moveGbayCardFocus(controller, 1, 0)).toBe(true)
    expect(controller.focusedItem?.id).toBe('custom-option-c')
  })
  it('uses the typed pagination node and preserves the selected card', async () => {
    const { controller, invoke } = fixture()
    controller.focus('vehicle-alpha')

    const result = await invokeGbaySemanticInput(controller, 'next-page')

    expect(result.handled).toBe(true)
    expect(controller.focusedItem?.id).toBe('vehicle-alpha')
    expect(invoke).toHaveBeenCalledWith({
      extensionId: 'allin1.gbay', menuId: 'vehicles', nodeId: 'pages',
      interaction: 'adjust', value: 2,
    })
  })

  it('changes categories without wrapping and keeps raw action ids host-owned', async () => {
    const { controller, invoke } = fixture()
    controller.focus('vehicle-bravo')
    await invokeGbaySemanticInput(controller, 'next-category')

    expect(controller.focusedItem?.id).toBe('vehicle-bravo')
    expect(invoke).toHaveBeenCalledWith({
      extensionId: 'allin1.gbay', menuId: 'vehicles', nodeId: 'category',
      interaction: 'set-value', value: 'sedan',
    })

    controller.focus('category')
    await controller.setValue('sport')
    invoke.mockClear()
    await invokeGbaySemanticInput(controller, 'next-category')
    expect(invoke).not.toHaveBeenCalled()
  })

  it('cycles the 0.5 ownership filter through its published choice options', async () => {
    const { controller, invoke } = fixture()
    controller.focus('vehicle-charlie')
    await invokeGbaySemanticInput(controller, 'filter-next')

    expect(invoke).toHaveBeenCalledWith({
      extensionId: 'allin1.gbay', menuId: 'vehicles', nodeId: 'ownership',
      interaction: 'set-value', value: 'owned',
    })
    expect(controller.focusedItem?.id).toBe('vehicle-charlie')
  })

  it('focuses the existing search editor without inventing a host operation', async () => {
    const { controller, invoke } = fixture()
    const focusSearch = vi.fn()
    const result = await invokeGbaySemanticInput(controller, 'search', { focusSearch })

    expect(result.handled).toBe(true)
    expect(controller.focusedItem?.id).toBe('search')
    expect(focusSearch).toHaveBeenCalledOnce()
    expect(invoke).not.toHaveBeenCalled()
  })

  it('hands the authoritative search node to a controller keyboard without invoking it early', async () => {
    const { controller, invoke } = fixture()
    const openSearch = vi.fn()

    const result = await invokeGbaySemanticInput(controller, 'search', { openSearch })

    expect(result.handled).toBe(true)
    expect(openSearch).toHaveBeenCalledWith(expect.objectContaining({
      id: 'search', type: 'search', action: 'vehicle.search',
    }))
    expect(invoke).not.toHaveBeenCalled()
  })

  it('routes R3 to the focused card favorite node and preserves confirmation policy', async () => {
    const { controller, invoke } = fixture()
    controller.focus('vehicle-alpha')
    const result = await invokeGbaySemanticInput(controller, 'favorite')

    expect(result.handled).toBe(true)
    expect(controller.focusedItem?.id).toBe('vehicle-alpha')
    expect(invoke).toHaveBeenCalledWith({
      extensionId: 'allin1.gbay', menuId: 'vehicles', nodeId: 'favorite-alpha',
      interaction: 'activate',
    })
  })

  it('fails closed when no focused listing has an authoritative favorite node', async () => {
    const { controller, invoke } = fixture()
    controller.focus('search')
    expect(await invokeGbaySemanticInput(controller, 'favorite')).toEqual({ handled: false })
    expect(invoke).not.toHaveBeenCalled()
  })

  it('moves through the visible three-column catalog without selecting hidden favorite actions', () => {
    const { controller } = fixture()
    controller.focus('vehicle-alpha')
    expect(moveGbayCardFocus(controller, 1, 0)).toBe(true)
    expect(controller.focusedItem?.id).toBe('vehicle-bravo')
    expect(moveGbayCardFocus(controller, -1, 0)).toBe(true)
    expect(controller.focusedItem?.id).toBe('vehicle-alpha')
    expect(moveGbayCardFocus(controller, 0, 1)).toBe(true)
    expect(controller.focusedItem?.id).toBe('vehicle-delta')
  })

  it('releases vertical grid edges back to the route focus ring', () => {
    const { controller } = fixture()
    controller.focus('vehicle-alpha')

    expect(moveGbayCardFocus(controller, 0, -1)).toBe(false)
    expect(controller.focusedItem?.id).toBe('vehicle-alpha')
    controller.moveFocus(-1)
    expect(controller.focusedItem?.id).toBe('ownership')

    controller.focus('vehicle-delta')
    expect(moveGbayCardFocus(controller, 0, 1)).toBe(false)
    expect(controller.focusedItem?.id).toBe('vehicle-delta')
  })

  it('routes horizontal grid edges through the authoritative page node', () => {
    const { controller } = fixture()
    controller.focus('vehicle-alpha')
    expect(gbayCardEdgePageAction(controller, -1)).toBe('previous-page')
    expect(gbayCardEdgePageAction(controller, 1)).toBeUndefined()

    controller.focus('vehicle-charlie')
    expect(gbayCardEdgePageAction(controller, 1)).toBe('next-page')

    controller.focus('vehicle-delta')
    expect(gbayCardEdgePageAction(controller, 1)).toBe('next-page')
  })
})
