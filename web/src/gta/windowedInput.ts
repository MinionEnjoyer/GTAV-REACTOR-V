import { bridge } from './bridge'
import {
  parseBootstrapAboutAction,
  parseWindowedKeyboardInput,
  parseWindowedPointerInput,
  nextEnabledIndex,
  shouldActivateForwardedPointerTarget,
  windowedKeyboardText,
  type WindowedKeyboardInput,
} from './windowedInputPolicy'
import {
  isProviderInputActive,
  onProviderInputReset,
  revokeProviderInput,
} from './providerInputGate'
import { parseHostProvider } from '../surface'

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function dispatchMouse(target: Element, type: string, x: number, y: number, buttons: number): void {
  target.dispatchEvent(new MouseEvent(type, {
    bubbles: true,
    cancelable: true,
    composed: true,
    clientX: x,
    clientY: y,
    button: 0,
    buttons,
  }))
}

function focusElement(target: Element): void {
  if (target instanceof HTMLElement && typeof target.focus === 'function') {
    target.focus({ preventScroll: true })
  }
}

function scrollTarget(target: Element | null, delta: number): void {
  let current = target instanceof HTMLElement ? target : null
  while (current) {
    if (current.scrollHeight > current.clientHeight || current.scrollWidth > current.clientWidth) {
      current.scrollBy({ top: -delta, behavior: 'auto' })
      return
    }
    current = current.parentElement
  }
  window.scrollBy({ top: -delta, behavior: 'auto' })
}

function dispatchWheel(target: Element | null, delta: number): boolean {
  const receiver = target ?? document.body
  return !receiver.dispatchEvent(new WheelEvent('wheel', {
    bubbles: true,
    cancelable: true,
    composed: true,
    deltaY: -delta,
  }))
}

function cycleSelect(select: HTMLSelectElement, direction: -1 | 1): void {
  if (select.disabled) return
  const next = nextEnabledIndex(
    Array.from(select.options, (option) => option.disabled),
    select.selectedIndex,
    direction,
  )
  if (next < 0 || next === select.selectedIndex) return
  select.selectedIndex = next
  select.dispatchEvent(new Event('input', { bubbles: true, composed: true }))
  select.dispatchEvent(new Event('change', { bubbles: true, composed: true }))
}

function createCursor(): HTMLDivElement {
  const cursor = document.createElement('div')
  cursor.dataset.reactorWindowedCursor = 'true'
  Object.assign(cursor.style, {
    position: 'fixed',
    left: '0',
    top: '0',
    width: '15px',
    height: '15px',
    zIndex: '2147483647',
    pointerEvents: 'none',
    border: '2px solid rgba(255,255,255,.96)',
    borderRadius: '50%',
    background: 'rgba(20,168,235,.34)',
    boxShadow: '0 0 0 1px rgba(0,0,0,.7), 0 0 9px rgba(38,168,255,.75)',
    transform: 'translate(-50%, -50%)',
  })
  document.body.append(cursor)
  return cursor
}

function editText(input: HTMLInputElement | HTMLTextAreaElement, key: WindowedKeyboardInput): void {
  const start = input.selectionStart ?? input.value.length
  const end = input.selectionEnd ?? start
  const replace = (text: string, nextSelection: number) => {
    input.setRangeText(text, start, end, 'end')
    input.setSelectionRange(nextSelection, nextSelection)
    input.dispatchEvent(new Event('input', { bubbles: true, composed: true }))
  }
  const text = windowedKeyboardText(key)
  if (text !== null) {
    replace(text, start + text.length)
    return
  }
  if (key.code === 'Back' && start === end && start > 0) {
    input.setRangeText('', start - 1, start, 'end')
    input.dispatchEvent(new Event('input', { bubbles: true, composed: true }))
  } else if (key.code === 'Back') {
    replace('', start)
  } else if (key.code === 'Delete' && start === end && end < input.value.length) {
    input.setRangeText('', end, end + 1, 'end')
    input.dispatchEvent(new Event('input', { bubbles: true, composed: true }))
  } else if (key.code === 'Delete') {
    replace('', start)
  } else if (key.code === 'Left') {
    input.setSelectionRange(Math.max(0, start - 1), Math.max(0, start - 1))
  } else if (key.code === 'Right') {
    const next = Math.min(input.value.length, end + 1)
    input.setSelectionRange(next, next)
  } else if (key.code === 'Home') {
    input.setSelectionRange(0, 0)
  } else if (key.code === 'End') {
    input.setSelectionRange(input.value.length, input.value.length)
  } else if (key.code === 'Enter') {
    input.dispatchEvent(new Event('change', { bubbles: true, composed: true }))
    input.blur()
  } else if (key.code === 'Escape') {
    input.blur()
  }
}

