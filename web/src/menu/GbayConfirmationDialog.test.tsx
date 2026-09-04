import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { GbayConfirmationDialog } from './MenuSurface'

describe('ALLIN1 confirmation dialog', () => {
  it('uses the GBAY visual language and clear adjacent actions', () => {
    const html = renderToStaticMarkup(
      <GbayConfirmationDialog
        confirmation={{
          title: 'Purchase suppressor?',
          message: 'Confirm the $12,500 attachment purchase.',
        }}
        onRespond={() => {}}
      />,
    )

    expect(html).toContain('class="menu-confirmation gbay-confirmation"')
    expect(html).toContain('class="gbay-confirmation-card"')
    expect(html).toContain('GBAY SECURE ACTION')
    expect(html).toContain('ALLIN1 confirmation')
    expect(html).toContain('Purchase suppressor?')
    expect(html).toContain('>Cancel</button><button type="button" class="primary" autofocus="">Confirm</button>')
  })
})
