import type {
  MenuDescriptor,
  MenuItem,
  MenuNode,
  MenuRoute,
  RoutedMenuDescriptor,
} from '../gta/types'

function common(node: MenuNode) {
  return {
    id: node.id,
    label: node.label,
    description: node.description || undefined,
    enabled: node.enabled,
    visible: node.visible,
  }
}

/**
 * Converts the extension host's exact, nested menu JSON into a route-oriented
 * view model. The wire descriptor is never rewritten or hidden from callers.
 */
export function adaptMenusToRoutes(menus: MenuDescriptor[], rootMenuId: string): RoutedMenuDescriptor {
  const root = menus.find((menu) => menu.id === rootMenuId)
  if (!root) throw new Error(`Menu '${rootMenuId}' was not returned by the extension host.`)

  const routes: MenuRoute[] = []
  const buildRoute = (
    menu: MenuDescriptor,
    routeId: string,
    title: string,
    nodes: MenuNode[],
    parentId?: string,
    layout: MenuRoute['layout'] = 'list',
    columns?: number,
    tabParentId?: string,
  ) => {
    const items: MenuItem[] = []
    const route: MenuRoute = { id: routeId, menuId: menu.id, title, parentId, layout, columns, tabParentId, items }
    routes.push(route)

    for (const node of nodes) {
      const base = common(node)
      switch (node.kind) {
        case 'action':
          items.push({ ...base, type: 'command', action: node.actionId })
          break
        case 'toggle':
          items.push({ ...base, type: 'toggle', action: node.actionId, value: node.value })
          break
        case 'choice':
          items.push({
            ...base,
            type: 'choice',
            action: node.actionId,
            value: node.selectedId,
            options: node.options.map((option) => ({ value: option.id, label: option.label })),
          })
          break
        case 'range':
          items.push({
            ...base,
            type: 'range',
            action: node.actionId,
            value: node.value,
            min: node.minimum,
            max: node.maximum,
            step: node.step,
          })
          break
        case 'text':
        case 'search':
          items.push({
            ...base,
            type: node.kind,
            action: node.actionId,
            value: node.value,
            placeholder: node.placeholder,
            maxLength: node.maximumLength,
          })
          break
        case 'keybind':
          items.push({ ...base, type: 'keybind', action: node.actionId, value: node.binding })
          break
        case 'status':
          items.push({ ...base, type: 'status', value: node.value, tone: node.tone })
          break
        case 'progress':
          items.push({ ...base, type: 'progress', value: node.value, max: 1 })
          break
        case 'pagination':
          items.push({ ...base, type: 'pagination', action: node.actionId, page: node.page, pageCount: node.pageCount })
          break
        case 'media':
          items.push({ ...base, type: 'media', source: node.source, mediaType: node.mediaType, alt: node.alternativeText })
          break
        case 'separator':
          items.push({ ...base, type: 'separator' })
          break
        case 'submenu':
          items.push({ ...base, type: 'route', routeId: node.menuId })
          break
        case 'list':
        case 'grid': {
          const childRouteId = `${menu.id}/${node.id}`
          items.push({ ...base, type: 'route', routeId: childRouteId })
          buildRoute(
            menu,
            childRouteId,
            node.label,
            node.nodes,
            routeId,
            node.kind,
            node.kind === 'grid' ? node.columns : undefined,
          )
          break
        }
        case 'tabs': {
          const tabHubId = `${menu.id}/${node.id}`
          items.push({ ...base, type: 'route', routeId: tabHubId })
          const tabItems: MenuItem[] = []
          routes.push({
            id: tabHubId,
            menuId: menu.id,
            title: node.label,
            parentId: routeId,
            initialFocusId: node.selectedId,
            items: tabItems,
          })
          for (const tab of node.tabs) {
            const tabRouteId = `${tabHubId}/${tab.id}`
            tabItems.push({ id: tab.id, label: tab.label, type: 'route', routeId: tabRouteId })
            buildRoute(menu, tabRouteId, tab.label, tab.nodes, tabHubId, 'list', undefined, tabHubId)
          }
          break
        }
      }
    }
  }

  for (const menu of menus) {
    buildRoute(menu, menu.id, menu.label, menu.nodes, menu.id === rootMenuId ? undefined : rootMenuId)
  }

  return {
    id: root.id,
    extensionId: root.extensionId,
    title: root.label,
    description: root.description || undefined,
    icon: root.icon || undefined,
    homeRouteId: root.id,
    routes,
  }
}
