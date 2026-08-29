import { DemoTransport } from './demoTransport'
import type { BridgeEvent, BridgeResponse, CefSharpBridge, GameState, RuntimeStatus, Vector3, WebViewTransport } from './types'

declare global {
  interface Window {
    chrome?: {
      webview?: WebViewTransport
    }
    CefSharp?: CefSharpBridge
  }
}

class CefTransport implements WebViewTransport {
  private readonly wrappers = new Map<(event: { data: unknown }) => void, EventListener>()

  postMessage(message: unknown): void {
    window.CefSharp!.PostMessage(message)
  }

  addEventListener(_type: 'message', listener: (event: { data: unknown }) => void): void {
    const wrapper: EventListener = (event) => listener({ data: (event as CustomEvent).detail })
    this.wrappers.set(listener, wrapper)
    window.addEventListener('ragewebui:message', wrapper)
  }

  removeEventListener(_type: 'message', listener: (event: { data: unknown }) => void): void {
    const wrapper = this.wrappers.get(listener)
    if (wrapper) window.removeEventListener('ragewebui:message', wrapper)
    this.wrappers.delete(listener)
  }
}

interface PendingRequest {
  resolve(value: unknown): void
  reject(reason: Error): void
  timeout: ReturnType<typeof setTimeout>
  removeAbortListener?: () => void
}

type EventListener<T = unknown> = (payload: T) => void

export interface InvokeOptions {
  timeoutMs?: number
  deadlineMs?: number
  idempotencyKey?: string
  confirmed?: boolean
  signal?: AbortSignal
}

const DEFAULT_TIMEOUT_MS = 5000
const MAX_TIMEOUT_MS = 300_000
const MAX_DEADLINE_MS = 120_000
const idempotencyKeyPattern = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/
const methodOrEventPattern = /^[a-z][A-Za-z0-9]*(\.[a-z][A-Za-z0-9]*)+$/
const requestIdPattern = /^[A-Za-z0-9_-]{1,64}$/
const errorCodePattern = /^[a-z][a-z0-9._-]{0,63}$/

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isBridgeResponse(value: unknown): value is BridgeResponse {
  if (!isRecord(value) || value.kind !== 'response' || typeof value.id !== 'string' ||
    !requestIdPattern.test(value.id)) {
    return false
  }
  if (value.protocolVersion !== undefined &&
    (!Number.isInteger(value.protocolVersion) || (value.protocolVersion as number) < 1)) return false
  const hasResult = Object.prototype.hasOwnProperty.call(value, 'result')
  const hasError = Object.prototype.hasOwnProperty.call(value, 'error')
  if (hasResult === hasError) return false
  if (!hasError) return true
  return isRecord(value.error) && typeof value.error.code === 'string' && errorCodePattern.test(value.error.code) &&
    typeof value.error.message === 'string' && value.error.message.length > 0 && value.error.message.length <= 2048
}

function isBridgeEvent(value: unknown): value is BridgeEvent {
  return isRecord(value) && value.kind === 'event' && typeof value.event === 'string' &&
    methodOrEventPattern.test(value.event) && Object.prototype.hasOwnProperty.call(value, 'payload') &&
    (value.protocolVersion === undefined || (Number.isInteger(value.protocolVersion) && (value.protocolVersion as number) >= 1))
}

function normalizeInvokeOptions(value: number | InvokeOptions): InvokeOptions & Required<Pick<InvokeOptions, 'timeoutMs'>> {
  if (typeof value !== 'number' && !isRecord(value)) {
    throw new GtaBridgeError('invalid_options', 'Invoke options must be a timeout number or an options object.')
  }
  const options = (typeof value === 'number' ? { timeoutMs: value } : value) as InvokeOptions
  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS
  if (!Number.isFinite(timeoutMs) || !Number.isInteger(timeoutMs) || timeoutMs <= 0 || timeoutMs > MAX_TIMEOUT_MS) {
    throw new GtaBridgeError(
      'invalid_timeout',
      `timeoutMs must be a whole number from 1 through ${MAX_TIMEOUT_MS}.`,
    )
  }
  if (options.deadlineMs !== undefined &&
    (!Number.isFinite(options.deadlineMs) || !Number.isInteger(options.deadlineMs) ||
      options.deadlineMs <= 0 || options.deadlineMs > MAX_DEADLINE_MS)) {
    throw new GtaBridgeError('invalid_deadline', `deadlineMs must be a whole number from 1 through ${MAX_DEADLINE_MS}.`)
  }
  if (options.idempotencyKey !== undefined &&
    (typeof options.idempotencyKey !== 'string' || !idempotencyKeyPattern.test(options.idempotencyKey))) {
    throw new GtaBridgeError('invalid_idempotency_key', 'idempotencyKey must be a 1-128 character URL-safe string.')
  }
  if (options.confirmed !== undefined && typeof options.confirmed !== 'boolean') {
    throw new GtaBridgeError('invalid_confirmation', 'confirmed must be a boolean.')
  }
  if (options.signal !== undefined &&
    (typeof options.signal !== 'object' || typeof options.signal.aborted !== 'boolean' ||
      typeof options.signal.addEventListener !== 'function' || typeof options.signal.removeEventListener !== 'function')) {
    throw new GtaBridgeError('invalid_abort_signal', 'signal must be an AbortSignal.')
  }
  return { ...options, timeoutMs }
}

