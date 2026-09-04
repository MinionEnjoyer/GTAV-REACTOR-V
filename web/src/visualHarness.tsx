import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { GbaySurface } from './menu/GbaySurface'
import type { MenuControllerSnapshot } from './menu/controller'
import './styles.css'
import './visualHarness.css'

const view = new URLSearchParams(window.location.search).get('view')

const selectionSnapshot: MenuControllerSnapshot = {
  menuId: 'weapons.customize',
  stack: ['home', 'weapons.customize'],
  focusedItemId: 'owned-weapon-carbine',
  route: {
    id: 'weapons.customize',
    menuId: 'weapons.customize',
    title: 'CUSTOMIZE WEAPONS',
    items: [
      { id: 'gbay-nav-home', label: 'Home', icon: '⌂', type: 'route', routeId: 'home' },
      { id: 'search', label: 'Search', value: '', placeholder: 'Search owned weapons', maxLength: 80, type: 'search', action: 'weapon.customize.search' },
      { id: 'category', label: 'Category', value: 'all', type: 'choice', action: 'weapon.customize.category', options: [{ value: 'all', label: 'All owned weapons' }] },
      ...['Pistol', 'Combat Pistol', 'AP Pistol', 'Heavy Pistol', 'Up-n-Atomizer', 'Carbine Rifle'].map((label, index) => ({
        id: index === 5 ? 'owned-weapon-carbine' : `owned-weapon-${index}`,
        label,
        description: `${index === 5 ? 'Rifles' : 'Pistols'} · ${index === 5 ? 240 : 120} ammo`,
        type: 'command' as const,
        action: 'weapon.customize.select',
      })),
      { id: 'pages', label: 'Page', type: 'pagination', page: 1, pageCount: 2, action: 'weapon.customize.page' },
    ],
  },
}

const workbenchSnapshot: MenuControllerSnapshot = {
  menuId: 'weapons.customize',
  stack: ['home', 'weapons.customize'],
  focusedItemId: 'component-suppressor',
  route: {
    id: 'weapons.customize',
    menuId: 'weapons.customize',
    title: 'CUSTOMIZE WEAPONS',
    items: [
      { id: 'gbay-nav-home', label: 'Home', icon: '⌂', type: 'route', routeId: 'home' },
      { id: 'selected-weapon', label: 'Selected weapon', value: 'Carbine Rifle', type: 'status' },
      { id: 'world-preview', label: 'In-world preview', value: 'Active alongside Reactor', tone: 'success', type: 'status' },
      { id: 'change-weapon', label: 'Change weapon', type: 'command', action: 'weapon.customize.back' },
      {
        id: 'workbench-group', label: 'Workbench group', value: 'components', type: 'choice',
        action: 'weapon.customize.group', options: [
          { value: 'ammo', label: 'Ammunition' },
          { value: 'components', label: 'Components' },
          { value: 'tints', label: 'Weapon finishes' },
          { value: 'livery', label: 'Livery colors' },
        ],
      },
      { id: 'component-default', label: 'Default Magazine', description: 'Type: Components · Status: Equipped · Price: FREE · Detail: Standard capacity', type: 'command', action: 'weapon.customize.apply' },
      { id: 'component-extended', label: 'Extended Magazine', description: 'Type: Components · Status: Owned · Price: $8,000 · Detail: Increased capacity', type: 'command', action: 'weapon.customize.apply' },
      { id: 'component-flashlight', label: 'Flashlight', description: 'Type: Components · Status: Owned · Price: $4,500 · Detail: Rail-mounted light', type: 'command', action: 'weapon.customize.apply' },
      { id: 'component-suppressor', label: 'Suppressor', description: 'Type: Components · Status: Available · Price: $12,500 · Detail: Reduced report', type: 'command', action: 'weapon.customize.apply' },
      { id: 'component-grip', label: 'Grip', description: 'Type: Components · Status: Available · Price: $7,000 · Detail: Improved control', type: 'command', action: 'weapon.customize.apply' },
      { id: 'pages', label: 'Page', type: 'pagination', page: 1, pageCount: 2, action: 'weapon.customize.page' },
    ],
  },
}

const snapshot = view === 'selection' ? selectionSnapshot : workbenchSnapshot
const noop = () => {}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <GbaySurface
      snapshot={snapshot}
      account={{ label: 'Balance', value: '$7,277,301' }}
      loading={false}
      busy={false}
      error={null}
      notice="Ready"
      onClose={noop}
      onFocus={noop}
      onActivate={noop}
      onSetValue={noop}
      onRetry={noop}
    />
  </StrictMode>,
)
