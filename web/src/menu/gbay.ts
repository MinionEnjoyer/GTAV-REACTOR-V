import type {
  JsonObject,
  MenuCommandItem,
  MenuItem,
  MenuMediaItem,
  MenuRoute,
  MenuRouteItem,
  RoutedMenuDescriptor,
} from '../gta/types'
import type { MenuPresentation } from './presentation'

export const ALLIN1_GBAY_EXTENSION_ID = 'allin1.gbay'
export const GBAY_NAVIGATION_PREFIX = 'gbay-nav-'

export type GbaySectionId =
  | 'home'
  | 'vehicles'
  | 'weapons'
  | 'customization'
  | 'gear'
  | 'garage'
  | 'addons'
  | 'diagnostics'
  | 'about'
  | 'delivery'
  | 'other'

export interface GbaySectionDefinition {
  id: Exclude<GbaySectionId, 'delivery' | 'other'>
  label: string
  icon: string
  routeId?: string
}

export interface GbayCardDetail {
  price: string
  ownership: string
  favorite: boolean
  category: string
  manufacturer: string
  model: string
  preview: string
}

/**
 * Account text belongs to the persistent GBAY shell rather than any one
 * route. ALLIN1 publishes it as a detached status node on Home (and on some
 * catalog pages); keep that existing host-owned value available while the
 * browser navigates to routes that intentionally do not repeat it.
 */
export interface GbayAccountState {
  label: string
  value: string
}

const sectionDefinitions: readonly Omit<GbaySectionDefinition, 'routeId'>[] = [
  { id: 'home', label: 'Home', icon: '⌂' },
  { id: 'vehicles', label: 'Vehicles', icon: '◆' },
  { id: 'weapons', label: 'Weapons', icon: '⌖' },
  { id: 'customization', label: 'Customize', icon: '✦' },
  { id: 'gear', label: 'Gear', icon: '▣' },
  { id: 'garage', label: 'My Garage', icon: '▤' },
  { id: 'addons', label: 'Add-ons', icon: '+' },
  { id: 'diagnostics', label: 'Diagnostics', icon: '✓' },
  { id: 'about', label: 'About', icon: 'i' },
]

const topLevelSections = new Set<GbaySectionId>(sectionDefinitions.map((definition) => definition.id))

function normalizedRouteText(route: MenuRoute): string {
  return `${route.id} ${route.menuId ?? ''} ${route.title}`.toLowerCase().replace(/[_-]+/g, ' ')
}

export function classifyGbayRoute(route: MenuRoute): GbaySectionId {
  const value = normalizedRouteText(route)
  if (/\b(deliver|delivery|location|checkout)\b/.test(value)) return 'delivery'
  if (/\b(customi[sz](?:e|ation)?|weapon mod|component|attachment)\b/.test(value)) return 'customization'
  if (/\b(weapons?|ammunition|ammo)\b/.test(value)) return 'weapons'
  if (/\b(gear|armor|armour|equipment)\b/.test(value)) return 'gear'
  if (/\b(garage|owned vehicle|my vehicles)\b/.test(value)) return 'garage'
  if (/\b(add ons?|addons?|packages?|content packs?)\b/.test(value)) return 'addons'
  if (/\b(diagnostics?|health|status|logs?)\b/.test(value)) return 'diagnostics'
  if (/\b(about|credit|information)\b/.test(value)) return 'about'
  if (/\b(vehicles?|catalog|marketplace)\b/.test(value)) return 'vehicles'
  if (/\b(home|hub|gbay|main)\b/.test(value)) return 'home'
  return 'other'
}

function rootSectionRoutes(menu: RoutedMenuDescriptor): Map<GbaySectionId, string> {
  const routes = menu.routes ?? []
  const result = new Map<GbaySectionId, string>()
  const home = routes.find((route) => route.id === menu.homeRouteId)
  if (home && classifyGbayRoute(home) === 'home') result.set('home', home.id)

  // Prefer actual menu roots. Nested catalog/delivery routes should remain
  // inside their owning section rather than becoming duplicate top tabs.
  for (const route of routes) {
    const section = classifyGbayRoute(route)
    if (!topLevelSections.has(section) || section === 'home' || result.has(section)) continue
    const isMenuRoot = route.id === route.menuId || !route.parentId || route.parentId === menu.homeRouteId
    if (isMenuRoot) result.set(section, route.id)
  }

  // A vehicle-only descriptor is still a complete, valid marketplace page.
  if (home && !result.has('vehicles') && classifyGbayRoute(home) === 'vehicles') {
    result.set('vehicles', home.id)
  }
  return result
}

