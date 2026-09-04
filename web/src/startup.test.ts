import { describe, expect, it } from 'vitest'
import type { StartupConsoleEntry, StartupStatus } from './gta/types'
import {
  STARTUP_CONSOLE_LIMIT,
  STARTUP_CONSOLE_VISIBLE_LIMIT,
  createStartupFallbackStatus,
  parseStartupStatus,
  selectCurrentStartupStatus,
  startupAutomaticMenuCopy,
  startupComponentIsReady,
  startupDisplayComponents,
  visibleStartupConsoleEntries,
} from './startup'

function entry(sequence: number): StartupConsoleEntry {
  return {
    sequence,
    timestampUtc: `2026-08-29T16:00:${String(sequence % 60).padStart(2, '0')}Z`,
    source: 'bootstrap',
    stage: `stage-${sequence}`,
    message: `Initialization event ${sequence}`,
  }
}

function status(sequence = 4): StartupStatus {
  return {
    schemaVersion: 1,
    sequence,
    sessionId: 'session-42',
    phase: 'waiting-for-provider',
    providerConnected: false,
    defaultMenuRequested: true,
    defaultMenuDeadlineUtc: '2026-08-29T16:02:00Z',
    components: [
      { id: 'reactor', label: 'REACTOR V', state: 'ready', detail: 'UI ready.' },
      { id: 'scripthook', label: 'ScriptHookV', state: 'initializing', detail: 'Creating threads.' },
      { id: 'managed-bridge', label: 'Managed bridge', state: 'waiting', detail: 'Waiting for provider.' },
      { id: 'allin1', label: 'ALLIN1', state: 'waiting', detail: 'Waiting for the bridge.' },
    ],
    console: { maxEntries: STARTUP_CONSOLE_LIMIT, dropped: 0, entries: [entry(1), entry(2)] },
  }
}

describe('startup transition contract', () => {
  it('promises an automatic menu only for an explicitly armed typed intent', () => {
    expect(startupAutomaticMenuCopy(status())).toContain('open automatically')
    expect(startupAutomaticMenuCopy({
      ...status(),
      defaultMenuRequested: false,
      defaultMenuDeadlineUtc: null,
    })).toContain('Initialization status only')
    const olderPayload = { ...status() } as Record<string, unknown>
    delete olderPayload.defaultMenuRequested
    delete olderPayload.defaultMenuDeadlineUtc
    const parsed = parseStartupStatus(olderPayload)
    expect(parsed?.defaultMenuRequested).toBe(false)
    expect(parsed?.defaultMenuDeadlineUtc).toBeNull()
  })

  it('parses the typed host response and rejects malformed or unbounded data', () => {
    expect(parseStartupStatus(status())).toEqual(status())
    expect(parseStartupStatus({
      ...status(),
      console: {
        maxEntries: STARTUP_CONSOLE_LIMIT,
        dropped: 0,
        entries: [{ ...entry(1), stage: 's'.repeat(96), message: '' }],
      },
    })).not.toBeNull()
    expect(parseStartupStatus({ ...status(), phase: 'invented' })).toBeNull()
    expect(parseStartupStatus({
      ...status(),
      console: { maxEntries: 49, dropped: 0, entries: [] },
    })).toBeNull()
    expect(parseStartupStatus({
      ...status(),
      console: {
        maxEntries: STARTUP_CONSOLE_LIMIT,
        dropped: 0,
        entries: Array.from({ length: STARTUP_CONSOLE_LIMIT + 1 }, (_, index) => entry(index)),
      },
    })).toBeNull()
    expect(parseStartupStatus({ ...status(), components: [] })).toBeNull()
    expect(parseStartupStatus({
      ...status(),
      components: status().components.filter((component) => component.id !== 'allin1'),
    })).toBeNull()
  })

  it('collapses the native and managed bridge checks into exactly three user-facing stages', () => {
    const display = startupDisplayComponents(status())
    expect(display.map((component) => component.id)).toEqual(['reactor', 'bridge', 'allin1'])
    expect(display[1]).toMatchObject({ label: 'ScriptHook bridge', state: 'initializing' })
  })

  it('keeps only the newest bounded console tail', () => {
    const many = status()
    many.console.entries = Array.from({ length: STARTUP_CONSOLE_LIMIT }, (_, index) => entry(index))
    const visible = visibleStartupConsoleEntries(many)
    expect(visible).toHaveLength(STARTUP_CONSOLE_VISIBLE_LIMIT)
    expect(visible[0].sequence).toBe(STARTUP_CONSOLE_LIMIT - STARTUP_CONSOLE_VISIBLE_LIMIT)
    expect(visible.at(-1)?.sequence).toBe(STARTUP_CONSOLE_LIMIT - 1)
  })

  it('rejects stale replay while accepting a new session and provides an older-host fallback', () => {
    expect(selectCurrentStartupStatus(status(10), status(9)).sequence).toBe(10)
    expect(selectCurrentStartupStatus(status(10), { ...status(1), sessionId: 'new-session' }).sessionId)
      .toBe('new-session')
    expect(createStartupFallbackStatus(false)).toMatchObject({
      phase: 'waiting-for-provider', providerConnected: false,
    })
    expect(startupDisplayComponents(createStartupFallbackStatus(true)).map((item) => item.state))
      .toEqual(['ready', 'ready', 'initializing'])
  })

  it('does not let a delayed bootstrap session demote managed ALLIN1 readiness', () => {
    const managed = {
      ...status(22),
      sessionId: 'managed-provider',
      phase: 'provider-connected' as const,
      providerConnected: true,
      components: status().components.map((component) => component.id === 'allin1'
        ? { ...component, state: 'ready' as const, detail: 'ALLIN1 is loaded.' }
        : component),
    }
    const delayedBootstrap = {
      ...status(99),
      sessionId: 'native-preloader',
      phase: 'provider-connected' as const,
      providerConnected: true,
      components: status().components.map((component) => component.id === 'allin1'
        ? { ...component, state: 'initializing' as const }
        : component),
    }

    expect(startupComponentIsReady(managed, 'allin1')).toBe(true)
    expect(selectCurrentStartupStatus(managed, delayedBootstrap)).toBe(managed)
    expect(selectCurrentStartupStatus(managed, {
      ...delayedBootstrap,
      phase: 'waiting-for-provider',
      providerConnected: false,
    })).toBe(managed)
  })
})
