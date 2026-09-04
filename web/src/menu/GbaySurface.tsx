import type { Ref } from 'react'
import type {
  MenuChoiceItem,
  MenuChoiceOption,
  MenuCommandItem,
  MenuItem,
  MenuPaginationItem,
  MenuSearchItem,
  MenuStatusItem,
  MenuTabsItem,
  MenuToggleItem,
} from '../gta/types'
import type { MenuControllerSnapshot } from './controller'
import { GbayAbout } from './GbayAbout'
import { GbayPreviewImage } from './GbayPreviewImage'
import {
  classifyGbayRoute,
  gbayCardPreview,
  gbayCustomizationCards,
  isGbayCustomizeWeapon,
  isGbayGarageVehicle,
  isGbayGearCard,
  isLegacyGbayStateRefreshItem,
  isGbayNavigationItem,
  isGbayVehicleCard,
  isGbayWeaponCard,
  parseGbayCardDetail,
  type GbayAccountState,
} from './gbay'

interface GbaySurfaceProps {
  surfaceRef?: Ref<HTMLElement>
  snapshot: MenuControllerSnapshot | null
  account?: GbayAccountState | null
  loading: boolean
  busy: boolean
  error: string | null
  notice: string | null
  onClose(): void | Promise<void>
  onFocus(item: MenuItem): void
  onActivate(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
  onRetry(): void
}

function itemByType<T extends MenuItem['type']>(
  items: MenuItem[], id: string, type: T,
): Extract<MenuItem, { type: T }> | undefined {
  const item = items.find((candidate) => candidate.id === id && candidate.type === type)
  return item as Extract<MenuItem, { type: T }> | undefined
}

function itemDisabled(item: MenuItem, busy: boolean): boolean {
  return busy || item.enabled === false
}

function routeIsActive(snapshot: MenuControllerSnapshot | null, routeId: string): boolean {
  if (!snapshot) return false
  if (snapshot.route.id === routeId) return true
  // Nested catalog/delivery routes keep their owning section in the stack.
  // The root Home route is an ancestor of every section and must not remain
  // highlighted once a marketplace section is open.
  return snapshot.stack.length > 1 && snapshot.stack[0] !== routeId &&
    snapshot.stack.includes(routeId)
}

function controllerHints(section: ReturnType<typeof classifyGbayRoute>): string {
  const common = 'D-PAD NAVIGATE'
  if (section === 'vehicles' || section === 'weapons') {
    return `${common} · LB/RB PAGES · LT/RT CATEGORY · Y FILTER · X SEARCH · R3 FAVORITE · A SELECT · B BACK`
  }
  if (section === 'customization') {
    return `${common} · LB/RB PAGES · X SEARCH · A SELECT · B BACK`
  }
  if (section === 'gear') return `${common} · LB/RB PAGES · LT/RT CATEGORY · A SELECT · B BACK`
  if (section === 'garage') return `${common} · SCROLL VEHICLES · A SELECT · B BACK`
  return `${common} · A SELECT · B BACK`
}

function WrenchIcon() {
  return (
    <svg
      className="gbay-wrench-icon"
      viewBox="0 0 24 24"
      aria-hidden="true"
      focusable="false"
    >
      <path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94Z" />
    </svg>
  )
}

export function GbaySurface({
  surfaceRef,
  snapshot,
  account,
  loading,
  busy,
  error,
  notice,
  onClose,
  onFocus,
  onActivate,
  onSetValue,
  onRetry,
}: GbaySurfaceProps) {
  const items = snapshot?.route.items.filter((item) => item.visible !== false) ?? []
  const navigation = items.filter(isGbayNavigationItem)
  const content = items.filter((item) =>
    !isGbayNavigationItem(item) && !isLegacyGbayStateRefreshItem(item))
  const balance = itemByType(content, 'balance', 'status') as MenuStatusItem | undefined
  const section = snapshot ? classifyGbayRoute(snapshot.route) : 'vehicles'
  const sectionTitle = section === 'home' ? 'MARKETPLACE' : snapshot?.route.title ?? 'VEHICLES'
  const weaponCustomizer = section === 'customization'

  if (weaponCustomizer) {
    const editorHome = navigation.find((item) => item.id === 'gbay-nav-home')
    return (
      <main
        ref={surfaceRef}
        className="gbay-weapon-editor-stage"
        data-reactor-menu-surface-root="true"
        aria-busy={loading || busy}
      >
        <section className="gbay-weapon-editor-shell" aria-label="GBAY weapon workbench">
          <header className="gbay-weapon-editor-header">
            <span className="gbay-weapon-editor-mark" aria-hidden="true"><WrenchIcon /></span>
            <span className="gbay-weapon-editor-title">
              <small>GBAY WORKBENCH</small>
              <strong>Weapon customization</strong>
            </span>
            <span className="gbay-weapon-editor-account">
              <small>{account?.label ?? balance?.label ?? 'PLAYER ACCOUNT'}</small>
              <strong>{account?.value ?? balance?.value ?? 'GBAY'}</strong>
            </span>
            <span className="gbay-weapon-editor-actions">
              {editorHome && (
                <button
                  type="button"
                  className={snapshot?.focusedItemId === editorHome.id ? 'focused' : ''}
                  disabled={itemDisabled(editorHome, busy)}
                  onMouseEnter={() => onFocus(editorHome)}
                  onClick={() => onActivate(editorHome)}
                >‹ GBAY</button>
              )}
              <button type="button" aria-label="Close weapon workbench" onClick={() => void onClose()}>×</button>
            </span>
          </header>

          <GbayWeaponCustomize
            items={content}
            focusedId={snapshot?.focusedItemId}
            busy={busy}
            loading={loading}
            error={error}
            onFocus={onFocus}
            onActivate={onActivate}
            onSetValue={onSetValue}
            onRetry={onRetry}
          />

          <footer className="gbay-weapon-editor-footer">
            <span className={error ? 'error' : ''}>{error ?? notice ?? (busy ? 'Updating…' : 'Ready')}</span>
            <span>{controllerHints(section)}</span>
          </footer>
        </section>
      </main>
    )
  }

  return (
    <main
      ref={surfaceRef}
      className="gbay-stage"
      data-reactor-menu-surface-root="true"
      aria-busy={loading || busy}
    >
      <section
        className="gbay-shell"
        aria-label="GBAY Story Mode marketplace"
      >
        <header className="gbay-header">
          <div className="gbay-brand" aria-label="GBAY">
            <strong>GBAY</strong>
            <span>STORY MODE MARKETPLACE</span>
          </div>
          <span className="gbay-section">{sectionTitle}</span>
          <div className="gbay-balance">
            <small>{account?.label ?? balance?.label ?? 'PLAYER ACCOUNT'}</small>
            <strong>{account?.value ?? balance?.value ?? 'GBAY'}</strong>
          </div>
        </header>

        {navigation.length > 1 && (
          <nav className="gbay-section-nav" aria-label="GBAY sections">
            {navigation.map((item) => {
              const active = routeIsActive(snapshot, item.routeId)
              const customizationTool = item.id === 'gbay-nav-customization'
              return (
                <button
                  key={item.id}
                  type="button"
                  className={`${active ? 'active' : ''}${customizationTool ? `${active ? ' ' : ''}gbay-nav-tool` : ''}`}
                  aria-current={active ? 'page' : undefined}
                  aria-label={customizationTool ? 'Customize weapons' : undefined}
                  title={customizationTool ? 'Customize weapons' : undefined}
                  disabled={itemDisabled(item, busy)}
                  onMouseEnter={() => onFocus(item)}
                  onClick={() => { if (!active) onActivate(item) }}
                >{customizationTool
                    ? <WrenchIcon />
                    : <><span aria-hidden="true">{item.icon}</span>{item.label}</>}</button>
              )
            })}
          </nav>
        )}

        {loading && !snapshot
          ? <GbayMessage label="Loading GBAY…" />
          : error && !snapshot
            ? <GbayMessage label="GBAY unavailable" detail={error} error onRetry={onRetry} />
            : section === 'home'
              ? <GbayHome items={content} focusedId={snapshot?.focusedItemId} busy={busy} onFocus={onFocus} onActivate={onActivate} />
              : section === 'vehicles'
                ? <GbayVehicles items={content} focusedId={snapshot?.focusedItemId} busy={busy} loading={loading} error={error} onFocus={onFocus} onActivate={onActivate} onSetValue={onSetValue} onRetry={onRetry} />
                : section === 'weapons'
                  ? <GbayWeapons items={content} focusedId={snapshot?.focusedItemId} busy={busy} loading={loading} error={error} onFocus={onFocus} onActivate={onActivate} onSetValue={onSetValue} onRetry={onRetry} />
                  : section === 'gear'
                    ? <GbayGear items={content} focusedId={snapshot?.focusedItemId} busy={busy} loading={loading} error={error} onFocus={onFocus} onActivate={onActivate} onSetValue={onSetValue} onRetry={onRetry} />
                  : section === 'garage'
                      ? <GbayGarage items={content} focusedId={snapshot?.focusedItemId} busy={busy} onFocus={onFocus} onActivate={onActivate} onSetValue={onSetValue} />
                    : section === 'about'
                      ? <GbayAbout items={content} focusedId={snapshot?.focusedItemId} busy={busy} onFocus={onFocus} onActivate={onActivate} />
                : section === 'delivery'
                  ? <GbayDelivery items={content} focusedId={snapshot?.focusedItemId} busy={busy} onFocus={onFocus} onActivate={onActivate} onSetValue={onSetValue} />
                  : <GbayPanel items={content} focusedId={snapshot?.focusedItemId} busy={busy} onFocus={onFocus} onActivate={onActivate} onSetValue={onSetValue} />}

        <footer className="gbay-footer">
          <span className={`gbay-state${error ? ' error' : ''}`}>{error ?? notice ?? (busy ? 'Updating…' : 'Ready')}</span>
          <span className="gbay-hints">{controllerHints(section)}</span>
          <button type="button" className="gbay-close" onClick={() => void onClose()}>Close</button>
        </footer>
      </section>
    </main>
  )
}

function GbayMessage({
  label,
  detail,
  error = false,
  onRetry,
}: {
  label: string
  detail?: string
  error?: boolean
  onRetry?: () => void
}) {
  return (
    <div className={`gbay-message${error ? ' error' : ''}`}>
      <strong>{label}</strong>
      {detail && <span>{detail}</span>}
      {onRetry && <button type="button" onClick={onRetry}>Try again</button>}
    </div>
  )
}

function GbayHome({
  items,
  focusedId,
  busy,
  onFocus,
  onActivate,
}: {
  items: MenuItem[]
  focusedId?: string
  busy: boolean
  onFocus(item: MenuItem): void
  onActivate(item: MenuItem): void
}) {
  const tiles = items.filter((item) => item.type === 'route' || item.type === 'command')
  const statuses = items.filter((item) => item.type === 'status')
  const routeSection = (item: MenuItem) => item.type === 'route'
    ? classifyGbayRoute({ id: item.routeId, menuId: item.routeId, title: item.label, items: [] })
    : 'other'
  const weaponsTile = tiles.find((item) => routeSection(item) === 'weapons')
  const customizationTool = tiles.find((item) => routeSection(item) === 'customization')
  const primaryTiles = weaponsTile && customizationTool
    ? tiles.filter((item) => item.id !== customizationTool.id)
    : tiles
  return (
    <div className="gbay-home">
      <div className="gbay-home-intro">
        <small>ALLIN1 STORY MODE SERVICES</small>
        <h1>What are you shopping for?</h1>
        <p>Choose a section. Every purchase and gameplay change is still validated by ALLIN1.</p>
      </div>
      <div className="gbay-home-grid">
        {primaryTiles.map((item) => {
          const attachedTool = item.id === weaponsTile?.id ? customizationTool : undefined
          return (
            <div key={item.id} className="gbay-home-tile">
              <button
                type="button"
                className={`gbay-home-primary${attachedTool ? ' has-tool' : ''}${focusedId === item.id ? ' focused' : ''}`}
                data-menu-focused={focusedId === item.id ? 'true' : 'false'}
                disabled={itemDisabled(item, busy)}
                onMouseEnter={() => onFocus(item)}
                onClick={() => onActivate(item)}
              >
                <span className="gbay-home-icon" aria-hidden="true">{item.icon ?? '◆'}</span>
                <span><strong>{item.label}</strong><small>{item.description ?? 'Open section'}</small></span>
                <em aria-hidden="true">›</em>
              </button>
              {attachedTool && (
                <button
                  type="button"
                  className={`gbay-home-tool${focusedId === attachedTool.id ? ' focused' : ''}`}
                  data-menu-focused={focusedId === attachedTool.id ? 'true' : 'false'}
                  aria-label="Customize weapons"
                  title="Customize weapons"
                  disabled={itemDisabled(attachedTool, busy)}
                  onMouseEnter={() => onFocus(attachedTool)}
                  onClick={() => onActivate(attachedTool)}
                ><WrenchIcon /></button>
              )}
            </div>
          )
        })}
      </div>
      {statuses.length > 0 && <div className="gbay-home-status">{statuses.map((item) => <GbayPassiveItem key={item.id} item={item} />)}</div>}
    </div>
  )
}

function GbayVehicles({
  items,
  focusedId,
  busy,
  loading,
  error,
  onFocus,
  onActivate,
  onSetValue,
  onRetry,
}: {
  items: MenuItem[]
  focusedId?: string
  busy: boolean
  loading: boolean
  error: string | null
  onFocus(item: MenuItem): void
  onActivate(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
  onRetry(): void
}) {
  const search = itemByType(items, 'search', 'search') as MenuSearchItem | undefined
  const category = itemByType(items, 'category', 'choice') as MenuChoiceItem | undefined
  const ownership = itemByType(items, 'ownership', 'choice') as MenuChoiceItem | undefined
  const favorites = itemByType(items, 'favorites', 'toggle') as MenuToggleItem | undefined
  const pages = itemByType(items, 'pages', 'pagination') as MenuPaginationItem | undefined
  const result = itemByType(items, 'results', 'status') as MenuStatusItem | undefined
  const empty = itemByType(items, 'empty', 'status') as MenuStatusItem | undefined
  const cards = items.filter(isGbayVehicleCard)
  const cardIds = new Set(cards.map((card) => card.id))
  const favoriteActions = items.filter((item) =>
    (item.type === 'command' || item.type === 'toggle') && /favou?rite/i.test(`${item.id} ${item.label} ${item.type === 'command' ? item.action : item.action ?? ''}`))
  const favoriteFor = (card: MenuCommandItem) => favoriteActions.find((item) => {
    const suffix = card.id.replace(/^vehicle-/, '')
    return item.id.includes(suffix) || item.id.includes(card.id)
  })

  return (
    <div className="gbay-vehicle-page">
      {category && (
        <nav className={`gbay-tabs${focusedId === category.id ? ' focused' : ''}`} aria-label="Vehicle categories">
          {category.options.map((option) => (
            <button
              key={option.value}
              type="button"
              className={category.value === option.value ? 'active' : ''}
              disabled={itemDisabled(category, busy) || option.disabled}
              onMouseEnter={() => onFocus(category)}
              onClick={() => onSetValue(category, option.value)}
            >{option.label}</button>
          ))}
        </nav>
      )}

      <div className="gbay-toolbar">
        {search && (
          <label className={`gbay-search${focusedId === search.id ? ' focused' : ''}`}>
            <span aria-hidden="true">⌕</span>
            <input
              key={`${search.id}:${search.value}`}
              type="search"
              defaultValue={search.value}
              placeholder={search.placeholder ?? 'Search vehicles or manufacturers'}
              maxLength={search.maxLength}
              disabled={itemDisabled(search, busy)}
              onFocus={() => onFocus(search)}
              onBlur={(event) => { if (event.currentTarget.value !== search.value) onSetValue(search, event.currentTarget.value) }}
              onKeyDown={(event) => { if (event.key === 'Enter') event.currentTarget.blur() }}
            />
          </label>
        )}
        {ownership && <GbayChoice item={ownership} focused={focusedId === ownership.id} busy={busy} onFocus={onFocus} onSetValue={onSetValue} />}
        {favorites && (
          <button
            type="button"
            className={`gbay-favorites${favorites.value ? ' active' : ''}${focusedId === favorites.id ? ' focused' : ''}`}
            disabled={itemDisabled(favorites, busy)}
            onMouseEnter={() => onFocus(favorites)}
            onClick={() => onSetValue(favorites, !favorites.value)}
          ><span aria-hidden="true">★</span> Favorites</button>
        )}
        {result && <span className="gbay-result-count">{result.value}</span>}
      </div>

      <section className="gbay-catalog" aria-label="Vehicle listings">
        {loading && cards.length === 0 && <GbayMessage label="Loading vehicles…" />}
        {!loading && error && cards.length === 0 && <GbayMessage label="Catalog unavailable" detail={error} error onRetry={onRetry} />}
        {empty && <GbayMessage label={empty.label} detail={empty.value || empty.description} />}
        {cards.map((card) => (
          <GbayVehicleCard
            key={card.id}
            card={card}
            preview={gbayCardPreview(card, items)}
            favoriteAction={favoriteFor(card)}
            focused={focusedId === card.id}
            busy={busy}
            onFocus={onFocus}
            onActivate={onActivate}
            onSetValue={onSetValue}
          />
        ))}
        {cards.length === 0 && !loading && !empty && !error && cardIds.size === 0 && <GbayMessage label="No vehicle listings" detail="Try another category or search." />}
      </section>

      <div className="gbay-catalog-footer">
        <div className="gbay-pager">
          <button type="button" aria-label="Previous page" disabled={!pages || itemDisabled(pages, busy) || pages.page <= 1} onMouseEnter={() => pages && onFocus(pages)} onClick={() => pages && onSetValue(pages, pages.page - 1)}>‹</button>
          <span>{pages ? `Page ${pages.page} / ${pages.pageCount}` : 'Page 1 / 1'}</span>
          <button type="button" aria-label="Next page" disabled={!pages || itemDisabled(pages, busy) || pages.page >= pages.pageCount} onMouseEnter={() => pages && onFocus(pages)} onClick={() => pages && onSetValue(pages, pages.page + 1)}>›</button>
        </div>
        <span>Select a vehicle to review delivery options</span>
      </div>
    </div>
  )
}

interface GbayCatalogProps {
  items: MenuItem[]
  focusedId?: string
  busy: boolean
  loading: boolean
  error: string | null
  onFocus(item: MenuItem): void
  onActivate(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
  onRetry(): void
}

function favoriteActionFor(
  card: MenuCommandItem,
  items: readonly MenuItem[],
): MenuCommandItem | MenuToggleItem | undefined {
  const suffix = card.id.replace(/^(?:vehicle|weapon)-/, '')
  return items.find((item): item is MenuCommandItem | MenuToggleItem =>
    (item.type === 'command' || item.type === 'toggle') &&
    /favou?rite/i.test(`${item.id} ${item.label} ${item.type === 'command' ? item.action : item.action ?? ''}`) &&
    (item.id.includes(suffix) || item.id.includes(card.id)))
}

function GbayWeapons(props: GbayCatalogProps) {
  const { items, focusedId, busy, loading, error, onFocus, onActivate, onSetValue, onRetry } = props
  const search = itemByType(items, 'search', 'search') as MenuSearchItem | undefined
  const category = itemByType(items, 'category', 'choice') as MenuChoiceItem | undefined
  const ownership = itemByType(items, 'ownership', 'choice') as MenuChoiceItem | undefined
  const favorites = itemByType(items, 'favorites', 'toggle') as MenuToggleItem | undefined
  const pages = itemByType(items, 'pages', 'pagination') as MenuPaginationItem | undefined
  const result = itemByType(items, 'results', 'status') as MenuStatusItem | undefined
  const empty = itemByType(items, 'empty', 'status') as MenuStatusItem | undefined
  const cards = items.filter(isGbayWeaponCard)
  return (
    <div className="gbay-vehicle-page gbay-weapons-page">
      {category && <GbayCategoryTabs item={category} focused={focusedId === category.id} busy={busy} onFocus={onFocus} onSetValue={onSetValue} label="Weapon categories" />}
      <div className="gbay-toolbar">
        {search && (
          <label className={`gbay-search${focusedId === search.id ? ' focused' : ''}`}>
            <span aria-hidden="true">⌕</span>
            <input key={`${search.id}:${search.value}`} type="search" defaultValue={search.value} placeholder={search.placeholder ?? 'Search weapons'} maxLength={search.maxLength} disabled={itemDisabled(search, busy)} onFocus={() => onFocus(search)} onBlur={(event) => { if (event.currentTarget.value !== search.value) onSetValue(search, event.currentTarget.value) }} onKeyDown={(event) => { if (event.key === 'Enter') event.currentTarget.blur() }} />
          </label>
        )}
        {ownership && <GbayChoice item={ownership} focused={focusedId === ownership.id} busy={busy} onFocus={onFocus} onSetValue={onSetValue} />}
        {favorites && <button type="button" className={`gbay-favorites${favorites.value ? ' active' : ''}${focusedId === favorites.id ? ' focused' : ''}`} disabled={itemDisabled(favorites, busy)} onMouseEnter={() => onFocus(favorites)} onClick={() => onSetValue(favorites, !favorites.value)}><span aria-hidden="true">★</span> Favorites</button>}
        {result && <span className="gbay-result-count">{result.value}</span>}
      </div>
      <section className="gbay-catalog" aria-label="Weapon listings">
        {loading && cards.length === 0 && <GbayMessage label="Loading weapons…" />}
        {!loading && error && cards.length === 0 && <GbayMessage label="Weapon catalog unavailable" detail={error} error onRetry={onRetry} />}
        {empty && <GbayMessage label={empty.label} detail={empty.value || empty.description} />}
        {cards.map((card) => <GbayProductCard key={card.id} card={card} preview={gbayCardPreview(card, items)} favoriteAction={favoriteActionFor(card, items)} focused={focusedId === card.id} busy={busy} kind="weapon" onFocus={onFocus} onActivate={onActivate} onSetValue={onSetValue} />)}
        {cards.length === 0 && !loading && !empty && !error && <GbayMessage label="No weapon listings" detail="Try another category or search." />}
      </section>
      <GbayCatalogPaging pages={pages} busy={busy} label="Select an unowned weapon to purchase. Use the wrench to customize owned weapons." onFocus={onFocus} onSetValue={onSetValue} />
    </div>
  )
}

function GbayWeaponCustomize(props: GbayCatalogProps) {
  const { items, focusedId, busy, loading, error, onFocus, onActivate, onSetValue, onRetry } = props
  const weapons = items.filter(isGbayCustomizeWeapon)
  const options = gbayCustomizationCards(items)
  const search = itemByType(items, 'search', 'search') as MenuSearchItem | undefined
  const category = itemByType(items, 'category', 'choice') as MenuChoiceItem | undefined
  const selected = items.find((item): item is MenuStatusItem => item.type === 'status' &&
    /(?:selected|current)[-_ ]weapon|^weapon$/i.test(`${item.id} ${item.label}`))
  const previewStatus = items.find((item): item is MenuStatusItem => item.type === 'status' &&
    /world[-_ ]preview|in[-_ ]world preview/i.test(`${item.id} ${item.label}`))
  const changeWeapon = items.find((item): item is MenuCommandItem => item.type === 'command' &&
    (item.action.toLowerCase().endsWith('.back') ||
      /(?:change|back).*(?:weapon|list)|(?:weapon|list).*(?:change|back)/i.test(`${item.id} ${item.label} ${item.action}`)))
  const group = items.find((item): item is MenuChoiceItem | MenuTabsItem =>
    (item.type === 'choice' || item.type === 'tabs') &&
    /group|section|workbench|option-kind/i.test(`${item.id} ${item.label}`))
  const groupRoutes = items.filter((item) => item.type === 'route' &&
    /ammo|component|finish|livery|color/i.test(`${item.id} ${item.label}`))
  const pages = itemByType(items, 'pages', 'pagination') as MenuPaginationItem | undefined ??
    items.find((item): item is MenuPaginationItem => item.type === 'pagination')
  const selectingWeapon = !selected && options.length === 0

  if (selectingWeapon) return (
    <div className="gbay-customize-page weapon-selection">
      <header className="gbay-workbench-header">
        <span><small>WEAPON WORKBENCH</small><h1>Choose an owned weapon</h1><p>Only weapons in the current character’s loadout are available.</p></span>
      </header>
      {category && <GbayCategoryTabs item={category} focused={focusedId === category.id} busy={busy} onFocus={onFocus} onSetValue={onSetValue} label="Owned weapon categories" />}
      {search && <div className="gbay-toolbar"><label className={`gbay-search${focusedId === search.id ? ' focused' : ''}`}><span aria-hidden="true">⌕</span><input key={`${search.id}:${search.value}`} type="search" defaultValue={search.value} placeholder={search.placeholder ?? 'Search owned weapons'} maxLength={search.maxLength} disabled={itemDisabled(search, busy)} onFocus={() => onFocus(search)} onBlur={(event) => { if (event.currentTarget.value !== search.value) onSetValue(search, event.currentTarget.value) }} onKeyDown={(event) => { if (event.key === 'Enter') event.currentTarget.blur() }} /></label></div>}
      <section className="gbay-catalog gbay-customize-weapons" aria-label="Owned weapons">
        {weapons.map((weapon) => {
          const preview = gbayCardPreview(weapon, items)
          const fallback = <span className="gbay-customize-weapon-mark" aria-hidden="true">⌖</span>
          return (
            <button
              key={weapon.id}
              type="button"
              className={`gbay-customize-weapon${focusedId === weapon.id ? ' focused' : ''}`}
              data-menu-focused={focusedId === weapon.id ? 'true' : 'false'}
              aria-label={`Customize ${weapon.label}`}
              title={`Customize ${weapon.label}`}
              disabled={itemDisabled(weapon, busy)}
              onMouseEnter={() => onFocus(weapon)}
              onClick={() => onActivate(weapon)}
            >
              <span className="gbay-customize-weapon-visual">
                {preview
                  ? <GbayPreviewImage source={preview} alt={`${weapon.label} preview`} fallback={fallback} />
                  : fallback}
              </span>
              <span className="gbay-customize-weapon-copy"><small>{customizeWeaponSummary(weapon.description)}</small><strong>{weapon.label}</strong></span>
              <span className="gbay-customize-weapon-tool" aria-hidden="true"><WrenchIcon /></span>
            </button>
          )
        })}
        {weapons.length === 0 && !loading && !error && <GbayMessage label="No owned weapons found" detail="The current character’s loadout is checked automatically whenever this page opens." />}
        {loading && weapons.length === 0 && <GbayMessage label="Loading owned weapons…" />}
        {!loading && error && weapons.length === 0 && <GbayMessage label="Weapon list unavailable" detail={error} error onRetry={onRetry} />}
      </section>
      <GbayCatalogPaging pages={pages} busy={busy} label="Select a weapon to open its guarded workbench" onFocus={onFocus} onSetValue={onSetValue} />
    </div>
  )

  const selectedName = selected?.value || selected?.label || 'Selected weapon'
  return (
    <div className="gbay-customize-page workbench">
      <header className="gbay-workbench-header">
        <span><small>WEAPON WORKBENCH</small><h1>{selectedName}</h1><p>Ammo, components, finishes, and livery colors are validated against live ownership.</p></span>
        {previewStatus && <span className="gbay-workbench-preview-status"><i aria-hidden="true" /> <small>{previewStatus.label}</small><strong>{previewStatus.value}</strong></span>}
        {changeWeapon && <button type="button" className={focusedId === changeWeapon.id ? 'focused' : ''} data-menu-focused={focusedId === changeWeapon.id ? 'true' : 'false'} disabled={itemDisabled(changeWeapon, busy)} onMouseEnter={() => onFocus(changeWeapon)} onClick={() => onActivate(changeWeapon)}>‹ Change weapon</button>}
      </header>

      {group && <GbayWorkbenchTabs item={group} busy={busy} focused={focusedId === group.id} onFocus={onFocus} onSetValue={onSetValue} />}
      {groupRoutes.length > 0 && <nav className="gbay-workbench-tabs" aria-label="Customization groups">{groupRoutes.map((item) => <button key={item.id} type="button" className={focusedId === item.id ? 'focused' : ''} disabled={itemDisabled(item, busy)} onMouseEnter={() => onFocus(item)} onClick={() => onActivate(item)}>{workbenchGroupLabel(item.label ?? 'Options')}</button>)}</nav>}

      <section className="gbay-catalog gbay-workbench-options gbay-workbench-scrollbox" aria-label="Weapon customization options" tabIndex={0}>
        {loading && options.length === 0 && <GbayMessage label="Loading workbench…" />}
        {!loading && error && options.length === 0 && <GbayMessage label="Workbench unavailable" detail={error} error onRetry={onRetry} />}
        {options.map(({ option, action, unequip }) => <GbayCustomizationOptionCard key={option.id} option={option} action={action} unequip={unequip} focused={focusedId === option.id || focusedId === action.id} busy={busy} onFocus={onFocus} onActivate={onActivate} />)}
        {options.length === 0 && !loading && !error && <GbayMessage label="No options in this group" detail="Choose another workbench group or weapon." />}
      </section>
      <div className="gbay-workbench-scroll-footer">
        <span>SCROLL TO VIEW ALL ATTACHMENTS</span>
        <small>Purchases and equipment changes require confirmation</small>
      </div>
    </div>
  )
}

function customizeWeaponSummary(description?: string): string {
  const values = (description ?? '').split('·').map((value) => value.trim())
  const category = values.find((value) => /^category\s*:/i.test(value))?.replace(/^category\s*:\s*/i, '')
  const ammo = values.find((value) => /^ammo\s*:/i.test(value))?.replace(/^ammo\s*:\s*/i, '')
  return [category, ammo && `${ammo} ammo`].filter(Boolean).join(' · ') || 'OWNED WEAPON'
}

function workbenchGroupLabel(value: string): string {
  const normalized = value.toLowerCase()
  if (normalized.includes('ammo')) return 'Ammo'
  if (normalized.includes('component') || normalized.includes('attachment')) return 'Components'
  if (normalized.includes('livery') || normalized.includes('color') || normalized.includes('colour')) return 'Livery Colors'
  if (normalized.includes('finish') || normalized.includes('tint')) return 'Weapon Finishes'
  return value
}

function GbayWorkbenchTabs({
  item,
  busy,
  focused,
  onFocus,
  onSetValue,
}: {
  item: MenuChoiceItem | MenuTabsItem
  busy: boolean
  focused: boolean
  onFocus(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
}) {
  const options = item.type === 'tabs' ? item.tabs : item.options
  return (
    <nav className={`gbay-workbench-tabs${focused ? ' focused' : ''}`} aria-label="Customization groups">
      {options.map((option) => <button key={option.value} type="button" className={item.value === option.value ? 'active' : ''} disabled={itemDisabled(item, busy) || option.disabled} onMouseEnter={() => onFocus(item)} onClick={() => onSetValue(item, option.value)}>{workbenchGroupLabel(option.label)}</button>)}
    </nav>
  )
}

function GbayCustomizationOptionCard({
  option,
  action,
  unequip,
  focused,
  busy,
  onFocus,
  onActivate,
}: {
  option: MenuCommandItem
  action: MenuCommandItem
  unequip: boolean
  focused: boolean
  busy: boolean
  onFocus(item: MenuItem): void
  onActivate(item: MenuItem): void
}) {
  const detail = parseGbayCardDetail(option.description)
  const state = detail.ownership || 'Available'
  const group = workbenchGroupLabel(detail.category || option.id)
  const equipped = /equipped|active|full/i.test(state)
  const owned = equipped || /owned|installed/i.test(state)
  return (
    <button type="button" className={`gbay-workbench-card${focused ? ' focused' : ''}${equipped ? ' equipped' : owned ? ' owned' : ''}${unequip ? ' removable' : ''}`} data-menu-focused={focused ? 'true' : 'false'} aria-label={unequip ? `Unequip ${option.label}` : undefined} title={unequip ? 'Unequip this attachment. It remains owned and can be equipped again for free.' : undefined} disabled={itemDisabled(action, busy)} onMouseEnter={() => onFocus(action)} onFocus={() => onFocus(action)} onClick={() => onActivate(action)}>
      <span className="gbay-workbench-card-mark" aria-hidden="true">{group === 'Ammo' ? '◉' : group === 'Components' ? '⌖' : group === 'Livery Colors' ? '◈' : '✦'}</span>
      <small>{group}</small><strong>{option.label}</strong>
      <span className="gbay-workbench-card-state">{state}</span>
      <em className={unequip ? 'gbay-attachment-unequip' : undefined}>{unequip ? 'UNEQUIP' : detail.price || (owned ? 'OWNED' : 'APPLY')}</em>
    </button>
  )
}

function GbayGear(props: GbayCatalogProps) {
  const { items, focusedId, busy, loading, error, onFocus, onActivate, onSetValue, onRetry } = props
  const category = itemByType(items, 'category', 'choice') as MenuChoiceItem | undefined
  const pages = itemByType(items, 'pages', 'pagination') as MenuPaginationItem | undefined
  const empty = itemByType(items, 'empty', 'status') as MenuStatusItem | undefined
  const cards = items.filter(isGbayGearCard)
  return (
    <div className="gbay-vehicle-page gbay-gear-page">
      {category && <GbayCategoryTabs item={category} focused={focusedId === category.id} busy={busy} onFocus={onFocus} onSetValue={onSetValue} label="Gear categories" />}
      <div className="gbay-toolbar gbay-gear-toolbar"><strong>Armor &amp; equipment</strong><span>Buy, equip, or unequip items for the current character.</span></div>
      <section className="gbay-catalog" aria-label="Gear listings">
        {loading && cards.length === 0 && <GbayMessage label="Loading gear…" />}
        {!loading && error && cards.length === 0 && <GbayMessage label="Gear catalog unavailable" detail={error} error onRetry={onRetry} />}
        {empty && <GbayMessage label={empty.label} detail={empty.value || empty.description} />}
        {cards.map((card) => <GbayProductCard key={card.id} card={card} preview={gbayCardPreview(card, items)} focused={focusedId === card.id} busy={busy} kind="gear" onFocus={onFocus} onActivate={onActivate} onSetValue={onSetValue} />)}
        {cards.length === 0 && !loading && !empty && !error && <GbayMessage label="No gear in this category" />}
      </section>
      <GbayCatalogPaging pages={pages} busy={busy} label="ALLIN1 validates ownership and current equipment state" onFocus={onFocus} onSetValue={onSetValue} />
    </div>
  )
}

function GbayCategoryTabs({
  item,
  focused,
  busy,
  onFocus,
  onSetValue,
  label,
}: {
  item: MenuChoiceItem
  focused: boolean
  busy: boolean
  onFocus(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
  label: string
}) {
  return (
    <nav className={`gbay-tabs${focused ? ' focused' : ''}`} aria-label={label}>
      {item.options.map((option) => <button key={option.value} type="button" className={item.value === option.value ? 'active' : ''} disabled={itemDisabled(item, busy) || option.disabled} onMouseEnter={() => onFocus(item)} onClick={() => onSetValue(item, option.value)}>{option.label}</button>)}
    </nav>
  )
}

function GbayCatalogPaging({
  pages,
  busy,
  label,
  onFocus,
  onSetValue,
}: {
  pages?: MenuPaginationItem
  busy: boolean
  label: string
  onFocus(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
}) {
  return (
    <div className="gbay-catalog-footer">
      <div className="gbay-pager">
        <button type="button" aria-label="Previous page" disabled={!pages || itemDisabled(pages, busy) || pages.page <= 1} onMouseEnter={() => pages && onFocus(pages)} onClick={() => pages && onSetValue(pages, pages.page - 1)}>‹</button>
        <span>{pages ? `Page ${pages.page} / ${pages.pageCount}` : 'Page 1 / 1'}</span>
        <button type="button" aria-label="Next page" disabled={!pages || itemDisabled(pages, busy) || pages.page >= pages.pageCount} onMouseEnter={() => pages && onFocus(pages)} onClick={() => pages && onSetValue(pages, pages.page + 1)}>›</button>
      </div>
      <span>{label}</span>
    </div>
  )
}

function GbayProductCard({
  card,
  preview,
  favoriteAction,
  focused,
  busy,
  kind,
  onFocus,
  onActivate,
  onSetValue,
}: {
  card: MenuCommandItem
  preview?: string
  favoriteAction?: MenuCommandItem | MenuToggleItem
  focused: boolean
  busy: boolean
  kind: 'weapon' | 'gear'
  onFocus(item: MenuItem): void
  onActivate(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
}) {
  const detail = parseGbayCardDetail(card.description)
  const sourceStatus = detail.ownership || (kind === 'gear' ? 'Purchase' : 'Available')
  const gearState = kind === 'gear'
    ? /(?:remove|unequip|equipped)/i.test(sourceStatus)
      ? 'equipped'
      : /(?:equip|owned)/i.test(sourceStatus)
        ? 'owned'
        : 'available'
    : ''
  const weaponOwned = kind === 'weapon' && /^owned$/i.test(sourceStatus)
  const status = kind === 'gear'
    ? gearState === 'equipped' ? 'Equipped' : gearState === 'owned' ? 'Owned' : 'Available'
    : sourceStatus
  const quote = kind === 'gear' && gearState === 'equipped'
    ? 'EQUIPPED'
    : kind === 'gear' && gearState === 'owned'
      ? 'OWNED'
      : detail.price || 'View'
  const favorite = detail.favorite || (favoriteAction?.label.toLowerCase().startsWith('remove favorite') ?? false)
  return (
    <article className={`gbay-card gbay-product-card ${kind}${gearState ? ` ${gearState}` : ''}${weaponOwned ? ' owned' : ''}${focused ? ' focused' : ''}`} data-menu-focused={focused ? 'true' : 'false'}>
      <button type="button" className="gbay-card-main" disabled={itemDisabled(card, busy)} onMouseEnter={() => onFocus(card)} onClick={() => onActivate(card)}>
        <span className="gbay-card-visual">{preview ? <GbayPreviewImage source={preview} alt={`${card.label} preview`} fallback={<span className="gbay-product-placeholder" aria-hidden="true">{kind === 'weapon' ? '⌖' : '▣'}</span>} /> : <span className="gbay-product-placeholder" aria-hidden="true">{kind === 'weapon' ? '⌖' : '▣'}</span>}<small>{detail.category || kind}</small>{kind === 'gear' && gearState === 'equipped' && <em className="gbay-gear-action">UNEQUIP</em>}</span>
        <span className="gbay-card-copy"><small>{status}</small><strong>{card.label}</strong><span className={`gbay-product-state ${status.toLowerCase()}`}>{status}</span><span className="gbay-price">{quote}</span></span>
      </button>
      {(weaponOwned || favoriteAction || favorite) && (
        <div className="gbay-card-corner-actions">
          {weaponOwned && <em className="gbay-weapon-owned-badge">OWNED</em>}
          {favoriteAction ? <button type="button" className={`gbay-card-favorite${favorite ? ' active' : ''}`} aria-label={`${favorite ? 'Remove' : 'Add'} ${card.label} ${favorite ? 'from' : 'to'} favorites`} disabled={itemDisabled(favoriteAction, busy)} onMouseEnter={() => onFocus(favoriteAction)} onClick={() => favoriteAction.type === 'toggle' ? onSetValue(favoriteAction, !favoriteAction.value) : onActivate(favoriteAction)}>★</button> : favorite ? <span className="gbay-card-favorite active" aria-label="Favorite">★</span> : null}
        </div>
      )}
    </article>
  )
}

function GbayGarage({
  items,
  focusedId,
  busy,
  onFocus,
  onActivate,
  onSetValue,
}: {
  items: MenuItem[]
  focusedId?: string
  busy: boolean
  onFocus(item: MenuItem): void
  onActivate(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
}) {
  const locationFilter = itemByType(items, 'location-filter', 'choice') as MenuChoiceItem | undefined
  const locations = items.filter((item): item is MenuStatusItem => item.type === 'status' && item.id.startsWith('location-'))
  const waypointActions = items.filter((item): item is MenuCommandItem => item.type === 'command' && item.action.toLowerCase() === 'garage.waypoint')
  const vehicles = items.filter(isGbayGarageVehicle)
  const protectedVehicles = items.filter((item): item is MenuStatusItem => item.type === 'status' && item.id.startsWith('stored-'))
  const empty = itemByType(items, 'empty', 'status') as MenuStatusItem | undefined
  const results = itemByType(items, 'results', 'status') as MenuStatusItem | undefined
  const interiorMode = itemByType(items, 'interior-mode', 'status') as MenuStatusItem | undefined
  const interiorChoices = items.filter((item): item is MenuChoiceItem =>
    item.type === 'choice' && item.action?.toLowerCase() === 'garage.customize')
  const emergencyRecovery = items.find((item): item is MenuCommandItem =>
    item.type === 'command' &&
    (item.id === 'emergency-recovery' || item.action.toLowerCase() === 'garage.recover'))
  const locationStatus = new Map(locations.map((location) => [location.id.replace(/^location-/, ''), location]))
  const locationWaypoints = new Map(waypointActions.map((action) => [action.id.replace(/^location-waypoint-/, '').toLowerCase(), action]))
  const locationOptions: MenuChoiceOption[] = locationFilter?.options ?? locations.map((location) => ({
    value: location.id.replace(/^location-/, ''),
    label: location.label,
  }))
  const activeLocation = locationOptions.find((option) => option.value === locationFilter?.value)
  const rows = garageVehicleRows(vehicles)
  return (
    <div className="gbay-garage-page">
      <section className="gbay-garage-storage" aria-label="Garage storage status">
        <header><span><small>MY GARAGE</small><strong>Storage</strong></span></header>
        <nav aria-label="Garage locations">
          {locationOptions.map((option) => {
            const status = locationStatus.get(option.value)
            const waypoint = locationWaypoints.get(option.value.toLowerCase())
            const active = locationFilter?.value === option.value
            const focused = focusedId === locationFilter?.id && active
            return <div key={option.value} className={`gbay-garage-location-row${active ? ' active' : ''}`}>
              <button
                type="button"
                className={`gbay-garage-location-select${active ? ' active' : ''}${focused ? `${active ? ' ' : ''}focused` : ''}`}
                data-location-value={option.value}
                data-menu-focused={focused ? 'true' : 'false'}
                aria-current={active ? 'true' : undefined}
                disabled={!locationFilter || itemDisabled(locationFilter, busy) || option.disabled}
                onMouseEnter={() => locationFilter && onFocus(locationFilter)}
                onClick={() => locationFilter && onSetValue(locationFilter, option.value)}
              ><span>{option.label}</span><em>{status?.value ?? (option.value === 'all' ? `${locations.length} locations` : 'Status pending')}</em></button>
              {waypoint && <button type="button" className={`gbay-garage-waypoint${focusedId === waypoint.id ? ' focused' : ''}`} data-menu-focused={focusedId === waypoint.id ? 'true' : 'false'} aria-label={`Navigate to ${option.label}`} title={`Navigate to ${option.label}`} disabled={itemDisabled(waypoint, busy)} onMouseEnter={() => onFocus(waypoint)} onClick={() => onActivate(waypoint)}>⌖</button>}
            </div>
          })}
        </nav>
      </section>
      <section className="gbay-garage-collection" aria-label="Stored vehicle collection">
        <header><span><small>{activeLocation?.label ?? 'All locations'}</small><strong>{results?.value ?? `${rows.length + protectedVehicles.length} stored vehicles`}</strong></span></header>
        {(interiorMode || interiorChoices.length > 0 || emergencyRecovery) && (
          <section className="gbay-garage-services" aria-label="Garage interior and recovery">
            {interiorMode && (
              <article className={`gbay-garage-interior tone-${interiorMode.tone ?? 'neutral'}`}>
                <span aria-hidden="true">▦</span>
                <span><small>GARAGE INTERIOR</small><strong>{interiorMode.label}</strong><em>{interiorMode.value || interiorMode.description || 'Managed by ALLIN1'}</em></span>
              </article>
            )}
            {interiorChoices.length > 0 && (
              <div className="gbay-garage-customization" aria-label="Interior customization">
                {interiorChoices.map((choice) => {
                  const focused = focusedId === choice.id
                  return (
                    <label key={choice.id} className={focused ? 'focused' : ''} data-menu-focused={focused ? 'true' : 'false'} onMouseEnter={() => onFocus(choice)}>
                      <span><small>APPEARANCE</small><strong>{choice.label}</strong></span>
                      <select aria-label={choice.label} value={choice.value} disabled={itemDisabled(choice, busy)} onFocus={() => onFocus(choice)} onChange={(event) => onSetValue(choice, event.currentTarget.value)}>
                        {choice.options.map((option) => <option key={option.value} value={option.value} disabled={option.disabled}>{option.label}</option>)}
                      </select>
                      {choice.description && <em>{choice.description}</em>}
                    </label>
                  )
                })}
              </div>
            )}
            {emergencyRecovery && (
              <button
                type="button"
                className={`gbay-garage-recovery${focusedId === emergencyRecovery.id ? ' focused' : ''}`}
                data-menu-focused={focusedId === emergencyRecovery.id ? 'true' : 'false'}
                disabled={itemDisabled(emergencyRecovery, busy)}
                onMouseEnter={() => onFocus(emergencyRecovery)}
                onClick={() => onActivate(emergencyRecovery)}
              >
                <span aria-hidden="true">!</span>
                <span><small>SAFETY</small><strong>{emergencyRecovery.label}</strong><em>{emergencyRecovery.description ?? 'Move outside after an interrupted garage transition.'}</em></span>
                <b>RECOVER</b>
              </button>
            )}
          </section>
        )}
        <div className="gbay-garage-grid gbay-garage-scrollbox" role="list" aria-label="Stored vehicles">
          {empty && <GbayMessage label={empty.label} detail={empty.value || empty.description} />}
          {rows.map((row) => {
            const focused = row.actions.some((action) => action.id === focusedId)
            return <article key={row.id} role="listitem" className={focused ? 'focused' : ''} data-garage-vehicle={row.id} data-menu-focused={focused ? 'true' : 'false'}>
              <span className="gbay-garage-vehicle-mark" aria-hidden="true">◆</span>
              <span><small>{garageVehicleLocation(row.description)}</small><strong>{row.label}</strong><em>{garageVehicleDetail(row.description)}</em></span>
              <span className="gbay-garage-actions">{row.actions.map((action) => {
                const retrieve = action.action.toLowerCase().includes('retrieve')
                return <button key={action.id} type="button" className={retrieve ? 'retrieve' : 'sell'} disabled={itemDisabled(action, busy)} onMouseEnter={() => onFocus(action)} onClick={() => onActivate(action)}>{retrieve ? 'RETRIEVE' : garageSellLabel(action.description)}</button>
              })}</span>
            </article>
          })}
          {protectedVehicles.map((vehicle) => <article key={vehicle.id} role="listitem" className="protected"><span className="gbay-garage-vehicle-mark" aria-hidden="true">◆</span><span><small>STORY VEHICLE</small><strong>{vehicle.label}</strong><em>{vehicle.value || vehicle.description}</em></span><span className="gbay-garage-protected">PROTECTED</span></article>)}
          {rows.length === 0 && protectedVehicles.length === 0 && !empty && <GbayMessage label="No stored vehicles" />}
        </div>
      </section>
    </div>
  )
}

interface GbayGarageVehicleRow {
  id: string
  label: string
  description: string
  actions: MenuCommandItem[]
}

function garageVehicleRows(actions: readonly MenuCommandItem[]): GbayGarageVehicleRow[] {
  const rows = new Map<string, GbayGarageVehicleRow>()
  for (const action of actions) {
    const id = action.id.replace(/-(?:retrieve|sell)$/i, '')
    const existing = rows.get(id)
    const label = action.label.replace(/^(?:retrieve|sell|remove)\s+/i, '')
    if (existing) {
      existing.actions.push(action)
      if (action.description?.toLowerCase().includes('sale value')) existing.description = action.description
    } else {
      rows.set(id, { id, label, description: action.description ?? '', actions: [action] })
    }
  }
  return [...rows.values()]
}

function garageVehicleLocation(description: string): string {
  return description.split('·').map((value) => value.trim()).find((value) => /^location\s*:/i.test(value)) ?? 'Stored vehicle'
}

function garageVehicleDetail(description: string): string {
  const details = description.split('·').map((value) => value.trim())
    .filter((value) => !/^location\s*:/i.test(value) && !/^sale value\s*:/i.test(value))
  return details.join(' · ') || 'Managed by ALLIN1'
}

function garageSellLabel(description?: string): string {
  const value = (description ?? '').split('·').map((part) => part.trim())
    .find((part) => /^sale value\s*:/i.test(part))
    ?.replace(/^sale value\s*:\s*/i, '')
  return value && value !== 'Remove' ? `SELL ${value}` : value === 'Remove' ? 'REMOVE' : 'SELL'
}

function GbayVehicleCard({
  card,
  preview,
  favoriteAction,
  focused,
  busy,
  onFocus,
  onActivate,
  onSetValue,
}: {
  card: MenuCommandItem
  preview: string
  favoriteAction?: MenuItem
  focused: boolean
  busy: boolean
  onFocus(item: MenuItem): void
  onActivate(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
}) {
  const detail = parseGbayCardDetail(card.description)
  const disabled = itemDisabled(card, busy)
  return (
    <article className={`gbay-card${focused ? ' focused' : ''}`} data-menu-focused={focused ? 'true' : 'false'}>
      <button type="button" className="gbay-card-main" disabled={disabled} onMouseEnter={() => onFocus(card)} onClick={() => onActivate(card)}>
        <span className="gbay-card-visual">
          {preview ? <GbayPreviewImage source={preview} alt={`${card.label} preview`} fallback={<span className="gbay-vehicle-placeholder" aria-hidden="true">▱</span>} /> : <span className="gbay-vehicle-placeholder" aria-hidden="true">▱</span>}
          <small>{detail.category || 'VEHICLE'}</small>
        </span>
        <span className="gbay-card-copy">
          <small>{detail.manufacturer || detail.ownership || 'Available'}</small>
          <strong>{card.label}</strong>
          {detail.model && <span className="gbay-model-name">{detail.model}</span>}
          <span className="gbay-price">{detail.price || 'View'}</span>
        </span>
      </button>
      {favoriteAction ? (
        <button
          type="button"
          className={`gbay-card-favorite${detail.favorite ? ' active' : ''}`}
          aria-label={`${detail.favorite ? 'Remove' : 'Add'} ${card.label} ${detail.favorite ? 'from' : 'to'} favorites`}
          disabled={itemDisabled(favoriteAction, busy)}
          onMouseEnter={() => onFocus(favoriteAction)}
          onClick={() => favoriteAction.type === 'toggle' ? onSetValue(favoriteAction, !favoriteAction.value) : onActivate(favoriteAction)}
        >★</button>
      ) : detail.favorite ? <span className="gbay-card-favorite active" aria-label="Favorite">★</span> : null}
    </article>
  )
}

function GbayDelivery({
  items,
  focusedId,
  busy,
  onFocus,
  onActivate,
  onSetValue,
}: {
  items: MenuItem[]
  focusedId?: string
  busy: boolean
  onFocus(item: MenuItem): void
  onActivate(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
}) {
  const choices = items.filter((item) => item.type === 'command' || item.type === 'route')
  const supporting = items.filter((item) => item.type !== 'command' && item.type !== 'route')
  return (
    <div className="gbay-delivery-page">
      <header><small>VEHICLE CHECKOUT</small><h1>Choose a delivery location</h1><p>Your selection is validated against the current vehicle, balance, and available garages.</p></header>
      <div className="gbay-delivery-grid">
        {choices.map((item) => (
          <button key={item.id} type="button" className={focusedId === item.id ? 'focused' : ''} data-menu-focused={focusedId === item.id ? 'true' : 'false'} disabled={itemDisabled(item, busy)} onMouseEnter={() => onFocus(item)} onClick={() => onActivate(item)}>
            <span aria-hidden="true">⌖</span><strong>{item.label}</strong><small>{item.description ?? 'Deliver here'}</small>
          </button>
        ))}
      </div>
      {supporting.length > 0 && <GbayPanel items={supporting} focusedId={focusedId} busy={busy} onFocus={onFocus} onActivate={onActivate} onSetValue={onSetValue} compact />}
    </div>
  )
}

function GbayPanel({
  items,
  focusedId,
  busy,
  onFocus,
  onActivate,
  onSetValue,
  compact = false,
}: {
  items: MenuItem[]
  focusedId?: string
  busy: boolean
  onFocus(item: MenuItem): void
  onActivate(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
  compact?: boolean
}) {
  return (
    <section className={`gbay-panel${compact ? ' compact' : ''}`}>
      {items.length === 0 && <GbayMessage label="Nothing to show yet" detail="This section has no actions exposed by ALLIN1." />}
      {items.map((item) => (
        <GbayPanelItem key={item.id} item={item} focused={focusedId === item.id} busy={busy} onFocus={onFocus} onActivate={onActivate} onSetValue={onSetValue} />
      ))}
    </section>
  )
}

function GbayPanelItem({
  item,
  focused,
  busy,
  onFocus,
  onActivate,
  onSetValue,
}: {
  item: MenuItem
  focused: boolean
  busy: boolean
  onFocus(item: MenuItem): void
  onActivate(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
}) {
  const commonClass = `gbay-panel-item${focused ? ' focused' : ''}`
  if (item.type === 'status' || item.type === 'progress' || item.type === 'media' || item.type === 'separator') {
    return <GbayPassiveItem item={item} />
  }
  if (item.type === 'choice') return <GbayChoice item={item} focused={focused} busy={busy} onFocus={onFocus} onSetValue={onSetValue} />
  if (item.type === 'toggle') return (
    <button type="button" className={commonClass} data-menu-focused={focused ? 'true' : 'false'} disabled={itemDisabled(item, busy)} onMouseEnter={() => onFocus(item)} onClick={() => onSetValue(item, !item.value)}>
      <span><strong>{item.label}</strong><small>{item.description}</small></span><em className={item.value ? 'on' : ''}>{item.value ? 'ON' : 'OFF'}</em>
    </button>
  )
  if (item.type === 'range') return (
    <label key={`${item.id}:${item.value}`} className={commonClass} data-menu-focused={focused ? 'true' : 'false'} onMouseEnter={() => onFocus(item)}>
      <span><strong>{item.label}</strong><small>{item.description}</small></span>
      <span className="gbay-range"><input type="range" min={item.min} max={item.max} step={item.step} defaultValue={item.value} disabled={itemDisabled(item, busy)} onFocus={() => onFocus(item)} onPointerUp={(event) => onSetValue(item, Number(event.currentTarget.value))} onKeyUp={(event) => { if (event.key === 'ArrowLeft' || event.key === 'ArrowRight' || event.key === 'Home' || event.key === 'End') onSetValue(item, Number(event.currentTarget.value)) }} /><output>{item.value}{item.unit}</output></span>
    </label>
  )
  if (item.type === 'text' || item.type === 'search' || item.type === 'keybind') return (
    <label className={commonClass} data-menu-focused={focused ? 'true' : 'false'} onMouseEnter={() => onFocus(item)}>
      <span><strong>{item.label}</strong><small>{item.description}</small></span>
      <input type={item.type === 'search' ? 'search' : 'text'} defaultValue={item.value} placeholder={'placeholder' in item ? item.placeholder : undefined} maxLength={'maxLength' in item ? item.maxLength : undefined} disabled={itemDisabled(item, busy)} onFocus={() => onFocus(item)} onBlur={(event) => { if (event.currentTarget.value !== item.value) onSetValue(item, event.currentTarget.value) }} />
    </label>
  )
  if (item.type === 'pagination') return (
    <div className={commonClass}><span><strong>{item.label}</strong><small>{item.description}</small></span><span className="gbay-inline-pager"><button type="button" disabled={itemDisabled(item, busy) || item.page <= 1} onClick={() => onSetValue(item, item.page - 1)}>‹</button><output>{item.page} / {item.pageCount}</output><button type="button" disabled={itemDisabled(item, busy) || item.page >= item.pageCount} onClick={() => onSetValue(item, item.page + 1)}>›</button></span></div>
  )
  if (item.type === 'list' || item.type === 'grid') return (
    <article className={`${commonClass} gbay-entry-control`}><span><strong>{item.label}</strong><small>{item.description}</small></span><div>{item.entries.map((entry) => <button key={entry.id} type="button" className={item.selectedId === entry.id ? 'selected' : ''} disabled={itemDisabled(item, busy) || entry.disabled} onClick={() => onSetValue(item, entry.id)}>{entry.image && <GbayPreviewImage source={entry.image} alt="" fallback={<span className="gbay-entry-image-placeholder" aria-hidden="true">▣</span>} />}<span>{entry.label}</span>{entry.badge && <small>{entry.badge}</small>}</button>)}</div></article>
  )
  if (item.type === 'tabs') return (
    <label className={commonClass}><span><strong>{item.label}</strong><small>{item.description}</small></span><select value={item.value} disabled={itemDisabled(item, busy)} onFocus={() => onFocus(item)} onChange={(event) => onSetValue(item, event.currentTarget.value)}>{item.tabs.map((tab) => <option key={tab.value} value={tab.value}>{tab.label}</option>)}</select></label>
  )
  return (
    <button type="button" className={commonClass} data-menu-focused={focused ? 'true' : 'false'} disabled={itemDisabled(item, busy)} onMouseEnter={() => onFocus(item)} onClick={() => onActivate(item)}>
      <span><strong>{item.label}</strong><small>{item.description}</small></span><em aria-hidden="true">›</em>
    </button>
  )
}

function GbayChoice({
  item,
  focused,
  busy,
  onFocus,
  onSetValue,
}: {
  item: MenuChoiceItem
  focused: boolean
  busy: boolean
  onFocus(item: MenuItem): void
  onSetValue(item: MenuItem, value: string | number | boolean): void
}) {
  return (
    <label className={`gbay-filter${focused ? ' focused' : ''}`}>
      <span>{item.label}</span>
      <select value={item.value} disabled={itemDisabled(item, busy)} onFocus={() => onFocus(item)} onChange={(event) => onSetValue(item, event.currentTarget.value)}>
        {item.options.map((option) => <option key={option.value} value={option.value} disabled={option.disabled}>{option.label}</option>)}
      </select>
    </label>
  )
}

function GbayPassiveItem({ item }: { item: MenuItem }) {
  if (item.type === 'separator') return <hr className="gbay-separator" />
  if (item.type === 'media') return <figure className="gbay-panel-media"><GbayPreviewImage source={item.source} alt={item.alt ?? item.label} fallback={<span className="gbay-product-placeholder" aria-hidden="true">▣</span>} /><figcaption>{item.label}</figcaption></figure>
  if (item.type === 'progress') return <article className="gbay-panel-item passive"><span><strong>{item.label}</strong><small>{item.description}</small></span><progress value={item.value} max={item.max} /></article>
  if (item.type === 'status') return <article className={`gbay-panel-item passive tone-${item.tone ?? 'neutral'}`}><span><strong>{item.label}</strong><small>{item.description}</small></span><output>{item.value}</output></article>
  return null
}
