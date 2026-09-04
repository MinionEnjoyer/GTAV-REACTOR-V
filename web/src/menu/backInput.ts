export interface MenuBackController {
  back(): boolean
}

export interface MenuContextMenuEvent {
  preventDefault(): void
  stopPropagation(): void
  stopImmediatePropagation?(): void
}

export type MenuBackDisposition = 'editor' | 'route' | 'close'

/**
 * Apply one semantic Back operation. A nested route pops once; Home closes
 * once. Focused editors consume Back by relinquishing focus before menu
 * navigation, matching the keyboard/controller behavior.
 */
export function performMenuBack(
  controller: MenuBackController | null,
  activeElement: HTMLElement | null,
  onClose: () => void | Promise<void>,
): MenuBackDisposition {
  if (activeElement?.matches('input, textarea, select')) {
    activeElement.blur()
    return 'editor'
  }
  if (controller?.back()) return 'route'
  void onClose()
  return 'close'
}

/**
 * Chromium must never open or propagate its own context menu over GTA. The
 * native host already translates the physical secondary edge (including the
 * Windows swapped-button preference), so only browser/demo mode synthesizes
 * Back here; that prevents one physical click from becoming two route pops.
 */
export function handleMenuContextMenu(
  event: MenuContextMenuEvent,
  nativeHost: boolean,
  onBack: () => void,
): boolean {
  event.preventDefault()
  event.stopPropagation()
  event.stopImmediatePropagation?.()
  if (nativeHost) return false
  onBack()
  return true
}
