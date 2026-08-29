import { MenuController, adaptMenusToRoutes, reactorV } from '../src/sdk'

// ALLIN1 owns these IDs. Reactor only describes and routes them.
const menuIndex = await reactorV.menu.list('allin1.online')
const menuDetails = await Promise.all(menuIndex.items.map((menu) =>
  reactorV.menu.get(menu.extensionId, menu.id)))
const menus = menuDetails.filter((menu): menu is NonNullable<typeof menu> => menu !== null)
const gbay = adaptMenusToRoutes(menus, 'gbay')
const controller = new MenuController(gbay, {
  invoke: (request) => reactorV.menu.invoke(request),
})

reactorV.events.onInput((input) => {
  if (input.phase !== 'pressed' && input.phase !== 'repeated') return
  if (input.action === 'navigate-up') controller.moveFocus(-1)
  if (input.action === 'navigate-down') controller.moveFocus(1)
  if (input.action === 'navigate-left') void controller.adjust(-1)
  if (input.action === 'navigate-right') void controller.adjust(1)
  if (input.action === 'accept') void controller.activate()
  if (input.action === 'back') controller.back()
})

// Persistent purchase: explicit confirmation plus replay-safe key. ALLIN1
// still commits the purchase only when its story-save transaction commits.
export async function purchaseListing(listingId: string) {
  return reactorV.extensions.invoke({
    extensionId: 'allin1.online',
    actionId: 'gbay.purchase',
    parameters: { listingId },
    confirmed: true,
    idempotencyKey: crypto.randomUUID(),
  })
}
