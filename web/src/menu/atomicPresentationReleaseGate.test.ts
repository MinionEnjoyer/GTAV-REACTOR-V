import { describe, expect, it, vi } from 'vitest'
import {
  commitAcceptedPresentation,
  resolveAtomicPresentationLayers,
} from './atomicPresentationHandoff'
import {
  acknowledgePaintedMenuPresentation,
  type MenuPresentation,
} from './presentation'

function presentation(presentationId: string, menuId: string): MenuPresentation {
  return {
    extensionId: 'allin1.gbay',
    menuId,
    presentationId,
    context: { menuRevision: presentationId },
    inputMode: 'interactive-menu',
  }
}

describe('visible menu replacement release gate', () => {
  it.each([
    ['cross-key', 'home', 'vehicles'],
    ['same-key', 'vehicles', 'vehicles'],
  ])('keeps the old %s owner visible through loading, assets, paint, and exact ready',
    async (_path, oldMenuId, replacementMenuId) => {
      const current = presentation(`current-${oldMenuId}`, oldMenuId)
      const replacement = presentation(`replacement-${replacementMenuId}`, replacementMenuId)
      let committed: MenuPresentation | null = current
      let resolveAssets!: () => void
      const assetsReady = new Promise<void>((resolve) => { resolveAssets = resolve })
      const acknowledge = vi.fn(async () => true)

      const ready = acknowledgePaintedMenuPresentation(
        assetsReady,
        (callback) => {
          callback(0)
          return 1
        },
        acknowledge,
        1000,
      )

      // A staged replacement may contain a loading shell and incomplete image
      // assets, but neither is the visible owner before exact ready.
      expect(resolveAtomicPresentationLayers(replacement, committed)).toEqual({
        visible: current,
        preparing: replacement,
      })
      await Promise.resolve()
      expect(acknowledge).not.toHaveBeenCalled()
      expect(resolveAtomicPresentationLayers(replacement, committed).visible).toBe(current)

      resolveAssets()
      await expect(ready).resolves.toBe(true)
      expect(acknowledge).toHaveBeenCalledTimes(1)

      // A stale token still cannot expose an intermediate tree. Only the exact
      // replacement acknowledgement performs the atomic ownership swap.
      committed = commitAcceptedPresentation(replacement, committed, current.presentationId)
      expect(resolveAtomicPresentationLayers(replacement, committed).visible).toBe(current)
      committed = commitAcceptedPresentation(replacement, committed, replacement.presentationId)
      expect(resolveAtomicPresentationLayers(replacement, committed)).toEqual({
        visible: replacement,
        preparing: null,
      })
    })
})
