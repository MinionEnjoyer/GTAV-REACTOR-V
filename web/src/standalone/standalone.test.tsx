import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { createStartupFallbackStatus, parseStartupStatus, selectCurrentStartupStatus } from './startup'
import { StartupTransitionSurface } from './StartupTransitionSurface'
import { adaptMenusToRoutes } from '../menu/adapter'
import { MenuController } from '../menu/controller'
import type { MenuDescriptor } from '../gta/types'

describe('standalone runtime', () => {
  it('renders neutral runtime services with no consumer dependency', () => {
    const state = createStartupFallbackStatus(false)
    expect(parseStartupStatus(state)).toEqual(state)
    const html = renderToStaticMarkup(<StartupTransitionSurface status={state} surfaceGeneration={1} onClose={() => {}} />)
    expect(html).toContain('Reactor V')
    expect(html).toContain('ragewebui-logo.png')
    expect(html).not.toMatch(/allin1|gbay/i)
  })

  it('accepts declared consumer services, but rejects duplicates, omissions and oversized payloads', () => {
    const state = createStartupFallbackStatus(false)
    const extra = { id: 'sample.mod', label: 'Sample mod', state: 'waiting', detail: 'Waiting for registration.' }
    expect(parseStartupStatus({ ...state, components: [...state.components, extra] })).not.toBeNull()
    expect(parseStartupStatus({ ...state, components: [...state.components, extra, extra] })).toBeNull()
    expect(parseStartupStatus({ ...state, components: state.components.slice(1) })).toBeNull()
    expect(parseStartupStatus({ ...state, components: [...state.components, ...Array(40).fill(extra)] })).toBeNull()
  })

  it('does not demote a connected provider on delayed bootstrap replay', () => {
    const ready = createStartupFallbackStatus(true)
    expect(selectCurrentStartupStatus(ready, createStartupFallbackStatus(false))).toBe(ready)
  })

  it('switches tabs by pointer without invoking game actions or growing the back stack', async () => {
    const menu: MenuDescriptor = {
      extensionId: 'sample.mod', id: 'settings', label: 'Settings', description: '', icon: '', order: 1,
      nodes: [{ id: 'tabs', kind: 'tabs', label: 'Sections', description: '', enabled: true, visible: true,
        selectedId: 'one', tabs: [
          { id: 'one', label: 'One', nodes: [{ id: 'a', kind: 'status', label: 'State', value: 'Ready', tone: 'success', description: '', enabled: true, visible: true }] },
          { id: 'two', label: 'Two', nodes: [{ id: 'b', kind: 'status', label: 'State', value: 'Ready', tone: 'success', description: '', enabled: true, visible: true }] },
        ] }],
    }
    const controller = new MenuController(adaptMenusToRoutes([menu], menu.id), { invoke: () => { throw Error('Navigation must not invoke') } })
    await controller.activate()
    await controller.activate()
    const depth = controller.snapshot.stack.length
    for (let i = 0; i < 100; i++) expect(controller.selectTab(`settings/tabs/${i % 2 ? 'one' : 'two'}`)).toBe(true)
    expect(controller.snapshot.stack).toHaveLength(depth)
    expect(controller.selectTab('other.mod/route')).toBe(false)
  })
})
