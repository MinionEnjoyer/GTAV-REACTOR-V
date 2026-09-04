import { describe, expect, it } from 'vitest'
import {
  browserCapabilities,
  browserRoleFromHostEvent,
  browserRoleFromLocation,
  canAcknowledgeHostSurface,
} from './browserRole'

describe('dual-browser authority role', () => {
  it('defaults a standalone browser to full authority', () => {
    expect(browserRoleFromLocation('')).toBe('primary')
    expect(browserCapabilities('primary')).toEqual({ bootstrapInput: true, providerInput: true })
  })

  it('identifies the external GPU document as provider-input-only', () => {
    expect(browserRoleFromLocation('?reactorBrowserRole=gpu-renderer')).toBe('gpu-renderer')
    expect(browserCapabilities('gpu-renderer')).toEqual({ bootstrapInput: false, providerInput: true })
  })

  it('accepts only bounded host role changes and keeps WebView out of provider input', () => {
    expect(browserRoleFromHostEvent({ role: 'webview-host' })).toBe('webview-host')
    expect(browserCapabilities('webview-host')).toEqual({ bootstrapInput: true, providerInput: false })
    expect(browserRoleFromHostEvent({ role: 'unknown' })).toBeNull()
    expect(browserRoleFromHostEvent('webview-host')).toBeNull()
  })

  it('allows read-only GPU paint proof for known bootstrap surfaces without adding input authority', () => {
    expect(canAcknowledgeHostSurface('gpu-renderer', 'initializing')).toBe(true)
    expect(canAcknowledgeHostSurface('gpu-renderer', 'about')).toBe(true)
    expect(canAcknowledgeHostSurface('gpu-renderer', 'verifying')).toBe(true)
    expect(canAcknowledgeHostSurface('gpu-renderer', 'setup-status')).toBe(true)
    expect(canAcknowledgeHostSurface('gpu-renderer', 'unknown')).toBe(false)
    expect(canAcknowledgeHostSurface('gpu-renderer', 'none')).toBe(false)
    expect(browserCapabilities('gpu-renderer').bootstrapInput).toBe(false)
    expect(canAcknowledgeHostSurface('webview-host', 'about')).toBe(true)
    expect(canAcknowledgeHostSurface('primary', 'setup-status')).toBe(true)
  })
})
