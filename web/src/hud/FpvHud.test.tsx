/// <reference types="node" />
import { afterEach, describe, expect, it, vi } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { FpvHud, PassiveHud, parseFpvHud, parsePassiveHud, subscribePassiveHud, type FpvHudFrame } from './FpvHud'
import type { GtaBridge } from '../gta/bridge'

const css = readFileSync(resolve(process.cwd(), 'src/hud/fpv-hud.css'), 'utf8')
const standard = { schema: 1 as const, visible: true as const, kind: 'speedometer' as const, speed: 58, units: 'KMH' as const, gear: '3', manual: false, notice: '' }
const fpv: FpvHudFrame = {
  schema: 1, layout: 'betaflight', color: '#79ff5c', opacity: .85, scale: 1.15,
  pitch: 4, roll: -12, speed: 51.7, altitude: 123.4, homeDistance: 485, heading: 271.2,
  timerSeconds: 125, flightMode: 'ACRO', cameraMode: 'FRONT', payload: 'MINE ARMED',
  show: { artificialHorizon: true, crosshair: true, speed: true, altitude: true, homeDistance: true, heading: true, timer: true, flightMode: true, cameraMode: true, payload: true, controlHints: true },
  controlHints: ['RT THROTTLE', 'RB ARM'],
}
afterEach(() => vi.useRealTimers())

describe('FPV passive HUD', () => {
  it('strictly accepts a bounded schema-one metadata payload', () => {
    expect(parseFpvHud(fpv)).toEqual(fpv)
    expect(parseFpvHud({ ...fpv, color: 'red; background:url(x)' })).toBeNull()
    expect(parseFpvHud({ ...fpv, layout: 'cinematic' })).toBeNull()
    expect(parseFpvHud({ ...fpv, scale: 3 })).toBeNull()
    expect(parseFpvHud({ ...fpv, pitch: Infinity })).toBeNull()
    expect(parseFpvHud({ ...fpv, controlHints: ['one', 'two', 'three', 'four', 'five'] })).toBeNull()
    expect(parseFpvHud({ ...fpv, show: { ...fpv.show, timer: 'yes' } })).toBeNull()
  })

  it('keeps the established speedometer valid when fpv metadata is absent or malformed', () => {
    expect(parsePassiveHud(standard)).toEqual({ speedometer: standard, fpv: null })
    expect(parsePassiveHud({ ...standard, fpv: { schema: 99 } })).toEqual({ speedometer: standard, fpv: null })
    expect(parsePassiveHud({ ...standard, fpv })).toEqual({ speedometer: standard, fpv })
  })

  it('renders a non-interactive Betaflight-like OSD with all requested telemetry', () => {
    const html = renderToStaticMarkup(<FpvHud frame={fpv} units="KMH" />)
    expect(html).toMatch(/FPV flight telemetry|271°|52 km\/h|123 m|485 m|2:05|ACRO|CAM FRONT|MINE ARMED|RT THROTTLE/)
    expect(html).not.toMatch(/button|input|tabindex|onClick|<script/i)
    expect(css).toContain('pointer-events: none')
    expect(css).toContain('reactor-fpv-hud--bomber')
    expect(css).toContain('reactor-fpv-hud--compact')
  })

  it('keeps the viewport root unscaled and scales each HUD visual from its fixed anchor', () => {
    const root = css.match(/\.reactor-fpv-hud \{([\s\S]*?)\n\}/)?.[1] ?? ''
    expect(root).not.toMatch(/transform\s*:/)
    expect(css).toContain('.reactor-fpv-value {')
    expect(css).toContain('transform: scale(var(--reactor-fpv-scale))')
    expect(css).toContain('transform-origin: top left')
    expect(css).toContain('transform-origin: top right')
    expect(css).toContain('transform-origin: bottom left')
    expect(css).toContain('transform-origin: bottom right')
    expect(css).toContain('transform-origin: bottom center')
    expect(css).toContain('.reactor-fpv-hud--compact .reactor-fpv-speed')
    expect(css).toContain('left: 38%; transform-origin: bottom left')
    expect(css).toContain('right: 38%; transform-origin: bottom right')
    expect(css).toContain('flex-wrap: wrap')
    expect(css).toContain('justify-content: center')
    expect(css).toContain('max-width: calc(100vw / var(--reactor-fpv-scale))')
  })

  it('honors individual visibility flags across compact and bomber layouts', () => {
    const onlyPayload = {
      ...fpv,
      layout: 'bomber' as const,
      show: { ...fpv.show, artificialHorizon: false, crosshair: false, speed: false, altitude: false,
        homeDistance: false, heading: false, timer: false, flightMode: false, cameraMode: false,
        controlHints: false },
    }
    const bomber = renderToStaticMarkup(<FpvHud frame={onlyPayload} units="KMH" />)
    expect(bomber).toContain('reactor-fpv-hud--bomber')
    expect(bomber).toContain('MINE ARMED')
    expect(bomber).not.toMatch(/271°|52 km\/h|123 m|RT THROTTLE/)
    const compact = renderToStaticMarkup(<FpvHud frame={{ ...fpv, layout: 'compact' }} units="KMH" />)
    expect(compact).toContain('reactor-fpv-hud--compact')
    expect(compact).toMatch(/485 m|2:05/)
  })

  it('derives metric and imperial FPV labels from the validated speedometer units', () => {
    const metric = renderToStaticMarkup(<FpvHud frame={fpv} units="KMH" />)
    expect(metric).toMatch(/52 km\/h|123 m|485 m/)
    const imperial = renderToStaticMarkup(<PassiveHud frame={{
      speedometer: { ...standard, units: 'MPH' }, fpv,
    }} />)
    expect(imperial).toMatch(/52 mph|123 ft|485 ft/)
    expect(imperial).not.toContain('km/h')
  })

  it('uses one hud.frame subscription, expires stale content, clears on disconnect, and cleans up', () => {
    vi.useFakeTimers()
    const listeners = new Map<string, (value: unknown) => void>()
    const bridge = { on: (name: string, fn: (value: unknown) => void) => { listeners.set(name, fn); return () => listeners.delete(name) } } as Pick<GtaBridge, 'on'>
    const update = vi.fn()
    const off = subscribePassiveHud(bridge, update)
    expect([...listeners.keys()].filter((name) => name === 'hud.frame')).toHaveLength(1)
    listeners.get('hud.frame')!({ ...standard, fpv }); expect(update).toHaveBeenLastCalledWith({ speedometer: standard, fpv })
    vi.advanceTimersByTime(1_000); expect(update).toHaveBeenLastCalledWith(null)
    listeners.get('hud.frame')!({ ...standard, fpv }); listeners.get('host.provider')!({ connected: false })
    expect(update).toHaveBeenLastCalledWith(null)
    off(); expect(listeners.size).toBe(0)
  })
})
