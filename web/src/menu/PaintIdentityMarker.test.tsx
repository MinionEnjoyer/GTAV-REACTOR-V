import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { hostPaintIdentity, menuPaintIdentity } from '../paintIdentity'
import { PaintIdentityMarker } from './PaintIdentityMarker'

describe('paint identity marker', () => {
  it('renders exactly eight stable cells without exposing a raw presentation id', () => {
    const html = renderToStaticMarkup(
      <PaintIdentityMarker identity={menuPaintIdentity(1, 'allin1.gbay:home:42')} />,
    )

    expect(html).toContain('class="reactor-paint-identity-marker"')
    expect(html).toContain('data-reactor-paint-fingerprint="26895c8d78e86ef8"')
    expect(html).toContain('data-reactor-paint-kind="menu"')
    expect(html).not.toContain('allin1.gbay:home:42')
    expect((html.match(/<i /g) ?? []).length).toBe(8)
  })

  it('renders setup status even for its standalone generation-zero preset', () => {
    const html = renderToStaticMarkup(
      <PaintIdentityMarker identity={hostPaintIdentity('setup-status', 0)} />,
    )
    expect(html).toContain('data-reactor-paint-fingerprint="c569b3b27f388731"')
    expect(html).toContain('data-reactor-paint-kind="host"')
  })

  it('renders nothing when no visible surface owns paint', () => {
    expect(renderToStaticMarkup(<PaintIdentityMarker identity={null} />)).toBe('')
  })
})