export function gbaySections(menu: RoutedMenuDescriptor): GbaySectionDefinition[] {
  const routes = rootSectionRoutes(menu)
  return sectionDefinitions.flatMap((definition) => {
    const routeId = routes.get(definition.id)
    return routeId ? [{ ...definition, routeId }] : []
  })
}

export function gbayAccountState(menu: RoutedMenuDescriptor): GbayAccountState | null {
  const routes = menu.routes ?? []
  const home = routes.find((route) => route.id === menu.homeRouteId)
  const candidates = home ? [home, ...routes.filter((route) => route !== home)] : routes

  for (const route of candidates) {
    const status = route.items.find((item) =>
      item.type === 'status' &&
      (item.id === 'balance' || item.id === 'custom-balance') &&
      typeof item.value === 'string' && item.value.trim().length > 0)
    if (status?.type === 'status' && typeof status.value === 'string') {
      return {
        label: status.label.trim() || 'Balance',
        value: status.value.trim(),
      }
    }
  }
  return null
}

export function isGbayNavigationItem(item: MenuItem): item is MenuRouteItem {
  return item.type === 'route' && item.id.startsWith(GBAY_NAVIGATION_PREFIX)
}

/**
 * Legacy GBAY descriptors exposed explicit refresh/load commands while the
 * browser had no state-change channel. The host now publishes exact changed
 * menu ids, so these commands must not remain in the player-facing surface.
 */
export function isLegacyGbayStateRefreshItem(item: MenuItem): item is MenuCommandItem {
  if (item.type !== 'command') return false
  const action = item.action.toLowerCase()
  if (action.endsWith('.refresh')) return true
  const identity = `${item.id} ${item.label} ${action}`
  return /(?:refresh|load|check)[-_ ]*(?:owned[-_ ]*)?weapons?/i.test(identity) ||
    /(?:weapons?)[-_ ]*(?:state|catalog|ownership)[-_ ]*(?:refresh|load|check)/i.test(identity)
}

export function isAllin1Presentation(presentation: MenuPresentation): boolean {
  if (presentation.extensionId !== ALLIN1_GBAY_EXTENSION_ID) return false
  const style = presentation.context.presentationStyle
  const route = presentation.context.route
  return style === 'allin1-shell' || style === 'allin1-gbay' ||
    (typeof route === 'string' && route.startsWith('gbay/'))
}

/**
 * Shape ALLIN1's typed menu tree for the established GBAY shell. This only
 * adds local route links and flattens the vehicle grid into its owning page;
 * action ids, parameters, enabled states, and host authority are untouched.
 */
export function projectAllin1GbayMenu(menu: RoutedMenuDescriptor): RoutedMenuDescriptor {
  const projected = structuredClone(menu)
  const routes = projected.routes ?? []

  for (const route of routes) {
    const section = classifyGbayRoute(route)
    route.items = route.items.flatMap((item) => {
      if (item.type !== 'route') return [item]
      const child = routes.find((candidate) => candidate.id === item.routeId)
      const isCatalog = item.id === 'catalog' || item.routeId.endsWith('/catalog')
      const isDeliveryGrid = item.id === 'delivery-destinations' ||
        item.routeId.endsWith('/delivery-destinations')
      const isFavoriteActions = item.id === 'favorite-actions' ||
        item.routeId.endsWith('/favorite-actions')
      const isGarageContent = section === 'garage' && (
        item.id === 'locations' || item.routeId.endsWith('/locations') ||
        item.id === 'vehicles' || item.routeId.endsWith('/vehicles')
      )
      const isCustomizeContent = section === 'customization' && (
        /^(?:owned-)?weapons$/.test(item.id) ||
        /^(?:workbench-)?options$/.test(item.id) ||
        /(?:owned-weapons|workbench-options|option-cards)$/.test(item.routeId)
      )
      if (!child || (!isCatalog && !isDeliveryGrid && !isFavoriteActions && !isGarageContent && !isCustomizeContent)) return [item]
      if (isCatalog && child.layout !== 'grid') return [item]
      if (isDeliveryGrid && child.layout !== 'grid') return [item]
      return child.items
    })
    // ALLIN1 pairs each visible apply card with a separately authorized
    // read-only preview action. Keep those host nodes callable by id while
    // removing them from the browser focus ring and visual card grid.
    route.items = route.items.map((item) =>
      isGbayWeaponPreviewAction(item) || isLegacyGbayStateRefreshItem(item) ||
      (section === 'garage' && item.type === 'pagination')
      ? { ...item, visible: false }
      : item)
    route.subtitle ??= projected.description
  }

  const sections = gbaySections(projected)
  const navigation: MenuRouteItem[] = sections.map((section) => ({
    id: `${GBAY_NAVIGATION_PREFIX}${section.id}`,
    label: section.label,
    icon: section.icon,
    type: 'route',
    routeId: section.routeId!,
  }))
  if (navigation.length > 1) {
    for (const route of routes) {
      route.items = [
        ...navigation.map((item) => ({ ...item, enabled: item.routeId !== route.id })),
        ...route.items.filter((item) => !isGbayNavigationItem(item)),
      ]
    }
  }
  return projected
}

