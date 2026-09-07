/// <reference types="node" />
import { afterEach, describe, expect, it, vi } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { parseSpeedometer, Speedometer, subscribeSpeedometer, type SpeedometerFrame } from './Speedometer'
import { hostSurfaceSupersedesPresentation, parseHostSurfaceMode } from '../surface'
import { canAcknowledgeHostSurface } from '../gta/browserRole'
import type { GtaBridge } from '../gta/bridge'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
const hudStyles = readFileSync(resolve(process.cwd(), 'src/hud/hud.css'), 'utf8')

const frame: SpeedometerFrame = { schema: 1, visible: true, kind: 'speedometer', speed: 42.6, units: 'MPH', gear: '3', manual: true, notice: '' }
afterEach(() => vi.useRealTimers())
describe('passive speedometer', () => {
  it('uses 150 percent larger numbers in a compact card-free corner HUD', () => {
    expect(hudStyles).toContain('clamp(90px, 7.5vw, 150px)')
    expect(hudStyles).toContain('font-size: 67.5px')
    expect(hudStyles).toContain('background: transparent')
    expect(hudStyles).toContain('width: max-content')
    expect(hudStyles).not.toMatch(/space-between|min-width|box-shadow|border-left/)
    expect(hudStyles).toContain('bottom: calc(max(5vh, 24px) + 110px)')
  })
  it('renders speed, units and gear without interactive elements or marketplace content', () => {
    const html = renderToStaticMarkup(<Speedometer frame={frame} />)
    expect(html).toContain('43'); expect(html).toContain('MPH'); expect(html).toContain('MANUAL')
    expect(html).not.toMatch(/button|input|tabindex|GBAY|ALLIN1/i)
  })
  it('rejects malformed values and explicit hide', () => {
    for (const patch of [{ speed: NaN }, { speed: Infinity }, { speed: -1 }, { units: 'knots' }, { visible: false }, { schema: '1' }, { gear: 'too long' }])
      expect(parseSpeedometer({ ...frame, ...patch })).toBeNull()
  })
  it('expires, clears on disconnect and cleans up both subscriptions', () => {
    vi.useFakeTimers()
    const listeners = new Map<string, (value: unknown) => void>()
    const bridge = { on: (name: string, fn: (value: unknown) => void) => { listeners.set(name, fn); return () => listeners.delete(name) } } as Pick<GtaBridge, 'on'>
    const update = vi.fn(), off = subscribeSpeedometer(bridge, update)
    listeners.get('hud.frame')!(frame); expect(update).toHaveBeenLastCalledWith(frame)
    vi.advanceTimersByTime(999); expect(update).toHaveBeenLastCalledWith(frame)
    vi.advanceTimersByTime(1); expect(update).toHaveBeenLastCalledWith(null)
    listeners.get('hud.frame')!(frame); listeners.get('host.provider')!({ connected: false })
    expect(update).toHaveBeenLastCalledWith(null)
    off(); expect(listeners.size).toBe(0)
  })
  it('does not supersede a menu and supports exact paint on both browser roles', () => {
    expect(parseHostSurfaceMode({ mode: 'passive-hud' })).toBe('passive-hud')
    expect(hostSurfaceSupersedesPresentation('passive-hud')).toBe(false)
    expect(canAcknowledgeHostSurface('gpu-renderer', 'passive-hud')).toBe(true)
    expect(canAcknowledgeHostSurface('webview-host', 'passive-hud')).toBe(true)
  })
})
