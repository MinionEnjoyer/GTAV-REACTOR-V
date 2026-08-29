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
  OverlayVisibility,
  RuntimeDescription,
  RuntimeHandshake,
  RuntimeHandshakeRequest,
  RuntimeLifecycleEvent,
} from './types'

export interface ManagedEventSubscription extends EventSubscription {
  unsubscribe(options?: InvokeOptions): Promise<boolean>
}

export class ReactorVApi {
  readonly runtime = {
    handshake: (request: RuntimeHandshakeRequest = {}, options?: InvokeOptions) =>
      this.bridge.invoke<RuntimeHandshake>('runtime.handshake', { ...request }, options),
    describe: (options?: InvokeOptions) =>
      this.bridge.invoke<RuntimeDescription>('runtime.describe', {}, options),
  }

  readonly overlay = {
    setVisibility: (visibility: OverlayVisibility, options?: InvokeOptions) =>
      this.bridge.invoke<OverlayState>('overlay.setVisibility', { visibility }, options),
    setInputMode: (mode: OverlayInputMode, options?: InvokeOptions) =>
      this.bridge.invoke<OverlayState>('overlay.setInputMode', { mode }, options),
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
      let subscription: EventSubscription
      try {
        subscription = await this.bridge.invoke<EventSubscription>('events.subscribe', { ...request }, options)
      } catch (error) {
        stops.forEach((stop) => stop())
        throw error
      }
      let active = true

      return {
        ...subscription,
        unsubscribe: async (unsubscribeOptions?: InvokeOptions) => {
          if (!active) return false
          stops.forEach((stop) => stop())
          const result = await this.bridge.invoke<{ removed: boolean }>(
            'events.unsubscribe',
            { subscriptionId: subscription.id },
            unsubscribeOptions,
          )
          active = false
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
