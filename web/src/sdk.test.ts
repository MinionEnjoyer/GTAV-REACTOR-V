import { afterEach, describe, expect, it, vi } from 'vitest'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.resetModules()
})

describe('browser globals', () => {
  it('preserves rageWebUI and adds the additive reactorV alias', async () => {
    const browserWindow: Record<string, unknown> = {
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    }
    vi.stubGlobal('window', browserWindow)

    const sdk = await import('./sdk')

    expect(browserWindow.rageWebUI).toMatchObject({ bridge: sdk.bridge, gta: sdk.gta })
    expect(browserWindow.reactorV).toMatchObject({
      bridge: sdk.bridge,
      runtime: sdk.reactorV.runtime,
      extensions: sdk.reactorV.extensions,
      menu: sdk.reactorV.menu,
    })
    expect(sdk.reactorV.MenuController).toBe(sdk.MenuController)
    sdk.bridge.destroy()
  })
})
