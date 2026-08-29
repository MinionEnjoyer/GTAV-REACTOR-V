import type {
  ConfirmedInvocation,
  JsonObject,
  JsonValue,
  MenuChoiceOption,
  MenuInvocation,
  MenuItem,
  MenuRoute,
  RoutedMenuDescriptor,
} from '../gta/types'

export interface MenuControllerSnapshot {
  menuId: string
  route: MenuRoute
  stack: string[]
  focusedItemId?: string
}

export interface MenuControllerOptions {
  invoke?: (invocation: MenuInvocation) => void | Promise<unknown>
  onChange?: (snapshot: MenuControllerSnapshot) => void
}

export interface MenuControllerInvocationOptions extends ConfirmedInvocation {
  parameters?: JsonObject
}

const passiveItemTypes = new Set<MenuItem['type']>(['status', 'progress', 'media', 'separator'])

function isAvailable(item: MenuItem): boolean {
  return item.visible !== false && item.enabled !== false
}

function isFocusable(item: MenuItem): boolean {
  return isAvailable(item) && !passiveItemTypes.has(item.type)
}

function availableOptions(options: MenuChoiceOption[]): MenuChoiceOption[] {
  return options.filter((option) => !option.disabled)
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value))
}

export class MenuController {
  private readonly routes = new Map<string, MenuRoute>()
  private readonly focusByRoute = new Map<string, string>()
  private stack: string[]

  constructor(
    private readonly menu: RoutedMenuDescriptor,
    private readonly options: MenuControllerOptions = {},
  ) {
    for (const route of structuredClone(menu.routes ?? [])) {
      this.routes.set(route.id, route)
    }
    if (!this.routes.has(menu.homeRouteId)) {
      throw new Error(`Menu '${menu.id}' does not contain home route '${menu.homeRouteId}'.`)
    }
    this.stack = [menu.homeRouteId]
    this.restoreFocus(menu.homeRouteId)
  }

  get snapshot(): MenuControllerSnapshot {
    const route = this.currentRoute
    return {
      menuId: route.menuId ?? this.menu.id,
      route: structuredClone(route),
      stack: [...this.stack],
      focusedItemId: this.focusByRoute.get(route.id),
    }
  }

  get currentRoute(): MenuRoute {
    return this.requireRoute(this.stack[this.stack.length - 1])
  }

  get focusedItem(): MenuItem | undefined {
    const itemId = this.focusByRoute.get(this.currentRoute.id)
    return itemId ? this.currentRoute.items.find((item) => item.id === itemId) : undefined
  }

  push(routeId: string): MenuControllerSnapshot {
    this.requireRoute(routeId)
    this.stack.push(routeId)
    this.restoreFocus(routeId)
    return this.changed()
  }

  replace(routeId: string): MenuControllerSnapshot {
    this.requireRoute(routeId)
    this.stack[this.stack.length - 1] = routeId
    this.restoreFocus(routeId)
    return this.changed()
  }

  back(): boolean {
    if (this.stack.length <= 1) return false
    this.stack.pop()
    this.restoreFocus(this.currentRoute.id)
    this.changed()
    return true
  }

  home(): MenuControllerSnapshot {
    this.stack = [this.menu.homeRouteId]
    this.restoreFocus(this.menu.homeRouteId)
    return this.changed()
  }

  focus(itemId: string): boolean {
    const item = this.currentRoute.items.find((candidate) => candidate.id === itemId)
    if (!item || !isFocusable(item)) return false
    this.focusByRoute.set(this.currentRoute.id, item.id)
    this.changed()
    return true
  }

  moveFocus(delta: number): MenuItem | undefined {
    if (!Number.isFinite(delta) || delta === 0) return this.focusedItem
    const items = this.currentRoute.items.filter(isFocusable)
    if (items.length === 0) return undefined
    const currentIndex = Math.max(0, items.findIndex((item) => item.id === this.focusedItem?.id))
    const direction = delta < 0 ? -1 : 1
    const nextIndex = (currentIndex + direction + items.length) % items.length
    this.focusByRoute.set(this.currentRoute.id, items[nextIndex].id)
    this.changed()
    return structuredClone(items[nextIndex])
  }

  async activate(metadata: MenuControllerInvocationOptions = {}): Promise<MenuInvocation | undefined> {
    const item = this.focusedItem
    if (!item || !isAvailable(item)) return undefined
    if (item.type === 'route') {
      this.push(item.routeId)
      return undefined
    }
    if (item.type === 'toggle') return this.setValue(!item.value, metadata)
    if (item.type === 'status' || item.type === 'progress' || item.type === 'media' || item.type === 'separator') {
      return undefined
    }
    return this.dispatch(
      'activate',
      item.type === 'command' ? undefined : this.itemValue(item),
      metadata,
    )
  }

  async setValue(value: JsonValue, metadata: MenuControllerInvocationOptions = {}): Promise<MenuInvocation | undefined> {
    const item = this.focusedItem
    if (!item || !isAvailable(item) || !this.applyValue(item, value)) return undefined
    this.changed()
    return this.dispatch('set-value', this.itemValue(item), metadata)
  }

