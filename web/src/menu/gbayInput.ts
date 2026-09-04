import type {
  MenuChoiceItem,
  MenuCommandItem,
  MenuInvocation,
  MenuItem,
  MenuPaginationItem,
  MenuSearchItem,
  MenuToggleItem,
} from '../gta/types'
import { MenuController } from './controller'
import {
  isGbayCustomizationOption,
  gbayCustomizationCards,
  isGbayCustomizeWeapon,
  isGbayGarageVehicle,
  isGbayGearCard,
  isGbayVehicleCard,
  isGbayWeaponCard,
} from './gbay'

export type GbaySemanticInputAction =
  | 'previous-page'
  | 'next-page'
  | 'previous-category'
  | 'next-category'
  | 'filter-next'
  | 'search'
  | 'favorite'

export interface GbaySemanticInputEnvironment {
  focusSearch?(): void
  openSearch?(item: MenuSearchItem): void
}

export interface GbaySemanticInputResult {
  handled: boolean
  invocation?: MenuInvocation
}

type FavoriteItem = MenuCommandItem | MenuToggleItem

function isAvailable(item: MenuItem): boolean {
  return item.visible !== false && item.enabled !== false
}

function actionOf(item: MenuItem): string {
  return 'action' in item && typeof item.action === 'string' ? item.action.toLowerCase() : ''
}

function pageItem(items: readonly MenuItem[]): MenuPaginationItem | undefined {
  return items.find((item): item is MenuPaginationItem =>
    item.type === 'pagination' && isAvailable(item) &&
    (item.id === 'pages' || actionOf(item).endsWith('.page')))
}

function categoryItem(items: readonly MenuItem[]): MenuChoiceItem | undefined {
  return items.find((item): item is MenuChoiceItem =>
    item.type === 'choice' && isAvailable(item) &&
    (item.id === 'category' || actionOf(item).endsWith('.category')))
}

function ownershipItem(items: readonly MenuItem[]): MenuChoiceItem | undefined {
  return items.find((item): item is MenuChoiceItem =>
    item.type === 'choice' && isAvailable(item) &&
    (item.id === 'ownership' || actionOf(item).endsWith('.ownership')))
}

function searchItem(items: readonly MenuItem[]): MenuSearchItem | undefined {
  return items.find((item): item is MenuSearchItem =>
    item.type === 'search' && isAvailable(item) &&
    (item.id === 'search' || actionOf(item).endsWith('.search')))
}

function isFavoriteCapable(item: MenuItem): item is FavoriteItem {
  return item.type === 'command' || item.type === 'toggle'
}

function isFavoriteItem(item: FavoriteItem): boolean {
  if (!isAvailable(item)) return false
  return /favou?rite/i.test(`${item.id} ${item.label} ${actionOf(item)}`)
}

function favoriteForFocusedItem(items: readonly MenuItem[], focused: MenuItem | undefined): FavoriteItem | undefined {
  if (!focused) return undefined
  if (isFavoriteCapable(focused) && isFavoriteItem(focused)) return focused
  if (focused.type !== 'command') return undefined

  const suffix = focused.id.replace(/^(?:vehicle|weapon)-/, '')
  const expectedIds = new Set([
    `favorite-${suffix}`,
    `vehicle-favorite-${suffix}`,
    `weapon-favorite-${suffix}`,
    `favorite-${focused.id}`,
    `${focused.id}-favorite`,
  ])
  return items.find((item): item is FavoriteItem => isFavoriteCapable(item) && isFavoriteItem(item) &&
    expectedIds.has(item.id))
}

async function withTemporaryFocus(
  controller: MenuController,
  itemId: string,
  operation: () => Promise<MenuInvocation | undefined>,
): Promise<MenuInvocation | undefined> {
  const routeId = controller.currentRoute.id
  const previousId = controller.focusedItem?.id
  if (!controller.focus(itemId)) return undefined
  try {
    return await operation()
  } finally {
    if (previousId && controller.currentRoute.id === routeId) controller.focus(previousId)
  }
}

async function adjustPage(
  controller: MenuController,
  direction: -1 | 1,
): Promise<GbaySemanticInputResult> {
  const item = pageItem(controller.currentRoute.items)
  if (!item) return { handled: false }
  const candidate = item.page + direction
  if (candidate < 1 || candidate > Math.max(1, item.pageCount)) return { handled: true }
  const invocation = await withTemporaryFocus(controller, item.id, () => controller.adjust(direction))
  return { handled: true, ...(invocation ? { invocation } : {}) }
}

