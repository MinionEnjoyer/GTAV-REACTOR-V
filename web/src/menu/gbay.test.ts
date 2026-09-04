import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { MenuPresentation } from './presentation'
import type { MenuControllerSnapshot } from './controller'
import type { RoutedMenuDescriptor } from '../gta/types'
import { GbaySurface } from './GbaySurface'
import {
  classifyGbayRoute,
  gbayAccountState,
  gbayCardPreview,
  gbayCustomizationCards,
  gbaySections,
  isAllin1Presentation,
  parseGbayCardDetail,
  projectAllin1GbayMenu,
  renderableGbayPreviewSource,
} from './gbay'

describe('ALLIN1 GBAY presentation', () => {
  it('pairs only an exact host-supplied removal node, retaining its explicit authority', () => {
    const component = { id: 'custom-option-abc', type: 'command' as const, action: 'weapon.customize.apply', label: 'Suppressor', enabled: false }
    const removal = { ...component, id: 'custom-option-abc-unequip', label: 'Unequip Suppressor', enabled: true, confirmation: 'always' as const }
    const other = { ...component, id: 'custom-option-def', label: 'Suppressor', enabled: true }
    const cards = gbayCustomizationCards([component, other, removal])
    expect(cards).toEqual([{ option: component, action: removal, unequip: true }, { option: other, action: other, unequip: false }])
    expect(cards[0].action).toBe(removal)
    expect(gbayCustomizationCards([component])[0].unequip).toBe(false)
    expect(gbayCustomizationCards([component, { ...removal, action: 'different.apply' }])[0].unequip).toBe(false)
    expect(gbayCustomizationCards([removal])[0].action).toBe(removal)
  })
  it.each([
    'assets/allin1/vehicles/adder.png',
    './assets/allin1/vehicles/addon-card.v2.png?v=1',
    '/assets/allin1/weapons/weapon_pistol.png',
    'data:image/png;base64,iVBORw0KGgo=',
  ])('accepts the bounded GBAY preview source %s', (source) => {
    expect(renderableGbayPreviewSource(source)).toBe(true)
  })

  it.each([
    '../assets/allin1/vehicles/adder.png',
    'assets/allin1/../secrets.png',
    'assets/allin1/%2e%2e/secrets.png',
    'assets/allin1/vehicles/not-an-image.js',
    'https://example.test/adder.png',
    'https://reactorv.local/assets/allin1/gear/armor_heavy.png',
    'https://ragewebui.local/assets/allin1/gear/armor_heavy.png',
    'http://reactorv.local/adder.png',
    'file:///C:/adder.png',
    'blob:https://ragewebui.local/id',
    'data:image/svg+xml;base64,PHN2Zz4=',
  ])('rejects the unsafe GBAY preview source %s', (source) => {
    expect(renderableGbayPreviewSource(source)).toBe(false)
  })

  it('keeps the authoritative Home balance available to every shell route', () => {
    const menu: RoutedMenuDescriptor = {
      id: 'home', extensionId: 'allin1.gbay', title: 'GBAY', homeRouteId: 'home', routes: [
        { id: 'home', menuId: 'home', title: 'GBAY', items: [
          { id: 'balance', label: 'Balance', type: 'status', value: '$1,234,567', tone: 'success' },
        ] },
        { id: 'about', menuId: 'about', parentId: 'home', title: 'About', items: [] },
        { id: 'weapons', menuId: 'weapons', parentId: 'home', title: 'Weapons', items: [
          { id: 'balance', label: 'Stale catalog balance', type: 'status', value: '$1' },
        ] },
      ],
    }

    expect(gbayAccountState(menu)).toEqual({ label: 'Balance', value: '$1,234,567' })
  })

  it('accepts the existing customization balance when a standalone descriptor has no Home route', () => {
    const menu: RoutedMenuDescriptor = {
      id: 'weapons.customize', extensionId: 'allin1.gbay', title: 'Customize',
      homeRouteId: 'weapons.customize', routes: [{
        id: 'weapons.customize', menuId: 'weapons.customize', title: 'Customize', items: [
          { id: 'custom-balance', label: 'Balance', type: 'status', value: '$800' },
        ],
      }],
    }

    expect(gbayAccountState(menu)).toEqual({ label: 'Balance', value: '$800' })
  })

  it('selects only the declared ALLIN1 vehicle presentation', () => {
    const base: MenuPresentation = {
      extensionId: 'allin1.gbay',
      menuId: 'vehicles',
      presentationId: 'test',
      inputMode: 'interactive-menu',
      context: { route: 'gbay/vehicles' },
    }
    expect(isAllin1Presentation(base)).toBe(true)
    expect(isAllin1Presentation({ ...base, extensionId: 'other.menu' })).toBe(false)
    expect(isAllin1Presentation({ ...base, context: { route: 'other' } })).toBe(false)
  })

  it('projects the typed catalog actions into the marketplace home without changing ids', () => {
    const menu: RoutedMenuDescriptor = {
      id: 'vehicles', extensionId: 'allin1.gbay', title: 'GBAY Vehicles',
      description: 'Browse vehicles.', homeRouteId: 'vehicles', routes: [
        {
          id: 'vehicles', menuId: 'vehicles', title: 'GBAY Vehicles', items: [
            { id: 'balance', label: 'Balance', type: 'status', value: '$10', tone: 'success' },
            { id: 'catalog', label: 'Vehicles', type: 'route', routeId: 'vehicles/catalog' },
            { id: 'favorite-actions', label: 'Favorites', type: 'route', routeId: 'vehicles/favorite-actions' },
            { id: 'pages', label: 'Page', type: 'pagination', page: 1, pageCount: 2 },
          ],
        },
        {
          id: 'vehicles/catalog', menuId: 'vehicles', title: 'Vehicles', layout: 'grid', columns: 3, items: [
            { id: 'vehicle-a', label: 'A', type: 'command', action: 'checkout' },
            { id: 'vehicle-b', label: 'B', type: 'command', action: 'checkout' },
          ],
        },
        {
          id: 'vehicles/favorite-actions', menuId: 'vehicles', title: 'Favorites', layout: 'list', items: [
            { id: 'favorite-a', label: 'Favorite A', type: 'command', action: 'vehicle.favorite' },
          ],
        },
      ],
    }
    const projected = projectAllin1GbayMenu(menu)
    expect(projected.routes?.[0].items.map((item) => item.id)).toEqual([
      'balance', 'vehicle-a', 'vehicle-b', 'favorite-a', 'pages',
    ])
    expect(menu.routes?.[0].items.map((item) => item.id)).toEqual(['balance', 'catalog', 'favorite-actions', 'pages'])
  })

  it('accepts a host-flattened GBAY catalog without rewriting its actions', () => {
    const menu: RoutedMenuDescriptor = {
      id: 'vehicles', extensionId: 'allin1.gbay', title: 'VEHICLES',
      homeRouteId: 'vehicles', routes: [{
        id: 'vehicles', menuId: 'vehicles', title: 'VEHICLES', items: [
          { id: 'balance', label: 'Balance', type: 'status', value: '$10', tone: 'success' },
          { id: 'vehicle-a', label: 'A', type: 'command', action: 'checkout' },
          { id: 'pages', label: 'Page', type: 'pagination', page: 1, pageCount: 1 },
        ],
      }],
    }
    const projected = projectAllin1GbayMenu(menu)
    expect(projected.routes?.[0].items.map((item) => item.id)).toEqual([
      'balance', 'vehicle-a', 'pages',
    ])
  })

  it('removes legacy manual refresh commands from the GBAY focus ring', () => {
    const menu: RoutedMenuDescriptor = {
      id: 'garage', extensionId: 'allin1.gbay', title: 'Garage',
      homeRouteId: 'garage', routes: [{
        id: 'garage', menuId: 'garage', title: 'Garage', items: [
          { id: 'refresh', label: 'Refresh My Garage', type: 'command', action: 'garage.refresh' },
          { id: 'retrieve', label: 'Retrieve vehicle', type: 'command', action: 'garage.retrieve' },
        ],
      }],
    }
    const projected = projectAllin1GbayMenu(menu)
    expect(projected.routes?.[0].items.find((item) => item.id === 'refresh')?.visible)
      .toBe(false)
    expect(projected.routes?.[0].items.find((item) => item.id === 'retrieve')?.visible)
      .not.toBe(false)
  })

  it('removes garage pagination from the focus ring without changing catalog pagination', () => {
    const menu: RoutedMenuDescriptor = {
      id: 'home', extensionId: 'allin1.gbay', title: 'GBAY', homeRouteId: 'home', routes: [
        { id: 'home', menuId: 'home', title: 'GBAY', items: [] },
        { id: 'garage', menuId: 'garage', parentId: 'home', title: 'My Garage', items: [
          { id: 'garage-pages', label: 'Page', type: 'pagination', page: 1, pageCount: 3, action: 'garage.page' },
        ] },
        { id: 'vehicles', menuId: 'vehicles', parentId: 'home', title: 'Vehicles', items: [
          { id: 'vehicle-pages', label: 'Page', type: 'pagination', page: 1, pageCount: 3, action: 'vehicle.page' },
        ] },
      ],
    }

    const projected = projectAllin1GbayMenu(menu)
    expect(projected.routes?.find((route) => route.id === 'garage')?.items
      .find((item) => item.id === 'garage-pages')?.visible).toBe(false)
    expect(projected.routes?.find((route) => route.id === 'vehicles')?.items
      .find((item) => item.id === 'vehicle-pages')?.visible).not.toBe(false)
  })

  it('extracts the existing detached listing description into card fields', () => {
    expect(parseGbayCardDetail('$125,000 · Owned · Favorite · Sports Classics')).toEqual({
      price: '$125,000', ownership: 'Owned', favorite: true, category: 'Sports Classics',
      manufacturer: '', model: '', preview: '',
    })
  })

  it('preserves labelled manufacturer, model, category, and preview metadata', () => {
    expect(parseGbayCardDetail(
      'Price: $92,000 · Ownership: Available · Manufacturer: Annis · Model: elegy · Category: Sports · Preview: previews/elegy.png',
    )).toEqual({
      price: '$92,000', ownership: 'Available', favorite: false, category: 'Sports',
      manufacturer: 'Annis', model: 'elegy', preview: 'previews/elegy.png',
    })
  })

  it('adds persistent navigation only for descriptor-backed GBAY sections', () => {
    const menu: RoutedMenuDescriptor = {
      id: 'gbay', extensionId: 'allin1.gbay', title: 'GBAY', homeRouteId: 'gbay', routes: [
        { id: 'gbay', menuId: 'gbay', title: 'GBAY Home', items: [
          { id: 'open-vehicles', label: 'Vehicles', type: 'route', routeId: 'vehicles' },
          { id: 'open-gear', label: 'Gear', type: 'route', routeId: 'gear' },
        ] },
        { id: 'vehicles', menuId: 'vehicles', title: 'Vehicles', parentId: 'gbay', items: [] },
        { id: 'gear', menuId: 'gear', title: 'Gear', parentId: 'gbay', items: [] },
      ],
    }
    expect(gbaySections(menu).map((section) => section.id)).toEqual(['home', 'vehicles', 'gear'])
    const projected = projectAllin1GbayMenu(menu)
    expect(projected.routes?.[1].items.map((item) => item.id)).toEqual([
      'gbay-nav-home', 'gbay-nav-vehicles', 'gbay-nav-gear',
    ])
    expect(classifyGbayRoute(projected.routes![1])).toBe('vehicles')
  })

  it('matches a typed media node to its vehicle card without changing the action', () => {
    const card = { id: 'vehicle-elegy', label: 'Elegy', type: 'command' as const, action: 'vehicle.checkout' }
    expect(gbayCardPreview(card, [
      card,
      { id: 'vehicle-elegy-preview', label: 'Elegy preview', type: 'media', source: 'previews/elegy.png', mediaType: 'image/png' },
    ])).toBe('previews/elegy.png')
  })

  it('matches staged ALLIN1 weapon artwork to its card without changing the action', () => {
    const card = { id: 'weapon-pistol', label: 'Pistol', type: 'command' as const, action: 'weapon.purchase' }
    expect(gbayCardPreview(card, [
      card,
      { id: 'weapon-pistol-preview', label: 'Pistol preview', type: 'media', source: 'assets/allin1/weapons/weapon_pistol.png', mediaType: 'image' },
    ])).toBe('assets/allin1/weapons/weapon_pistol.png')
    expect(card.action).toBe('weapon.purchase')
  })

  it('does not present an unsafe media node as a GBAY card preview', () => {
    const card = { id: 'vehicle-elegy', label: 'Elegy', type: 'command' as const, action: 'vehicle.checkout' }
    expect(gbayCardPreview(card, [
      card,
      { id: 'vehicle-elegy-preview', label: 'Elegy preview', type: 'media', source: 'https://example.test/elegy.png', mediaType: 'image/png' },
    ])).toBe('')
  })

  it('renders staged weapon artwork on the GBAY weapon card', () => {
    const snapshot: MenuControllerSnapshot = {
      menuId: 'weapons', stack: ['home', 'weapons'], route: {
        id: 'weapons', menuId: 'weapons', title: 'PURCHASE WEAPONS', items: [
          { id: 'weapon-pistol', label: 'Pistol', description: 'Price: $500 · Ownership: Available · Category: Pistols', type: 'command', action: 'weapon.purchase' },
          { id: 'weapon-pistol-preview', label: 'Pistol preview', type: 'media', source: 'assets/allin1/weapons/weapon_pistol.png', mediaType: 'image' },
        ],
      },
    }
    const html = renderToStaticMarkup(createElement(GbaySurface, {
      snapshot,
      loading: false,
      busy: false,
      error: null,
      notice: null,
      onClose: () => {},
      onFocus: () => {},
      onActivate: () => {},
      onSetValue: () => {},
      onRetry: () => {},
    }))
    expect(html).toContain('src="assets/allin1/weapons/weapon_pistol.png"')
    expect(html).toContain('alt="Pistol preview"')
    expect(html).not.toContain('gbay-product-placeholder')
  })

  it('projects typed delivery destinations into the checkout page', () => {
    const menu: RoutedMenuDescriptor = {
      id: 'vehicles', extensionId: 'allin1.gbay', title: 'CHOOSE DELIVERY', homeRouteId: 'vehicles', routes: [
        { id: 'vehicles', menuId: 'vehicles', title: 'CHOOSE DELIVERY', items: [
          { id: 'delivery-destinations', label: 'Delivery locations', type: 'route', routeId: 'vehicles/delivery-destinations' },
          { id: 'delivery-back', label: 'Back to vehicles', type: 'command', action: 'vehicle.delivery.back' },
        ] },
        { id: 'vehicles/delivery-destinations', menuId: 'vehicles', title: 'Delivery locations', parentId: 'vehicles', layout: 'grid', items: [
          { id: 'destination-harmony', label: 'Harmony Garage', type: 'command', action: 'vehicle.delivery.complete' },
        ] },
      ],
    }
    const projected = projectAllin1GbayMenu(menu)
    expect(classifyGbayRoute(projected.routes![0])).toBe('delivery')
    expect(projected.routes![0].items.map((item) => item.id)).toEqual([
      'destination-harmony', 'delivery-back',
    ])
  })

  it('inlines authoritative weapon, gear, and garage collections without changing actions', () => {
    const menu: RoutedMenuDescriptor = {
      id: 'home', extensionId: 'allin1.gbay', title: 'GBAY', homeRouteId: 'home', routes: [
        { id: 'home', menuId: 'home', title: 'GBAY', items: [] },
        { id: 'weapons', menuId: 'weapons', parentId: 'home', title: 'Purchase Weapons', items: [
          { id: 'catalog', label: 'Weapons', type: 'route', routeId: 'weapons/catalog' },
          { id: 'favorite-actions', label: 'Favorites', type: 'route', routeId: 'weapons/favorite-actions' },
        ] },
        { id: 'weapons/catalog', menuId: 'weapons', parentId: 'weapons', title: 'Weapons', layout: 'grid', items: [
          { id: 'weapon-a', label: 'Pistol', type: 'command', action: 'weapon.purchase' },
        ] },
        { id: 'weapons/favorite-actions', menuId: 'weapons', parentId: 'weapons', title: 'Favorites', layout: 'list', items: [
          { id: 'weapon-favorite-a', label: 'Favorite Pistol', type: 'command', action: 'weapon.favorite' },
        ] },
        { id: 'gear', menuId: 'gear', parentId: 'home', title: 'Gear', items: [
          { id: 'catalog', label: 'Gear', type: 'route', routeId: 'gear/catalog' },
        ] },
        { id: 'gear/catalog', menuId: 'gear', parentId: 'gear', title: 'Gear', layout: 'grid', items: [
          { id: 'gear-armor', label: 'Armor', type: 'command', action: 'gear.apply' },
        ] },
        { id: 'garage', menuId: 'garage', parentId: 'home', title: 'My Garage', items: [
          { id: 'locations', label: 'Storage', type: 'route', routeId: 'garage/locations' },
          { id: 'vehicles', label: 'Stored vehicles', type: 'route', routeId: 'garage/vehicles' },
        ] },
        { id: 'garage/locations', menuId: 'garage', parentId: 'garage', title: 'Storage', layout: 'list', items: [
          { id: 'location-harmony', label: 'Harmony', type: 'status', value: '2 / 10' },
        ] },
        { id: 'garage/vehicles', menuId: 'garage', parentId: 'garage', title: 'Stored vehicles', layout: 'grid', items: [
          { id: 'stored-harmony-0', label: 'Elegy', type: 'command', action: 'garage.sell' },
        ] },
      ],
    }
    const projected = projectAllin1GbayMenu(menu)
    const routeItems = (id: string) => projected.routes!.find((route) => route.id === id)!.items
      .filter((item) => !item.id.startsWith('gbay-nav-'))
    expect(routeItems('weapons').map((item) => [item.id, item.type === 'command' ? item.action : ''])).toEqual([
      ['weapon-a', 'weapon.purchase'], ['weapon-favorite-a', 'weapon.favorite'],
    ])
    expect(routeItems('gear').map((item) => item.id)).toEqual(['gear-armor'])
    expect(routeItems('garage').map((item) => item.id)).toEqual(['location-harmony', 'stored-harmony-0'])
  })

  it('inlines customization weapon and option grids while preserving typed workbench actions', () => {
    const menu: RoutedMenuDescriptor = {
      id: 'weapons.customize', extensionId: 'allin1.gbay', title: 'CUSTOMIZE WEAPONS', homeRouteId: 'weapons.customize', routes: [
        { id: 'weapons.customize', menuId: 'weapons.customize', title: 'CUSTOMIZE WEAPONS', items: [
          { id: 'owned-weapons', label: 'Owned weapons', type: 'route', routeId: 'weapons.customize/owned-weapons' },
          { id: 'workbench-options', label: 'Options', type: 'route', routeId: 'weapons.customize/workbench-options' },
        ] },
        { id: 'weapons.customize/owned-weapons', menuId: 'weapons.customize', title: 'Owned weapons', layout: 'grid', items: [
          { id: 'owned-weapon-pistol', label: 'Pistol', type: 'command', action: 'weapon.customize.select' },
        ] },
        { id: 'weapons.customize/workbench-options', menuId: 'weapons.customize', title: 'Components', layout: 'grid', items: [
          { id: 'component-suppressor', label: 'Suppressor', type: 'command', action: 'weapon.customize.apply' },
          { id: 'component-suppressor-preview', label: 'Preview Suppressor', type: 'command', action: 'weapon.customize.preview' },
          { id: 'stop-world-preview', label: 'Close world preview', type: 'command', action: 'weapon.customize.preview.stop' },
        ] },
      ],
    }
    const projected = projectAllin1GbayMenu(menu)
    expect(projected.routes![0].items.map((item) => [item.id, item.type === 'command' ? item.action : ''])).toEqual([
      ['owned-weapon-pistol', 'weapon.customize.select'],
      ['component-suppressor', 'weapon.customize.apply'],
      ['component-suppressor-preview', 'weapon.customize.preview'],
      ['stop-world-preview', 'weapon.customize.preview.stop'],
    ])
    expect(projected.routes![0].items.find((item) =>
      item.id === 'component-suppressor-preview')?.visible).toBe(false)
    expect(projected.routes![0].items.find((item) =>
      item.id === 'stop-world-preview')?.visible).toBe(false)
  })

  it('projects the complete nine-route GBAY navigation matrix in stable order', () => {
    const route = (id: string, title: string) => ({
      id, menuId: id, parentId: id === 'home' ? undefined : 'home', title, items: [],
    })
    const menu: RoutedMenuDescriptor = {
      id: 'home', extensionId: 'allin1.gbay', title: 'GBAY', homeRouteId: 'home', routes: [
        route('home', 'GBAY Home'),
        route('vehicles', 'Purchase Vehicles'),
        route('weapons', 'Purchase Weapons'),
        route('weapons.customize', 'Customize Weapons'),
        route('gear', 'Gear'),
        route('garage', 'My Garage'),
        route('addons', 'Add-ons'),
        route('diagnostics', 'Diagnostics'),
        route('about', 'About'),
      ],
    }

    const projected = projectAllin1GbayMenu(menu)
    const navigation = projected.routes![0].items.filter((item) => item.id.startsWith('gbay-nav-'))
    expect(navigation.map((item) => item.id)).toEqual([
      'gbay-nav-home', 'gbay-nav-vehicles', 'gbay-nav-weapons',
      'gbay-nav-customization', 'gbay-nav-gear', 'gbay-nav-garage',
      'gbay-nav-addons', 'gbay-nav-diagnostics', 'gbay-nav-about',
    ])
    expect(navigation.find((item) => item.id === 'gbay-nav-customization')).toMatchObject({
      type: 'route', routeId: 'weapons.customize',
    })
    expect(projected.routes!.every((candidate) =>
      candidate.items.filter((item) => item.id.startsWith('gbay-nav-')).length === 9)).toBe(true)
  })
})
