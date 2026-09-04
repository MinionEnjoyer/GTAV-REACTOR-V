import type { ExtensionListResult, ExtensionSummary } from '../gta/types'

const maximumExtensions = 128
const maximumCacheCharacters = 65_536
const maximumCacheAgeMilliseconds = 90 * 24 * 60 * 60 * 1000
const maximumFutureSkewMilliseconds = 5 * 60 * 1000
const identifierPattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/
export const detectedModsCacheKey = 'reactorv.detected-mods.v1'

export interface DetectedModsStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
}

export interface LastDetectedMods {
  capturedAtUtc: string
  catalog: ExtensionListResult
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function boundedString(value: unknown, maximumLength: number): value is string {
  return typeof value === 'string' && value.length > 0 && value.length <= maximumLength &&
    value.trim() === value && !/[\u0000-\u001f\u007f]/.test(value)
}

function boundedCount(value: unknown, maximum = 4096): value is number {
  return Number.isInteger(value) && (value as number) >= 0 && (value as number) <= maximum
}

function parseSummary(value: unknown): ExtensionSummary | null {
  if (!isRecord(value) || !boundedString(value.id, 64) || !identifierPattern.test(value.id) ||
    !boundedString(value.name, 120) || !boundedString(value.version, 64) ||
    !boundedCount(value.extensionApiVersion, 64) || value.extensionApiVersion === 0 ||
    !boundedCount(value.actionCount) || !boundedCount(value.eventCount) ||
    !boundedCount(value.menuCount)) return null

  return {
    id: value.id,
    name: value.name,
    version: value.version,
    extensionApiVersion: value.extensionApiVersion,
    actionCount: value.actionCount,
    eventCount: value.eventCount,
    menuCount: value.menuCount,
  }
}

/**
 * Fail closed if an untyped bridge payload does not match the bounded public
 * extension-summary contract. The About surface never renders arbitrary
 * extension markup, filesystem paths, or action payloads.
 */
export function parseDetectedMods(value: unknown): ExtensionListResult | null {
  if (!isRecord(value) || !boundedCount(value.total, maximumExtensions) ||
    !Array.isArray(value.items) || value.items.length > maximumExtensions ||
    value.total !== value.items.length) return null

  const items: ExtensionSummary[] = []
  const ids = new Set<string>()
  for (const candidate of value.items) {
    const item = parseSummary(candidate)
    if (!item || ids.has(item.id.toLowerCase())) return null
    ids.add(item.id.toLowerCase())
    items.push(item)
  }

  return {
    total: value.total,
    items: items.sort((left, right) => left.name.localeCompare(right.name) || left.id.localeCompare(right.id)),
  }
}

function defaultStorage(): DetectedModsStorage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage
  } catch {
    return null
  }
}

export function parseLastDetectedModsCache(
  serialized: unknown,
  nowMilliseconds = Date.now(),
): LastDetectedMods | null {
  if (typeof serialized !== 'string' || serialized.length < 2 ||
    serialized.length > maximumCacheCharacters || !Number.isFinite(nowMilliseconds)) return null
  try {
    const value: unknown = JSON.parse(serialized)
    if (!isRecord(value) || value.schemaVersion !== 1 ||
      typeof value.capturedAtUtc !== 'string' || value.capturedAtUtc.length > 32) return null
    const capturedAtMilliseconds = Date.parse(value.capturedAtUtc)
    if (!Number.isFinite(capturedAtMilliseconds) ||
      new Date(capturedAtMilliseconds).toISOString() !== value.capturedAtUtc ||
      capturedAtMilliseconds > nowMilliseconds + maximumFutureSkewMilliseconds ||
      nowMilliseconds - capturedAtMilliseconds > maximumCacheAgeMilliseconds) return null
    const catalog = parseDetectedMods(value.catalog)
    return catalog ? { capturedAtUtc: value.capturedAtUtc, catalog } : null
  } catch {
    return null
  }
}

/**
 * Reads only the bounded public extension summaries previously returned by
 * the typed live registry. This cache is a non-authoritative convenience for
 * the pre-provider About surface; it never conveys readiness or operations.
 */
export function readLastDetectedMods(
  storage: DetectedModsStorage | null = defaultStorage(),
  nowMilliseconds = Date.now(),
): LastDetectedMods | null {
  if (!storage) return null
  try {
    return parseLastDetectedModsCache(storage.getItem(detectedModsCacheKey), nowMilliseconds)
  } catch {
    return null
  }
}

export function writeLastDetectedMods(
  catalog: ExtensionListResult,
  storage: DetectedModsStorage | null = defaultStorage(),
  nowMilliseconds = Date.now(),
): boolean {
  if (!storage || !Number.isFinite(nowMilliseconds)) return false
  const boundedCatalog = parseDetectedMods(catalog)
  if (!boundedCatalog) return false
  const capturedAt = new Date(nowMilliseconds)
  if (!Number.isFinite(capturedAt.getTime())) return false
  const serialized = JSON.stringify({
    schemaVersion: 1,
    capturedAtUtc: capturedAt.toISOString(),
    catalog: boundedCatalog,
  })
  if (serialized.length > maximumCacheCharacters) return false
  try {
    storage.setItem(detectedModsCacheKey, serialized)
    return true
  } catch {
    return false
  }
}
