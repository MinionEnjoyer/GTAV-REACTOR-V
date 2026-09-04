import type {
  MenuDescriptor,
  MenuItem,
  RoutedMenuDescriptor,
} from '../gta/types'
import type { MenuControllerSnapshot } from './controller'

export interface GbayStateChangedEvent {
  revision: number
  menus: string[]
}

export type PendingGbayStateChange = GbayStateChangedEvent

const menuIdPattern = /^[a-z0-9][a-z0-9._-]{0,95}$/
const filterIdentityPattern = /(?:^|[._-])(search|filter|category|ownership|location|page|pages)(?:$|[._-])/i

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/** Fail closed before an extension event can select host descriptors. */
export function parseGbayStateChangedEvent(value: unknown): GbayStateChangedEvent | null {
  if (!record(value) || Object.keys(value).some((key) => key !== 'revision' && key !== 'menus') ||
    typeof value.revision !== 'number' || !Number.isSafeInteger(value.revision) || value.revision < 1 ||
    !Array.isArray(value.menus) || value.menus.length < 1 || value.menus.length > 64) return null
  const menus: string[] = []
  const seen = new Set<string>()
  for (const candidate of value.menus) {
    if (typeof candidate !== 'string' || !menuIdPattern.test(candidate) || seen.has(candidate)) return null
    seen.add(candidate)
    menus.push(candidate)
  }
  return { revision: value.revision, menus }
}

/**
 * Combine revisions received while a descriptor request is in flight. Menus
 * are unioned even when a newer revision is already pending: each event names
 * a delta, so dropping an older unseen delta could retain stale state.
 */
export function coalesceGbayStateChange(
  pending: PendingGbayStateChange | null,
  incoming: GbayStateChangedEvent,
  appliedRevision: number,
): PendingGbayStateChange | null {
  if (incoming.revision <= appliedRevision) return pending
  if (!pending) return { revision: incoming.revision, menus: [...incoming.menus] }
  return {
    revision: Math.max(pending.revision, incoming.revision),
    menus: [...new Set([...pending.menus, ...incoming.menus])],
  }
}

export function changedMenuIdsInLoadedTree(
  descriptors: readonly MenuDescriptor[],
  requestedMenuIds: readonly string[],
): string[] {
  const loaded = new Set(descriptors.map((descriptor) => descriptor.id))
  return [...new Set(requestedMenuIds)].filter((menuId) => loaded.has(menuId))
}

export function mergeChangedMenuDescriptors(
  descriptors: readonly MenuDescriptor[],
  replacements: readonly MenuDescriptor[],
  extensionId: string,
): MenuDescriptor[] {
  const loaded = new Set(descriptors.map((descriptor) => descriptor.id))
  const replacementById = new Map<string, MenuDescriptor>()
  for (const descriptor of replacements) {
    if (descriptor.extensionId !== extensionId || !loaded.has(descriptor.id) ||
      replacementById.has(descriptor.id)) {
      throw new Error('The host returned an unexpected GBAY descriptor.')
    }
    replacementById.set(descriptor.id, descriptor)
  }
  return descriptors.map((descriptor) =>
    structuredClone(replacementById.get(descriptor.id) ?? descriptor))
}

function itemIdentity(item: MenuItem): string {
  return `${item.id} ${'action' in item ? item.action ?? '' : ''}`
}

function preserveViewValue(next: MenuItem, previous: MenuItem): void {
  if (next.type !== previous.type) return
  if (next.type === 'search' && previous.type === 'search') {
    next.value = previous.maxLength
      ? previous.value.slice(0, previous.maxLength)
      : previous.value
    return
  }
  if (next.type === 'choice' && previous.type === 'choice' &&
    filterIdentityPattern.test(itemIdentity(next)) &&
    next.options.some((option) => option.value === previous.value && !option.disabled)) {
    next.value = previous.value
    return
  }
  if (next.type === 'tabs' && previous.type === 'tabs' &&
    filterIdentityPattern.test(itemIdentity(next)) &&
    next.tabs.some((tab) => tab.value === previous.value && !tab.disabled)) {
    next.value = previous.value
    return
  }
  if (next.type === 'pagination' && previous.type === 'pagination') {
    next.page = Math.max(1, Math.min(previous.page, Math.max(1, next.pageCount)))
    return
  }
  if ((next.type === 'list' || next.type === 'grid') &&
    (previous.type === 'list' || previous.type === 'grid') &&
    previous.selectedId && next.entries.some((entry) =>
      entry.id === previous.selectedId && !entry.disabled)) {
    next.selectedId = previous.selectedId
  }
}

/** Preserve only browser navigation/filter state; gameplay values stay host-owned. */
export function preserveGbayViewState(
  menu: RoutedMenuDescriptor,
  previous: MenuControllerSnapshot,
): RoutedMenuDescriptor {
  const preserved = structuredClone(menu)
  const route = preserved.routes?.find((candidate) =>
    candidate.id === previous.route.id)
  if (!route) return preserved
  const previousItems = new Map(previous.route.items.map((item) => [item.id, item]))
  for (const item of route.items) {
    const previousItem = previousItems.get(item.id)
    if (previousItem) preserveViewValue(item, previousItem)
  }
  return preserved
}
