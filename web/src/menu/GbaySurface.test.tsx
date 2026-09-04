import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { MenuControllerSnapshot } from './controller'
import { GbaySurface } from './GbaySurface'

const callbacks = {
  onClose: () => {},
  onFocus: () => {},
  onActivate: () => {},
  onSetValue: () => {},
  onRetry: () => {},
}

function render(snapshot: MenuControllerSnapshot, account?: { label: string, value: string }) {
  return renderToStaticMarkup(
    <GbaySurface
      snapshot={snapshot}
      account={account}
      loading={false}
      busy={false}
      error={null}
      notice={null}
      {...callbacks}
    />,
  )
}

describe('GBAY marketplace surface', () => {
  it('shows Unequip on the equipped attachment card instead of a separate removal listing', () => {
    const html = render({
      menuId: 'weapons.customize', stack: ['home', 'weapons.customize'], focusedItemId: 'custom-option-can-unequip', route: {
        id: 'weapons.customize', title: 'Customize Vector', items: [
          { id: 'selected-weapon', type: 'status', label: 'Weapon', value: 'KRISS Vector' },
          { id: 'custom-option-can', type: 'command', action: 'weapon.customize.apply', label: 'Suppressor', enabled: false,
            description: 'Type: Component · Status: Active · Price: FREE' },
          { id: 'custom-option-scope', type: 'command', action: 'weapon.customize.apply', label: 'Scope',
            description: 'Type: Component · Status: Owned — equip · Price: FREE' },
          { id: 'custom-option-can-unequip', type: 'command', action: 'weapon.customize.apply', label: 'Unequip Suppressor', enabled: true,
            description: 'Type: Unequip attachment · Status: Unequip · Price: FREE' },
        ],
      },
    })
    expect((html.match(/class="gbay-workbench-card /g) ?? [])).toHaveLength(2)
    expect(html).toContain('aria-label="Unequip Suppressor"')
    expect(html).toContain('>Suppressor</strong>')
    expect(html).not.toContain('>Unequip Suppressor</strong>')
    expect(html).toContain('class="gbay-attachment-unequip">UNEQUIP</em>')
    expect(html).toContain('It remains owned and can be equipped again for free.')
    expect(html).toContain('equipped removable" data-menu-focused="true"')
  })
  it('publishes a concrete per-presentation readiness root', () => {
    const html = render({
      menuId: 'home', stack: ['home'], route: {
        id: 'home', menuId: 'home', title: 'GBAY Home', items: [],
      },
    })

    expect(html).toContain('data-reactor-menu-surface-root="true"')
  })

  it('renders persistent section navigation and a six-card vehicle marketplace', () => {
    const snapshot: MenuControllerSnapshot = {
      menuId: 'vehicles',
      stack: ['gbay', 'vehicles'],
      focusedItemId: 'vehicle-0',
      route: {
        id: 'vehicles', menuId: 'vehicles', title: 'Vehicles', items: [
          { id: 'gbay-nav-home', label: 'Home', icon: '⌂', type: 'route', routeId: 'gbay' },
          { id: 'gbay-nav-vehicles', label: 'Vehicles', icon: '◆', type: 'route', routeId: 'vehicles' },
          { id: 'balance', label: 'Balance', value: '$2,000,000', tone: 'success', type: 'status' },
          { id: 'search', label: 'Search', value: '', type: 'search', action: 'vehicle.search' },
          { id: 'category', label: 'Category', value: 'all', type: 'choice', action: 'vehicle.category', options: [{ value: 'all', label: 'All' }] },
          ...Array.from({ length: 6 }, (_, index) => ({
            id: `vehicle-${index}`,
            label: `Vehicle ${index + 1}`,
            description: `$${index + 1},000 · Available · Sports`,
            type: 'command' as const,
            action: 'vehicle.checkout',
          })),
          { id: 'pages', label: 'Page', type: 'pagination', page: 1, pageCount: 2, action: 'vehicle.page' },
        ],
      },
    }
    const html = render(snapshot)
    expect(html).toContain('gbay-section-nav')
    expect(html).toContain('aria-current="page"')
    expect((html.match(/<article class="gbay-card/g) ?? [])).toHaveLength(6)
    expect(html).toContain('Select a vehicle to review delivery options')
    expect(html).toContain('Previous page')
    expect(html).toContain('Next page')
    expect(html).toContain('LB/RB PAGES')
  })

  it('renders the home hub from typed route tiles', () => {
    const html = render({
      menuId: 'gbay', stack: ['gbay'], route: {
        id: 'gbay', menuId: 'gbay', title: 'GBAY Home', items: [
          { id: 'vehicles-link', label: 'Vehicles', description: 'Browse the catalog.', type: 'route', routeId: 'vehicles' },
          { id: 'gear-link', label: 'Gear', description: 'Armor and equipment.', type: 'route', routeId: 'gear' },
        ],
      },
    })
    expect(html).toContain('gbay-home-grid')
    expect(html).toContain('What are you shopping for?')
    expect(html).toContain('Browse the catalog.')
  })

  it('attaches the typed customization route to the Weapons home tile as a wrench tool', () => {
    const html = render({
      menuId: 'home', stack: ['home'], route: {
        id: 'home', menuId: 'home', title: 'GBAY Home', items: [
          { id: 'open-weapons', label: 'Weapons', description: 'Purchase weapons.', type: 'route', routeId: 'weapons' },
          { id: 'open-customization', label: 'Customize', description: 'Modify owned weapons.', type: 'route', routeId: 'weapons.customize' },
          { id: 'open-gear', label: 'Gear', description: 'Armor and equipment.', type: 'route', routeId: 'gear' },
        ],
      },
    })
    expect((html.match(/class="gbay-home-tile"/g) ?? [])).toHaveLength(2)
    expect(html).toContain('class="gbay-home-primary has-tool"')
    expect(html).toContain('class="gbay-home-tool"')
    expect(html).toContain('aria-label="Customize weapons"')
    expect(html).toContain('title="Customize weapons"')
    expect(html).toContain('Purchase weapons.')
    expect(html).not.toContain('Modify owned weapons.')
    expect(html).not.toContain('>Customize</strong>')
  })

  it('preserves the 0.5.0 home services while keeping customization attached to Weapons', () => {
    const html = render({
      menuId: 'home', stack: ['home'], route: {
        id: 'home', menuId: 'home', title: 'GBAY Home', items: [
          { id: 'open-vehicles', label: 'Vehicles', description: 'Browse vehicles.', type: 'route', routeId: 'vehicles' },
          { id: 'open-weapons', label: 'Purchase Weapons', description: 'Purchase weapons.', type: 'route', routeId: 'weapons' },
          { id: 'open-customization', label: 'Customize Weapons', description: 'Modify owned weapons.', type: 'route', routeId: 'weapons.customize' },
          { id: 'open-gear', label: 'Gear', description: 'Armor and equipment.', type: 'route', routeId: 'gear' },
          { id: 'open-garage', label: 'My Garage', description: 'Manage stored vehicles.', type: 'route', routeId: 'garage' },
          { id: 'open-diagnostics', label: 'Diagnostics', description: 'Review service health.', type: 'route', routeId: 'diagnostics' },
          { id: 'open-about', label: 'About', description: 'About ALLIN1.', type: 'route', routeId: 'about' },
        ],
      },
    })

    expect((html.match(/class="gbay-home-tile"/g) ?? [])).toHaveLength(6)
    expect(html).toContain('>Vehicles<')
    expect(html).toContain('>Purchase Weapons<')
    expect(html).toContain('>Gear<')
    expect(html).toContain('>My Garage<')
    expect(html).toContain('>Diagnostics<')
    expect(html).toContain('>About<')
    expect(html).toContain('aria-label="Customize weapons"')
    expect(html).not.toContain('>Customize Weapons<')
  })

  it.each([
    ['weapons.customize', 'Customize Weapons'],
    ['garage', 'My Garage'],
    ['diagnostics', 'Diagnostics'],
    ['about', 'About'],
  ])('keeps the shell account visible on the %s route', (routeId, title) => {
    const html = render({
      menuId: routeId, stack: ['home', routeId], route: {
        id: routeId, menuId: routeId, title, items: [],
      },
    }, { label: 'Balance', value: '$2,000,000' })

    expect(html).toContain('<small>Balance</small><strong>$2,000,000</strong>')
    expect(html).not.toContain('PLAYER ACCOUNT')
    expect(html).not.toContain('<strong>GBAY</strong></div>')
  })

  it('renders descriptor-backed delivery choices without inventing a purchase action', () => {
    const html = render({
      menuId: 'delivery', stack: ['gbay', 'vehicles', 'vehicle-delivery'], route: {
        id: 'vehicle-delivery', menuId: 'delivery', title: 'Delivery locations', items: [
          { id: 'deliver-garage', label: 'Downtown Garage', description: 'Six open spaces', type: 'command', action: 'vehicle.deliver' },
          { id: 'quote', label: 'Total', value: '$45,000', type: 'status' },
        ],
      },
    })
    expect(html).toContain('Choose a delivery location')
    expect(html).toContain('Downtown Garage')
    expect(html).not.toContain('vehicle.deliver')
  })

  it('renders Purchase Weapons as a filterable six-card catalog', () => {
    const html = render({
      menuId: 'weapons', stack: ['home', 'weapons'], route: {
        id: 'weapons', menuId: 'weapons', title: 'PURCHASE WEAPONS', items: [
          { id: 'search', label: 'Search', value: '', type: 'search', action: 'weapon.search' },
          { id: 'category', label: 'Category', value: 'all', type: 'choice', action: 'weapon.category', options: [{ value: 'all', label: 'All' }] },
          { id: 'ownership', label: 'Ownership', value: 'all', type: 'choice', action: 'weapon.ownership', options: [{ value: 'all', label: 'All listings' }] },
          { id: 'favorites', label: 'Favorites only', value: false, type: 'toggle', action: 'weapon.favorites' },
          ...Array.from({ length: 6 }, (_, index) => ({ id: `weapon-${index}`, label: `Weapon ${index}`, description: `Price: $${index + 1},000 · Ownership: Available · Category: Pistols`, type: 'command' as const, action: 'weapon.purchase' })),
          { id: 'weapon-favorite-0', label: 'Remove favorite', type: 'command', action: 'weapon.favorite' },
          { id: 'pages', label: 'Page', type: 'pagination', page: 1, pageCount: 3, action: 'weapon.page' },
        ],
      },
    })
    expect(html).toContain('aria-label="Weapon listings"')
    expect((html.match(/<article class="gbay-card gbay-product-card weapon/g) ?? [])).toHaveLength(6)
    expect(html).toContain('Select an unowned weapon to purchase. Use the wrench to customize owned weapons.')
    expect(html).not.toContain('refill ammunition')
    expect(html).toContain('class="gbay-card-favorite active"')
    expect(html).not.toContain('weapon.purchase')
  })

  it('marks permanent owned weapons while leaving consumable smoke bundles repeatable', () => {
    const html = render({
      menuId: 'weapons', stack: ['home', 'weapons'], route: {
        id: 'weapons', menuId: 'weapons', title: 'PURCHASE WEAPONS', items: [
          {
            id: 'weapon-pistol', label: 'Pistol  ·  OWNED',
            description: 'Price: $500 · Ownership: Owned · Category: Pistols · Status: Already owned',
            enabled: false, type: 'command', action: 'weapon.purchase',
          },
          {
            id: 'weapon-smoke', label: 'Smoke Grenade Bundle',
            description: 'Price: $250 · Ownership: 3 in stock · Category: Throwables · Status: Available',
            enabled: true, type: 'command', action: 'weapon.purchase',
          },
          { id: 'weapon-favorite-pistol', label: 'Add favorite', type: 'command', action: 'weapon.favorite' },
        ],
      },
    })

    expect((html.match(/class="gbay-card gbay-product-card weapon owned"/g) ?? [])).toHaveLength(1)
    expect((html.match(/class="gbay-weapon-owned-badge">OWNED<\/em>/g) ?? [])).toHaveLength(1)
    expect(html).toMatch(/class="gbay-card-corner-actions"><em class="gbay-weapon-owned-badge">OWNED<\/em><button[^>]+class="gbay-card-favorite"/)
    expect(html).toContain('class="gbay-product-state owned">Owned</span>')
    expect(html).toMatch(/class="gbay-card-main" disabled=""[^>]*>.*Pistol  ·  OWNED/)
    expect(html).toContain('Smoke Grenade Bundle')
    expect(html).toContain('>3 in stock</span>')
    expect(html).not.toContain('Smoke Grenade Bundle  ·  OWNED')
  })

  it('renders weapon customization as a compact accessible wrench beside Weapons', () => {
    const html = render({
      menuId: 'weapons', stack: ['home', 'weapons'], route: {
        id: 'weapons', menuId: 'weapons', title: 'PURCHASE WEAPONS', items: [
          { id: 'gbay-nav-weapons', label: 'Weapons', icon: '⌖', type: 'route', routeId: 'weapons' },
          { id: 'gbay-nav-customization', label: 'Customize', icon: '✦', type: 'route', routeId: 'weapons.customize' },
        ],
      },
    })
    expect(html).toContain('>Weapons</button>')
    expect(html).toContain('class="gbay-nav-tool"')
    expect(html).toContain('aria-label="Customize weapons"')
    expect(html).toContain('title="Customize weapons"')
    expect(html).toContain('class="gbay-wrench-icon"')
    expect(html).not.toContain('>Customize</button>')
  })

  it('renders gear state cards and category paging', () => {
    const html = render({
      menuId: 'gear', stack: ['home', 'gear'], route: {
        id: 'gear', menuId: 'gear', title: 'GEAR', items: [
          { id: 'category', label: 'Category', value: 'armor', type: 'choice', action: 'gear.category', options: [{ value: 'armor', label: 'Armor' }] },
          { id: 'gear-heavy', label: 'Heavy Armor', description: 'Category: Protection · Status: Remove (repurchase required) · Price: $500', type: 'command', action: 'gear.apply' },
          { id: 'gear-heavy-preview', label: 'Heavy Armor preview', type: 'media', source: 'assets/allin1/gear/armor_heavy.png', mediaType: 'image' },
          { id: 'pages', label: 'Page', type: 'pagination', page: 1, pageCount: 1, action: 'gear.page' },
        ],
      },
    })
    expect(html).toContain('aria-label="Gear listings"')
    expect(html).toContain('Heavy Armor')
    expect(html).toContain('src="assets/allin1/gear/armor_heavy.png"')
    expect(html).toContain('alt="Heavy Armor preview"')
    expect(html).toContain('gbay-product-card gear equipped')
    expect(html).toContain('class="gbay-gear-action">UNEQUIP<')
    expect(html).toContain('>EQUIPPED<')
    expect(html).not.toContain('gbay-product-placeholder')
  })

  it('keeps the safe placeholder only when a gear listing has no published artwork', () => {
    const html = render({
      menuId: 'gear', stack: ['home', 'gear'], route: {
        id: 'gear', menuId: 'gear', title: 'GEAR', items: [
          { id: 'gear-custom', label: 'Custom Gear', description: 'Category: Equipment · Status: Purchase · Price: $900', type: 'command', action: 'gear.apply' },
        ],
      },
    })
    expect(html).toContain('gbay-product-placeholder')
    expect(html).toContain('$900')
    expect(html).not.toContain('gbay-gear-action')
  })

  it('renders the typed garage selector and groups guarded actions into one stored-vehicle row', () => {
    const html = render({
      menuId: 'garage', stack: ['home', 'garage'], route: {
        id: 'garage', menuId: 'garage', title: 'MY GARAGE', items: [
          { id: 'location-filter', label: 'Garage', value: 'harmony', type: 'choice', action: 'garage.location', options: [
            { value: 'all', label: 'All garages' },
            { value: 'harmony', label: 'Harmony Garage' },
            { value: 'harbour', label: 'Harbour Marina' },
          ] },
          { id: 'location-harmony', label: 'Harmony Garage', value: '2 / 10 used', tone: 'neutral', type: 'status' },
          { id: 'location-harbour', label: 'Harbour Marina', value: '1 / 8 used', tone: 'neutral', type: 'status' },
          { id: 'location-waypoint-harmony', label: 'Navigate to Harmony Garage', description: 'Set a GPS route to this garage.', type: 'command', action: 'garage.waypoint' },
          { id: 'stored-harmony-0-retrieve', label: 'Retrieve Elegy', description: 'Location: Harmony Garage · Plate: GBAY · Move this vehicle into the world.', type: 'command', action: 'garage.retrieve' },
          { id: 'stored-harmony-0-sell', label: 'Sell Elegy', description: 'Location: Harmony Garage · Plate: GBAY · Sale value: $12,000', type: 'command', action: 'garage.sell' },
          { id: 'results', label: 'Results', value: '1 matching vehicle', tone: 'neutral', type: 'status' },
          { id: 'pages', label: 'Page', page: 1, pageCount: 2, type: 'pagination', action: 'garage.page' },
          { id: 'refresh', label: 'Refresh My Garage', type: 'command', action: 'garage.refresh' },
        ],
      },
    })
    expect(html).toContain('class="gbay-garage-page"')
    expect(html).toContain('gbay-garage-storage')
    expect(html).toContain('2 / 10 used')
    expect(html).not.toContain('>Unavailable<')
    expect(html).toContain('aria-label="Navigate to Harmony Garage"')
    expect(html).toContain('aria-label="Garage locations"')
    expect(html).toContain('data-location-value="harmony"')
    expect(html).toContain('aria-current="true"')
    expect(html).toContain('Harmony Garage')
    expect(html).toContain('2 / 10 used')
    expect(html).toContain('aria-label="Stored vehicle collection"')
    expect(html).toContain('class="gbay-garage-grid gbay-garage-scrollbox"')
    expect(html).toContain('role="list"')
    expect(html).toContain('aria-label="Stored vehicles"')
    expect(html).toContain('1 matching vehicle')
    expect((html.match(/data-garage-vehicle="stored-harmony-0"/g) ?? [])).toHaveLength(1)
    expect((html.match(/gbay-garage-vehicle-mark/g) ?? [])).toHaveLength(1)
    expect(html).toContain('>SELL $12,000<')
    expect(html).toContain('>RETRIEVE<')
    expect(html).not.toContain('Previous page')
    expect(html).not.toContain('Next page')
    expect(html).not.toContain('LB/RB PAGES')
    expect(html).toContain('SCROLL VEHICLES')
    expect(html).not.toContain('Refresh My Garage')
    expect(html).not.toContain('garage.sell')
    expect(html).not.toContain('garage.retrieve')
  })

  it('renders the complete garage collection inside one pager-free scroll box', () => {
    const html = render({
      menuId: 'garage', stack: ['home', 'garage'], route: {
        id: 'garage', menuId: 'garage', title: 'MY GARAGE', items: [
          { id: 'location-filter', label: 'Garage', value: 'all', type: 'choice', action: 'garage.location', options: [
            { value: 'all', label: 'All garages' },
          ] },
          ...Array.from({ length: 24 }, (_, index) => ({
            id: `stored-harmony-${index}-retrieve`,
            label: `Retrieve Vehicle ${index + 1}`,
            description: `Location: Harmony Garage · Plate: TEST${index + 1}`,
            type: 'command' as const,
            action: 'garage.retrieve',
          })),
          { id: 'results', label: 'Results', value: '24 matching vehicles', tone: 'neutral', type: 'status' },
          { id: 'pages', label: 'Page', page: 1, pageCount: 4, type: 'pagination', action: 'garage.page' },
        ],
      },
    })

    expect((html.match(/data-garage-vehicle=/g) ?? [])).toHaveLength(24)
    expect(html).toContain('class="gbay-garage-grid gbay-garage-scrollbox"')
    expect(html).toContain('24 matching vehicles')
    expect(html).not.toContain('Previous page')
    expect(html).not.toContain('Next page')
  })

  it('surfaces authoritative garage interior, customization, and emergency recovery controls', () => {
    const html = render({
      menuId: 'garage', stack: ['home', 'garage'], focusedItemId: 'davis-style', route: {
        id: 'garage', menuId: 'garage', title: 'MY GARAGE', items: [
          { id: 'location-filter', label: 'Garage', value: 'davis', type: 'choice', action: 'garage.location', options: [
            { value: 'all', label: 'All garages' },
            { value: 'davis', label: 'Davis Auto Shop' },
          ] },
          { id: 'location-davis', label: 'Davis Auto Shop', value: '1 / 10 used', tone: 'neutral', type: 'status' },
          { id: 'interior-mode', label: 'Davis Auto Shop interior', value: 'Live preview active', tone: 'success', type: 'status' },
          { id: 'davis-style', label: 'Interior style', description: 'Saved per character · live preview active.', value: 'clean', type: 'choice', action: 'garage.customize', options: [
            { value: 'clean', label: 'Clean' },
            { value: 'industrial', label: 'Industrial' },
          ] },
          { id: 'davis-floor', label: 'Floor finish', value: 'light', type: 'choice', action: 'garage.customize', options: [
            { value: 'light', label: 'Light' },
            { value: 'dark', label: 'Dark' },
          ] },
          { id: 'emergency-recovery', label: 'Emergency Recovery', description: 'Use only if a managed garage transition leaves you stuck.', type: 'command', action: 'garage.recover' },
          { id: 'refresh', label: 'Refresh My Garage', type: 'command', action: 'garage.refresh' },
        ],
      },
    })
    expect(html).toContain('aria-label="Garage interior and recovery"')
    expect(html).toContain('class="gbay-garage-interior tone-success"')
    expect(html).toContain('Davis Auto Shop interior')
    expect(html).toContain('Live preview active')
    expect(html).toContain('aria-label="Interior customization"')
    expect(html).toContain('aria-label="Interior style"')
    expect(html).toContain('<option value="clean" selected="">Clean</option>')
    expect(html).toContain('class="focused" data-menu-focused="true"')
    expect(html).toContain('Floor finish')
    expect(html).toContain('class="gbay-garage-recovery"')
    expect(html).toContain('Emergency Recovery')
    expect(html).toContain('>RECOVER<')
    expect(html).not.toContain('garage.customize')
    expect(html).not.toContain('garage.recover')
  })

  it('honors bridge-disabled garage specialization controls', () => {
    const html = render({
      menuId: 'garage', stack: ['home', 'garage'], route: {
        id: 'garage', menuId: 'garage', title: 'MY GARAGE', items: [
          { id: 'interior-mode', label: 'Davis Auto Shop interior', value: 'Unavailable in this session', tone: 'warning', type: 'status' },
          { id: 'davis-style', label: 'Interior style', description: 'Finish loading Davis to edit.', value: 'clean', enabled: false, type: 'choice', action: 'garage.customize', options: [{ value: 'clean', label: 'Clean' }] },
          { id: 'emergency-recovery', label: 'Emergency Recovery', description: 'Recovery is unavailable.', enabled: false, type: 'command', action: 'garage.recover' },
        ],
      },
    })
    expect(html).toContain('class="gbay-garage-interior tone-warning"')
    expect(html).toContain('aria-label="Interior style" disabled=""')
    expect(html).toMatch(/class="gbay-garage-recovery"[^>]*disabled=""/)
  })

  it('renders automatically populated owned weapons in the dedicated readable editor', () => {
    const html = render({
      menuId: 'weapons.customize', stack: ['home', 'weapons.customize'], route: {
        id: 'weapons.customize', menuId: 'weapons.customize', title: 'CUSTOMIZE WEAPONS', items: [
          { id: 'gbay-nav-home', label: 'Home', icon: '⌂', type: 'route', routeId: 'home' },
          { id: 'search', label: 'Search', value: '', type: 'search', action: 'weapon.customize.search' },
          { id: 'category', label: 'Category', value: 'all', type: 'choice', action: 'weapon.customize.category', options: [{ value: 'all', label: 'All' }] },
          { id: 'load-owned-weapons', label: 'Load owned weapons', type: 'command', action: 'weapon.customize.load' },
          ...Array.from({ length: 6 }, (_, index) => ({ id: `owned-weapon-${index}`, label: `Owned Weapon ${index + 1}`, description: 'Category: Rifles · Ammo: 240', type: 'command' as const, action: 'weapon.customize.select' })),
          ...Array.from({ length: 6 }, (_, index) => ({ id: `owned-weapon-${index}-preview`, label: `Owned Weapon ${index + 1} artwork`, mediaType: 'image' as const, source: `images/weapons/owned-weapon-${index}.png`, type: 'media' as const })),
        ],
      },
    })
    expect(html).toContain('class="gbay-weapon-editor-stage"')
    expect(html).toContain('class="gbay-weapon-editor-shell"')
    expect(html).toContain('aria-label="GBAY weapon workbench"')
    expect(html).toContain('GBAY WORKBENCH')
    expect(html).toContain('Weapon customization')
    expect(html).toContain('>‹ GBAY</button>')
    expect(html).not.toContain('class="gbay-section-nav"')
    expect(html).not.toContain('gbay-customizer-shell')
    expect(html).toContain('Choose an owned weapon')
    expect(html).toContain('aria-label="Owned weapons"')
    expect((html.match(/class="gbay-customize-weapon(?: focused)?"/g) ?? [])).toHaveLength(6)
    expect((html.match(/class="gbay-customize-weapon-visual"/g) ?? [])).toHaveLength(6)
    expect((html.match(/data-reactor-gbay-preview="true"/g) ?? [])).toHaveLength(6)
    expect(html).toContain('src="images/weapons/owned-weapon-0.png"')
    expect(html).toContain('alt="Owned Weapon 1 preview"')
    expect(html).toContain('Rifles · 240 ammo')
    expect(html).not.toContain('Load owned weapons')
    expect(html).not.toContain('Check owned weapons')
    expect(html).not.toContain('class="gbay-customize-weapon-mark"')
    expect((html.match(/class="gbay-customize-weapon-tool"/g) ?? [])).toHaveLength(6)
    expect((html.match(/class="gbay-wrench-icon"/g) ?? [])).toHaveLength(7)
    expect(html).toContain('aria-label="Customize Owned Weapon 1"')
    expect(html).toContain('title="Customize Owned Weapon 1"')
    expect(html).not.toContain('<em>CUSTOMIZE')
    expect(html).not.toContain('weapon.customize.select')
  })

  it('renders selected weapon workbench groups, option state, and change control', () => {
    const html = render({
      menuId: 'weapons.customize', stack: ['home', 'weapons.customize'], route: {
        id: 'weapons.customize', menuId: 'weapons.customize', title: 'CUSTOMIZE WEAPONS', items: [
          { id: 'selected-weapon', label: 'Selected weapon', value: 'Carbine Rifle', type: 'status' },
          { id: 'world-preview', label: 'In-world preview', value: 'Active alongside Reactor', tone: 'success', type: 'status' },
          { id: 'change-weapon', label: 'Change weapon', type: 'command', action: 'weapon.customize.back' },
          { id: 'workbench-group', label: 'Workbench group', value: 'components', type: 'choice', action: 'weapon.customize.group', options: [
            { value: 'ammo', label: 'Ammo' }, { value: 'components', label: 'Components' },
            { value: 'tints', label: 'Tints' }, { value: 'livery', label: 'Livery' },
          ] },
          { id: 'component-suppressor', label: 'Suppressor', description: 'Type: Components · Status: Equipped · Price: $12,500 · Detail: Muzzle', type: 'command', action: 'weapon.customize.apply' },
          { id: 'component-magazine', label: 'Extended Magazine', description: 'Type: Components · Status: Owned · Price: $8,000 · Detail: Magazine', type: 'command', action: 'weapon.customize.apply' },
          ...Array.from({ length: 10 }, (_, index) => ({ id: `component-option-${index}`, label: `Attachment ${index + 1}`, description: `Type: Components · Status: Available · Price: $${index + 1},000 · Detail: Rail ${index + 1}`, type: 'command' as const, action: 'weapon.customize.apply' })),
          { id: 'pages', label: 'Page', type: 'pagination', page: 1, pageCount: 2, action: 'weapon.customize.page' },
        ],
      },
    })
    expect(html).toContain('Carbine Rifle')
    expect(html).toContain('Ammo')
    expect(html).toContain('Components')
    expect(html).toContain('Weapon Finishes')
    expect(html).toContain('Livery Colors')
    expect(html).toContain('Change weapon')
    expect(html).toContain('In-world preview')
    expect(html).toContain('Active alongside Reactor')
    expect(html).toContain('gbay-workbench-preview-status')
    expect(html).toContain('gbay-workbench-card equipped')
    expect(html).toContain('$12,500')
    expect(html).toContain('class="gbay-weapon-editor-stage"')
    expect(html).toContain('class="gbay-weapon-editor-shell"')
    expect(html).toContain('GBAY WORKBENCH')
    expect(html).toContain('aria-label="Weapon customization options"')
    expect(html).toContain('gbay-workbench-scrollbox')
    expect(html).toContain('tabindex="0"')
    expect((html.match(/<button[^>]+class="gbay-workbench-card(?:\s|\")/g) ?? [])).toHaveLength(12)
    expect(html).toContain('SCROLL TO VIEW ALL ATTACHMENTS')
    expect(html).not.toContain('Page 1 / 2')
    expect(html).not.toContain('Previous page')
    expect(html).not.toContain('gbay-customizer-shell')
    expect(html).not.toContain('weapon.customize.apply')
  })

  it.each([
    ['addons', 'ADD-ONS', 'Open package manager', 'addons.open'],
    ['diagnostics', 'DIAGNOSTICS', 'View last health report', 'diagnostics.open'],
  ] as const)('renders the %s service route through the guarded generic panel', (
    routeId,
    title,
    label,
    action,
  ) => {
    const html = render({
      menuId: routeId,
      stack: ['home', routeId],
      focusedItemId: `${routeId}-action`,
      route: {
        id: routeId,
        menuId: routeId,
        title,
        items: [
          { id: 'gbay-nav-home', label: 'Home', icon: '⌂', type: 'route', routeId: 'home' },
          { id: `gbay-nav-${routeId}`, label: title, icon: 'i', type: 'route', routeId },
          {
            id: `${routeId}-action`, label, description: 'Host-validated read-only fixture.',
            type: 'command', action,
          },
        ],
      },
    })
    expect(html).toContain(`>${title}<`)
    expect(html).toContain(label)
    expect(html).toContain('Host-validated read-only fixture.')
    expect(html).toContain('data-menu-focused="true"')
    expect(html).not.toContain(action)
  })

  it('retires legacy GBAY refresh commands from generic service routes', () => {
    const html = render({
      menuId: 'diagnostics', stack: ['home', 'diagnostics'], route: {
        id: 'diagnostics', menuId: 'diagnostics', title: 'DIAGNOSTICS', items: [
          { id: 'health', label: 'Runtime', value: 'Healthy', type: 'status' },
          { id: 'refresh', label: 'Run health check', type: 'command', action: 'diagnostics.refresh' },
        ],
      },
    })
    expect(html).toContain('Runtime')
    expect(html).toContain('Healthy')
    expect(html).not.toContain('Run health check')
  })

  it('renders the branded ALLIN1 About route instead of the generic service panel', () => {
    const html = render({
      menuId: 'about', stack: ['home', 'about'], focusedItemId: 'open-support', route: {
        id: 'about', menuId: 'about', title: 'ABOUT ALLIN1', items: [
          { id: 'gbay-nav-home', label: 'Home', icon: '⌂', type: 'route', routeId: 'home' },
          { id: 'gbay-nav-about', label: 'About', icon: 'i', type: 'route', routeId: 'about' },
          { id: 'version', label: 'Version', value: '0.6.1', tone: 'success', type: 'status' },
          { id: 'edition', label: 'GTA edition', value: 'Enhanced', tone: 'success', type: 'status' },
          { id: 'runtime', label: 'Script runtime', value: 'ScriptHookVDotNet3 v3.6.0', tone: 'success', type: 'status' },
          { id: 'purpose', label: 'ALLIN1', value: 'Bring GTA Online DLC content into Story Mode with one click.', type: 'status' },
          { id: 'creator', label: 'Created and maintained by', value: 'MinionEnjoyer', type: 'status' },
          { id: 'support', label: 'Support', value: 'buymeacoffee.com/minionenjoyer', type: 'status' },
          { id: 'open-support', label: 'Open support page', description: 'Host-confirmed support action.', type: 'command', action: 'about.support' },
        ],
      },
    }, { label: 'Balance', value: '$2,000,000' })

    expect(html).toContain('class="gbay-about"')
    expect(html).toContain('src="allin1-logo.png"')
    expect(html).toContain('0.6.1')
    expect(html).toContain('Enhanced')
    expect(html).toContain('ScriptHookVDotNet3 v3.6.0')
    expect(html).toContain('buymeacoffee.com/minionenjoyer')
    expect(html).toContain('<small>Balance</small><strong>$2,000,000</strong>')
    expect(html).not.toContain('PLAYER ACCOUNT')
    expect(html).toContain('data-menu-focused="true"')
    expect(html).not.toContain('about.support')
  })
})
