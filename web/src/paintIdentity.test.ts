import { describe, expect, it } from 'vitest'
import {
  canonicalPaintIdentity,
  hostPaintIdentity,
  menuPaintIdentity,
  paintIdentityFingerprintHex,
  paintIdentityMarkerColors,
  resolveVisiblePaintIdentity,
} from './paintIdentity'

describe('cross-language paint identity', () => {
  it('matches the stable UTF-8 FNV-1a vectors used by the native host', () => {
    const fixtures = [
      [hostPaintIdentity('initializing', 42), '9793139a7e096240'],
      [hostPaintIdentity('about', 7), '937437cfa9254291'],
      [hostPaintIdentity('verifying', 99), '989ab95c6e364c88'],
      [hostPaintIdentity('setup-status', 0), 'c569b3b27f388731'],
      [menuPaintIdentity(1, 'allin1.gbay:home:42'), '26895c8d78e86ef8'],
      [menuPaintIdentity(12, 'gbay-startup'), 'f7f189ac2750f682'],
    ] as const

    for (const [identity, expected] of fixtures) {
      expect(identity).not.toBeNull()
      expect(paintIdentityFingerprintHex(identity!)).toBe(expected)
    }
  })

  it('uses an exact, case-sensitive, NUL-delimited canonical contract', () => {
    expect(canonicalPaintIdentity(hostPaintIdentity('about', 7)!))
      .toBe('reactor-v-paint/v1\0host\0about\0' + '7')
    expect(canonicalPaintIdentity(menuPaintIdentity(1, 'Menu:A')!))
      .toBe('reactor-v-paint/v1\0menu\0' + '1\0Menu:A')
    expect(paintIdentityFingerprintHex(menuPaintIdentity(1, 'Menu:A')!))
      .not.toBe(paintIdentityFingerprintHex(menuPaintIdentity(1, 'menu:a')!))
  })

  it('changes when any ownership field changes', () => {
    const base = paintIdentityFingerprintHex(menuPaintIdentity(3, 'allin1.gbay:home:42')!)
    expect(paintIdentityFingerprintHex(menuPaintIdentity(4, 'allin1.gbay:home:42')!))
      .not.toBe(base)
    expect(paintIdentityFingerprintHex(menuPaintIdentity(3, 'allin1.gbay:home:43')!))
      .not.toBe(base)
    expect(paintIdentityFingerprintHex(hostPaintIdentity('initializing', 3)!))
      .not.toBe(paintIdentityFingerprintHex(hostPaintIdentity('about', 3)!))
    expect(paintIdentityFingerprintHex(hostPaintIdentity('about', 4)!))
      .not.toBe(paintIdentityFingerprintHex(hostPaintIdentity('about', 3)!))
  })

  it('selects exactly the currently visible host or menu ownership epoch', () => {
    expect(resolveVisiblePaintIdentity('transparent', 12, 4, null)).toBeNull()
    expect(resolveVisiblePaintIdentity('setup-status', 0, 0, null)).toEqual({
      kind: 'host', mode: 'setup-status', generation: 0,
    })
    expect(resolveVisiblePaintIdentity('initializing', 12, 4, 'menu-pending')).toEqual({
      kind: 'host', mode: 'initializing', generation: 12,
    })
    expect(resolveVisiblePaintIdentity('presentation', 12, 4, 'menu-visible')).toEqual({
      kind: 'menu', providerSessionGeneration: 4, presentationId: 'menu-visible',
    })
    expect(resolveVisiblePaintIdentity('presentation', 12, 4, null)).toBeNull()
  })

  it('encodes all eight fingerprint bytes in native little-endian order', () => {
    expect(paintIdentityMarkerColors(menuPaintIdentity(1, 'allin1.gbay:home:42')!)).toEqual([
      'rgb(244, 160, 208)', // f8
      'rgb(136, 232, 208)', // 6e
      'rgb(232, 160, 208)', // e8
      'rgb(148, 160, 208)', // 78
      'rgb(160, 220, 208)', // 8d
      'rgb(124, 208, 208)', // 5c
      'rgb(160, 172, 208)', // 89
      'rgb(88, 136, 208)', // 26
    ])
  })

  it('rejects identities outside the shared signed-32-bit generation contract', () => {
    expect(hostPaintIdentity('initializing', -1)).toBeNull()
    expect(hostPaintIdentity('initializing', 0x80000000)).toBeNull()
    expect(menuPaintIdentity(-1, 'menu')).toBeNull()
    expect(menuPaintIdentity(0x80000000, 'menu')).toBeNull()
    expect(menuPaintIdentity(1, '')).toBeNull()
    expect(menuPaintIdentity(1, '   ')).toBeNull()
  })
})
