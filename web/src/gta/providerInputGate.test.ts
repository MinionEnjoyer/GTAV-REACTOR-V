import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  activateProviderInput,
  isProviderInputActive,
  onProviderInputReset,
  prepareProviderInput,
  revokeProviderInput,
} from './providerInputGate'

describe('provider input gate', () => {
  afterEach(() => revokeProviderInput())

  it('opens only for the exact prepared presentation', () => {
    prepareProviderInput('menu-1')

    expect(isProviderInputActive()).toBe(false)
    expect(activateProviderInput('stale-menu')).toBe(false)
    expect(isProviderInputActive()).toBe(false)
    expect(activateProviderInput('menu-1')).toBe(true)
    expect(isProviderInputActive('menu-1')).toBe(true)
    expect(isProviderInputActive('stale-menu')).toBe(false)
  })

  it('closes immediately on replacement and ignores stale release or activation', () => {
    prepareProviderInput('menu-1')
    expect(activateProviderInput('menu-1')).toBe(true)

    prepareProviderInput('menu-2')

    expect(isProviderInputActive()).toBe(false)
    expect(revokeProviderInput('menu-1')).toBe(false)
    expect(activateProviderInput('menu-1')).toBe(false)
    expect(activateProviderInput('menu-2')).toBe(true)
    expect(isProviderInputActive('menu-2')).toBe(true)
  })

  it('resets held browser input whenever the current lease is prepared or revoked', () => {
    const reset = vi.fn()
    const unsubscribe = onProviderInputReset(reset)

    prepareProviderInput('menu-1')
    activateProviderInput('menu-1')
    revokeProviderInput('menu-1')

    expect(reset).toHaveBeenCalledTimes(2)
    unsubscribe()
  })
})
