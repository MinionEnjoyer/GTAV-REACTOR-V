import { describe, expect, it } from 'vitest'
import type { MenuDescriptor, RoutedMenuDescriptor } from '../gta/types'
import type { MenuControllerSnapshot } from './controller'
import {
  changedMenuIdsInLoadedTree,
  coalesceGbayStateChange,
  mergeChangedMenuDescriptors,
  parseGbayStateChangedEvent,
  preserveGbayViewState,
} from './gbayStateRefresh'

const descriptor = (id: string, label = id): MenuDescriptor => ({
  extensionId: 'allin1.gbay', id, label, description: '', icon: '', order: 1,
  nodes: [],
})

describe('GBAY event-driven descriptor refresh', () => {
  it('accepts only bounded typed state-change payloads', () => {
    expect(parseGbayStateChangedEvent({ revision: 7, menus: ['garage', 'gear'] }))
      .toEqual({ revision: 7, menus: ['garage', 'gear'] })
    expect(parseGbayStateChangedEvent({ revision: 0, menus: ['garage'] })).toBeNull()
    expect(parseGbayStateChangedEvent({ revision: 1, menus: [] })).toBeNull()
    expect(parseGbayStateChangedEvent({ revision: 1, menus: ['../garage'] })).toBeNull()
    expect(parseGbayStateChangedEvent({ revision: 1, menus: ['garage'], extra: true })).toBeNull()
    expect(parseGbayStateChangedEvent({ revision: 1, menus: ['garage', 'garage'] })).toBeNull()
  })

  it('coalesces overlapping deltas without losing menus and ignores applied revisions', () => {
    const first = coalesceGbayStateChange(null, { revision: 4, menus: ['garage'] }, 3)
    const combined = coalesceGbayStateChange(first, { revision: 6, menus: ['gear'] }, 3)
    const olderDelta = coalesceGbayStateChange(combined, { revision: 5, menus: ['weapons'] }, 3)
    expect(olderDelta).toEqual({ revision: 6, menus: ['garage', 'gear', 'weapons'] })
    expect(coalesceGbayStateChange(olderDelta, { revision: 3, menus: ['home'] }, 3))
      .toEqual(olderDelta)
  })

  it('fetches and merges only descriptors already present in the loaded tree', () => {
    const loaded = [descriptor('home', 'Home'), descriptor('garage', 'Old garage')]
    expect(changedMenuIdsInLoadedTree(loaded, ['garage', 'unknown', 'garage']))
      .toEqual(['garage'])
    const merged = mergeChangedMenuDescriptors(
      loaded, [descriptor('garage', 'Current garage')], 'allin1.gbay')
    expect(merged.map((menu) => menu.label)).toEqual(['Home', 'Current garage'])
    expect(loaded[1].label).toBe('Old garage')
    expect(() => mergeChangedMenuDescriptors(
      loaded, [{ ...descriptor('garage'), extensionId: 'other' }], 'allin1.gbay'))
      .toThrow(/unexpected GBAY descriptor/)
  })

  it('preserves route focus inputs and filters without overriding gameplay settings', () => {
    const menu: RoutedMenuDescriptor = {
      id: 'home', extensionId: 'allin1.gbay', title: 'GBAY', homeRouteId: 'home',
      routes: [{ id: 'garage', menuId: 'garage', title: 'Garage', items: [
        { id: 'search', label: 'Search', type: 'search', value: '', action: 'garage.search' },
        { id: 'location-filter', label: 'Location', type: 'choice', value: 'all', action: 'garage.location', options: [
          { value: 'all', label: 'All' }, { value: 'davis', label: 'Davis' },
        ] },
        { id: 'pages', label: 'Page', type: 'pagination', page: 1, pageCount: 2, action: 'garage.page' },
        { id: 'interior', label: 'Interior', type: 'choice', value: 'new', action: 'garage.customize', options: [
          { value: 'old', label: 'Old' }, { value: 'new', label: 'New' },
        ] },
        { id: 'sell', label: 'Sell', type: 'command', action: 'garage.sell' },
      ] }],
    }
    const previous: MenuControllerSnapshot = {
      menuId: 'garage', stack: ['home', 'garage'], focusedItemId: 'sell',
      route: { id: 'garage', menuId: 'garage', title: 'Garage', items: [
        { id: 'search', label: 'Search', type: 'search', value: 'bus', action: 'garage.search' },
        { id: 'location-filter', label: 'Location', type: 'choice', value: 'davis', action: 'garage.location', options: [{ value: 'davis', label: 'Davis' }] },
        { id: 'pages', label: 'Page', type: 'pagination', page: 5, pageCount: 8, action: 'garage.page' },
        { id: 'interior', label: 'Interior', type: 'choice', value: 'old', action: 'garage.customize', options: [{ value: 'old', label: 'Old' }] },
        { id: 'sell', label: 'Sell', type: 'command', action: 'garage.sell' },
      ] },
    }
    const preserved = preserveGbayViewState(menu, previous)
    const items = preserved.routes![0].items
    expect(items.find((item) => item.id === 'search')).toMatchObject({ value: 'bus' })
    expect(items.find((item) => item.id === 'location-filter')).toMatchObject({ value: 'davis' })
    expect(items.find((item) => item.id === 'pages')).toMatchObject({ page: 2 })
    expect(items.find((item) => item.id === 'interior')).toMatchObject({ value: 'new' })
  })
})
