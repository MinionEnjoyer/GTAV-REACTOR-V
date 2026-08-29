export interface Vector3 {
  x: number
  y: number
  z: number
}

export type JsonPrimitive = string | number | boolean | null
export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue }
export type JsonObject = { [key: string]: JsonValue }

export interface PlayerState {
  health: number
  maxHealth: number
  armor: number
  wantedLevel: number
  invincible: boolean
  position: Vector3
  heading: number
}

export interface VehicleState {
  handle: number
  displayName: string
  speedMps: number
  engineHealth: number
}

export interface GameState {
  gameTime: number
  paused: boolean
  player: PlayerState
  vehicle: VehicleState | null
  world: {
    time: string
    weather: string
  }
}

export interface DependencyStatus {
  id: string
  name: string
  loaded: boolean
  required: boolean
  detail: string
}

export interface RuntimeStatus {
  apiVersion: number
  runtime: string
  renderer: string
  edition: 'Legacy' | 'Enhanced' | string
  dependencies: DependencyStatus[]
}

export interface RuntimeClientIdentity {
  id: string
  name: string
  version: string
}

export interface RuntimeHandshakeRequest {
  apiVersions?: readonly number[]
  client?: RuntimeClientIdentity
  requestedCapabilities?: readonly string[]
}

export interface RuntimeHandshake {
  apiVersion: number
  supportedApiVersions: number[]
  sessionId: string
  runtime: string
  runtimeVersion: string
  renderer: string
  edition: 'Legacy' | 'Enhanced' | string
  capabilities: string[]
  extensionApiVersion: number
  dependencies: DependencyStatus[]
}

export interface RuntimeMethodDescriptor {
  method: string
  capability?: string
  confirmed?: boolean
  idempotency?: 'none' | 'optional' | 'required'
}

export interface RuntimeEventDescriptor {
  event: string
  capability?: string
  replay?: boolean
}

export interface RuntimeDescription {
  apiVersion: number
  extensionApiVersion: number
  sessionId: string
  capabilities: string[]
  methods: RuntimeMethodDescriptor[]
  events: RuntimeEventDescriptor[]
  limits?: {
    requestBytes?: number
    queueDepth?: number
    requestsPerFrame?: number
    subscriptions?: number
  }
}

export type OverlayVisibility = 'hidden' | 'visible' | 'toggle'
export type OverlayInputMode = 'game' | 'menu' | 'pointer' | 'exclusive'

export interface OverlayState {
  visible: boolean
  inputMode: OverlayInputMode
}

export interface ExtensionParameterDescriptor {
  name: string
  type: 'boolean' | 'integer' | 'number' | 'string' | 'object' | 'array'
  required: boolean
  minimum?: number
  maximum?: number
  maximumLength?: number
  allowedValues?: string[]
}

export interface ExtensionActionDescriptor {
  id: string
  label: string
  description: string
  risk: 'read' | 'gameplay' | 'persistent'
  requiresConfirmation: boolean
  allowAdditionalParameters: boolean
  parameters: ExtensionParameterDescriptor[]
}

export interface ExtensionEventDescriptor {
  id: string
  description: string
  maximumPayloadBytes: number
}

export interface ExtensionDescriptor {
  id: string
  name: string
  version: string
  description: string
  capabilities: string[]
  extensionApiVersion: number
  actions: ExtensionActionDescriptor[]
  events: ExtensionEventDescriptor[]
  menuIds: string[]
}

export interface ExtensionSummary {
  id: string
  name: string
  version: string
  extensionApiVersion: number
  actionCount: number
  eventCount: number
  menuCount: number
}

export interface ExtensionListResult {
  total: number
  items: ExtensionSummary[]
}

export interface ConfirmedInvocation {
  confirmed?: boolean
  idempotencyKey?: string
}

export interface ExtensionInvocation extends ConfirmedInvocation {
  extensionId: string
  actionId: string
  parameters?: JsonObject
}

export interface ExtensionInvocationResult {
  succeeded: boolean
  confirmationRequired: boolean
  replayed: boolean
  value?: JsonValue
  error?: {
    code: string
    message: string
  }
}

export interface MenuNodeBase {
  id: string
  kind: string
  label: string
  description: string
  enabled: boolean
  visible: boolean
}

