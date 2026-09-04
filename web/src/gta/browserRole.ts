export type ReactorBrowserRole = 'primary' | 'webview-host' | 'gpu-renderer'

export interface ReactorBrowserCapabilities {
  bootstrapInput: boolean
  providerInput: boolean
}

export function parseReactorBrowserRole(value: unknown): ReactorBrowserRole | null {
  if (typeof value !== 'string') return null
  return value === 'primary' || value === 'webview-host' || value === 'gpu-renderer'
    ? value
    : null
}

export function browserRoleFromLocation(search: string): ReactorBrowserRole {
  try {
    return parseReactorBrowserRole(new URLSearchParams(search).get('reactorBrowserRole')) ?? 'primary'
  } catch {
    return 'primary'
  }
}

export function browserRoleFromHostEvent(payload: unknown): ReactorBrowserRole | null {
  if (typeof payload !== 'object' || payload === null || Array.isArray(payload)) return null
  return parseReactorBrowserRole((payload as Record<string, unknown>).role)
}

export function browserCapabilities(role: ReactorBrowserRole): ReactorBrowserCapabilities {
  switch (role) {
    case 'webview-host':
      return { bootstrapInput: true, providerInput: false }
    case 'gpu-renderer':
      return { bootstrapInput: false, providerInput: true }
    default:
      return { bootstrapInput: true, providerInput: true }
  }
}

export function canAcknowledgeHostSurface(
  role: ReactorBrowserRole,
  surfaceMode: string,
): boolean {
  if (browserCapabilities(role).bootstrapInput) return true
  // Paint acknowledgement does not grant bootstrap visibility or input authority.
  return role === 'gpu-renderer' && ['initializing', 'about', 'verifying', 'setup-status'].includes(surfaceMode)
}
