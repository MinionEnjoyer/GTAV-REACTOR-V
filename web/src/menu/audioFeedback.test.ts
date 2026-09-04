import { describe, expect, it, vi } from 'vitest'
import { MenuAudioFeedback } from './audioFeedback'

describe('menu audio feedback', () => {
  it('emits the four bounded frontend cue families', () => {
    let time = 1_000
    const emit = vi.fn()
    const feedback = new MenuAudioFeedback(emit, () => time)

    expect(feedback.play('navigate')).toBe(true)
    time += 100
    expect(feedback.play('select')).toBe(true)
    time += 100
    expect(feedback.play('back')).toBe(true)
    time += 100
    expect(feedback.play('error')).toBe(true)
    expect(emit.mock.calls.map(([cue]) => cue)).toEqual(['navigate', 'select', 'back', 'error'])
  })

  it('rate-limits held navigation without muting normal menu cadence', () => {
    let time = 2_000
    const emit = vi.fn()
    const feedback = new MenuAudioFeedback(emit, () => time)

    expect(feedback.play('navigate', 'semantic')).toBe(true)
    time += 20
    expect(feedback.play('navigate', 'semantic')).toBe(false)
    time += 45
    expect(feedback.play('navigate', 'semantic')).toBe(true)
    expect(emit).toHaveBeenCalledTimes(2)
  })

  it('treats a rapid pointer hover and click as one gesture', () => {
    let time = 3_000
    const emit = vi.fn()
    const feedback = new MenuAudioFeedback(emit, () => time)

    expect(feedback.play('navigate', 'pointer')).toBe(true)
    time += 50
    expect(feedback.play('select', 'pointer')).toBe(false)
    time += 41
    expect(feedback.play('select', 'pointer')).toBe(true)
    expect(emit.mock.calls.map(([cue]) => cue)).toEqual(['navigate', 'select'])
  })

  it('does not let a failed audio host reject a menu operation', async () => {
    const feedback = new MenuAudioFeedback(() => Promise.reject(new Error('older host')), () => 4_000)
    expect(feedback.play('select')).toBe(true)
    await Promise.resolve()
  })
})
