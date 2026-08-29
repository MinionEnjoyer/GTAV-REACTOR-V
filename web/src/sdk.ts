import { bridge, gta, GtaBridgeError } from './gta/bridge'
import { ReactorVApi } from './gta/reactor'
import { adaptMenusToRoutes } from './menu/adapter'
import { MenuController } from './menu/controller'

const api = new ReactorVApi(bridge)

export const reactorV = {
  bridge,
  gta,
  runtime: api.runtime,
  overlay: api.overlay,
  extensions: api.extensions,
  menu: api.menu,
  events: api.events,
  GtaBridgeError,
  MenuController,
  adaptMenusToRoutes,
}

declare global {
  interface Window {
    rageWebUI: {
      bridge: typeof bridge
      gta: typeof gta
      GtaBridgeError: typeof GtaBridgeError
    }
    reactorV: typeof reactorV
  }
}

window.rageWebUI = { bridge, gta, GtaBridgeError }
window.reactorV = reactorV

export { bridge, gta, GtaBridgeError, ReactorVApi, MenuController, adaptMenusToRoutes }
export type * from './gta/types'
export type { InvokeOptions } from './gta/bridge'