export function isGbayVehicleCard(item: MenuItem): item is MenuCommandItem {
  return item.type === 'command' && (
    item.id.startsWith('vehicle-') ||
    item.action.toLowerCase().includes('vehicle.checkout') ||
    item.action.toLowerCase().includes('vehicle.purchase')
  )
}

export function isGbayWeaponCard(item: MenuItem): item is MenuCommandItem {
  return item.type === 'command' && item.action.toLowerCase().includes('weapon.purchase')
}

export function isGbayGearCard(item: MenuItem): item is MenuCommandItem {
  return item.type === 'command' && item.action.toLowerCase().includes('gear.apply')
}

export function isGbayGarageVehicle(item: MenuItem): item is MenuCommandItem {
  if (item.type !== 'command') return false
  const action = item.action.toLowerCase()
  return item.id.startsWith('stored-') || action.includes('garage.sell') || action.includes('garage.retrieve')
}

export function isGbayCustomizeWeapon(item: MenuItem): item is MenuCommandItem {
  if (item.type !== 'command') return false
  const action = item.action.toLowerCase()
  return /^(?:owned-weapon|customize-weapon)-/.test(item.id) ||
    (action.includes('custom') && action.includes('weapon') && action.endsWith('.select'))
}

export function isGbayCustomizationOption(item: MenuItem): item is MenuCommandItem {
  if (item.type !== 'command') return false
  const action = item.action.toLowerCase()
  if (/^(?:owned-weapon|customize-weapon)-/.test(item.id) ||
    (action.includes('custom') && action.includes('weapon') && action.endsWith('.select'))) return false
  return /^(?:ammo|component|finish|livery|option)-/.test(item.id) ||
    (action.includes('custom') && (
      action.endsWith('.apply') || action.endsWith('.purchase') || action.endsWith('.equip')
    ))
}

export function isGbayWeaponPreviewAction(
  item: MenuItem,
): item is MenuCommandItem {
  return item.type === 'command' && (
    item.action.toLowerCase() === 'weapon.customize.preview' ||
    item.action.toLowerCase() === 'weapon.customize.preview.stop'
  )
}

/** The bridge supplies a separate, confirmed removal command for an exact
 * weapon/component/attachment identity. Never manufacture one from UI state. */
export function gbayCustomizationCards(items: readonly MenuItem[]): {
  option: MenuCommandItem; action: MenuCommandItem; unequip: boolean
}[] {
  const options = items.filter(isGbayCustomizationOption)
  const paired = new Set<string>()
  const cards = options.filter(option => !option.id.endsWith('-unequip')).map(option => {
    const matches = options.filter(candidate => candidate.id === `${option.id}-unequip` &&
      candidate.action.toLowerCase() === 'weapon.customize.apply')
    const removal = matches.length === 1 && option.action.toLowerCase() === 'weapon.customize.apply'
      ? matches[0] : undefined
    if (removal) paired.add(removal.id)
    return { option, action: removal ?? option, unequip: Boolean(removal) }
  })
  // An older or partial descriptor still exposes its explicit action; do not
  // silently lose it or associate it with a similarly named attachment.
  return [...cards, ...options.filter(option => option.id.endsWith('-unequip') && !paired.has(option.id))
    .map(option => ({ option, action: option, unequip: false }))]
}

export function gbayWeaponPreviewNode(
  items: readonly MenuItem[],
  option: MenuItem,
): MenuCommandItem | undefined {
  if (!isGbayCustomizationOption(option)) return undefined
  return items.find((candidate): candidate is MenuCommandItem =>
    isGbayWeaponPreviewAction(candidate) &&
    candidate.action.toLowerCase() === 'weapon.customize.preview' &&
    candidate.id === `${option.id}-preview`)
}

