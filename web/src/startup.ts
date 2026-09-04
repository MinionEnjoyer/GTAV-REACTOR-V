import type {
  StartupComponentId,
  StartupComponentState,
  StartupComponentStatus,
  StartupConsoleEntry,
  StartupPhase,
  StartupStatus,
} from './gta/types'

export const STARTUP_CONSOLE_LIMIT = 48
export const STARTUP_CONSOLE_VISIBLE_LIMIT = 12

const componentIds = new Set<StartupComponentId>([
  'reactor', 'scripthook', 'managed-bridge', 'allin1',
])
const componentStates = new Set<StartupComponentState>([
  'ready', 'initializing', 'waiting', 'unavailable',
])
const phases = new Set<StartupPhase>([
  'reactor-starting', 'waiting-for-provider', 'provider-connected',
])
const safeToken = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function boundedText(value: unknown, maximumLength: number): string | null {
  if (typeof value !== 'string') return null
  const normalized = value.trim().replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, '')
  return normalized.length > 0 && normalized.length <= maximumLength ? normalized : null
}

function boundedOptionalText(value: unknown, maximumLength: number): string | null {
  if (typeof value !== 'string') return null
  const normalized = value.trim().replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, '')
  return normalized.length <= maximumLength ? normalized : null
}

function boundedInteger(value: unknown, minimum: number, maximum: number): number | null {
  return Number.isInteger(value) && (value as number) >= minimum && (value as number) <= maximum
    ? value as number
    : null
}

function parseComponent(value: unknown): StartupComponentStatus | null {
  if (!isRecord(value) || !componentIds.has(value.id as StartupComponentId) ||
    !componentStates.has(value.state as StartupComponentState)) return null
  const label = boundedText(value.label, 64)
  const detail = boundedText(value.detail, 256)
  if (!label || !detail) return null
  return {
    id: value.id as StartupComponentId,
    label,
    state: value.state as StartupComponentState,
    detail,
  }
}

function parseConsoleEntry(value: unknown): StartupConsoleEntry | null {
  if (!isRecord(value)) return null
  const sequence = boundedInteger(value.sequence, 0, Number.MAX_SAFE_INTEGER)
  const timestampUtc = boundedText(value.timestampUtc, 40)
  const source = boundedText(value.source, 48)
  const stage = boundedText(value.stage, 96)
  // StartupTrace deliberately emits an empty message when a stage has no
  // detail; the stage token remains the useful console text in that case.
  const message = boundedOptionalText(value.message, 240)
  if (sequence === null || !timestampUtc || !source || !stage || message === null) return null
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(timestampUtc)) return null
  return { sequence, timestampUtc, source, stage, message }
}

/** Parse the untrusted host payload without allowing unbounded console data into React. */
export function parseStartupStatus(value: unknown): StartupStatus | null {
  if (!isRecord(value) || value.schemaVersion !== 1 || !phases.has(value.phase as StartupPhase) ||
    typeof value.providerConnected !== 'boolean' || !Array.isArray(value.components) ||
    !isRecord(value.console)) return null
  const sequence = boundedInteger(value.sequence, 0, Number.MAX_SAFE_INTEGER)
  const sessionId = boundedText(value.sessionId, 128)
  const maxEntries = boundedInteger(value.console.maxEntries, 1, STARTUP_CONSOLE_LIMIT)
  const dropped = boundedInteger(value.console.dropped, 0, Number.MAX_SAFE_INTEGER)
  if (sequence === null || !sessionId || !safeToken.test(sessionId) || maxEntries === null ||
    dropped === null || !Array.isArray(value.console.entries) ||
    value.console.entries.length > STARTUP_CONSOLE_LIMIT) return null

  const components = value.components.map(parseComponent)
  const entries = value.console.entries.map(parseConsoleEntry)
  if (components.some((item) => item === null) || entries.some((item) => item === null)) return null
  const typedComponents = components as StartupComponentStatus[]
  const parsedComponentIds = new Set(typedComponents.map((item) => item.id))
  if (typedComponents.length !== componentIds.size ||
    parsedComponentIds.size !== componentIds.size ||
    [...componentIds].some((id) => !parsedComponentIds.has(id))) return null

  return {
    schemaVersion: 1,
    sequence,
    sessionId,
    phase: value.phase as StartupPhase,
    providerConnected: value.providerConnected,
    // Older bootstrap hosts did not expose the typed intent state. Treat
    // omission as no automatic-menu promise rather than inventing one.
    defaultMenuRequested: value.defaultMenuRequested === true,
    defaultMenuDeadlineUtc:
      typeof value.defaultMenuDeadlineUtc === 'string' &&
      /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(value.defaultMenuDeadlineUtc)
        ? value.defaultMenuDeadlineUtc
        : null,
    components: typedComponents,
    console: {
      maxEntries,
      dropped,
      entries: (entries as StartupConsoleEntry[]).slice(-maxEntries),
    },
  }
}

