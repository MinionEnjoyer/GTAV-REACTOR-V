import {
  paintIdentityFingerprintHex,
  paintIdentityMarkerColors,
  type PaintIdentity,
} from '../paintIdentity'

export function PaintIdentityMarker({ identity }: { identity: PaintIdentity | null }) {
  if (!identity) return null
  const colors = paintIdentityMarkerColors(identity)
  const fingerprint = paintIdentityFingerprintHex(identity)

  return (
    <span
      key={fingerprint}
      className="reactor-paint-identity-marker"
      data-reactor-paint-fingerprint={fingerprint}
      data-reactor-paint-kind={identity.kind}
      aria-hidden="true"
    >
      {colors.map((color, index) => (
        <i key={index} style={{ backgroundColor: color }} />
      ))}
    </span>
  )
}
