import type { JsonObject, JsonValue, MenuDescriptor, MenuInvocation, MenuInvocationResult, MenuNode } from '../gta/types'
import { waitForHostSurfacePaint, type AnimationFrameRequester } from '../hostSurfacePaint'

const identifierPattern = /^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$/
const presentationIdPattern = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/

export interface MenuPresentation {
  extensionId: string
  menuId: string
  presentationId: string
  context: JsonObject
  inputMode: string
}

export interface MenuDismissal {
  extensionId: string
  menuId: string
  presentationId: string
  reason: 'extension-request' | 'overlay-hidden' | 'superseded' | 'presentation-failed'
}

export type MenuFetcher = (extensionId: string, menuId: string) => Promise<MenuDescriptor>

const maximumCachedMenuTrees = 16
const menuTreeCache = new Map<string, Promise<MenuDescriptor[]>>()

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isJsonValue(value: unknown, depth = 0): value is JsonValue {
  if (depth > 16) return false
  if (value === null || typeof value === 'string' || typeof value === 'boolean') return true
  if (typeof value === 'number') return Number.isFinite(value)
  if (Array.isArray(value)) return value.length <= 256 && value.every((item) => isJsonValue(item, depth + 1))
  if (!isRecord(value) || Object.keys(value).length > 256) return false
  return Object.values(value).every((item) => isJsonValue(item, depth + 1))
}

function safeIdentifier(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 && value.length <= 64 && identifierPattern.test(value)
}

function safePresentationId(value: unknown): value is string {
  return typeof value === 'string' && presentationIdPattern.test(value)
}

/** Validate the host event before it can select a route or influence browser state. */
export function parseMenuPresentation(value: unknown): MenuPresentation | null {
  if (!isRecord(value) || !safeIdentifier(value.extensionId) || !safeIdentifier(value.menuId) ||
    !safePresentationId(value.presentationId)) return null
  if (value.inputMode !== undefined &&
    (typeof value.inputMode !== 'string' || value.inputMode.length > 32)) return null
  const context = value.context ?? {}
  if (!isRecord(context) || !isJsonValue(context)) return null
  return {
    extensionId: value.extensionId,
    menuId: value.menuId,
    presentationId: value.presentationId,
    context: context as JsonObject,
    inputMode: typeof value.inputMode === 'string' ? value.inputMode : 'interactive-menu',
  }
}

export function parseMenuDismissal(value: unknown): MenuDismissal | null {
  if (!isRecord(value) || !safeIdentifier(value.extensionId) || !safeIdentifier(value.menuId) ||
    !safePresentationId(value.presentationId) ||
    (value.reason !== 'extension-request' && value.reason !== 'overlay-hidden' &&
      value.reason !== 'superseded' && value.reason !== 'presentation-failed')) return null
  return {
    extensionId: value.extensionId,
    menuId: value.menuId,
    presentationId: value.presentationId,
    reason: value.reason,
  }
}

function referencedMenus(nodes: readonly MenuNode[], result: Set<string>): void {
  for (const node of nodes) {
    if (node.kind === 'submenu') result.add(node.menuId)
    else if (node.kind === 'list' || node.kind === 'grid') referencedMenus(node.nodes, result)
    else if (node.kind === 'tabs') {
      for (const tab of node.tabs) referencedMenus(tab.nodes, result)
    }
  }
}

/**
 * Fetch only the presented menu and the submenu descriptors reachable from it.
 * This keeps one extension with many unrelated tools from consuming the menu's
 * transport and render budget.
 */
export async function loadPresentedMenuTree(
  fetchMenu: MenuFetcher,
  extensionId: string,
  rootMenuId: string,
  maximumMenus = 64,
): Promise<MenuDescriptor[]> {
  if (!safeIdentifier(extensionId) || !safeIdentifier(rootMenuId)) {
    throw new Error('The menu presentation contains an invalid extension or menu identifier.')
  }
  if (!Number.isInteger(maximumMenus) || maximumMenus < 1 || maximumMenus > 64) {
    throw new Error('The menu traversal limit is invalid.')
  }

  const pending = [rootMenuId]
  const requested = new Set<string>()
  const menus: MenuDescriptor[] = []
  while (pending.length > 0) {
    const menuId = pending.shift()!
    if (requested.has(menuId)) continue
    if (requested.size >= maximumMenus) throw new Error('The presented menu exceeds the 64-menu traversal limit.')
    requested.add(menuId)

    const menu = await fetchMenu(extensionId, menuId)
    if (!menu || menu.extensionId !== extensionId || menu.id !== menuId || !Array.isArray(menu.nodes)) {
      throw new Error(`The host returned an invalid descriptor for menu '${menuId}'.`)
    }
    menus.push(menu)
    const references = new Set<string>()
    referencedMenus(menu.nodes, references)
    for (const reference of [...references].sort()) {
      if (!requested.has(reference)) pending.push(reference)
    }
  }
  return menus
}

/**
 * Reuse an immutable menu tree only when the authoritative host supplies an
 * explicit revision. Presentations without a revision always perform a fresh
 * fetch, so caching can never silently preserve stale gameplay state.
 */