/** Ignore delayed replay from an older session or an older sequence. */
export function selectCurrentStartupStatus(
  current: StartupStatus | null,
  next: StartupStatus,
): StartupStatus {
  // While the host still reports one connected provider session, a delayed
  // bootstrap snapshot must never demote an ALLIN1-ready managed snapshot.
  // App clears this state on an authoritative provider disconnect, allowing
  // a later provider generation to begin from initializing again.
  const currentAllin1Ready = current?.components.some((component) =>
    component.id === 'allin1' && component.state === 'ready') ?? false
  const nextAllin1Ready = next.components.some((component) =>
    component.id === 'allin1' && component.state === 'ready')
  if (current?.providerConnected && currentAllin1Ready &&
    (!next.providerConnected || !nextAllin1Ready)) return current
  if (!current || current.sessionId !== next.sessionId || next.sequence >= current.sequence) return next
  return current
}

export function startupComponentIsReady(
  status: StartupStatus,
  componentId: StartupComponentId,
): boolean {
  return status.components.some((component) =>
    component.id === componentId && component.state === 'ready')
}

export function createStartupFallbackStatus(providerConnected: boolean): StartupStatus {
  return {
    schemaVersion: 1,
    sequence: 0,
    sessionId: 'web-fallback',
    phase: providerConnected ? 'provider-connected' : 'waiting-for-provider',
    providerConnected,
    defaultMenuRequested: false,
    defaultMenuDeadlineUtc: null,
    components: [
      {
        id: 'reactor', label: 'REACTOR V', state: 'ready',
        detail: 'Interface host is ready.',
      },
      {
        id: 'scripthook', label: 'ScriptHookV',
        state: providerConnected ? 'ready' : 'initializing',
        detail: providerConnected ? 'Gameplay bridge connected.' : 'Waiting for GTA script threads.',
      },
      {
        id: 'managed-bridge', label: 'Managed bridge',
        state: providerConnected ? 'ready' : 'waiting',
        detail: providerConnected ? 'Managed provider connected.' : 'Waiting for ScriptHookV.',
      },
      {
        id: 'allin1', label: 'ALLIN1', state: 'initializing',
        detail: providerConnected ? 'Loading the GBAY provider.' : 'Waiting for the gameplay bridge.',
      },
    ],
    console: {
      maxEntries: STARTUP_CONSOLE_LIMIT,
      dropped: 0,
      entries: [
        {
          sequence: 0,
          timestampUtc: '1970-01-01T00:00:00Z',
          source: 'reactor',
          stage: 'status-channel',
          message: 'Waiting for detailed initialization telemetry.',
        },
      ],
    },
  }
}

export function startupAutomaticMenuCopy(status: StartupStatus): string {
  return status.defaultMenuRequested
    ? 'GBAY will open automatically when the gameplay bridge is ready.'
    : 'Initialization status only. Press F9 when the gameplay bridge is ready to open its menu.'
}

export interface StartupDisplayComponent {
  id: 'reactor' | 'bridge' | 'allin1'
  label: string
  state: StartupComponentState
  detail: string
}

const stateRank: Record<StartupComponentState, number> = {
  unavailable: 4,
  initializing: 3,
  waiting: 2,
  ready: 1,
}

function combineBridgeStatus(status: StartupStatus): StartupDisplayComponent {
  const parts = status.components.filter((component) =>
    component.id === 'scripthook' || component.id === 'managed-bridge')
  const selected = parts.reduce<StartupComponentStatus | undefined>((current, item) =>
    !current || stateRank[item.state] > stateRank[current.state] ? item : current, undefined)
  return {
    id: 'bridge',
    label: 'ScriptHook bridge',
    state: selected?.state ?? (status.providerConnected ? 'ready' : 'waiting'),
    detail: selected?.detail ?? (status.providerConnected
      ? 'Gameplay provider connected.'
      : 'Waiting for GTA script threads.'),
  }
}

export function startupDisplayComponents(status: StartupStatus): StartupDisplayComponent[] {
  const component = (id: 'reactor' | 'allin1', label: string): StartupDisplayComponent => {
    const match = status.components.find((candidate) => candidate.id === id)
    return {
      id,
      label,
      state: match?.state ?? (id === 'reactor' ? 'ready' : 'waiting'),
      detail: match?.detail ?? (id === 'reactor'
        ? 'Interface host is ready.'
        : 'Waiting for the ALLIN1 provider.'),
    }
  }
  return [component('reactor', 'REACTOR V'), combineBridgeStatus(status), component('allin1', 'ALLIN1')]
}

export function visibleStartupConsoleEntries(status: StartupStatus): StartupConsoleEntry[] {
  return status.console.entries.slice(-STARTUP_CONSOLE_VISIBLE_LIMIT)
}

export function formatStartupTimestamp(timestampUtc: string): string {
  const match = /T(\d{2}:\d{2}:\d{2})/.exec(timestampUtc)
  return match?.[1] ?? '--:--:--'
}
