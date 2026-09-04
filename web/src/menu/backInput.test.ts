import { describe, expect, it, vi } from 'vitest'
import { handleMenuContextMenu, performMenuBack } from './backInput'

describe('menu Back input', () => {
  it('pops one nested route and closes exactly once on Home', () => {
    const close = vi.fn()
    const nestedBack = vi.fn().mockReturnValue(true)
    expect(performMenuBack({ back: nestedBack }, null, close)).toBe('route')
    expect(nestedBack).toHaveBeenCalledTimes(1)
    expect(close).not.toHaveBeenCalled()

    const homeBack = vi.fn().mockReturnValue(false)
    expect(performMenuBack({ back: homeBack }, null, close)).toBe('close')
    expect(homeBack).toHaveBeenCalledTimes(1)
    expect(close).toHaveBeenCalledTimes(1)
  })

  it('suppresses Chromium context menus without duplicating native Back', () => {
    const event = {
      preventDefault: vi.fn(),
      stopPropagation: vi.fn(),
      stopImmediatePropagation: vi.fn(),
    }
    const back = vi.fn()

    expect(handleMenuContextMenu(event, true, back)).toBe(false)
    expect(back).not.toHaveBeenCalled()
    expect(event.preventDefault).toHaveBeenCalledTimes(1)
    expect(event.stopPropagation).toHaveBeenCalledTimes(1)
    expect(event.stopImmediatePropagation).toHaveBeenCalledTimes(1)

    expect(handleMenuContextMenu(event, false, back)).toBe(true)
    expect(back).toHaveBeenCalledTimes(1)
  })

  it('lets one native secondary edge pop one route while its contextmenu duplicate is inert', () => {
    const event = {
      preventDefault: vi.fn(),
      stopPropagation: vi.fn(),
      stopImmediatePropagation: vi.fn(),
    }
    const close = vi.fn()
    const routeBack = vi.fn().mockReturnValue(true)
    const semanticBack = () => performMenuBack({ back: routeBack }, null, close)

    // Native Win32 input owns the physical edge. Chromium may still publish a
    // contextmenu event, but it is suppression-only on the native host.
    expect(handleMenuContextMenu(event, true, semanticBack)).toBe(false)
    expect(routeBack).not.toHaveBeenCalled()
    expect(semanticBack()).toBe('route')
    expect(routeBack).toHaveBeenCalledTimes(1)
    expect(close).not.toHaveBeenCalled()
  })
})