export class GtaBridgeError extends Error {
  constructor(
    public readonly code: string,
    message: string,
  ) {
    super(message)
    this.name = 'GtaBridgeError'
  }
}

export class GtaBridge {
  private sequence = 0
  private destroyed = false
  private readonly pending = new Map<string, PendingRequest>()
  private readonly listeners = new Map<string, Set<EventListener>>()
  private readonly onMessageBound = (event: { data: unknown }) => this.onMessage(event.data)

  constructor(
    private readonly transport: WebViewTransport,
    public readonly isNative: boolean,
  ) {
    transport.addEventListener('message', this.onMessageBound)
  }

  invoke<T>(
    method: string,
    params: Record<string, unknown> = {},
    timeoutOrOptions: number | InvokeOptions = DEFAULT_TIMEOUT_MS,
  ): Promise<T> {
    if (this.destroyed) {
      return Promise.reject(new GtaBridgeError('disposed', 'The GTA bridge was disposed.'))
    }
    if (!methodOrEventPattern.test(method) || method.length > 96) {
      return Promise.reject(new GtaBridgeError('invalid_method', 'GTA method names must be bounded dot-separated identifiers.'))
    }
    if (!isRecord(params)) {
      return Promise.reject(new GtaBridgeError('invalid_params', 'GTA request params must be an object.'))
    }

    let options: ReturnType<typeof normalizeInvokeOptions>
    try {
      options = normalizeInvokeOptions(timeoutOrOptions)
    } catch (error) {
      return Promise.reject(error)
    }

    if (options.signal?.aborted) {
      return Promise.reject(new GtaBridgeError('aborted', `GTA request '${method}' was cancelled.`))
    }

    const id = `web-${Date.now().toString(36)}-${(++this.sequence).toString(36)}`
    return new Promise<T>((resolve, reject) => {
      const timeout = globalThis.setTimeout(() => {
        const pending = this.pending.get(id)
        if (!pending) return
        this.pending.delete(id)
        pending.removeAbortListener?.()
        this.postCancel(id, 'timeout')
        reject(new GtaBridgeError('timeout', `GTA did not answer '${method}' within ${options.timeoutMs} ms.`))
      }, options.timeoutMs)

      const onAbort = () => {
        const pending = this.pending.get(id)
        if (!pending) return
        this.pending.delete(id)
        globalThis.clearTimeout(pending.timeout)
        pending.removeAbortListener?.()
        reject(new GtaBridgeError('aborted', `GTA request '${method}' was cancelled.`))
        this.postCancel(id, 'abort_signal')
      }

      const removeAbortListener = options.signal
        ? () => options.signal?.removeEventListener('abort', onAbort)
        : undefined
      options.signal?.addEventListener('abort', onAbort, { once: true })

      this.pending.set(id, { resolve: resolve as (value: unknown) => void, reject, timeout, removeAbortListener })
      try {
        this.transport.postMessage({
          kind: 'request',
          id,
          method,
          params,
          protocolVersion: 2,
          minimumProtocolVersion: 1,
          ...(options.deadlineMs === undefined ? {} : { deadlineMs: options.deadlineMs }),
          ...(options.idempotencyKey === undefined ? {} : { idempotencyKey: options.idempotencyKey }),
          ...(options.confirmed === undefined ? {} : { confirmed: options.confirmed }),
        })
      } catch (error) {
        this.pending.delete(id)
        globalThis.clearTimeout(timeout)
        removeAbortListener?.()
        reject(new GtaBridgeError('transport_error', error instanceof Error ? error.message : 'GTA transport failed.'))
      }
    })
  }

