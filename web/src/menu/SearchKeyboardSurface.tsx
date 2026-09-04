import type { KeyboardEvent as ReactKeyboardEvent } from 'react'
import {
  SEARCH_KEYBOARD_ROWS,
  selectedSearchKeyboardKey,
  type SearchKeyboardKey,
  type SearchKeyboardSession,
} from './searchKeyboard'

interface SearchKeyboardProps {
  session: SearchKeyboardSession
  onDraft(value: string): void
  onMove(horizontal: -1 | 0 | 1, vertical: -1 | 0 | 1): void
  onFocusKey(keyId: string): void
  onActivate(key: SearchKeyboardKey): void
  onApply(): void
  onCancel(): void
}

export function SearchKeyboard({
  session,
  onDraft,
  onMove,
  onFocusKey,
  onActivate,
  onApply,
  onCancel,
}: SearchKeyboardProps) {
  const selected = selectedSearchKeyboardKey(session)

  const onKeyDown = (event: ReactKeyboardEvent) => {
    let handled = true
    switch (event.key) {
      case 'ArrowUp': onMove(0, -1); break
      case 'ArrowDown': onMove(0, 1); break
      case 'ArrowLeft': onMove(-1, 0); break
      case 'ArrowRight': onMove(1, 0); break
      case 'Escape': onCancel(); break
      case 'Enter':
        if (event.target instanceof HTMLInputElement) onApply()
        else onActivate(selected)
        break
      case ' ':
        if (event.target instanceof HTMLInputElement) handled = false
        else onActivate(selected)
        break
      default: handled = false
    }
    if (handled) {
      event.preventDefault()
      event.stopPropagation()
    }
  }

  return (
    <div
      className="search-keyboard-overlay"
      role="dialog"
      aria-modal="true"
      aria-labelledby="search-keyboard-title"
      onKeyDown={onKeyDown}
    >
      <section className="search-keyboard-panel">
        <header>
          <span>
            <small>Controller text entry</small>
            <h2 id="search-keyboard-title">{session.label}</h2>
          </span>
          <button type="button" aria-label="Cancel search entry" onClick={onCancel}>×</button>
        </header>
        <input
          autoFocus
          className="search-keyboard-draft"
          type="search"
          value={session.value}
          maxLength={session.maximumLength}
          aria-label={`${session.label} text`}
          onChange={(event) => onDraft(event.currentTarget.value)}
        />
        <div className="search-keyboard-grid" aria-label="On-screen keyboard">
          {SEARCH_KEYBOARD_ROWS.map((row, rowIndex) => (
            <div className="search-keyboard-row" key={`row-${rowIndex}`}>
              {row.map((key) => {
                const focused = key.id === selected.id
                return (
                  <button
                    key={key.id}
                    type="button"
                    className={`search-keyboard-key kind-${key.kind}${focused ? ' focused' : ''}`}
                    data-search-key-focused={focused ? 'true' : 'false'}
                    aria-label={key.kind === 'character' ? `Type ${key.label}` : key.label}
                    onMouseEnter={() => onFocusKey(key.id)}
                    onFocus={() => onFocusKey(key.id)}
                    onClick={() => onActivate(key)}
                  >{key.label}</button>
                )
              })}
            </div>
          ))}
        </div>
        <footer>
          <span>D-PAD MOVE · A SELECT · B CANCEL</span>
          <span>{Array.from(session.value).length} / {session.maximumLength}</span>
        </footer>
      </section>
    </div>
  )
}