export async function loadPresentedMenuTreeCached(
  fetchMenu: MenuFetcher,
  extensionId: string,
  rootMenuId: string,
  revision: string | number | null | undefined,
  maximumMenus = 64,
): Promise<MenuDescriptor[]> {
  if ((typeof revision !== 'string' || revision.length === 0 || revision.length > 64) &&
    (typeof revision !== 'number' || !Number.isSafeInteger(revision) || revision < 0)) {
    return loadPresentedMenuTree(fetchMenu, extensionId, rootMenuId, maximumMenus)
  }

  // The traversal limit is part of the caller's safety contract. Reusing a
  // tree fetched with a wider limit would otherwise let a later, stricter
  // caller receive more descriptors than it explicitly allowed.
  const cacheKey = `${extensionId}\u0000${rootMenuId}\u0000${String(revision)}\u0000${maximumMenus}`
  const existing = menuTreeCache.get(cacheKey)
  if (existing) return existing

  const pending = loadPresentedMenuTree(fetchMenu, extensionId, rootMenuId, maximumMenus)
  menuTreeCache.set(cacheKey, pending)
  if (menuTreeCache.size > maximumCachedMenuTrees) {
    const oldest = menuTreeCache.keys().next().value as string | undefined
    if (oldest) menuTreeCache.delete(oldest)
  }
  try {
    return await pending
  } catch (error) {
    if (menuTreeCache.get(cacheKey) === pending) menuTreeCache.delete(cacheKey)
    throw error
  }
}

/**
 * Retry only transport failures while acknowledging a committed surface.
 * A typed `false` is authoritative (the host replaced, cancelled, or expired
 * the presentation) and must never be retried into a newer reveal gate.
 */
export async function acknowledgeMenuPresentationWithRetry(
  acknowledge: () => boolean | Promise<boolean>,
  maximumAttempts = 3,
  retryDelayMilliseconds = 100,
): Promise<boolean> {
  if (!Number.isInteger(maximumAttempts) || maximumAttempts < 1 || maximumAttempts > 5) {
    throw new Error('The presentation acknowledgement retry limit is invalid.')
  }
  if (!Number.isInteger(retryDelayMilliseconds) || retryDelayMilliseconds < 0 ||
    retryDelayMilliseconds > 1000) {
    throw new Error('The presentation acknowledgement retry delay is invalid.')
  }

  for (let attempt = 1; attempt <= maximumAttempts; attempt += 1) {
    try {
      return await acknowledge()
    } catch (error) {
      if (attempt === maximumAttempts) throw error
      await new Promise<void>((resolve) => globalThis.setTimeout(resolve, retryDelayMilliseconds))
    }
  }
  return false
}

/**
 * Cross the same two-frame browser paint boundary used by bootstrap surfaces
 * before acknowledging a provider menu. Native code can then complete the
 * matching DirectComposition commit and expose the HWND atomically.
 */
export async function acknowledgePaintedMenuPresentation(
  assetsReady: Promise<unknown>,
  requestFrame: AnimationFrameRequester,
  acknowledge: () => boolean | Promise<boolean>,
  assetTimeoutMilliseconds = 250,
): Promise<boolean> {
  await waitForHostSurfacePaint(
    assetsReady,
    requestFrame,
    assetTimeoutMilliseconds,
  )
  return acknowledgeMenuPresentationWithRetry(acknowledge)
}

/**
 * Starts at most one complete paint/ready attempt for each presentation token.
 * React may re-run the owning layout effect while assets or paint frames are
 * still pending; those re-renders must not publish duplicate ready messages.
 * Transport retries remain contained inside that one attempt.
 */
export function runMenuPresentationAcknowledgementOnce(
  attemptedPresentationIds: Set<string>,
  presentationId: string,
  attempt: () => boolean | Promise<boolean>,
): Promise<boolean> | null {
  if (attemptedPresentationIds.has(presentationId)) return null
  attemptedPresentationIds.add(presentationId)
  return Promise.resolve().then(attempt)
}

let invocationSequence = 0

/** Session-local, bridge-safe key used only after an explicit confirmation. */
export function createMenuInvocationKey(presentationId: string, nodeId: string): string {
  const safePresentation = safePresentationId(presentationId) ? presentationId : 'presentation'
  const safeNode = safeIdentifier(nodeId) ? nodeId : 'action'
  invocationSequence = (invocationSequence + 1) % 0x7fffffff
  return `menu:${safePresentation}:${safeNode}:${Date.now().toString(36)}:${invocationSequence.toString(36)}`.slice(0, 128)
}

function stableJson(value: JsonValue | undefined): string {
  if (value === undefined) return ''
  if (value === null || typeof value !== 'object') return JSON.stringify(value)
  if (Array.isArray(value)) return `[${value.map((item) => stableJson(item)).join(',')}]`
  return `{${Object.keys(value).sort().map((key) =>
    `${JSON.stringify(key)}:${stableJson(value[key])}`).join(',')}}`
}

/** Stable identity used to retain one confirmation key across transport retries. */
export function menuInvocationIdentity(presentationId: string, invocation: MenuInvocation): string {
  return [
    presentationId,
    invocation.extensionId,
    invocation.menuId,
    invocation.nodeId,
    invocation.interaction,
    stableJson(invocation.value),
    stableJson(invocation.parameters),
  ].join('|')
}

/**
 * Presentation directives are accepted only from a successful, typed host
 * result. Extension-authored browser events and node metadata cannot close the
 * surface, which keeps navigation authority at the Reactor host boundary.
 */
export type MenuResultPresentationDirective = 'close' | 'refresh'

export function menuResultPresentationDirective(result: MenuInvocationResult): MenuResultPresentationDirective | null {
  if (result.succeeded !== true || !isRecord(result.value)) return null
  return result.value.presentation === 'close' || result.value.presentation === 'refresh'
    ? result.value.presentation
    : null
}