/**
 * WebView2 runs as a non-activating owned window so GTA remains foreground.
 * This adapter turns the game cursor and SHVDN key stream into bounded DOM
 * input instead of depending on an OS cursor that GTA keeps captured.
 */
export function installWindowedInputForwarding(): () => void {
  let providerSessionGeneration = 0
  if (!bridge.isNative) return () => {}
  let cursor: HTMLDivElement | null = null
  let providerHovered: Element | null = null
  let providerPressed: HTMLElement | null = null
  let bootstrapHovered: HTMLElement | null = null
  let bootstrapPressed: HTMLElement | null = null

  const providerControl = (target: Element | null): HTMLElement | null =>
    target?.closest<HTMLElement>(
      'button, input, select, textarea, label, [role="button"], [role="tab"]',
    ) ?? null

  const showCursor = (x: number, y: number) => {
    cursor ??= createCursor()
    cursor.style.display = 'block'
    cursor.style.left = `${x}px`
    cursor.style.top = `${y}px`
  }

  const bootstrapTargetAt = (x: number, y: number): HTMLElement | null => {
    const marker = document.elementFromPoint(x, y)
      ?.closest<HTMLElement>('[data-reactor-bootstrap-action]') ?? null
    if (!(marker instanceof HTMLButtonElement)) return null
    const action = parseBootstrapAboutAction(marker.dataset.reactorBootstrapAction)
    if (!action || !marker.closest('.reactor-about-surface')) return null
    if ((action === 'overview' || action === 'detected-mods') &&
      !marker.closest('.reactor-about-tabs')) return null
    if ((action === 'retry-detected-mods' || action === 'refresh-detected-mods') &&
      !marker.closest('.reactor-detected-mods')) return null
    return marker
  }

  const resetBootstrapPointer = () => {
    if (bootstrapHovered) dispatchMouse(bootstrapHovered, 'mouseout', 0, 0, 0)
    if (bootstrapPressed) dispatchMouse(bootstrapPressed, 'mouseup', 0, 0, 0)
    bootstrapHovered = null
    bootstrapPressed = null
    if (cursor) cursor.style.display = 'none'
  }

  const resetProviderPointer = () => {
    if (providerHovered) dispatchMouse(providerHovered, 'mouseout', 0, 0, 0)
    if (providerPressed) dispatchMouse(providerPressed, 'mouseup', 0, 0, 0)
    providerHovered = null
    providerPressed = null
    if (cursor) cursor.style.display = 'none'
  }

  // A presentation replacement can arrive between two native pointer samples.
  // Release the old DOM press synchronously so it can never click the new tree.
  const removeProviderGateReset = onProviderInputReset(resetProviderPointer)

  const removePointer = bridge.on<unknown>('input.pointer', (raw) => {
    if (!isProviderInputActive()) {
      resetProviderPointer()
      return
    }
    const input = parseWindowedPointerInput(raw)
    if (!input) return
    resetBootstrapPointer()
    const x = Math.round(input.x * Math.max(1, window.innerWidth - 1))
    const y = Math.round(input.y * Math.max(1, window.innerHeight - 1))
    showCursor(x, y)
    const target = document.elementFromPoint(x, y)
    if (target !== providerHovered) {
      if (providerHovered) dispatchMouse(providerHovered, 'mouseout', x, y, 0)
      if (target) dispatchMouse(target, 'mouseover', x, y, 0)
      providerHovered = target
    }
    if (target) dispatchMouse(target, 'mousemove', x, y, providerPressed ? 1 : 0)
    if (input.pressed && target) {
      // Store the semantic control rather than the leaf under the cursor.
      // Icon/text descendants can differ by one pixel between the GTA press
      // and release samples while still belonging to the same tab/button.
      providerPressed = providerControl(target)
      if (providerPressed) {
        focusElement(providerPressed)
        dispatchMouse(providerPressed, 'mousedown', x, y, 1)
      }
    }
    if (input.released) {
      const releaseTarget = providerControl(target)
      // A drag off the original control still owes that control its mouseup,
      // but blank space must not be promoted into a successful click target.
      const mouseUpTarget = releaseTarget ?? providerPressed
      if (mouseUpTarget) dispatchMouse(mouseUpTarget, 'mouseup', x, y, 0)
      if (shouldActivateForwardedPointerTarget(providerPressed, releaseTarget)) {
        const clickable = releaseTarget
        if (clickable instanceof HTMLSelectElement) {
          // WinForms WebView2 cannot open a native select popup from an
          // untrusted forwarded click. Deterministic cycling keeps every
          // choice usable with the game cursor; controller arrows still offer
          // bidirectional selection.
          focusElement(clickable)
          cycleSelect(clickable, 1)
        } else {
          clickable?.click()
        }
      }
      providerPressed = null
    }
    if (input.wheelDelta !== 0) {
      const select = target?.closest('select')
      if (select instanceof HTMLSelectElement) cycleSelect(select, input.wheelDelta > 0 ? -1 : 1)
      else if (!dispatchWheel(target, input.wheelDelta)) scrollTarget(target, input.wheelDelta)
    }
  })

  const removeBootstrapPointer = bridge.on<unknown>('input.bootstrapPointer', (raw) => {
    const input = parseWindowedPointerInput(raw)
    if (!input || input.wheelDelta !== 0) {
      resetBootstrapPointer()
      return
    }
    const x = Math.round(input.x * Math.max(1, window.innerWidth - 1))
    const y = Math.round(input.y * Math.max(1, window.innerHeight - 1))
    showCursor(x, y)
    const target = bootstrapTargetAt(x, y)
    if (target !== bootstrapHovered) {
      if (bootstrapHovered) dispatchMouse(bootstrapHovered, 'mouseout', x, y, 0)
      if (target) dispatchMouse(target, 'mouseover', x, y, 0)
      bootstrapHovered = target
    }
    if (target) dispatchMouse(target, 'mousemove', x, y, bootstrapPressed ? 1 : 0)
    if (input.pressed) {
      bootstrapPressed = target
      if (target) {
        focusElement(target)
        dispatchMouse(target, 'mousedown', x, y, 1)
      }
    }
    if (input.released) {
      const pressedTarget = bootstrapPressed
      if (pressedTarget) dispatchMouse(pressedTarget, 'mouseup', x, y, 0)
      if (pressedTarget && target === pressedTarget) pressedTarget.click()
      bootstrapPressed = null
    }
  })

  const removeBootstrapReset = bridge.on<unknown>('input.bootstrapPointerReset', () => {
    resetBootstrapPointer()
  })
  const removeProviderReset = bridge.on<unknown>('input.pointerReset', () => {
    resetProviderPointer()
  })
  const removeProviderBoundary = bridge.on<unknown>('host.provider', (raw) => {
    const provider = parseHostProvider(raw)
    if (!provider || provider.sessionGeneration < providerSessionGeneration) return
    const providerSessionChanged = provider.sessionGeneration > providerSessionGeneration
    providerSessionGeneration = provider.sessionGeneration
    if (providerSessionChanged || !provider.connected) revokeProviderInput()
    resetProviderPointer()
    if (provider.connected) resetBootstrapPointer()
  })
  const removeSurfaceBoundary = bridge.on<unknown>('host.surface', (raw) => {
    const mode = typeof raw === 'string'
      ? raw
      : record(raw) && typeof raw.mode === 'string'
        ? raw.mode
        : null
    if (mode !== null && mode !== 'none') revokeProviderInput()
    resetProviderPointer()
    resetBootstrapPointer()
  })

  const removeKeyboard = bridge.on<unknown>('input.keyboard', (raw) => {
    if (!isProviderInputActive()) return
    const input = parseWindowedKeyboardInput(raw)
    if (!input) return
    const active = document.activeElement
    if (active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement) {
      editText(active, input)
    }
  })

  return () => {
    removePointer()
    removeProviderReset()
    removeBootstrapPointer()
    removeBootstrapReset()
    removeProviderBoundary()
    removeSurfaceBoundary()
    removeKeyboard()
    removeProviderGateReset()
    resetProviderPointer()
    resetBootstrapPointer()
    cursor?.remove()
  }
}