  on<T>(eventName: string, listener: EventListener<T>): () => void {
    if (this.destroyed) throw new GtaBridgeError('disposed', 'The GTA bridge was disposed.')
    if (!methodOrEventPattern.test(eventName) || eventName.length > 96) {
      throw new GtaBridgeError('invalid_event', 'GTA event names must be bounded dot-separated identifiers.')
    }
    const listeners = this.listeners.get(eventName) ?? new Set<EventListener>()
    listeners.add(listener as EventListener)
    this.listeners.set(eventName, listeners)
    return () => listeners.delete(listener as EventListener)
  }

  destroy(): void {
    if (this.destroyed) return
    this.destroyed = true
    this.transport.removeEventListener('message', this.onMessageBound)
    this.pending.forEach((request, id) => {
      globalThis.clearTimeout(request.timeout)
      request.removeAbortListener?.()
      this.postCancel(id, 'bridge_disposed')
      request.reject(new GtaBridgeError('disposed', 'The GTA bridge was disposed.'))
    })
    this.pending.clear()
    this.listeners.clear()
  }

  private onMessage(data: unknown): void {
    if (isBridgeResponse(data)) {
      const pending = this.pending.get(data.id)
      if (!pending) return
      this.pending.delete(data.id)
      globalThis.clearTimeout(pending.timeout)
      pending.removeAbortListener?.()
      if (data.error) {
        pending.reject(new GtaBridgeError(data.error.code, data.error.message))
      } else {
        pending.resolve(data.result)
      }
      return
    }

    if (isBridgeEvent(data)) {
      this.listeners.get(data.event)?.forEach((listener) => {
        try {
          listener(data.payload)
        } catch (error) {
          globalThis.console?.error?.(`ReactorV listener for '${data.event}' failed.`, error)
        }
      })
    }
  }

  private postCancel(id: string, reason: 'abort_signal' | 'timeout' | 'bridge_disposed'): void {
    try {
      this.transport.postMessage({
        kind: 'cancel',
        id,
        protocolVersion: 2,
        minimumProtocolVersion: 1,
        reason,
      })
    } catch {
      // The local promise is already settled; a closed host needs no follow-up.
    }
  }
}

const webViewTransport = typeof window === 'undefined' ? undefined : window.chrome?.webview
const cefTransport = typeof window !== 'undefined' && window.CefSharp?.PostMessage ? new CefTransport() : undefined
const nativeTransport = webViewTransport ?? cefTransport
export const bridge = new GtaBridge(nativeTransport ?? new DemoTransport(), Boolean(nativeTransport))

export const gta = {
  ready: () => bridge.invoke<RuntimeStatus>('overlay.ready'),
  closeOverlay: () => bridge.invoke<{ visible: false }>('overlay.close'),
  getState: () => bridge.invoke<GameState>('game.getState'),
  notify: (message: string) => bridge.invoke<{ shown: boolean }>('ui.notify', { message }),
  player: {
    heal: () => bridge.invoke<{ health: number; armor: number }>('player.heal'),
    setInvincible: (enabled: boolean) =>
      bridge.invoke<{ enabled: boolean }>('player.setInvincible', { enabled }),
    setWantedLevel: (level: number) =>
      bridge.invoke<{ level: number }>('player.setWantedLevel', { level }),
    teleport: (position: Vector3, keepVehicle = true) =>
      bridge.invoke<{ position: Vector3 }>('player.teleport', { ...position, keepVehicle }),
  },
  vehicle: {
    repair: () => bridge.invoke<{ repaired: boolean; engineHealth: number }>('vehicle.repair'),
    spawn: (model: string, warpIntoVehicle = true) =>
      bridge.invoke<{ handle: number; displayName: string }>('vehicle.spawn', { model, warpIntoVehicle }, 8000),
  },
  world: {
    setTime: (hour: number, minute: number) =>
      bridge.invoke<{ time: string }>('world.setTime', { hour, minute }),
    setWeather: (weather: string) =>
      bridge.invoke<{ weather: string }>('world.setWeather', { weather }),
  },
}
