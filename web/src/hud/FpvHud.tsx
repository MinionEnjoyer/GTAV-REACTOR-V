import { useEffect, useState, type CSSProperties } from 'react'
import type { GtaBridge } from '../gta/bridge'
import { Speedometer, parseSpeedometer, type SpeedometerFrame } from './Speedometer'
import './fpv-hud.css'

const MAX_LABEL_LENGTH = 32
const MAX_PAYLOAD_LENGTH = 64
const MAX_HINTS = 4
const colorPattern = /^#[0-9a-fA-F]{6}$/

export type FpvHudLayout = 'betaflight' | 'compact' | 'bomber'

export interface FpvHudShowFlags {
  artificialHorizon: boolean
  crosshair: boolean
  speed: boolean
  altitude: boolean
  homeDistance: boolean
  heading: boolean
  timer: boolean
  flightMode: boolean
  cameraMode: boolean
  payload: boolean
  controlHints: boolean
}

export interface FpvHudFrame {
  schema: 1
  layout: FpvHudLayout
  color: string
  opacity: number
  scale: number
  pitch: number
  roll: number
  speed: number
  altitude: number
  homeDistance: number
  heading: number
  timerSeconds: number
  flightMode: string
  cameraMode: string
  payload: string
  show: FpvHudShowFlags
  controlHints?: readonly string[]
}

export interface PassiveHudFrame {
  speedometer: SpeedometerFrame
  fpv: FpvHudFrame | null
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isFiniteInRange(value: unknown, minimum: number, maximum: number): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= minimum && value <= maximum
}

function isLabel(value: unknown, maximum = MAX_LABEL_LENGTH): value is string {
  return typeof value === 'string' && value.length > 0 && value.length <= maximum &&
    /^[A-Za-z0-9][A-Za-z0-9 ._+\-/]*$/.test(value)
}

function parseShowFlags(value: unknown): FpvHudShowFlags | null {
  if (!isRecord(value)) return null
  const names: (keyof FpvHudShowFlags)[] = [
    'artificialHorizon', 'crosshair', 'speed', 'altitude', 'homeDistance',
    'heading', 'timer', 'flightMode', 'cameraMode', 'payload', 'controlHints',
  ]
  return names.every((name) => typeof value[name] === 'boolean')
    ? value as unknown as FpvHudShowFlags
    : null
}

function parseControlHints(value: unknown): readonly string[] | undefined | null {
  if (value === undefined) return undefined
  if (!Array.isArray(value) || value.length > MAX_HINTS || !value.every((hint) => isLabel(hint))) return null
  return value
}

/**
 * FPV metadata lives inside the existing, host-validated speedometer frame.
 * Invalid metadata must not take down the established speedometer fallback.
 */
export function parseFpvHud(value: unknown): FpvHudFrame | null {
  const timerSeconds = isRecord(value) ? value.timerSeconds : undefined
  if (!isRecord(value) || value.schema !== 1 ||
    (value.layout !== 'betaflight' && value.layout !== 'compact' && value.layout !== 'bomber') ||
    typeof value.color !== 'string' || !colorPattern.test(value.color) ||
    !isFiniteInRange(value.opacity, 0, 1) || !isFiniteInRange(value.scale, 0.5, 2.5) ||
    !isFiniteInRange(value.pitch, -90, 90) || !isFiniteInRange(value.roll, -180, 180) ||
    !isFiniteInRange(value.speed, 0, 9999) || !isFiniteInRange(value.altitude, -9999, 99999) ||
    !isFiniteInRange(value.homeDistance, 0, 999999) || !isFiniteInRange(value.heading, 0, 359.999) ||
    !isFiniteInRange(timerSeconds, 0, 86_400) || !Number.isInteger(timerSeconds) ||
    !isLabel(value.flightMode) || !isLabel(value.cameraMode) ||
    typeof value.payload !== 'string' || value.payload.length > MAX_PAYLOAD_LENGTH ||
    !/^[A-Za-z0-9 ._+\-/]*$/.test(value.payload)) return null

  const show = parseShowFlags(value.show)
  const controlHints = parseControlHints(value.controlHints)
  if (!show || controlHints === null) return null
  return {
    schema: 1,
    layout: value.layout,
    color: value.color,
    opacity: value.opacity,
    scale: value.scale,
    pitch: value.pitch,
    roll: value.roll,
    speed: value.speed,
    altitude: value.altitude,
    homeDistance: value.homeDistance,
    heading: value.heading,
    timerSeconds,
    flightMode: value.flightMode,
    cameraMode: value.cameraMode,
    payload: value.payload,
    show,
    ...(controlHints === undefined ? {} : { controlHints }),
  }
}

