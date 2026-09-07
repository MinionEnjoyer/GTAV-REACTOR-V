import { describe, expect, it } from 'vitest'
import type { MenuPaginationItem } from '../gta/types'
import { gbayWheelPageAction } from './gbayWheelInput'

const pages: MenuPaginationItem = { id: 'pages', type: 'pagination', label: 'Page',
  page: 2, pageCount: 3, action: 'weapon.page' }

describe('GBAY wheel ownership', () => {
  it('does not consume a list with no pager', () => {
    expect(gbayWheelPageAction([], 120)).toBeNull()
  })
  it('keeps both directions for a real paginated catalog', () => {
    expect(gbayWheelPageAction([pages], 120)).toBe('next-page')
    expect(gbayWheelPageAction([pages], -120)).toBe('previous-page')
  })
  it.each([0, Number.NaN, Number.POSITIVE_INFINITY])('rejects invalid vertical deltas: %s', delta => {
    expect(gbayWheelPageAction([pages], delta)).toBeNull()
  })
  it.each([{ enabled: false }, { visible: false }, { pageCount: 1 }])('ignores inactive pagers: %j', override => {
    expect(gbayWheelPageAction([{ ...pages, ...override }], 120)).toBeNull()
  })
  it('does not consume scrolling when already at the requested boundary', () => {
    expect(gbayWheelPageAction([{ ...pages, page: 1 }], -120)).toBeNull()
    expect(gbayWheelPageAction([{ ...pages, page: 3 }], 120)).toBeNull()
  })
})