function labelledValue(parts: readonly string[], labels: readonly string[]): string {
  for (const part of parts) {
    const separator = part.search(/[:=]/)
    if (separator < 1) continue
    const label = part.slice(0, separator).trim().toLowerCase().replace(/\s+/g, '')
    if (labels.includes(label)) return part.slice(separator + 1).trim()
  }
  return ''
}

export function parseGbayCardDetail(description?: string): GbayCardDetail {
  const parts = (description ?? '').split('·').map((part) => part.trim()).filter(Boolean)
  const unlabelled = parts.filter((part) => !part.includes(':') && !part.includes('='))
  const labelledPrice = labelledValue(parts, ['price'])
  const labelledOwnership = labelledValue(parts, ['ownership', 'status'])
  const price = labelledPrice || unlabelled[0] || ''
  const ownership = labelledOwnership || unlabelled[1] || ''
  const favorite = parts.some((part) => /^(favorite|favourite)(?:\s*[:=]\s*(?:true|yes|1))?$/i.test(part))
  const manufacturer = labelledValue(parts, ['manufacturer', 'make', 'brand'])
  const model = labelledValue(parts, ['model', 'spawn'])
  const preview = labelledValue(parts, ['preview', 'image', 'thumbnail'])
  const category = labelledValue(parts, ['category', 'class', 'group', 'type']) ||
    [...unlabelled].reverse().find((part) =>
      !/^(favorite|favourite)$/i.test(part) && part !== ownership && part !== price) || ''
  return { price, ownership, favorite, category, manufacturer, model, preview }
}

function normalizedAssetId(value: string): string {
  return value.toLowerCase().replace(/(?:-?(?:preview|image|thumbnail))$/g, '')
    .replace(/^(?:preview|image|thumbnail)-?/, '')
}

export function gbayCardPreview(card: MenuCommandItem, items: readonly MenuItem[]): string {
  const detail = parseGbayCardDetail(card.description)
  if (renderableGbayPreviewSource(detail.preview)) return detail.preview
  const cardKey = normalizedAssetId(card.id)
  const media = items.find((item): item is MenuMediaItem => item.type === 'media' &&
    normalizedAssetId(item.id) === cardKey &&
    (item.mediaType === 'image' || item.mediaType.startsWith('image/')) &&
    renderableGbayPreviewSource(item.source))
  return media?.source ?? ''
}

const safePreviewExtension = /\.(?:avif|gif|jpe?g|png|webp)$/i
const safePreviewSegment = /^[A-Za-z0-9][A-Za-z0-9._-]*$/

/** Relative paths are portable across Reactor's CEF and WebView2 local hosts. */
export function renderableGbayPreviewSource(source: string): boolean {
  const value = source.trim()
  if (!value || value.includes('\\')) return false
  if (/^data:image\/(?:avif|gif|jpeg|png|webp);base64,[a-z0-9+/=]+$/i.test(value)) return true
  if (/^[a-z][a-z0-9+.-]*:/i.test(value)) return false
  if (value.startsWith('//')) return false

  // Reject traversal before URL normalization can erase it. Decode once so
  // extension-authored values cannot smuggle dot segments through `%2e`.
  let decodedValue: string
  try {
    decodedValue = decodeURIComponent(value)
  } catch {
    return false
  }
  const authoredPath = decodedValue.split(/[?#]/, 1)[0]
  const authoredRelativePath = authoredPath.startsWith('./') ? authoredPath.slice(2) : authoredPath
  if (authoredRelativePath.split('/').some((segment) => segment === '.' || segment === '..')) return false

  let resolved: URL
  try {
    resolved = new URL(value, 'https://reactorv.local/')
  } catch {
    return false
  }
  if (resolved.protocol !== 'https:' ||
    resolved.hostname.toLowerCase() !== 'reactorv.local' ||
    resolved.username || resolved.password || resolved.port) return false

  let decodedPath: string
  try {
    decodedPath = decodeURIComponent(resolved.pathname)
  } catch {
    return false
  }
  const segments = decodedPath.split('/').filter(Boolean)
  return segments.length > 0 &&
    segments.every((segment) => segment !== '.' && segment !== '..' && safePreviewSegment.test(segment)) &&
    safePreviewExtension.test(segments[segments.length - 1])
}

export function gbayCatalogRevision(context: JsonObject): string | undefined {
  return typeof context.catalogRevision === 'string' ? context.catalogRevision : undefined
}
