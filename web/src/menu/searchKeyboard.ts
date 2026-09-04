import type { MenuInvocation, MenuSearchItem } from '../gta/types'
import { MenuController } from './controller'

export type SearchKeyboardKeyKind =
  | 'character'
  | 'space'
  | 'backspace'
  | 'clear'
  | 'cancel'
  | 'apply'

export interface SearchKeyboardKey {
  id: string
  label: string
  kind: SearchKeyboardKeyKind
  value?: string
}

export interface SearchKeyboardSession {
  routeId: string
  itemId: string
  label: string
  value: string
  maximumLength: number
  row: number
  column: number
}

export interface SearchKeyboardActivation {
  session: SearchKeyboardSession
  intent?: 'apply' | 'cancel'
}

function characters(values: string): SearchKeyboardKey[] {
  return Array.from(values, (value) => ({
    id: `character-${value.toLowerCase()}`,
    label: value,
    kind: 'character' as const,
    value: value.toLowerCase(),
  }))
}

/**
 * A fixed, bounded key catalog keeps controller text entry deterministic and
 * entirely inside the provider UI. It never opens an operating-system input
 * surface or synthesizes keyboard input outside ReactorV.
 */
export const SEARCH_KEYBOARD_ROWS: readonly (readonly SearchKeyboardKey[])[] = [
  characters('ABCDEFGH'),
  characters('IJKLMNOP'),
  characters('QRSTUVWX'),
  [
    ...characters('YZ012345'),
  ],
  [
    ...characters('6789'),
    { id: 'character-period', label: '.', kind: 'character', value: '.' },
    { id: 'character-hyphen', label: '−', kind: 'character', value: '-' },
    { id: 'space', label: 'SPACE', kind: 'space', value: ' ' },
    { id: 'backspace', label: '⌫', kind: 'backspace' },
  ],
  [
    { id: 'clear', label: 'CLEAR', kind: 'clear' },
    { id: 'cancel', label: 'CANCEL', kind: 'cancel' },
    { id: 'apply', label: 'APPLY', kind: 'apply' },
  ],
]

function boundedMaximumLength(value: number | undefined): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return 256
  return Math.max(1, Math.min(256, Math.trunc(value)))
}

function boundedValue(value: string, maximumLength: number): string {
  return Array.from(value).slice(0, maximumLength).join('')
}

export function createSearchKeyboardSession(
  routeId: string,
  item: MenuSearchItem,
): SearchKeyboardSession {
  const maximumLength = boundedMaximumLength(item.maxLength)
  return {
    routeId,
    itemId: item.id,
    label: item.label || 'Search',
    value: boundedValue(item.value, maximumLength),
    maximumLength,
    row: 0,
    column: 0,
  }
}

export function selectedSearchKeyboardKey(session: SearchKeyboardSession): SearchKeyboardKey {
  const row = SEARCH_KEYBOARD_ROWS[Math.max(0, Math.min(SEARCH_KEYBOARD_ROWS.length - 1, session.row))]
  return row[Math.max(0, Math.min(row.length - 1, session.column))]
}

export function moveSearchKeyboardSelection(
  session: SearchKeyboardSession,
  horizontal: -1 | 0 | 1,
  vertical: -1 | 0 | 1,
): SearchKeyboardSession {
  if (horizontal === 0 && vertical === 0) return session
  let row = session.row
  let column = session.column
  if (vertical !== 0) {
    row = (row + vertical + SEARCH_KEYBOARD_ROWS.length) % SEARCH_KEYBOARD_ROWS.length
    column = Math.min(column, SEARCH_KEYBOARD_ROWS[row].length - 1)
  }
  if (horizontal !== 0) {
    const width = SEARCH_KEYBOARD_ROWS[row].length
    column = (column + horizontal + width) % width
  }
  return { ...session, row, column }
}

export function focusSearchKeyboardKey(
  session: SearchKeyboardSession,
  keyId: string,
): SearchKeyboardSession {
  for (let row = 0; row < SEARCH_KEYBOARD_ROWS.length; row += 1) {
    const column = SEARCH_KEYBOARD_ROWS[row].findIndex((key) => key.id === keyId)
    if (column >= 0) return { ...session, row, column }
  }
  return session
}

export function updateSearchKeyboardDraft(
  session: SearchKeyboardSession,
  value: string,
): SearchKeyboardSession {
  return { ...session, value: boundedValue(value, session.maximumLength) }
}

export function activateSearchKeyboardKey(
  session: SearchKeyboardSession,
  key: SearchKeyboardKey = selectedSearchKeyboardKey(session),
): SearchKeyboardActivation {
  switch (key.kind) {
    case 'character':
    case 'space':
      return {
        session: updateSearchKeyboardDraft(session, session.value + (key.value ?? '')),
      }
    case 'backspace':
      return {
        session: updateSearchKeyboardDraft(session, Array.from(session.value).slice(0, -1).join('')),
      }
    case 'clear': return { session: { ...session, value: '' } }
    case 'cancel': return { session, intent: 'cancel' }
    case 'apply': return { session, intent: 'apply' }
  }
}

/**
 * Commits only through the exact search node that opened the keyboard. Route
 * changes, removed nodes, and node-kind drift fail closed without dispatching
 * an operation or inventing host parameters.
 */
export async function commitSearchKeyboardSession(
  controller: MenuController,
  session: SearchKeyboardSession,
): Promise<MenuInvocation | undefined> {
  if (controller.currentRoute.id !== session.routeId || !controller.focus(session.itemId)) return undefined
  const item = controller.focusedItem
  if (!item || item.type !== 'search') return undefined
  return controller.setValue(session.value)
}
