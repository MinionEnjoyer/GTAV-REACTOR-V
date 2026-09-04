import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import type { StartupStatus } from '../gta/types'
import { StartupTransitionSurface } from './StartupTransitionSurface'

const fixture: StartupStatus = {
  schemaVersion: 1,
  sequence: 8,
  sessionId: 'fixture',
  phase: 'waiting-for-provider',
  providerConnected: false,
  defaultMenuRequested: true,
  defaultMenuDeadlineUtc: '2026-08-29T16:44:01Z',
  components: [
    { id: 'reactor', label: 'REACTOR V', state: 'ready', detail: 'Interface ready.' },
    { id: 'scripthook', label: 'ScriptHookV', state: 'initializing', detail: 'Creating GTA script threads.' },
    { id: 'managed-bridge', label: 'Managed bridge', state: 'waiting', detail: 'Waiting for ScriptHookV.' },
    { id: 'allin1', label: 'ALLIN1', state: 'waiting', detail: 'Waiting for gameplay provider.' },
  ],
  console: {
    maxEntries: 48,
    dropped: 2,
    entries: [{
      sequence: 8,
      timestampUtc: '2026-08-29T16:42:01Z',
      source: 'bootstrap',
      stage: 'threads',
      message: 'ScriptHookV initialized; waiting for managed threads.',
    }],
  },
}

describe('ALLIN1 startup transition surface', () => {
  it('renders the ALLIN1 preloader, three services, and a readable bounded log', () => {
    const html = renderToStaticMarkup(
      <StartupTransitionSurface status={fixture} surfaceGeneration={7} onClose={vi.fn()} />,
    )

    expect(html).toContain('ALLIN1 Preloader')
    expect(html).toContain('src="./allin1-logo.png"')
    expect(html).not.toContain('>A1</span>')
    expect(html).toContain('Powered by Reactor V')
    expect(html).toContain('Startup service checklist')
    expect(html).toContain('ScriptHook bridge')
    expect(html).toContain('Startup log')
    expect(html).toContain('ScriptHookV initialized; waiting for managed threads.')
    expect(html).toContain('>Reactor V</span>')
    expect(html).not.toContain('bootstrap/threads')
    expect(html).toContain('2 earlier events omitted')
    expect((html.match(/class="startup-component /g) ?? [])).toHaveLength(3)
    // The pre-provider bootstrap WebView cannot activate or accept pointer
    // input. Escape is the authoritative close action; repeated pre-runtime
    // F9 requests show or refresh the initializer instead of closing stale
    // logical state left behind by the loading transition.
    expect(html).not.toContain('aria-label="Close ALLIN1 Preloader"')
    expect(html).toContain('<kbd>Esc</kbd> close')
    expect(html).not.toContain('startup-check')
    expect(html).not.toContain('reactor-about-surface')
    expect(html).not.toContain('reactor-paint-identity-marker')
  })

  it('offers the pointer close control only after the managed bridge can receive it', () => {
    const html = renderToStaticMarkup(
      <StartupTransitionSurface
        status={{ ...fixture, providerConnected: true }}
        surfaceGeneration={7}
        onClose={vi.fn()}
      />,
    )

    expect(html).toContain('aria-label="Close ALLIN1 Preloader"')
  })

  it('does not add menu-routing copy to the focused preloader surface', () => {
    const html = renderToStaticMarkup(
      <StartupTransitionSurface
        status={{
          ...fixture,
          defaultMenuRequested: false,
          defaultMenuDeadlineUtc: null,
        }}
        surfaceGeneration={7}
        onClose={vi.fn()}
      />,
    )
    expect(html).not.toContain('GBAY will open automatically')
    expect(html).not.toContain('Initialization status only')
    expect(html).toContain('ALLIN1 Preloader')
  })

  it('humanizes an empty log stage while retaining the raw tokens as its title', () => {
    const html = renderToStaticMarkup(
      <StartupTransitionSurface
        status={{
          ...fixture,
          console: {
            ...fixture.console,
            entries: [{
              sequence: 9,
              timestampUtc: '2026-08-29T16:42:02Z',
              source: 'managed-bridge',
              stage: 'provider_connected',
              message: '',
            }],
          },
        }}
        surfaceGeneration={7}
        onClose={vi.fn()}
      />,
    )

    expect(html).toContain('Managed bridge')
    expect(html).toContain('Provider connected complete.')
    expect(html).toContain('title="managed-bridge / provider_connected"')
  })

  it('summarizes structured telemetry but preserves a long plain-language event', () => {
    const longMessage = 'Waiting for the managed gameplay provider to finish registering the selected Story Mode services before ALLIN1 can open its menu without interrupting the current loading transition.'
    const html = renderToStaticMarkup(
      <StartupTransitionSurface
        status={{
          ...fixture,
          console: {
            ...fixture.console,
            entries: [
              {
                sequence: 10,
                timestampUtc: '2026-08-29T16:42:03Z',
                source: 'preloader',
                stage: 'webview_page_timing',
                message: 'metrics={"domContentLoaded":34,"loadEvent":35}',
              },
              {
                sequence: 11,
                timestampUtc: '2026-08-29T16:42:04Z',
                source: 'allin1',
                stage: 'provider_wait',
                message: longMessage,
              },
            ],
          },
        }}
        surfaceGeneration={7}
        onClose={vi.fn()}
      />,
    )

    expect(html).toContain('Webview page timing recorded.')
    expect(html).toContain('domContentLoaded')
    expect(html).toContain(longMessage)
    expect(html).toContain('class="startup-console-message"')
  })

})
