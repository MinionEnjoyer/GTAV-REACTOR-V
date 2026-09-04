export interface WindowedPointerInput {
  x: number
  y: number
  pressed: boolean
  released: boolean
  wheelDelta: number
}

export interface WindowedKeyboardInput {
  code: string
  shift: boolean
  control: boolean
  alt: boolean
}

export type BootstrapAboutAction =
  | 'overview'
  | 'detected-mods'
  | 'retry-detected-mods'
  | 'refresh-detected-mods'

const bootstrapAboutActions = new Set<BootstrapAboutAction>([
  'overview',
  'detected-mods',
  'retry-detected-mods',
  'refresh-detected-mods',
])

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export function parseWindowedPointerInput(value: unknown): WindowedPointerInput | null {
  if (!record(value) || typeof value.x !== 'number' || !Number.isFinite(value.x) ||
    typeof value.y !== 'number' || !Number.isFinite(value.y) ||
    typeof value.pressed !== 'boolean' || typeof value.released !== 'boolean' ||
    typeof value.wheelDelta !== 'number' || !Number.isInteger(value.wheelDelta) ||
    Math.abs(value.wheelDelta) > 1200) return null
  return {
    x: Math.min(1, Math.max(0, value.x)),
    y: Math.min(1, Math.max(0, value.y)),
    pressed: value.pressed,
    released: value.released,
    wheelDelta: value.wheelDelta,
  }
}

/**
 * Fixed allow-list for the external preloader's private About pointer lane.
 * Attribute presence alone is insufficient: arbitrary extension markup can
 * never opt itself into pre-provider input.
 */
export function parseBootstrapAboutAction(value: unknown): BootstrapAboutAction | null {
  return typeof value === 'string' && bootstrapAboutActions.has(value as BootstrapAboutAction)
    ? value as BootstrapAboutAction
    : null
}

export function parseWindowedKeyboardInput(value: unknown): WindowedKeyboardInput | null {
  if (!record(value) || typeof value.code !== 'string' || value.code.length < 1 || value.code.length > 32 ||
    typeof value.shift !== 'boolean' || typeof value.control !== 'boolean' || typeof value.alt !== 'boolean') return null
  return {
    code: value.code,
    shift: value.shift,
    control: value.control,
    alt: value.alt,
  }
}

const shiftedDigits = ')!@#$%^&*('
const punctuation: Record<string, [string, string]> = {
  Oemcomma: [',', '<'],
  OemPeriod: ['.', '>'],
  OemMinus: ['-', '_'],
  Oemplus: ['=', '+'],
  OemQuestion: ['/', '?'],
  Oem1: [';', ':'],
  Oem7: ["'", '"'],
  OemOpenBrackets: ['[', '{'],
  Oem6: [']', '}'],
  Oem5: ['\\', '|'],
  Oemtilde: ['`', '~'],
}

export function windowedKeyboardText(value: WindowedKeyboardInput): string | null {
  if (value.control || value.alt) return null
  if (/^[A-Z]$/.test(value.code)) return value.shift ? value.code : value.code.toLowerCase()
  const digit = /^D([0-9])$/.exec(value.code)
  if (digit) return value.shift ? shiftedDigits[Number(digit[1])] : digit[1]
  const numberPad = /^NumPad([0-9])$/.exec(value.code)
  if (numberPad) return numberPad[1]
  if (value.code === 'Space') return ' '
  const pair = punctuation[value.code]
  return pair ? pair[value.shift ? 1 : 0] : null
}

export function nextEnabledIndex(
  disabled: readonly boolean[],
  selectedIndex: number,
  direction: -1 | 1,
): number {
  if (disabled.length === 0) return -1
  for (let offset = 1; offset <= disabled.length; offset += 1) {
    const candidate = (selectedIndex + direction * offset + disabled.length) % disabled.length
    if (!disabled[candidate]) return candidate
  }
  return selectedIndex >= 0 && selectedIndex < disabled.length ? selectedIndex : -1
}

/**
 * A forwarded click belongs to the nearest interactive control, not the exact
 * text/icon descendant returned by elementFromPoint. Press and release may
 * legitimately land on different descendants of one button; callers resolve
 * both descendants to their common control before applying this identity gate.
 */
export function shouldActivateForwardedPointerTarget<T extends object>(
  pressedTarget: T | null,
  releasedTarget: T | null,
): boolean {
  return pressedTarget !== null && pressedTarget === releasedTarget
}
