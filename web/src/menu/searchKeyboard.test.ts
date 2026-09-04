import { describe, expect, it, vi } from 'vitest'
import type { RoutedMenuDescriptor } from '../gta/types'
import { MenuController } from './controller'
import {
  SEARCH_KEYBOARD_ROWS,
  activateSearchKeyboardKey,
  commitSearchKeyboardSession,
  createSearchKeyboardSession,
  focusSearchKeyboardKey,
  moveSearchKeyboardSelection,
  selectedSearchKeyboardKey,
  updateSearchKeyboardDraft,
} from './searchKeyboard'

function session(value = '', maximumLength = 12) {
  return createSearchKeyboardSession('vehicles', {
    id: 'search', type: 'search', label: 'Find a vehicle', value,
    action: 'vehicle.search', maxLength: maximumLength,
  })
}

describe('controller search keyboard', () => {
  it('offers bounded characters and editing controls without an OS input surface', () => {
    const keys = SEARCH_KEYBOARD_ROWS.flat()
    expect(keys.filter((key) => key.kind === 'character').map((key) => key.value).join(''))
      .toBe('abcdefghijklmnopqrstuvwxyz0123456789.-')
    expect(keys.map((key) => key.kind)).toEqual(expect.arrayContaining([
      'space', 'backspace', 'clear', 'cancel', 'apply',
    ]))
  })

  it('moves deterministically across rows and clamps columns on the action row', () => {
    let current = focusSearchKeyboardKey(session(), 'backspace')
    current = moveSearchKeyboardSelection(current, 0, 1)
    expect(selectedSearchKeyboardKey(current).id).toBe('apply')
    current = moveSearchKeyboardSelection(current, 1, 0)
    expect(selectedSearchKeyboardKey(current).id).toBe('clear')
    current = moveSearchKeyboardSelection(current, 0, 1)
    expect(selectedSearchKeyboardKey(current).id).toBe('character-a')
  })

  it('edits a bounded draft and distinguishes apply from cancel', () => {
    let current = session('bu', 3)
    current = activateSearchKeyboardKey(
      current,
      SEARCH_KEYBOARD_ROWS[2].find((key) => key.id === 'character-s'),
    ).session
    current = activateSearchKeyboardKey(
      current,
      SEARCH_KEYBOARD_ROWS[0].find((key) => key.id === 'character-a'),
    ).session
    expect(current.value).toBe('bus')

    current = activateSearchKeyboardKey(
      current,
      SEARCH_KEYBOARD_ROWS[4].find((key) => key.id === 'backspace'),
    ).session
    expect(current.value).toBe('bu')
    expect(activateSearchKeyboardKey(
      current,
      SEARCH_KEYBOARD_ROWS[5].find((key) => key.id === 'apply'),
    ).intent).toBe('apply')
    expect(activateSearchKeyboardKey(
      current,
      SEARCH_KEYBOARD_ROWS[5].find((key) => key.id === 'cancel'),
    ).intent).toBe('cancel')
  })

  it('bounds physical-keyboard draft updates to the published maximum length', () => {
    expect(updateSearchKeyboardDraft(session('', 4), 'coach').value).toBe('coac')
  })

  it('commits only through the authoritative typed search node', async () => {
    const invoke = vi.fn()
    const menu: RoutedMenuDescriptor = {
      id: 'vehicles', extensionId: 'allin1.gbay', title: 'Vehicles', homeRouteId: 'vehicles',
      routes: [{
        id: 'vehicles', menuId: 'vehicles', title: 'Vehicles', items: [
          { id: 'search', type: 'search', label: 'Search', value: '', action: 'vehicle.search', maxLength: 12 },
          { id: 'vehicle', type: 'command', label: 'Bus', action: 'vehicle.checkout' },
        ],
      }],
    }
    const controller = new MenuController(menu, { invoke })
    const current = updateSearchKeyboardDraft(session(), 'metrobus')

    await commitSearchKeyboardSession(controller, current)

    expect(invoke).toHaveBeenCalledWith({
      extensionId: 'allin1.gbay', menuId: 'vehicles', nodeId: 'search',
      interaction: 'set-value', value: 'metrobus',
    })
  })

  it('fails closed after a route change rather than targeting another node', async () => {
    const invoke = vi.fn()
    const menu: RoutedMenuDescriptor = {
      id: 'menu', extensionId: 'allin1.gbay', title: 'Menu', homeRouteId: 'vehicles',
      routes: [
        { id: 'vehicles', title: 'Vehicles', items: [
          { id: 'search', type: 'search', label: 'Search', value: '', action: 'vehicle.search' },
          { id: 'to-weapons', type: 'route', label: 'Weapons', routeId: 'weapons' },
        ] },
        { id: 'weapons', title: 'Weapons', items: [
          { id: 'search', type: 'search', label: 'Search', value: '', action: 'weapon.search' },
        ] },
      ],
    }
    const controller = new MenuController(menu, { invoke })
    const current = updateSearchKeyboardDraft(session(), 'bus')
    controller.push('weapons')

    expect(await commitSearchKeyboardSession(controller, current)).toBeUndefined()
    expect(invoke).not.toHaveBeenCalled()
  })
})
