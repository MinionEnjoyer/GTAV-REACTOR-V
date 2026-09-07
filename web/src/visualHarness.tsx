import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { GbaySurface } from './menu/GbaySurface'
import { GbayConfirmationDialog } from './menu/MenuSurface'
import { readabilityFixtures } from './readabilityFixtures'
import { handleGbayCatalogWheel } from './menu/gbayWheelInput'
import { StartupTransitionSurface } from './menu/StartupTransitionSurface'
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

import { Speedometer } from './hud/Speedometer'

const snapshot = structuredClone(readabilityFixtures[view ?? ''] ?? (view === 'selection' ? selectionSnapshot : workbenchSnapshot))
const scrollAudit = new URLSearchParams(window.location.search).has('scroll-audit')
if (scrollAudit) {
  const cards = snapshot.route.items.filter(item => item.type === 'command' && /weapon\.customize\.(select|apply)$/.test(item.action))
  for (let copy = 1; copy < 4; copy++) {
    snapshot.route.items.push(...cards.map(item => ({ ...item, id: `${item.id}-audit-${copy}` })))
  }
  // Use the same window-level handler as MenuSurface. Plain GbaySurface
  // screenshots missed wheel interception because they omit that controller.
  window.addEventListener('wheel', event => {
    handleGbayCatalogWheel(event, document.querySelector('[data-reactor-menu-surface-root]'),
      snapshot.route.items, action => { document.documentElement.dataset.auditPage = action })
  }, { passive: false })
}
const noop = () => {}
const activate = (item: { id: string }) => { document.documentElement.dataset.auditAction = item.id }

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {view === 'speedometer' ? <Speedometer frame={{ schema: 1, visible: true, kind: 'speedometer', speed: 128, units: 'MPH', gear: '5', manual: false, notice: '' }} /> : view === 'preloader' ? <StartupTransitionSurface surfaceGeneration={1} onClose={noop} status={{
      schemaVersion: 1, sequence: 1, sessionId: 'readability-fixture', phase: 'waiting-for-provider',
      providerConnected: false, defaultMenuRequested: false, defaultMenuDeadlineUtc: null,
      components: [
        {id:'reactor', label:'Reactor V', state:'ready', detail:'Interface ready.'},
        {id:'scripthook', label:'ScriptHookV', state:'initializing', detail:'Creating GTA script threads.'},
        {id:'allin1', label:'ALLIN1', state:'waiting', detail:'Waiting for the gameplay provider.'},
      ],
      console: {maxEntries:48, dropped:0, entries:Array.from({length:20}, (_,i)=>({sequence:i+1, timestampUtc:'2026-09-04T18:00:00Z', source:'bootstrap', stage:'provider-wait', message:'Waiting for the managed gameplay provider to finish registering Story Mode services.'}))},
    }} /> : <GbaySurface
      snapshot={snapshot}
      account={{ label: 'Balance', value: '$7,277,301' }}
      loading={false}
      busy={false}
      error={null}
      notice="Ready"
      onClose={noop}
      onFocus={noop}
      onActivate={activate}
      onSetValue={noop}
      onRetry={noop}
    />}
    {view === 'confirmation' && <GbayConfirmationDialog confirmation={{ title: 'Purchase Special Carbine Mk II suppressor?', message: 'Pay $12,500 from Michael’s account? The attachment remains owned when unequipped, and can be equipped again for free.' }} onRespond={(confirmed) => { document.documentElement.dataset.auditConfirmation = String(confirmed) }} />}
  </StrictMode>,
)
