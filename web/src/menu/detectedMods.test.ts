import { describe, expect, it } from 'vitest'
import {
  detectedModsCacheKey,
  parseDetectedMods,
  parseLastDetectedModsCache,
  readLastDetectedMods,
  writeLastDetectedMods,
} from './detectedMods'

const fixture = {
  total: 2,
  items: [
    { id: 'fixture.z', name: 'Zulu Mod', version: '2.0.0', extensionApiVersion: 1, actionCount: 3, eventCount: 1, menuCount: 2 },
    { id: 'fixture.a', name: 'Alpha Mod', version: '1.0.0', extensionApiVersion: 1, actionCount: 0, eventCount: 0, menuCount: 1 },
  ],
}

describe('detected-mod catalog parser', () => {
  it('keeps only the bounded public summary fields and sorts by display name', () => {
    expect(parseDetectedMods(fixture)).toEqual({
      total: 2,
      items: [fixture.items[1], fixture.items[0]],
    })
  })

  it('fails closed on unsafe identifiers, duplicate ids, and malformed counts', () => {
    expect(parseDetectedMods({ ...fixture, items: [{ ...fixture.items[0], id: '../mod' }] })).toBeNull()
    expect(parseDetectedMods({ ...fixture, items: [fixture.items[0], { ...fixture.items[1], id: 'FIXTURE.Z' }] })).toBeNull()
    expect(parseDetectedMods({ ...fixture, items: [{ ...fixture.items[0], menuCount: -1 }] })).toBeNull()
    expect(parseDetectedMods({ ...fixture, items: [{ ...fixture.items[0], name: 'Bad\nName' }] })).toBeNull()
    expect(parseDetectedMods({ ...fixture, total: 3 })).toBeNull()
  })

  it('rejects an extension list larger than the host registry limit', () => {
    expect(parseDetectedMods({ total: 129, items: [] })).toBeNull()
  })

  it('round-trips a bounded last-detected summary without retaining extra fields', () => {
    const values = new Map<string, string>()
    const storage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => { values.set(key, value) },
    }
    const now = Date.parse('2026-08-29T12:00:00.000Z')
    expect(writeLastDetectedMods({
      ...fixture,
      items: fixture.items.map((item) => ({ ...item, sourcePath: 'C:/private/mod' })),
    }, storage, now)).toBe(true)
    expect(values.get(detectedModsCacheKey)).not.toContain('sourcePath')
    expect(values.get(detectedModsCacheKey)).not.toContain('C:/private')
    expect(readLastDetectedMods(storage, now)).toEqual({
      capturedAtUtc: '2026-08-29T12:00:00.000Z',
      catalog: {
        total: 2,
        items: [fixture.items[1], fixture.items[0]],
      },
    })
  })

  it('rejects corrupt, oversized, expired, and future cache records', () => {
    const now = Date.parse('2026-08-29T12:00:00.000Z')
    expect(parseLastDetectedModsCache('{bad json', now)).toBeNull()
    expect(parseLastDetectedModsCache('x'.repeat(65_537), now)).toBeNull()
    expect(parseLastDetectedModsCache(JSON.stringify({
      schemaVersion: 1,
      capturedAtUtc: '2025-01-01T00:00:00.000Z',
      catalog: fixture,
    }), now)).toBeNull()
    expect(parseLastDetectedModsCache(JSON.stringify({
      schemaVersion: 1,
      capturedAtUtc: '2026-08-29T13:00:00.000Z',
      catalog: fixture,
    }), now)).toBeNull()
  })

  it('lets a valid empty live snapshot replace a stale non-empty cache', () => {
    const values = new Map<string, string>()
    const storage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => { values.set(key, value) },
    }
    const now = Date.parse('2026-08-29T12:00:00.000Z')
    expect(writeLastDetectedMods(fixture, storage, now)).toBe(true)
    expect(writeLastDetectedMods({ total: 0, items: [] }, storage, now + 1000)).toBe(true)
    expect(readLastDetectedMods(storage, now + 1000)?.catalog).toEqual({ total: 0, items: [] })
  })
})
