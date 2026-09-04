import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { MenuItem } from '../gta/types'
import { GbayAbout } from './GbayAbout'

const statuses: MenuItem[] = [
  { id: 'version', label: 'Version', value: '0.6.1', tone: 'success', type: 'status' },
  { id: 'edition', label: 'GTA edition', value: 'Enhanced', tone: 'success', type: 'status' },
  { id: 'runtime', label: 'Script runtime', value: 'ScriptHookVDotNet v3.6.0', tone: 'success', type: 'status' },
  { id: 'purpose', label: 'ALLIN1', value: 'Bring GTA Online DLC content into Story Mode with one click.', type: 'status' },
  { id: 'creator', label: 'Created and maintained by', value: 'MinionEnjoyer', type: 'status' },
  { id: 'support', label: 'Support', value: 'buymeacoffee.com/minionenjoyer', type: 'status' },
]

describe('ALLIN1 GBAY About surface', () => {
  it('restores branded build, edition, runtime, credit, and support information', () => {
    const html = renderToStaticMarkup(
      <GbayAbout
        items={[
          ...statuses,
          { id: 'open-support', label: 'Open support page', type: 'command', action: 'about.support' },
        ]}
        focusedId="open-support"
        busy={false}
        onFocus={() => {}}
        onActivate={() => {}}
      />,
    )

    expect(html).toContain('aria-label="About ALLIN1"')
    expect(html).toContain('src="allin1-logo.png"')
    expect(html).toContain('ABOUT ALLIN1')
    expect(html).toContain('0.6.1')
    expect(html).toContain('Enhanced')
    expect(html).toContain('ScriptHookVDotNet v3.6.0')
    expect(html).toContain('MinionEnjoyer')
    expect(html).toContain('buymeacoffee.com/minionenjoyer')
    expect(html).toContain('data-menu-focused="true"')
    expect(html).toContain('Open support page')
    expect(html).not.toContain('<a ')
    expect(html).not.toContain('target="_blank"')
  })

  it('fails closed to read-only support text without the exact typed host action', () => {
    const html = renderToStaticMarkup(
      <GbayAbout
        items={[
          ...statuses,
          { id: 'wrong-action', label: 'Untrusted support action', type: 'command', action: 'browser.open' },
        ]}
        busy={false}
        onFocus={() => {}}
        onActivate={() => {}}
      />,
    )

    expect(html).toContain('buymeacoffee.com/minionenjoyer')
    expect(html).not.toContain('<button')
    expect(html).not.toContain('browser.open')
  })
})
