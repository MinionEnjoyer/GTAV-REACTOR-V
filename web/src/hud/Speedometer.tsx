import { useEffect, useState } from 'react'
import type { GtaBridge } from '../gta/bridge'
import './hud.css'

export interface SpeedometerFrame {
  schema: 1; visible: true; kind: 'speedometer'; speed: number
  units: 'KMH' | 'MPH'; gear: string; manual: boolean; notice: string
}
export function parseSpeedometer(value: unknown): SpeedometerFrame | null {
  if (!value || typeof value !== 'object') return null
  const f = value as SpeedometerFrame
  return f.schema === 1 && f.visible === true && f.kind === 'speedometer' &&
    Number.isFinite(f.speed) && f.speed >= 0 && f.speed <= 9999 &&
    (f.units === 'KMH' || f.units === 'MPH') && typeof f.gear === 'string' && f.gear.length <= 3 &&
    typeof f.manual === 'boolean' && typeof f.notice === 'string' && f.notice.length <= 180 ? f : null
}

export function Speedometer({ frame }: { frame: SpeedometerFrame }) {
  return <aside className="reactor-speedometer" aria-label="Vehicle speed and gear">
    <div className="reactor-speedometer-speed"><strong>{Math.round(frame.speed)}</strong><span>{frame.units}</span></div>
    <div className="reactor-speedometer-gear"><span>{frame.manual ? 'MANUAL' : 'AUTO'}</span><strong>{frame.gear}</strong></div>
    {frame.notice && <p className="reactor-speedometer-notice" role="status">{frame.notice}</p>}
  </aside>
}

// Mounted for the lifetime of App so a hidden host can receive its first frame before paint acknowledgement.
export function useSpeedometer(bridge: Pick<GtaBridge, 'on'>) {
  const [frame, setFrame] = useState<SpeedometerFrame | null>(null)
  useEffect(() => subscribeSpeedometer(bridge, setFrame), [bridge])
  return frame
}

export function subscribeSpeedometer(bridge: Pick<GtaBridge, 'on'>, update: (frame: SpeedometerFrame | null) => void) {
    let timeout: ReturnType<typeof setTimeout> | undefined
    const off = bridge.on<unknown>('hud.frame', value => {
      clearTimeout(timeout)
      update(parseSpeedometer(value))
      timeout = setTimeout(() => update(null), 1000)
    })
    const disconnect = bridge.on<{ connected?: boolean }>('host.provider', value => {
      if (value?.connected === false) { clearTimeout(timeout); update(null) }
    })
    return () => { off(); disconnect(); clearTimeout(timeout) }
}
