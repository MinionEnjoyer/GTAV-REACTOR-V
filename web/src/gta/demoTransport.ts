import type {
  ExtensionDescriptor,
  GameState,
  MenuDescriptor,
  OverlayState,
  RuntimeDescription,
  RuntimeHandshake,
  WebViewTransport,
} from './types'

type MessageListener = (event: { data: unknown }) => void

interface RequestMessage {
  kind: 'request'
  id: string
  method: string
  params: Record<string, unknown>
}

interface CancelMessage {
  kind: 'cancel'
  id: string
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isRequest(value: unknown): value is RequestMessage {
  return isRecord(value) && value.kind === 'request' && typeof value.id === 'string' &&
    typeof value.method === 'string' && isRecord(value.params)
}

function isCancel(value: unknown): value is CancelMessage {
  return isRecord(value) && value.kind === 'cancel' && typeof value.id === 'string'
}

const initialState: GameState = {
  gameTime: 42_420,
  paused: false,
  player: {
    health: 200,
    maxHealth: 200,
    armor: 72,
    wantedLevel: 2,
    invincible: false,
    position: { x: -75.3, y: -818.9, z: 326.2 },
    heading: 182.4,
  },
  vehicle: { handle: 1042, displayName: 'Buffalo STX', speedMps: 18.6, engineHealth: 842 },
  world: { time: '21:48', weather: 'Clear' },
}

const runtime: RuntimeHandshake = {
  apiVersion: 2,
  supportedApiVersions: [1, 2],
  sessionId: 'browser-demo',
  runtime: 'REACTOR V browser demo',
  runtimeVersion: '0.2.0',
  renderer: 'Browser preview',
  edition: 'Enhanced',
  capabilities: [
    'events.lifecycle', 'events.subscriptions', 'extension.actions', 'extension.discovery',
    'extension.events', 'game.actions', 'game.state', 'input.semantic', 'menu.actions',
    'menu.discovery', 'overlay.input', 'overlay.visibility', 'runtime.discovery',
  ],
  extensionApiVersion: 1,
  dependencies: [
    { id: 'scripthookv', name: 'Script Hook V', loaded: true, required: true, detail: 'Preview status' },
    { id: 'scripthookdotnet', name: 'ScriptHookVDotNet Enhanced', loaded: true, required: true, detail: 'Preview status' },
    { id: 'allin1', name: 'ALLIN1 client', loaded: true, required: true, detail: 'Preview status' },
    { id: 'lemonui', name: 'LemonUI', loaded: true, required: true, detail: 'Preview status' },
    { id: 'compositor', name: 'REACTOR V compositor', loaded: true, required: true, detail: 'Preview status' },
    { id: 'chromium', name: 'Chromium runtime', loaded: true, required: true, detail: 'Preview status' },
  ],
}

const runtimeDescription: RuntimeDescription = {
  apiVersion: 2,
  extensionApiVersion: 1,
  sessionId: runtime.sessionId,
  capabilities: runtime.capabilities,
  methods: [
    { method: 'runtime.handshake' }, { method: 'runtime.describe' },
    { method: 'overlay.setVisibility', capability: 'overlay-input' },
    { method: 'overlay.setInputMode', capability: 'overlay-input' },
    { method: 'extensions.list', capability: 'extensions' }, { method: 'extensions.get', capability: 'extensions' },
    { method: 'extensions.invoke', capability: 'extensions', confirmed: true, idempotency: 'optional' },
    { method: 'menu.list', capability: 'menus' }, { method: 'menu.get', capability: 'menus' },
    { method: 'menu.invoke', capability: 'menus', confirmed: true, idempotency: 'optional' },
    { method: 'events.subscribe', capability: 'events' }, { method: 'events.unsubscribe', capability: 'events' },
  ],
  events: [
    { event: 'runtime.lifecycle', replay: true }, { event: 'input.action' },
  ],
  limits: { requestBytes: 65_536, queueDepth: 256, requestsPerFrame: 32, subscriptions: 128 },
}

const extensions: ExtensionDescriptor[] = [{
  id: 'allin1.online', name: 'ALLIN1 Online Content', version: '0.6.0',
  description: 'GBAY, owned vehicles, garages, and customization.',
  capabilities: ['gbay.catalog', 'gbay.purchase', 'vehicles.storage'], extensionApiVersion: 1,
  actions: [
    { id: 'gbay.open', label: 'Open GBAY', description: '', risk: 'gameplay', requiresConfirmation: false, allowAdditionalParameters: false, parameters: [] },
    {
      id: 'gbay.purchase', label: 'Purchase listing', description: '', risk: 'persistent', requiresConfirmation: true,
      allowAdditionalParameters: false,
      parameters: [{ name: 'listingId', type: 'string', required: true, maximumLength: 64 }],
    },
    {
      id: 'traffic.setEnabled', label: 'DLC traffic', description: '', risk: 'persistent', requiresConfirmation: true,
      allowAdditionalParameters: false, parameters: [{ name: 'value', type: 'boolean', required: true }],
    },
    {
      id: 'gbay.delivery', label: 'Delivery point', description: '', risk: 'gameplay', requiresConfirmation: false,
      allowAdditionalParameters: false,
      parameters: [{ name: 'value', type: 'string', required: true, maximumLength: 32, allowedValues: ['garage', 'helipad', 'harbour'] }],
    },
    {
      id: 'weapons.search', label: 'Search weapons', description: '', risk: 'read', requiresConfirmation: false,
      allowAdditionalParameters: false,
      parameters: [{ name: 'value', type: 'string', required: true, maximumLength: 80 }],
    },
    { id: 'weapons.equip', label: 'Equip weapon', description: '', risk: 'gameplay', requiresConfirmation: false, allowAdditionalParameters: true, parameters: [] },
  ],
  events: [{ id: 'catalog.changed', description: 'GBAY catalog changed.', maximumPayloadBytes: 16_384 }],
  menuIds: ['gbay', 'weapons.customize'],
}]

const menus: MenuDescriptor[] = [
  {
    extensionId: 'allin1.online', id: 'gbay', label: 'GBAY', description: 'Story Mode marketplace', icon: 'gbay', order: 10,
    nodes: [
      { id: 'purchase', kind: 'action', label: 'Purchase Weapons', description: '', enabled: true, visible: true, actionId: 'gbay.open' },
      { id: 'customize', kind: 'submenu', label: 'Customize Weapons', description: '', enabled: true, visible: true, menuId: 'weapons.customize' },
      { id: 'traffic', kind: 'toggle', label: 'DLC traffic', description: '', enabled: true, visible: true, actionId: 'traffic.setEnabled', value: true },
      {
        id: 'delivery', kind: 'choice', label: 'Delivery point', description: '', enabled: true, visible: true,
        actionId: 'gbay.delivery', selectedId: 'garage', options: [
          { id: 'garage', label: 'Garage' }, { id: 'helipad', label: 'Helipad' }, { id: 'harbour', label: 'Harbour' },
        ],
      },
      { id: 'status', kind: 'status', label: 'Save policy', description: '', enabled: true, visible: true, value: 'Commits with the next story save', tone: 'neutral' },
    ],
  },
  {
    extensionId: 'allin1.online', id: 'weapons.customize', label: 'Customize Weapons', description: '', icon: 'wrench', order: 20,
    nodes: [
      { id: 'weaponSearch', kind: 'search', label: 'Find a weapon', description: '', enabled: true, visible: true, actionId: 'weapons.search', value: '', placeholder: 'Search', maximumLength: 80 },
      { id: 'equip', kind: 'action', label: 'Equip selected weapon', description: '', enabled: true, visible: true, actionId: 'weapons.equip' },
    ],
  },
]

function countNodes(nodes: MenuDescriptor['nodes']): number {
  return nodes.reduce((total, node) => {
    if (node.kind === 'list' || node.kind === 'grid') return total + 1 + countNodes(node.nodes)
    if (node.kind === 'tabs') return total + 1 + node.tabs.reduce((sum, tab) => sum + countNodes(tab.nodes), 0)
    return total + 1
  }, 0)
}

export class DemoTransport implements WebViewTransport {
  private readonly listeners = new Set<MessageListener>()
  private readonly scheduled = new Map<string, ReturnType<typeof setTimeout>>()
  private readonly subscriptions = new Map<string, string[]>()
  private state = structuredClone(initialState)
  private overlay: OverlayState = { visible: false, inputMode: 'game' }
  private subscriptionSequence = 0