async function adjustChoice(
  controller: MenuController,
  item: MenuChoiceItem | undefined,
  direction: -1 | 1,
  wrap: boolean,
): Promise<GbaySemanticInputResult> {
  if (!item) return { handled: false }
  const options = item.options.filter((option) => !option.disabled)
  if (options.length === 0) return { handled: true }
  const index = Math.max(0, options.findIndex((option) => option.value === item.value))
  const candidate = index + direction
  const nextIndex = wrap
    ? (candidate + options.length) % options.length
    : Math.min(options.length - 1, Math.max(0, candidate))
  if (nextIndex === index) return { handled: true }
  const invocation = await withTemporaryFocus(
    controller,
    item.id,
    () => controller.setValue(options[nextIndex].value),
  )
  return { handled: true, ...(invocation ? { invocation } : {}) }
}

/**
 * Apply ALLIN1 0.5's GBAY shortcuts through the currently published typed
 * menu nodes. This layer never dispatches a raw action id or supplies host
 * parameters: the authoritative descriptor and MenuController remain the
 * only mutation path.
 */
export async function invokeGbaySemanticInput(
  controller: MenuController,
  action: string,
  environment: GbaySemanticInputEnvironment = {},
): Promise<GbaySemanticInputResult> {
  const items = controller.currentRoute.items
  switch (action) {
    case 'previous-page': return adjustPage(controller, -1)
    case 'next-page': return adjustPage(controller, 1)
    case 'previous-category': return adjustChoice(controller, categoryItem(items), -1, false)
    case 'next-category': return adjustChoice(controller, categoryItem(items), 1, false)
    case 'filter-next': return adjustChoice(controller, ownershipItem(items), 1, true)
    case 'search': {
      const item = searchItem(items)
      if (!item || !controller.focus(item.id)) return { handled: false }
      if (environment.openSearch) environment.openSearch(item)
      else environment.focusSearch?.()
      return { handled: true }
    }
    case 'favorite': {
      const favorite = favoriteForFocusedItem(items, controller.focusedItem)
      if (!favorite) return { handled: false }
      const invocation = await withTemporaryFocus(
        controller,
        favorite.id,
        () => favorite.type === 'toggle'
          ? controller.setValue(!favorite.value)
          : controller.activate(),
      )
      return { handled: true, ...(invocation ? { invocation } : {}) }
    }
    default: return { handled: false }
  }
}

function isCatalogCard(item: MenuItem): boolean {
  return isGbayVehicleCard(item) || isGbayWeaponCard(item) || isGbayGearCard(item) ||
    isGbayCustomizeWeapon(item) || isGbayCustomizationOption(item) || isGbayGarageVehicle(item)
}

function availableCatalogCards(items: readonly MenuItem[]): MenuItem[] {
  const customization = gbayCustomizationCards(items)
  if (customization.length) return customization.map(card => card.action).filter(isAvailable)
  return items.filter(item => isAvailable(item) && isCatalogCard(item))
}

/** Move among the visible three-column GBAY cards without landing on hidden
 * favorite commands or toolbar controls. Page shoulders remain authoritative
 * for crossing page boundaries. */
export function moveGbayCardFocus(
  controller: MenuController,
  horizontal: -1 | 0 | 1,
  vertical: -1 | 0 | 1,
): boolean {
  const cards = availableCatalogCards(controller.currentRoute.items)
  const focusedId = controller.focusedItem?.id
  const index = cards.findIndex((item) => item.id === focusedId)
  if (index < 0 || cards.length === 0) return false

  const columns = 3
  const column = index % columns
  let next = index
  if (horizontal < 0 && column > 0) next -= 1
  else if (horizontal > 0 && column < columns - 1 && index + 1 < cards.length) next += 1
  else if (vertical < 0 && index - columns >= 0) next -= columns
  else if (vertical > 0 && index + columns < cards.length) next += columns
  if (next !== index) {
    controller.focus(cards[next].id)
    return true
  }
  // Horizontal edges stay inside the catalog; paging has an explicit LB/RB
  // path. A vertical edge must fall through to MenuController's focus ring so
  // controller users can reach the toolbar and persistent section navigation
  // again after selecting a card.
  return horizontal !== 0
}

/** Return the 0.5-style page action when horizontal card navigation reaches
 * the visible grid edge. Keeping this separate from focus movement lets the
 * caller route the page change through the authoritative pagination node. */
export function gbayCardEdgePageAction(
  controller: MenuController,
  horizontal: -1 | 0 | 1,
): 'previous-page' | 'next-page' | undefined {
  if (horizontal === 0 || !pageItem(controller.currentRoute.items)) return undefined
  const cards = availableCatalogCards(controller.currentRoute.items)
  const index = cards.findIndex((item) => item.id === controller.focusedItem?.id)
  if (index < 0 || cards.length === 0) return undefined
  const column = index % 3
  if (horizontal < 0 && column === 0) return 'previous-page'
  if (horizontal > 0 && (column === 2 || index === cards.length - 1)) return 'next-page'
  return undefined
}
