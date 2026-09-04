import { describe, expect, it } from 'vitest'
import {
  commitAcceptedPresentation,
  resolveAtomicPresentationLayers,
  selectReplacementRestoreSnapshot,
} from './atomicPresentationHandoff'
import type { MenuControllerSnapshot } from './controller'
import type { MenuPresentation } from './presentation'

function presentation(presentationId: string, menuId = presentationId): MenuPresentation {
  return {
    extensionId: 'allin1.gbay',
    menuId,
    presentationId,
    context: {},
    inputMode: 'interactive-menu',
  }
}

describe('atomic menu presentation handoff', () => {
  it('keeps the committed tree visible while its replacement prepares', () => {
    const current = presentation('current', 'home')
    const replacement = presentation('replacement', 'vehicles')

    expect(resolveAtomicPresentationLayers(replacement, current)).toEqual({
      visible: current,
      preparing: replacement,
    })
  })

  it('swaps the tree only after the exact current request is accepted', () => {
    const current = presentation('current', 'home')
    const replacement = presentation('replacement', 'vehicles')

    const afterStaleAck = commitAcceptedPresentation(replacement, current, 'current')
    expect(resolveAtomicPresentationLayers(replacement, afterStaleAck)).toEqual({
      visible: current,
      preparing: replacement,
    })

    const afterExactAck = commitAcceptedPresentation(replacement, current, 'replacement')
    expect(resolveAtomicPresentationLayers(replacement, afterExactAck)).toEqual({
      visible: replacement,
      preparing: null,
    })
  })

  it('never exposes an unaccepted initial or superseded request', () => {
    const first = presentation('first')
    const latest = presentation('latest')

    expect(resolveAtomicPresentationLayers(first, null)).toEqual({
      visible: null,
      preparing: first,
    })
    expect(resolveAtomicPresentationLayers(latest, null)).toEqual({
      visible: null,
      preparing: latest,
    })
    expect(commitAcceptedPresentation(latest, null, 'first')).toBeNull()
  })

  it('retains one committed frame across repeated pre-accept supersessions', () => {
    const current = presentation('current')
    const firstReplacement = presentation('replacement-1')
    const finalReplacement = presentation('replacement-2')

    expect(resolveAtomicPresentationLayers(firstReplacement, current).visible).toBe(current)
    expect(resolveAtomicPresentationLayers(finalReplacement, current)).toEqual({
      visible: current,
      preparing: finalReplacement,
    })
  })

  it('hands the committed route and focus to a same-menu replacement tree', () => {
    const current = presentation('current', 'home')
    const replacement = presentation('replacement', 'home')
    const snapshot: MenuControllerSnapshot = {
      menuId: 'about',
      route: { id: 'about', title: 'About', items: [] },
      stack: ['home', 'about'],
      focusedItemId: 'about-alpha',
    }

    expect(selectReplacementRestoreSnapshot(replacement, current, snapshot)).toBe(snapshot)
  })

  it('does not leak route state across unrelated provider menus', () => {
    const current = presentation('current', 'home')
    const snapshot: MenuControllerSnapshot = {
      menuId: 'about',
      route: { id: 'about', title: 'About', items: [] },
      stack: ['home', 'about'],
    }

    expect(selectReplacementRestoreSnapshot(
      { ...presentation('other', 'home'), extensionId: 'other.provider' },
      current,
      snapshot,
    )).toBeNull()
    expect(selectReplacementRestoreSnapshot(presentation('other', 'settings'), current, snapshot)).toBeNull()
  })
})