export function parsePassiveHud(value: unknown): PassiveHudFrame | null {
  const parsedSpeedometer = parseSpeedometer(value)
  if (!parsedSpeedometer) return null
  // Do not retain arbitrary metadata on the legacy view. This keeps the
  // speedometer's public data shape identical to its pre-FPV shape.
  const speedometer: SpeedometerFrame = {
    schema: 1,
    visible: true,
    kind: 'speedometer',
    speed: parsedSpeedometer.speed,
    units: parsedSpeedometer.units,
    gear: parsedSpeedometer.gear,
    manual: parsedSpeedometer.manual,
    notice: parsedSpeedometer.notice,
  }
  const fpv = isRecord(value) ? parseFpvHud(value.fpv) : null
  return { speedometer, fpv }
}

// One hud.frame subscription keeps the host's single passive-HUD lease intact.
export function subscribePassiveHud(
  bridge: Pick<GtaBridge, 'on'>,
  update: (frame: PassiveHudFrame | null) => void,
) {
  let timeout: ReturnType<typeof setTimeout> | undefined
  const clear = () => {
    clearTimeout(timeout)
    update(null)
  }
  const off = bridge.on<unknown>('hud.frame', (value) => {
    clearTimeout(timeout)
    update(parsePassiveHud(value))
    timeout = setTimeout(() => update(null), 1000)
  })
  const disconnect = bridge.on<{ connected?: boolean }>('host.provider', (value) => {
    if (value?.connected === false) clear()
  })
  return () => { off(); disconnect(); clearTimeout(timeout) }
}

export function usePassiveHud(bridge: Pick<GtaBridge, 'on'>) {
  const [frame, setFrame] = useState<PassiveHudFrame | null>(null)
  useEffect(() => subscribePassiveHud(bridge, setFrame), [bridge])
  return frame
}

function formatTimer(seconds: number): string {
  const minutes = Math.floor(seconds / 60)
  return `${minutes}:${(seconds % 60).toString().padStart(2, '0')}`
}

function cardinal(heading: number): string {
  return ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'][Math.round(heading / 45) % 8]
}

function FpvValue({ label, value, className }: { label: string; value: string; className: string }) {
  return <div className={`reactor-fpv-value ${className}`}><span>{label}</span><strong>{value}</strong></div>
}

export function FpvHud({ frame, units }: { frame: FpvHudFrame; units: SpeedometerFrame['units'] }) {
  const horizonOffset = Math.round(frame.pitch * 1.4)
  const imperial = units === 'MPH'
  const style = {
    '--reactor-fpv-color': frame.color,
    '--reactor-fpv-opacity': frame.opacity,
    '--reactor-fpv-scale': frame.scale,
    '--reactor-fpv-roll': `${frame.roll}deg`,
    '--reactor-fpv-pitch': `${horizonOffset}px`,
  } as CSSProperties
  return <aside className={`reactor-fpv-hud reactor-fpv-hud--${frame.layout}`} style={style} aria-label="FPV flight telemetry">
    {frame.show.artificialHorizon && <div className="reactor-fpv-horizon" aria-hidden="true"><div className="reactor-fpv-horizon-lines"><i /><i /><i /><i /><i /></div></div>}
    {frame.show.crosshair && <div className="reactor-fpv-crosshair" aria-hidden="true"><i /><b /><em /></div>}
    {frame.show.heading && <FpvValue label={cardinal(frame.heading)} value={`${Math.round(frame.heading).toString().padStart(3, '0')}°`} className="reactor-fpv-heading" />}
    {frame.show.speed && <FpvValue label="SPD" value={`${Math.round(frame.speed)} ${imperial ? 'mph' : 'km/h'}`} className="reactor-fpv-speed" />}
    {frame.show.altitude && <FpvValue label="ALT" value={`${Math.round(frame.altitude)} ${imperial ? 'ft' : 'm'}`} className="reactor-fpv-altitude" />}
    {frame.show.homeDistance && <FpvValue label="HOME" value={`${Math.round(frame.homeDistance)} ${imperial ? 'ft' : 'm'}`} className="reactor-fpv-home" />}
    {frame.show.timer && <FpvValue label="TIME" value={formatTimer(frame.timerSeconds)} className="reactor-fpv-timer" />}
    {frame.show.flightMode && <p className="reactor-fpv-flight-mode">{frame.flightMode}</p>}
    {frame.show.cameraMode && <p className="reactor-fpv-camera-mode">CAM {frame.cameraMode}</p>}
    {frame.show.payload && <p className="reactor-fpv-payload">{frame.payload || 'PAYLOAD SAFE'}</p>}
    {frame.show.controlHints && frame.controlHints && frame.controlHints.length > 0 && <ul className="reactor-fpv-controls" aria-label="FPV controls">{frame.controlHints.map((hint) => <li key={hint}>{hint}</li>)}</ul>}
  </aside>
}

export function PassiveHud({ frame }: { frame: PassiveHudFrame }) {
  return frame.fpv
    ? <FpvHud frame={frame.fpv} units={frame.speedometer.units} />
    : <Speedometer frame={frame.speedometer} />
}
