export type MenuAudioCue = 'navigate' | 'select' | 'back' | 'error'
export type MenuAudioSource = 'semantic' | 'keyboard' | 'pointer'

export type MenuAudioEmitter = (cue: MenuAudioCue) => void | Promise<unknown>

const duplicateCueWindowMs = 36
const navigationWindowMs = 65
const pointerGestureWindowMs = 90

/**
 * Keeps menu feedback responsive without letting mouse-enter churn or held
 * controller input flood the game-thread bridge. A fast pointer hover followed
 * immediately by its click is treated as one gesture; a deliberate click after
 * the pointer has rested still receives the Select cue.
 */
export class MenuAudioFeedback {
  private readonly lastCueAt = new Map<MenuAudioCue, number>()
  private lastPointerNavigationAt = Number.NEGATIVE_INFINITY

  constructor(
    private readonly emit: MenuAudioEmitter,
    private readonly now: () => number = () => globalThis.performance?.now?.() ?? Date.now(),
  ) {}

  play(cue: MenuAudioCue, source: MenuAudioSource = 'semantic'): boolean {
    const current = this.now()
    if (!Number.isFinite(current)) return false

    const previous = this.lastCueAt.get(cue) ?? Number.NEGATIVE_INFINITY
    if (current - previous < duplicateCueWindowMs) return false
    if (cue === 'navigate' && current - previous < navigationWindowMs) return false
    if (cue === 'select' && source === 'pointer' &&
      current - this.lastPointerNavigationAt < pointerGestureWindowMs) return false

    this.lastCueAt.set(cue, current)
    if (cue === 'navigate' && source === 'pointer') this.lastPointerNavigationAt = current

    try {
      const result = this.emit(cue)
      if (result && typeof (result as Promise<unknown>).catch === 'function') {
        void (result as Promise<unknown>).catch(() => {})
      }
    } catch {
      // Audio is non-essential feedback. A missing/older host must never make
      // an otherwise valid menu action fail.
    }
    return true
  }
}
