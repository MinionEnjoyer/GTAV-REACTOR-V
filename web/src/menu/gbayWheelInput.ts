import type { MenuItem } from '../gta/types'

export type GbayWheelPageAction = 'next-page' | 'previous-page'

export function gbayWheelPageAction(items: readonly MenuItem[], delta: number): GbayWheelPageAction | null {
  if (!Number.isFinite(delta) || delta === 0) return null
  const page = items.find(item => item.type === 'pagination' && item.visible !== false &&
    item.enabled !== false && (item.id === 'pages' || item.action?.toLowerCase().endsWith('.page')))
  if (!page || page.type !== 'pagination' || page.pageCount <= 1) return null
  if (delta > 0 && page.page < page.pageCount) return 'next-page'
  if (delta < 0 && page.page > 1) return 'previous-page'
  return null
}

/** Only paginated marketplace catalogs own wheel-to-page input. Scroll lists
 * must retain browser default scrolling (or the forwarded-pointer fallback).
 * Scope to the active tree: retained/hidden presentations must not consume it.
 */
export function handleGbayCatalogWheel(
  event: WheelEvent,
  root: HTMLElement | null,
  items: readonly MenuItem[],
  navigate: (action: GbayWheelPageAction) => void,
): boolean {
  const target = event.target instanceof Element ? event.target : null
  if (!target || !root?.contains(target) || !target.closest('.gbay-catalog')) return false
  if (target.closest('.gbay-weapon-editor-shell, .gbay-workbench-scrollbox, .gbay-garage-scrollbox')) return false
  const action = gbayWheelPageAction(items, event.deltaY)
  if (!action) return false
  event.preventDefault()
  navigate(action)
  return true
}