export interface MenuActionNode extends MenuNodeBase {
  kind: 'action'
  actionId: string
}

export interface MenuToggleNode extends MenuNodeBase {
  kind: 'toggle'
  actionId: string
  value: boolean
}

export interface MenuNodeChoiceOption {
  id: string
  label: string
}

export interface MenuChoiceNode extends MenuNodeBase {
  kind: 'choice'
  actionId: string
  selectedId: string
  options: MenuNodeChoiceOption[]
}

export interface MenuRangeNode extends MenuNodeBase {
  kind: 'range'
  actionId: string
  value: number
  minimum: number
  maximum: number
  step: number
}

export interface MenuTextNode extends MenuNodeBase {
  kind: 'text' | 'search'
  actionId: string
  value: string
  placeholder: string
  maximumLength: number
}

export interface MenuKeybindNode extends MenuNodeBase {
  kind: 'keybind'
  actionId: string
  binding: string
}

export interface MenuTab {
  id: string
  label: string
  nodes: MenuNode[]
}

export interface MenuTabsNode extends MenuNodeBase {
  kind: 'tabs'
  selectedId: string
  tabs: MenuTab[]
}

export interface MenuListNode extends MenuNodeBase {
  kind: 'list'
  nodes: MenuNode[]
}

export interface MenuGridNode extends MenuNodeBase {
  kind: 'grid'
  columns: number
  nodes: MenuNode[]
}

export interface MenuMediaNode extends MenuNodeBase {
  kind: 'media'
  source: string
  mediaType: string
  alternativeText: string
}

export interface MenuStatusNode extends MenuNodeBase {
  kind: 'status'
  value: string
  tone: string
}

export interface MenuProgressNode extends MenuNodeBase {
  kind: 'progress'
  value: number
  indeterminate: boolean
}

export interface MenuPaginationNode extends MenuNodeBase {
  kind: 'pagination'
  actionId: string
  page: number
  pageCount: number
}

export interface MenuSeparatorNode extends MenuNodeBase {
  kind: 'separator'
}

export interface MenuSubmenuNode extends MenuNodeBase {
  kind: 'submenu'
  menuId: string
}

export type MenuNode =
  | MenuActionNode
  | MenuToggleNode
  | MenuChoiceNode
  | MenuRangeNode
  | MenuTextNode
  | MenuKeybindNode
  | MenuTabsNode
  | MenuListNode
  | MenuGridNode
  | MenuMediaNode
  | MenuStatusNode
  | MenuProgressNode
  | MenuPaginationNode
  | MenuSeparatorNode
  | MenuSubmenuNode

/** Exact JSON shape published by the extension host. */
export interface MenuDescriptor {
  extensionId: string
  id: string
  label: string
  description: string
  icon: string
  order: number
  nodes: MenuNode[]
}

export interface MenuSummary {
  extensionId: string
  id: string
  label: string
  order: number
  nodeCount: number
}

export interface MenuListResult {
  total: number
  truncated: boolean
  items: MenuSummary[]
}

export interface MenuItemBase {
  id: string
  label: string
  description?: string
  icon?: string
  badge?: string
  visible?: boolean
  enabled?: boolean
  disabledReason?: string
}

export interface MenuCommandItem extends MenuItemBase {
  type: 'command'
  action: string
  confirmation?: 'never' | 'when-destructive' | 'always'
}

export interface MenuRouteItem extends MenuItemBase {
  type: 'route'
  routeId: string
}

export interface MenuToggleItem extends MenuItemBase {
  type: 'toggle'
  value: boolean
  action?: string
}

export interface MenuChoiceOption {
  value: string
  label: string
  description?: string
  disabled?: boolean
}

export interface MenuChoiceItem extends MenuItemBase {
  type: 'choice'
  value: string
  options: MenuChoiceOption[]
  wrap?: boolean
  action?: string
}

export interface MenuRangeItem extends MenuItemBase {
  type: 'range'
  value: number
  min: number
  max: number
  step: number
  unit?: string
  action?: string
}

export interface MenuTextItem extends MenuItemBase {
  type: 'text'
  value: string
  placeholder?: string
  maxLength?: number
  sensitive?: boolean
  action?: string
}

export interface MenuSearchItem extends MenuItemBase {
  type: 'search'
  value: string
  placeholder?: string
  maxLength?: number
  action?: string
}

