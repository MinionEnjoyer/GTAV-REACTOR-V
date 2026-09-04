import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { SearchKeyboard } from './SearchKeyboardSurface'
import { createSearchKeyboardSession } from './searchKeyboard'

describe('search keyboard surface', () => {
  it('renders a controller-readable, pointer-accessible modal and bounded draft', () => {
    const session = createSearchKeyboardSession('vehicles', {
      id: 'search', type: 'search', label: 'Search vehicles', value: 'bus',
      action: 'vehicle.search', maxLength: 24,
    })
    const html = renderToStaticMarkup(<SearchKeyboard
      session={session}
      onDraft={() => {}}
      onMove={() => {}}
      onFocusKey={() => {}}
      onActivate={() => {}}
      onApply={() => {}}
      onCancel={() => {}}
    />)

    expect(html).toContain('role="dialog"')
    expect(html).toContain('Controller text entry')
    expect(html).toContain('value="bus"')
    expect(html).toContain('maxLength="24"')
    expect(html).toContain('D-PAD MOVE · A SELECT · B CANCEL')
    expect(html).toContain('data-search-key-focused="true"')
    expect(html).toContain('aria-label="APPLY"')
    expect(html).toContain('aria-label="CANCEL"')
  })
})
