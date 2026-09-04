import type { InvokeOptions, GtaBridge } from './bridge'
import type {
  EventSubscription,
  EventSubscriptionRequest,
  ExtensionDescriptor,
  ExtensionListResult,
  ExtensionInvocation,
  ExtensionInvocationResult,
  InputActionEvent,
  MenuDescriptor,
  MenuListResult,
  MenuInvocation,
  MenuInvocationResult,
  OverlayInputMode,
  OverlayState,
  OverlayStateRequest,
  OverlayVisibility,
  PresentationReadyResult,
  RuntimeDescription,
  RuntimeHandshake,
  RuntimeHandshakeRequest,
  RuntimeLifecycleEvent,
  StartupStatus,
} from './types'

export interface ManagedEventSubscription extends EventSubscription {
  disposeLocal(): void
  unsubscribe(options?: InvokeOptions): Promise<boolean>
}

export type MenuAudioCue = 'navigate' | 'select' | 'back' | 'error'

export class ReactorVApi {
  readonly ui = {
    playMenuCue: (cue: MenuAudioCue, options?: InvokeOptions) =>
      this.bridge.invoke<{ played: boolean; cue: MenuAudioCue }>('ui.playMenuCue', { cue }, options),
  }

  readonly startup = {
    getStatus: (options?: InvokeOptions) =>
      this.bridge.invoke<StartupStatus>('startup.getStatus', {}, options),
  }

  readonly runtime = {
    handshake: (request: RuntimeHandshakeRequest = {}, options?: InvokeOptions) =>
      this.bridge.invoke<RuntimeHandshake>('runtime.handshake', { ...request }, options),
    describe: (options?: InvokeOptions) =>
      this.bridge.invoke<RuntimeDescription>('runtime.describe', {}, options),
  }

  readonly overlay = {
    setState: (state: OverlayStateRequest, options?: InvokeOptions) =>
      this.bridge.invoke<OverlayState>('overlay.setState', { ...state }, options),
    setVisibility: (visibility: OverlayVisibility, options?: InvokeOptions) =>
      this.bridge.invoke<OverlayState>('overlay.setVisibility', { visibility }, options),
    setInputMode: (mode: OverlayInputMode, options?: InvokeOptions) =>
      this.bridge.invoke<OverlayState>('overlay.setInputMode', { mode }, options),
    presentationReady: (presentationId: string, options?: InvokeOptions) =>
      this.bridge.invoke<PresentationReadyResult>(
        'overlay.presentationReady',
        { presentationId },
        options,
      ),
  }

  readonly extensions = {
    list: (options?: InvokeOptions) =>
      this.bridge.invoke<ExtensionListResult>('extensions.list', {}, options),
    get: (extensionId: string, options?: InvokeOptions) =>
      this.bridge.invoke<ExtensionDescriptor>('extensions.get', { extensionId }, options),
    invoke: (invocation: ExtensionInvocation, options?: InvokeOptions) =>
      this.bridge.invoke<ExtensionInvocationResult>(
        'extensions.invoke',
        { ...invocation },
        this.invocationOptions(invocation, options),
      ),
  }

  readonly menu = {
    list: (extensionId?: string, options?: InvokeOptions) =>
      this.bridge.invoke<MenuListResult>('menu.list', extensionId ? { extensionId } : {}, options),
    get: (extensionId: string, menuId: string, options?: InvokeOptions) =>
      this.bridge.invoke<MenuDescriptor>('menu.get', { extensionId, menuId }, options),
    invoke: (invocation: MenuInvocation, options?: InvokeOptions) =>
      this.bridge.invoke<MenuInvocationResult>(
        'menu.invoke',
        { ...invocation },
        this.invocationOptions(invocation, options),
      ),
  }

  readonly events = {
    subscribe: async (
      request: EventSubscriptionRequest,
      listener?: (eventName: string, payload: unknown) => void,
      options?: InvokeOptions,
    ): Promise<ManagedEventSubscription> => {
      const stops = listener
        ? [...new Set(request.events)].map((eventName) =>
            this.bridge.on(eventName, (payload) => listener(eventName, payload)))
        : []
      let localActive = true
      const disposeLocal = () => {
        if (!localActive) return
        localActive = false
        stops.forEach((stop) => stop())
      }
      let subscription: EventSubscription
      try {
        subscription = await this.bridge.invoke<EventSubscription>('events.subscribe', { ...request }, options)
      } catch (error) {
        disposeLocal()
        throw error
      }
      let active = true

      return {
        ...subscription,
        disposeLocal,
        unsubscribe: async (unsubscribeOptions?: InvokeOptions) => {
          if (!active) return false
          active = false
          disposeLocal()
          const result = await this.bridge.invoke<{ removed: boolean }>(
            'events.unsubscribe',
            { subscriptionId: subscription.id },
            unsubscribeOptions,
          )
          return result.removed
        },
      }
    },
    unsubscribe: (subscriptionId: string, options?: InvokeOptions) =>
      this.bridge.invoke<{ removed: boolean }>('events.unsubscribe', { subscriptionId }, options),
    on: <T>(eventName: string, listener: (payload: T) => void) => this.bridge.on(eventName, listener),
    onLifecycle: (listener: (payload: RuntimeLifecycleEvent) => void) =>
      this.bridge.on('runtime.lifecycle', listener),
    onInput: (listener: (payload: InputActionEvent) => void) =>
      this.bridge.on('input.action', listener),
  }

  constructor(readonly bridge: GtaBridge) {}

  private invocationOptions(
    invocation: { confirmed?: boolean; idempotencyKey?: string },
    options?: InvokeOptions,
  ): InvokeOptions {
    return {
      ...options,
      ...(invocation.confirmed === undefined ? {} : { confirmed: invocation.confirmed }),
      ...(invocation.idempotencyKey === undefined ? {} : { idempotencyKey: invocation.idempotencyKey }),
    }
  }
}
