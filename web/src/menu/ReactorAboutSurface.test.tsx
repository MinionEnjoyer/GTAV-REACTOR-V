import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { DetectedModsPanel, ReactorAboutSurface } from './ReactorAboutSurface'

describe('minimal REACTOR V About surface', () => {
  it('preserves identity, purpose, and detected target on the Overview tab', () => {
    const html = renderToStaticMarkup(<ReactorAboutSurface gameLabel="GTA V Enhanced 1.0.9999.0" />)

    expect(html).toContain('ragewebui-logo.png')
    expect(html).toContain('Real-time Embedded Application Component Toolkit &amp; Overlay Runtime')
    expect(html).toContain('A lightweight embedded interface runtime for GTA V Story Mode.')
    expect(html).toContain('Created by MinionEnjoyer for GTA V Enhanced 1.0.9999.0')
    expect(html).toContain('Overview')
    expect(html).toContain('Detected Mods')
    expect(html).toContain('<nav')
    expect(html).toContain('reactor-about-copy-panel')
    expect(html).not.toContain('reactor-paint-identity-marker')
  })

  it('defaults the credit target to GTA V', () => {
    expect(renderToStaticMarkup(<ReactorAboutSurface />))
      .toContain('Created by MinionEnjoyer for GTA V')
  })

  it('renders bounded registered-mod readiness without extension-authored markup', () => {
    const html = renderToStaticMarkup(
      <DetectedModsPanel
        state="ready"
        catalog={{
          total: 1,
          items: [{
            id: 'allin1.gbay', name: 'ALLIN1', version: '0.6.0', extensionApiVersion: 1,
            actionCount: 4, eventCount: 2, menuCount: 1,
          }],
        }}
        source="live"
        onRetry={() => {}}
      />,
    )

    expect(html).toContain('1 registered mod')
    expect(html).toContain('ALLIN1')
    expect(html).toContain('allin1.gbay')
    expect(html).toContain('Registered')
    expect(html).toContain('API v1 · 1 menus · 4 actions · 2 events')
  })

  it('explains that the preload catalog is still being prepared', () => {
    const html = renderToStaticMarkup(
      <DetectedModsPanel state="error" catalog={null} source={null} onRetry={() => {}} />,
    )
    expect(html).toContain('Mod catalog is still preparing')
    expect(html).toContain('installed package manifests')
    expect(html).toContain('Retry')
  })

  it('marks last-detected rows as non-authoritative while runtime is unavailable', () => {
    const html = renderToStaticMarkup(
      <DetectedModsPanel
        state="ready"
        source="cache"
        catalog={{
          total: 1,
          items: [{
            id: 'allin1.gbay', name: 'ALLIN1', version: '0.6.0', extensionApiVersion: 1,
            actionCount: 4, eventCount: 2, menuCount: 1,
          }],
        }}
        onRetry={() => {}}
      />,
    )
    expect(html).toContain('1 last detected mod')
    expect(html).toContain('Last detected / awaiting runtime')
    expect(html).not.toContain('>Ready<')
  })

  it('labels preload-manifest discoveries as installed rather than runtime registered', () => {
    const html = renderToStaticMarkup(
      <DetectedModsPanel
        state="ready"
        source="bootstrap"
        catalog={{
          total: 1,
          items: [{
            id: 'allin1.online-content', name: 'ALLIN1 Online Content', version: '0.6.1',
            extensionApiVersion: 1, actionCount: 0, eventCount: 0, menuCount: 0,
          }],
        }}
        onRetry={() => {}}
      />,
    )
    expect(html).toContain('1 detected mod')
    expect(html).toContain('Installed / awaiting runtime')
    expect(html).not.toContain('Registered / runtime connected')
  })

  it('marks every pre-provider control with a fixed bootstrap action', () => {
    const overview = renderToStaticMarkup(<ReactorAboutSurface />)
    expect(overview).toContain('data-reactor-bootstrap-action="overview"')
    expect(overview).toContain('data-reactor-bootstrap-action="detected-mods"')
    const unavailable = renderToStaticMarkup(
      <DetectedModsPanel state="error" catalog={null} source={null} onRetry={() => {}} />,
    )
    expect(unavailable).toContain('data-reactor-bootstrap-action="retry-detected-mods"')
  })
})