  async adjust(delta: number, metadata: MenuControllerInvocationOptions = {}): Promise<MenuInvocation | undefined> {
    const item = this.focusedItem
    if (!item || !isAvailable(item) || !Number.isFinite(delta) || delta === 0) return undefined
    const direction = delta < 0 ? -1 : 1
    let value: JsonValue | undefined

    switch (item.type) {
      case 'toggle':
        value = !item.value
        break
      case 'range':
        value = clamp(item.value + item.step * direction, item.min, item.max)
        break
      case 'choice': {
        const options = availableOptions(item.options)
        value = this.cycleValue(options, item.value, direction, item.wrap !== false)
        break
      }
      case 'tabs': {
        const options = availableOptions(item.tabs)
        value = this.cycleValue(options, item.value, direction, true)
        break
      }
      case 'pagination':
        value = clamp(item.page + direction, 1, Math.max(1, item.pageCount))
        break
      case 'list':
      case 'grid': {
        const entries = item.entries.filter((entry) => !entry.disabled)
        const current = item.selectedId ?? entries[0]?.id ?? ''
        value = this.cycleValue(entries, current, direction, true)
        break
      }
      default:
        return undefined
    }

    if (value === undefined || !this.applyValue(item, value)) return undefined
    this.changed()
    return this.dispatch('adjust', value, metadata)
  }

  private applyValue(item: MenuItem, value: JsonValue): boolean {
    switch (item.type) {
      case 'toggle':
        if (typeof value !== 'boolean') return false
        item.value = value
        return true
      case 'range':
        if (typeof value !== 'number' || !Number.isFinite(value)) return false
        item.value = clamp(value, item.min, item.max)
        return true
      case 'choice':
        if (typeof value !== 'string' || !availableOptions(item.options).some((option) => option.value === value)) return false
        item.value = value
        return true
      case 'tabs':
        if (typeof value !== 'string' || !availableOptions(item.tabs).some((option) => option.value === value)) return false
        item.value = value
        return true
      case 'text':
      case 'search':
      case 'keybind':
        if (typeof value !== 'string') return false
        item.value = (item.type === 'text' || item.type === 'search') && item.maxLength
          ? value.slice(0, item.maxLength)
          : value
        return true
      case 'pagination':
        if (typeof value !== 'number' || !Number.isInteger(value)) return false
        item.page = clamp(value, 1, Math.max(1, item.pageCount))
        return true
      case 'list':
      case 'grid':
        if (typeof value !== 'string' || !item.entries.some((entry) => entry.id === value && !entry.disabled)) return false
        item.selectedId = value
        return true
      default:
        return false
    }
  }

  private cycleValue<T extends { value?: string; id?: string }>(
    values: T[],
    current: string,
    direction: number,
    wrap: boolean,
  ): string | undefined {
    if (values.length === 0) return undefined
    const keys = values.map((value) => value.value ?? value.id ?? '')
    const currentIndex = Math.max(0, keys.indexOf(current))
    const candidate = currentIndex + direction
    const index = wrap ? (candidate + keys.length) % keys.length : clamp(candidate, 0, keys.length - 1)
    return keys[index]
  }

  private itemValue(item: MenuItem): JsonValue {
    switch (item.type) {
      case 'toggle':
      case 'range':
      case 'choice':
      case 'tabs':
      case 'text':
      case 'search':
      case 'keybind':
        return item.value
      case 'pagination':
        return item.page
      case 'list':
      case 'grid':
        return item.selectedId ?? null
      default:
        return null
    }
  }

  private async dispatch(
    interaction: MenuInvocation['interaction'],
    value: JsonValue | undefined,
    metadata: MenuControllerInvocationOptions,
  ): Promise<MenuInvocation> {
    const { parameters, ...invocationMetadata } = metadata
    const invocation: MenuInvocation = {
      extensionId: this.menu.extensionId,
      menuId: this.currentRoute.menuId ?? this.menu.id,
      nodeId: this.focusedItem!.id,
      interaction,
      ...(value === undefined ? {} : { value }),
      ...(parameters === undefined
        ? {}
        : { parameters: value === undefined ? parameters : { ...parameters, value } }),
      ...invocationMetadata,
    }
    await this.options.invoke?.(invocation)
    return invocation
  }

  private restoreFocus(routeId: string): void {
    const route = this.requireRoute(routeId)
    const remembered = this.focusByRoute.get(routeId)
    if (remembered && route.items.some((item) => item.id === remembered && isFocusable(item))) return
    if (route.initialFocusId && route.items.some((item) => item.id === route.initialFocusId && isFocusable(item))) {
      this.focusByRoute.set(routeId, route.initialFocusId)
      return
    }
    const first = route.items.find(isFocusable)
    if (first) this.focusByRoute.set(routeId, first.id)
    else this.focusByRoute.delete(routeId)
  }

  private requireRoute(routeId: string): MenuRoute {
    const route = this.routes.get(routeId)
    if (!route) throw new Error(`Menu '${this.menu.id}' does not contain route '${routeId}'.`)
    return route
  }

  private changed(): MenuControllerSnapshot {
    const snapshot = this.snapshot
    this.options.onChange?.(snapshot)
    return snapshot
  }
}
