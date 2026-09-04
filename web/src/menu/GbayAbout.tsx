import type { MenuCommandItem, MenuItem, MenuStatusItem } from '../gta/types'

interface GbayAboutProps {
  items: MenuItem[]
  focusedId?: string
  busy: boolean
  onFocus(item: MenuItem): void
  onActivate(item: MenuItem): void
}

function statusById(items: readonly MenuItem[], id: string): MenuStatusItem | undefined {
  const item = items.find((candidate) => candidate.id === id && candidate.type === 'status')
  return item as MenuStatusItem | undefined
}

function supportAction(items: readonly MenuItem[]): MenuCommandItem | undefined {
  // The presentation never invents a URL or bypasses the typed host action.
  // An older/malformed provider therefore renders the address as read-only
  // instead of gaining an unguarded window.open path.
  return items.find((item): item is MenuCommandItem =>
    item.type === 'command' && item.action === 'about.support')
}

function AboutFact({ label, value, detail }: {
  label: string
  value: string
  detail?: string
}) {
  return (
    <article className="gbay-about-fact">
      <small>{label}</small>
      <strong>{value}</strong>
      {detail && <span>{detail}</span>}
    </article>
  )
}

/**
 * Branded ALLIN1 information page inside the Story Mode marketplace.
 * This is intentionally distinct from Reactor's main-menu About surface.
 */
export function GbayAbout({
  items,
  focusedId,
  busy,
  onFocus,
  onActivate,
}: GbayAboutProps) {
  const version = statusById(items, 'version')
  const edition = statusById(items, 'edition')
  const runtime = statusById(items, 'runtime')
  const purpose = statusById(items, 'purpose')
  const creator = statusById(items, 'creator')
  const support = statusById(items, 'support')
  const action = supportAction(items)

  return (
    <section className="gbay-about" aria-label="About ALLIN1">
      <header className="gbay-about-hero">
        <img src="allin1-logo.png" alt="ALLIN1" />
        <span>ABOUT ALLIN1</span>
        <p>{purpose?.value ?? 'GTA V Story Mode expansion and mod platform'}</p>
      </header>

      <div className="gbay-about-diagnostics" aria-label="ALLIN1 build and runtime information">
        <AboutFact
          label={version?.label ?? 'Version'}
          value={version?.value ?? 'Unavailable'}
          detail="Installed ALLIN1 client"
        />
        <AboutFact
          label={edition?.label ?? 'GTA edition'}
          value={edition?.value ?? 'Unavailable'}
          detail="Detected game target"
        />
        <AboutFact
          label={runtime?.label ?? 'Script runtime'}
          value={runtime?.value ?? 'Unavailable'}
          detail="Managed gameplay host"
        />
      </div>

      <div className="gbay-about-credit">
        <small>{creator?.label ?? 'Created and maintained by'}</small>
        <strong>{creator?.value ?? 'MinionEnjoyer'}</strong>
      </div>

      <div className="gbay-about-support">
        <span>
          <small>SUPPORT THE PROJECT</small>
          <strong>{support?.value ?? 'Support link unavailable'}</strong>
          <em>Continue supporting ALLIN1 development.</em>
        </span>
        {action && (
          <button
            type="button"
            className={focusedId === action.id ? 'focused' : ''}
            data-menu-focused={focusedId === action.id ? 'true' : 'false'}
            disabled={busy || action.enabled === false}
            onMouseEnter={() => onFocus(action)}
            onClick={() => onActivate(action)}
          >Open support page <span aria-hidden="true">↗</span></button>
        )}
      </div>
    </section>
  )
}