  postMessage(message: unknown): void {
    if (isCancel(message)) {
      const timer = this.scheduled.get(message.id)
      if (timer) globalThis.clearTimeout(timer)
      this.scheduled.delete(message.id)
      return
    }
    if (!isRequest(message)) return
    const timer = globalThis.setTimeout(() => {
      this.scheduled.delete(message.id)
      this.respond(message)
    }, 45)
    this.scheduled.set(message.id, timer)
  }

  addEventListener(_type: 'message', listener: MessageListener): void { this.listeners.add(listener) }
  removeEventListener(_type: 'message', listener: MessageListener): void { this.listeners.delete(listener) }
  publish(event: string, payload: unknown): void { this.emit({ kind: 'event', event, payload }) }

  private respond(request: RequestMessage): void {
    try {
      const result = this.handle(request.method, request.params)
      this.emit({ kind: 'response', id: request.id, result })
      this.emit({ kind: 'event', event: 'game.state', payload: this.state })
    } catch (error) {
      this.emit({ kind: 'response', id: request.id, error: { code: 'demo_error', message: error instanceof Error ? error.message : 'Demo request failed.' } })
    }
  }

  private handle(method: string, params: Record<string, unknown>): unknown {
    switch (method) {
      case 'runtime.handshake': return runtime
      case 'runtime.describe': return runtimeDescription
      case 'overlay.setVisibility':
        this.overlay.visible = params.visibility === 'toggle' ? !this.overlay.visible : params.visibility === 'visible'
        return this.overlay
      case 'overlay.setInputMode':
        this.overlay.inputMode = String(params.mode) as OverlayState['inputMode']
        return this.overlay
      case 'extensions.list':
        return {
          total: extensions.length,
          items: extensions.map((extension) => ({
            id: extension.id, name: extension.name, version: extension.version,
            extensionApiVersion: extension.extensionApiVersion, actionCount: extension.actions.length,
            eventCount: extension.events.length, menuCount: extension.menuIds.length,
          })),
        }
      case 'extensions.get': {
        const extension = extensions.find((candidate) => candidate.id === params.extensionId)
        if (!extension) throw new Error('Extension not found.')
        return extension
      }
      case 'extensions.invoke':
        return { succeeded: true, confirmationRequired: false, replayed: false, value: { invoked: params.actionId } }
      case 'menu.list': {
        const selected = params.extensionId ? menus.filter((menu) => menu.extensionId === params.extensionId) : menus
        return {
          total: selected.length,
          truncated: false,
          items: selected.map((menu) => ({
            extensionId: menu.extensionId, id: menu.id, label: menu.label, order: menu.order,
            nodeCount: countNodes(menu.nodes),
          })),
        }
      }
      case 'menu.get': {
        const menu = menus.find((candidate) => candidate.extensionId === params.extensionId && candidate.id === params.menuId)
        if (!menu) throw new Error('Menu not found.')
        return menu
      }
      case 'menu.invoke':
        return { succeeded: true, confirmationRequired: false, replayed: false, value: { nodeId: params.nodeId, value: params.value } }
      case 'events.subscribe': {
        const events = Array.isArray(params.events) ? params.events.filter((event): event is string => typeof event === 'string') : []
        const id = `demo-sub-${++this.subscriptionSequence}`
        this.subscriptions.set(id, events)
        return { id, events, cadenceMs: Number(params.cadenceMs ?? 100) }
      }
      case 'events.unsubscribe': return { removed: this.subscriptions.delete(String(params.subscriptionId)) }
      case 'overlay.ready': return runtime
      case 'overlay.close': this.overlay.visible = false; return { visible: false }
      case 'game.getState': return this.state
      case 'player.heal':
        this.state.player.health = this.state.player.maxHealth; this.state.player.armor = 100
        return { health: this.state.player.health, armor: 100 }
      case 'player.setInvincible': this.state.player.invincible = Boolean(params.enabled); return { enabled: this.state.player.invincible }
      case 'player.setWantedLevel': this.state.player.wantedLevel = Number(params.level); return { level: this.state.player.wantedLevel }
      case 'player.teleport':
        this.state.player.position = { x: Number(params.x), y: Number(params.y), z: Number(params.z) }
        return { position: this.state.player.position }
      case 'vehicle.repair':
        if (!this.state.vehicle) throw new Error('The player is not in a vehicle.')
        this.state.vehicle.engineHealth = 1000; return { repaired: true, engineHealth: 1000 }
      case 'vehicle.spawn':
        this.state.vehicle = { handle: 2048, displayName: String(params.model).toUpperCase(), speedMps: 0, engineHealth: 1000 }
        return this.state.vehicle
      case 'world.setTime':
        this.state.world.time = `${String(params.hour).padStart(2, '0')}:${String(params.minute).padStart(2, '0')}`
        return { time: this.state.world.time }
      case 'world.setWeather': this.state.world.weather = String(params.weather); return { weather: this.state.world.weather }
      case 'ui.notify': return { shown: true }
      default: throw new Error(`Unknown method '${method}'.`)
    }
  }

  private emit(data: unknown): void { this.listeners.forEach((listener) => listener({ data })) }
}