export interface MenuKeybindItem extends MenuItemBase {
  type: 'keybind'
  value: string
  allowController?: boolean
  action?: string
}

export interface MenuListEntry {
  id: string
  label: string
  description?: string
  image?: string
  badge?: string
  disabled?: boolean
}

export interface MenuListItem extends MenuItemBase {
  type: 'list' | 'grid'
  selectedId?: string
  entries: MenuListEntry[]
  action?: string
}

export interface MenuTabsItem extends MenuItemBase {
  type: 'tabs'
  value: string
  tabs: MenuChoiceOption[]
  action?: string
}

export interface MenuPaginationItem extends MenuItemBase {
  type: 'pagination'
  page: number
  pageCount: number
  action?: string
}

export interface MenuStatusItem extends MenuItemBase {
  type: 'status'
  tone?: string
  value?: string
}

export interface MenuProgressItem extends MenuItemBase {
  type: 'progress'
  value: number
  max: number
}

export interface MenuMediaItem extends MenuItemBase {
  type: 'media'
  source: string
  mediaType: string
  alt?: string
}

export interface MenuSeparatorItem extends Omit<MenuItemBase, 'label'> {
  type: 'separator'
  label?: string
}

export type MenuItem =
  | MenuCommandItem
  | MenuRouteItem
  | MenuToggleItem
  | MenuChoiceItem
  | MenuRangeItem
  | MenuTextItem
  | MenuSearchItem
  | MenuKeybindItem
  | MenuListItem
  | MenuTabsItem
  | MenuPaginationItem
  | MenuStatusItem
  | MenuProgressItem
  | MenuMediaItem
  | MenuSeparatorItem

export interface MenuRoute {
  id: string
  menuId?: string
  title: string
  subtitle?: string
  home?: boolean
  parentId?: string
  revision?: string
  initialFocusId?: string
  layout?: 'list' | 'grid'
  columns?: number
  items: MenuItem[]
}

/** Route-oriented view model produced by adaptMenusToRoutes. */
export interface RoutedMenuDescriptor {
  id: string
  extensionId: string
  title: string
  description?: string
  icon?: string
  homeRouteId: string
  routes?: MenuRoute[]
}

export type MenuInteraction = 'activate' | 'set-value' | 'adjust'

export interface MenuInvocation extends ConfirmedInvocation {
  extensionId: string
  menuId: string
  nodeId: string
  interaction: MenuInteraction
  parameters?: JsonObject
  value?: JsonValue
}

export interface MenuInvocationResult {
  succeeded: boolean
  confirmationRequired: boolean
  replayed: boolean
  route?: MenuRoute
  value?: JsonValue
  error?: {
    code: string
    message: string
  }
}

export interface EventSubscriptionRequest {
  events: string[]
  filters?: JsonObject
  cadenceMs?: number
  replayLatest?: boolean
}

export interface EventSubscription {
  id: string
  events: string[]
  cadenceMs: number
}

export interface RuntimeLifecycleEvent {
  phase: 'booting' | 'browser-ready' | 'story-loading' | 'story-ready' | 'paused' | 'shutting-down'
  previousPhase?: RuntimeLifecycleEvent['phase']
  timestamp: number
  reason?: string
}

export interface InputActionEvent {
  action: string
  phase: 'pressed' | 'released' | 'repeated' | 'changed'
  value?: number | string | boolean
  source: 'keyboard' | 'mouse' | 'controller' | 'game'
  timestamp: number
}

export interface OverlaySnapshot {
  runtime: RuntimeStatus
  state: GameState
}

export interface BridgeErrorPayload {
  code: string
  message: string
}

export interface BridgeResponse {
  kind: 'response'
  id: string
  protocolVersion?: number
  result?: unknown
  error?: BridgeErrorPayload
}

export interface BridgeEvent<T = unknown> {
  kind: 'event'
  event: string
  payload: T
  protocolVersion?: number
}

export interface BridgeCancel {
  kind: 'cancel'
  id: string
  protocolVersion: 2
  minimumProtocolVersion: 1
  reason?: string
}

export interface WebViewTransport {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void
  removeEventListener(type: 'message', listener: (event: { data: unknown }) => void): void
}

export interface CefSharpBridge {
  PostMessage(message: unknown): void
}
