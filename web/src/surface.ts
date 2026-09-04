export type HostSurfaceMode = 'none' | 'about' | 'verifying' | 'setup-status' | 'initializing'
export type HostSurfaceHandoff = 'presentation'
export type SurfaceView = 'transparent' | 'about' | 'verifying' | 'setup-status' | 'initializing' | 'presentation'

export interface HostSurfaceDescriptor {
  mode: HostSurfaceMode
  generation?: number
  edition?: string
  gameVersion?: string
  handoff?: HostSurfaceHandoff
}

export interface HostProviderDescriptor {
  connected: boolean
  sessionGeneration: number
}

export interface PresentationHandoffState {
  holdInitializer: boolean
  menuInteractive: boolean
}

/**
 * The embedded page is shared by the always-warm runtime and the installer
 * verification preset. A hidden/idle runtime must never infer that it should
 * show setup UI merely because no extension currently owns a presentation.
 * The dedicated setup preset opts in with `?surface=setup-status`.
 */
export function resolveInitialHostSurface(search: string): HostSurfaceMode {
  const params = new URLSearchParams(search)
  return params.get('surface') === 'setup-status' ? 'setup-status' : 'none'
}

function boundedLabel(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined
  const normalized = value.trim().replace(/\s+/g, ' ')
  return normalized.length > 0 && normalized.length <= 64 &&
    /^[A-Za-z0-9][A-Za-z0-9 ._()+-]*$/.test(normalized)
    ? normalized
    : undefined
}

export function parseHostSurface(value: unknown): HostSurfaceDescriptor | null {
  const mode = typeof value === 'string'
    ? value
    : typeof value === 'object' && value !== null && !Array.isArray(value)
      ? (value as Record<string, unknown>).mode
      : undefined
  if (mode !== 'none' && mode !== 'about' && mode !== 'verifying' && mode !== 'setup-status' && mode !== 'initializing') return null
  if (typeof value === 'string') return { mode }
  const record = value as Record<string, unknown>
  if (record.handoff !== undefined && record.handoff !== 'presentation') return null
  const generation = Number.isSafeInteger(record.generation) &&
    (record.generation as number) > 0
    ? record.generation as number
    : undefined
  const descriptor: HostSurfaceDescriptor = {
    mode,
    generation,
    edition: boundedLabel(record.edition),
    gameVersion: boundedLabel(record.gameVersion ?? record.gameBuild),
  }
  if (record.handoff === 'presentation') descriptor.handoff = 'presentation'
  return descriptor
}

export function parseHostSurfaceMode(value: unknown): HostSurfaceMode | null {
  return parseHostSurface(value)?.mode ?? null
}

export function parseHostProvider(value: unknown): HostProviderDescriptor | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null
  const record = value as Record<string, unknown>
  if (typeof record.connected !== 'boolean') return null
  if (record.sessionGeneration !== undefined &&
    (!Number.isSafeInteger(record.sessionGeneration) || (record.sessionGeneration as number) < 0)) return null
  return {
    connected: record.connected,
    // Generation zero preserves compatibility with older persistent hosts.
    sessionGeneration: (record.sessionGeneration as number | undefined) ?? 0,
  }
}

export function parseProviderConnected(value: unknown): boolean | null {
  return parseHostProvider(value)?.connected ?? null
}

/**
 * Bootstrap surfaces can replace an extension-owned presentation, but the
 * bootstrap host's idle `none` notification cannot. The host publishes
 * `none` immediately before a managed menu presentation to guarantee that no
 * About/initializer pixels leak into the handoff. Treating that idle marker
 * as a dismissal briefly unmounted the existing menu controller and lost its
 * route/focus state during an authoritative refresh.
 */
export function hostSurfaceSupersedesPresentation(mode: HostSurfaceMode): boolean {
  return mode !== 'none'
}

/**
 * A native `none` boundary can arrive while a provider replacement is still
 * preparing. Keep the last painted bootstrap frame until the exact requested
 * presentation is accepted, rather than exposing an opacity-zero tree on a
 * still-visible overlay window.
 */
export function shouldRetainBootstrapFrame(
  nextMode: HostSurfaceMode,
  currentMode: HostSurfaceMode,
  hasRequestedPresentation: boolean,
  hasCommittedPresentation: boolean,
  handoff?: HostSurfaceHandoff,
): boolean {
  return nextMode === 'none' && currentMode !== 'none' &&
    !hasCommittedPresentation &&
    (hasRequestedPresentation || handoff === 'presentation')
}

/** Mirror native ownership after a menu is accepted. */
export function shouldRetireBootstrapAfterAcceptance(
  currentMode: HostSurfaceMode,
  deferredNoneBoundary: boolean,
): boolean {
  return currentMode === 'initializing' || deferredNoneBoundary
}

/** Presentation ownership wins only after a fresh presentation event. */
export function resolveSurfaceView(
  hostSurface: HostSurfaceMode,
  hasPresentation: boolean,
): SurfaceView {
  if (hasPresentation) return 'presentation'
  if (hostSurface === 'none') return 'transparent'
  return hostSurface
}

/**
 * A provider presentation is prepared inside the persistent browser, but it
 * cannot replace a last-known-good bootstrap frame until the native host
 * accepts that exact presentation token. Presentations opened from transparent
 * idle do not carry a bootstrap ownership token.
 */
export function resolvePresentationHandoff(
  presentationId: string | null,
  initializerPresentationId: string | null,
  acceptedPresentationId: string | null,
): PresentationHandoffState {
  const holdInitializer = presentationId !== null &&
    initializerPresentationId === presentationId &&
    acceptedPresentationId !== presentationId
  return {
    holdInitializer,
    menuInteractive: presentationId !== null && !holdInitializer,
  }
}

export function formatDetectedGtaTarget(edition?: string, gameVersion?: string): string {
  const normalizedEdition = boundedLabel(edition)?.replace(/^GTA\s*V\s*/i, '')
  const normalizedVersion = boundedLabel(gameVersion)
  return ['GTA V', normalizedEdition, normalizedVersion].filter(Boolean).join(' ')
}
