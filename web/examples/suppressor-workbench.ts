import { MenuController, adaptMenusToRoutes, reactorV } from '../src/sdk'

const extensionIndex = await reactorV.extensions.list()
const extensionResults = await Promise.all(extensionIndex.items.map((extension) =>
  reactorV.extensions.get(extension.id)))
const extensionDetails = extensionResults.filter((extension): extension is NonNullable<typeof extension> => extension !== null)
const suppressors = extensionDetails.find((extension) => extension.capabilities.includes('weapons.components'))

if (!suppressors) throw new Error('No weapon-component extension is registered.')

const menuIndex = await reactorV.menu.list(suppressors.id)
const menuResults = await Promise.all(menuIndex.items.map((menu) => reactorV.menu.get(menu.extensionId, menu.id)))
const menus = menuResults.filter((menu): menu is NonNullable<typeof menu> => menu !== null)
const root = menus[0]
if (!root) throw new Error('The extension did not publish a menu.')

const controller = new MenuController(adaptMenusToRoutes(menus, root.id), {
  invoke: (request) => reactorV.menu.invoke(request),
})

const equipment = await reactorV.events.subscribe({
  events: [`${suppressors.id}.weapon.changed`, `${suppressors.id}.component.changed`],
  replayLatest: true,
})

// This example never asks Reactor for arbitrary weapon natives or memory
// writes. The extension owns profiling/effects and exposes only typed actions.
export { controller, equipment }
