import { describe, expect, it } from 'vitest'
import {
  parseBootstrapAboutAction,
  parseWindowedKeyboardInput,
  parseWindowedPointerInput,
  nextEnabledIndex,
  shouldActivateForwardedPointerTarget,
  windowedKeyboardText,
} from './windowedInputPolicy'

describe('windowed input policy', () => {
  it('bounds pointer coordinates and rejects malformed input', () => {
    expect(parseWindowedPointerInput({ x: -2, y: 4, pressed: true, released: false, wheelDelta: 120 }))
      .toEqual({ x: 0, y: 1, pressed: true, released: false, wheelDelta: 120 })
    expect(parseWindowedPointerInput({ x: Number.NaN, y: 0, pressed: false, released: false, wheelDelta: 0 }))
      .toBeNull()
    expect(parseWindowedPointerInput({ x: 0, y: 0, pressed: false, released: false, wheelDelta: 9999 }))
      .toBeNull()
  })

  it('allows only fixed bundled About actions before the provider connects', () => {
    expect(parseBootstrapAboutAction('overview')).toBe('overview')
    expect(parseBootstrapAboutAction('detected-mods')).toBe('detected-mods')
    expect(parseBootstrapAboutAction('retry-detected-mods')).toBe('retry-detected-mods')
    expect(parseBootstrapAboutAction('refresh-detected-mods')).toBe('refresh-detected-mods')
    expect(parseBootstrapAboutAction('player.heal')).toBeNull()
    expect(parseBootstrapAboutAction('../detected-mods')).toBeNull()
    expect(parseBootstrapAboutAction({ action: 'overview' })).toBeNull()
  })

  it('maps bounded SHVDN key identities to text', () => {
    const lower = parseWindowedKeyboardInput({ code: 'A', shift: false, control: false, alt: false })!
    const upper = parseWindowedKeyboardInput({ code: 'A', shift: true, control: false, alt: false })!
    const symbol = parseWindowedKeyboardInput({ code: 'D1', shift: true, control: false, alt: false })!
    expect(windowedKeyboardText(lower)).toBe('a')
    expect(windowedKeyboardText(upper)).toBe('A')
    expect(windowedKeyboardText(symbol)).toBe('!')
    expect(windowedKeyboardText({ ...lower, control: true })).toBeNull()
  })

  it('cycles native select choices without landing on disabled options', () => {
    expect(nextEnabledIndex([false, true, false], 0, 1)).toBe(2)
    expect(nextEnabledIndex([false, true, false], 0, -1)).toBe(2)
    expect(nextEnabledIndex([true, true], 0, 1)).toBe(0)
    expect(nextEnabledIndex([], -1, 1)).toBe(-1)
  })

  it('activates only the interactive control that owned both pointer edges', () => {
    const tab = {}
    const neighboringTab = {}
    expect(shouldActivateForwardedPointerTarget(tab, tab)).toBe(true)
    expect(shouldActivateForwardedPointerTarget(tab, neighboringTab)).toBe(false)
    expect(shouldActivateForwardedPointerTarget(tab, null)).toBe(false)
    expect(shouldActivateForwardedPointerTarget(null, tab)).toBe(false)
  })
})
