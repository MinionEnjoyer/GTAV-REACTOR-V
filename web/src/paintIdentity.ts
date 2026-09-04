export type HostPaintMode = 'about' | 'verifying' | 'setup-status' | 'initializing'

export type PaintIdentity =
  | { kind: 'host'; mode: HostPaintMode; generation: number }
  | { kind: 'menu'; providerSessionGeneration: number; presentationId: string }

const contractPrefix = 'reactor-v-paint/v1'
const fnvOffsetBasis = 0xcbf29ce484222325n
const fnvPrime = 0x100000001b3n
const uint64Mask = 0xffffffffffffffffn

function isContractGeneration(value: number): boolean {
  return Number.isSafeInteger(value) && value >= 0 && value <= 0x7fffffff
}

export function hostPaintIdentity(
  mode: HostPaintMode,
  generation: number,
): PaintIdentity | null {
  if (!isContractGeneration(generation)) return null
  return { kind: 'host', mode, generation }
}

export function menuPaintIdentity(
  providerSessionGeneration: number,
  presentationId: string,
): PaintIdentity | null {
  if (!isContractGeneration(providerSessionGeneration) ||
    presentationId.trim().length === 0) return null
  return { kind: 'menu', providerSessionGeneration, presentationId }
}

export function resolveVisiblePaintIdentity(
  surface: HostPaintMode | 'transparent' | 'presentation',
  hostSurfaceGeneration: number,
  providerSessionGeneration: number,
  presentationId: string | null,
): PaintIdentity | null {
  if (surface === 'transparent') return null
  if (surface === 'presentation') {
    return presentationId === null
      ? null
      : menuPaintIdentity(providerSessionGeneration, presentationId)
  }
  return hostPaintIdentity(surface, hostSurfaceGeneration)
}

export function canonicalPaintIdentity(identity: PaintIdentity): string {
  if (identity.kind === 'host') {
    return `${contractPrefix}\0host\0${identity.mode}\0${identity.generation}`
  }
  return `${contractPrefix}\0menu\0${identity.providerSessionGeneration}\0${identity.presentationId}`
}

/**
 * Stable cross-language FNV-1a over the canonical UTF-8 identity. This token
 * correlates browser pixels with a native ownership epoch; it is deliberately
 * not used as an authorization or cryptographic boundary.
 */
export function paintIdentityFingerprint(identity: PaintIdentity): bigint {
  let hash = fnvOffsetBasis
  for (const value of new TextEncoder().encode(canonicalPaintIdentity(identity))) {
    hash ^= BigInt(value)
    hash = (hash * fnvPrime) & uint64Mask
  }
  return hash
}

export function paintIdentityFingerprintHex(identity: PaintIdentity): string {
  return paintIdentityFingerprint(identity).toString(16).padStart(16, '0')
}

/** Eight little-endian fingerprint bytes encoded with the native marker palette. */
export function paintIdentityMarkerColors(identity: PaintIdentity): string[] {
  const fingerprint = paintIdentityFingerprint(identity)
  return Array.from({ length: 8 }, (_, byteIndex) => {
    const encoded = Number((fingerprint >> BigInt(byteIndex * 8)) & 0xffn)
    const red = 64 + ((encoded >>> 4) * 12)
    const green = 64 + ((encoded & 0x0f) * 12)
    return `rgb(${red}, ${green}, 208)`
  })
}
