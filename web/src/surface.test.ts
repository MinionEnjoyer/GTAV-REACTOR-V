import { describe, expect, it } from 'vitest'
import {
  formatDetectedGtaTarget,
  hostSurfaceSupersedesPresentation,
  parseHostProvider,
  parseHostSurface,
  parseHostSurfaceMode,
  parseProviderConnected,
  resolveInitialHostSurface,
  resolvePresentationHandoff,
  resolveSurfaceView,
  shouldRetainBootstrapFrame,
  shouldRetireBootstrapAfterAcceptance,
} from './surface'

describe('idle Reactor surface', () => {
  it('is fully transparent unless the setup preset opts in explicitly', () => {
    expect(resolveInitialHostSurface('')).toBe('none')
    expect(resolveInitialHostSurface('?renderer=windowed')).toBe('none')
    expect(resolveInitialHostSurface('?surface=splash')).toBe('none')
  })

  it('retains the installer status surface as an explicit setup-only mode', () => {
    expect(resolveInitialHostSurface('?surface=setup-status')).toBe('setup-status')
    expect(resolveInitialHostSurface('?edition=enhanced&surface=setup-status')).toBe('setup-status')
  })

  it('accepts only the bounded bootstrap surface contract', () => {
    expect(parseHostSurfaceMode({ mode: 'none' })).toBe('none')
    expect(parseHostSurfaceMode({ mode: 'about' })).toBe('about')
    expect(parseHostSurfaceMode({ mode: 'verifying' })).toBe('verifying')
    expect(parseHostSurfaceMode('setup-status')).toBe('setup-status')
    expect(parseHostSurfaceMode({ mode: 'initializing' })).toBe('initializing')
    expect(parseHostSurfaceMode({ mode: 'splash' })).toBeNull()
    expect(parseHostSurfaceMode({ mode: true })).toBeNull()
    expect(parseHostSurface({ mode: 'about', generation: 7, edition: 'Enhanced', gameVersion: '1.0.9999.0' })).toEqual({
      mode: 'about', generation: 7, edition: 'Enhanced', gameVersion: '1.0.9999.0',
    })
    expect(parseHostSurface({ mode: 'about', edition: '<script>' })).toEqual({
      mode: 'about', edition: undefined, gameVersion: undefined,
    })
    expect(parseHostSurface({ mode: 'initializing', generation: 0 })).toEqual({
      mode: 'initializing', generation: undefined, edition: undefined, gameVersion: undefined,
    })
    expect(parseHostSurface({
      mode: 'none', generation: 8, handoff: 'presentation',
    })).toEqual({
      mode: 'none', generation: 8, edition: undefined, gameVersion: undefined,
      handoff: 'presentation',
    })
    expect(parseHostSurface({ mode: 'none', handoff: 'provider-connected' })).toBeNull()
  })

  it('accepts only an explicit provider connectivity boolean', () => {
    expect(parseProviderConnected({ connected: true })).toBe(true)
    expect(parseProviderConnected({ connected: false })).toBe(false)
    expect(parseProviderConnected({ connected: 'yes' })).toBeNull()
    expect(parseProviderConnected(true)).toBeNull()
  })

  it('preserves a bounded provider session generation across reconnects', () => {
    expect(parseHostProvider({ connected: true, sessionGeneration: 1 })).toEqual({
      connected: true,
      sessionGeneration: 1,
    })
    expect(parseHostProvider({ connected: false, sessionGeneration: 1 })).toEqual({
      connected: false,
      sessionGeneration: 1,
    })
    expect(parseHostProvider({ connected: true })).toEqual({
      connected: true,
      sessionGeneration: 0,
    })
    expect(parseHostProvider({ connected: true, sessionGeneration: -1 })).toBeNull()
    expect(parseHostProvider({ connected: true, sessionGeneration: 1.5 })).toBeNull()
  })

  it('covers bootstrap and connected F9 open/close surface transitions', () => {
    expect(resolveSurfaceView('none', false)).toBe('transparent')
    // Pre-provider F9 can show and hide About with no managed presentation.
    expect(resolveSurfaceView('about', false)).toBe('about')
    expect(resolveSurfaceView('none', false)).toBe('transparent')
    // Once connected, a fresh typed presentation takes ownership until close.
    expect(resolveSurfaceView('about', true)).toBe('presentation')
    expect(resolveSurfaceView('none', false)).toBe('transparent')
    // Ready Story mode returns to transparent idle. A later managed F9 press
    // must draw GBAY directly; neither bootstrap surface may be inserted.
    expect(resolveSurfaceView('none', false)).toBe('transparent')
    expect(resolveSurfaceView('none', true)).toBe('presentation')
  })

  it('keeps an active managed presentation mounted across the idle handoff marker', () => {
    expect(hostSurfaceSupersedesPresentation('none')).toBe(false)
    expect(hostSurfaceSupersedesPresentation('about')).toBe(true)
    expect(hostSurfaceSupersedesPresentation('verifying')).toBe(true)
    expect(hostSurfaceSupersedesPresentation('initializing')).toBe(true)
    expect(hostSurfaceSupersedesPresentation('setup-status')).toBe(true)
  })

  it('keeps the early GBAY initializer visible until close or a real presentation', () => {
    expect(resolveSurfaceView('initializing', false)).toBe('initializing')
    // F9/Escape closes through the bootstrap host and its authoritative
    // host.surface=none event returns the WebView to transparent idle.
    expect(resolveSurfaceView('none', false)).toBe('transparent')
    // A typed menu presentation wins without an About/setup interstitial.
    expect(resolveSurfaceView('initializing', true)).toBe('presentation')
  })

  it('holds a painted bootstrap presentation until its exact acknowledgement', () => {
    expect(resolvePresentationHandoff('gbay-startup', 'gbay-startup', null)).toEqual({
      holdInitializer: true,
      menuInteractive: false,
    })
    expect(resolvePresentationHandoff('gbay-startup', 'gbay-startup', 'gbay-startup')).toEqual({
      holdInitializer: false,
      menuInteractive: true,
    })
    // Ordinary post-load GBAY has no initializer ownership token and keeps its
    // established direct presentation behavior.
    expect(resolvePresentationHandoff('gbay-later', null, null)).toEqual({
      holdInitializer: false,
      menuInteractive: true,
    })
    // A stale acknowledgement cannot release a newer startup presentation.
    expect(resolvePresentationHandoff('gbay-new', 'gbay-new', 'gbay-old')).toEqual({
      holdInitializer: true,
      menuInteractive: false,
    })
  })

  it.each(['about', 'verifying', 'setup-status', 'initializing'] as const)(
    'retains the %s frame across an early native none boundary',
    (mode) => {
      expect(shouldRetainBootstrapFrame('none', mode, true, false)).toBe(true)
      expect(shouldRetainBootstrapFrame('none', mode, true, true)).toBe(false)
      expect(shouldRetainBootstrapFrame('none', mode, false, false)).toBe(false)
    },
  )

  it('retains an initializer for an explicit early presentation handoff only', () => {
    expect(shouldRetainBootstrapFrame(
      'none', 'initializing', false, false, 'presentation',
    )).toBe(true)
    expect(shouldRetainBootstrapFrame(
      'none', 'initializing', false, false,
    )).toBe(false)
    expect(shouldRetainBootstrapFrame(
      'none', 'initializing', false, true, 'presentation',
    )).toBe(false)
  })

  it('never retains an idle or replacement bootstrap mode', () => {
    expect(shouldRetainBootstrapFrame('none', 'none', true, false)).toBe(false)
    expect(shouldRetainBootstrapFrame('about', 'initializing', true, false)).toBe(false)
  })

  it('mirrors native bootstrap ownership after exact menu acceptance', () => {
    expect(shouldRetireBootstrapAfterAcceptance('initializing', false)).toBe(true)
    expect(shouldRetireBootstrapAfterAcceptance('about', false)).toBe(false)
    expect(shouldRetireBootstrapAfterAcceptance('verifying', false)).toBe(false)
    expect(shouldRetireBootstrapAfterAcceptance('setup-status', false)).toBe(false)
    expect(shouldRetireBootstrapAfterAcceptance('about', true)).toBe(true)
  })

  it('enforces mutually exclusive frontend, Story-loading, and ready views', () => {
    // Before a fresh native snapshot, F9 does not guess either destination.
    expect(resolveSurfaceView('verifying', false)).toBe('verifying')
    // Main-menu F9: Reactor splash/About only.
    expect(resolveSurfaceView('about', false)).toBe('about')
    // Story loading before the managed owner is ready: preloader only.
    expect(resolveSurfaceView('initializing', false)).toBe('initializing')
    // Ready with no F9 request: no surface at all.
    expect(resolveSurfaceView('none', false)).toBe('transparent')
    // Ready after F9: an actual typed ALLIN1 presentation, never a preloader.
    expect(resolveSurfaceView('none', true)).toBe('presentation')
  })

  it('formats detected GTA edition and version without duplicating the product name', () => {
    expect(formatDetectedGtaTarget()).toBe('GTA V')
    expect(formatDetectedGtaTarget('Enhanced')).toBe('GTA V Enhanced')
    expect(formatDetectedGtaTarget('GTA V Legacy', '1.0.3788.0')).toBe('GTA V Legacy 1.0.3788.0')
  })
})
