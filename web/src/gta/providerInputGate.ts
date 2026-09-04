type ProviderInputResetListener = () => void

let preparedPresentationId: string | null = null
let interactivePresentationId: string | null = null
const resetListeners = new Set<ProviderInputResetListener>()

function notifyReset(): void {
  for (const listener of resetListeners) listener()
}

/**
 * Closes provider input synchronously while a new typed presentation prepares.
 * An exact duplicate event retains its established lease; a provider loss
 * revokes that lease separately before any replay can be accepted.
 */
export function prepareProviderInput(presentationId: string): boolean {
  if (preparedPresentationId === presentationId) return false
  preparedPresentationId = presentationId
  interactivePresentationId = null
  notifyReset()
  return true
}

/** Opens input only for the exact presentation that currently owns the gate. */
export function activateProviderInput(presentationId: string): boolean {
  if (preparedPresentationId !== presentationId) return false
  interactivePresentationId = presentationId
  return true
}

/**
 * Revokes either the exact presentation or, when omitted, the entire provider
 * lease. A stale dismissal must never close a newer presentation.
 */
export function revokeProviderInput(presentationId?: string): boolean {
  if (presentationId !== undefined && preparedPresentationId !== presentationId) return false
  const changed = preparedPresentationId !== null || interactivePresentationId !== null
  preparedPresentationId = null
  interactivePresentationId = null
  notifyReset()
  return changed
}

export function isProviderInputActive(presentationId?: string): boolean {
  if (interactivePresentationId === null || interactivePresentationId !== preparedPresentationId) return false
  return presentationId === undefined || presentationId === interactivePresentationId
}

export function onProviderInputReset(listener: ProviderInputResetListener): () => void {
  resetListeners.add(listener)
  return () => resetListeners.delete(listener)
}
