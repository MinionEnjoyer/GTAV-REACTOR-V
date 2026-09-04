import type { StartupStatus } from '../gta/types'
import {
  formatStartupTimestamp,
  startupDisplayComponents,
  visibleStartupConsoleEntries,
} from '../startup'

interface StartupTransitionSurfaceProps {
  status: StartupStatus
  surfaceGeneration: number
  onClose(): void
}

const stateLabels = {
  ready: 'Ready',
  initializing: 'Starting',
  waiting: 'Waiting',
  unavailable: 'Unavailable',
} as const

const sourceLabels: Record<string, string> = {
  allin1: 'ALLIN1',
  bootstrap: 'Reactor V',
  preloader: 'Preloader',
  reactor: 'Reactor V',
  runtime: 'Reactor V',
  script: 'ScriptHook',
  scripthook: 'ScriptHook',
}

function readableToken(value: string): string {
  return value
    .replace(/[-_]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/^./, (character) => character.toUpperCase())
}

function readableSource(source: string): string {
  return sourceLabels[source.toLowerCase()] ?? readableToken(source)
}

function readableLogMessage(message: string, stage: string): string {
  const stageLabel = readableToken(stage)
  const keyValueCount = message.match(/(?:^|\s)[A-Za-z][A-Za-z0-9_.-]*=/g)?.length ?? 0
  const containsStructuredPayload = /(?:metrics|payload|json)\s*=\s*[{[]/i.test(message) ||
    /^\s*[{[]/.test(message)
  if (!message) return `${stageLabel} complete.`
  if (containsStructuredPayload) return `${stageLabel} recorded.`
  if (keyValueCount >= 1) return `${stageLabel}.`
  return message
}

export function StartupTransitionSurface({
  status,
  surfaceGeneration,
  onClose,
}: StartupTransitionSurfaceProps) {
  const components = startupDisplayComponents(status)
  const entries = visibleStartupConsoleEntries(status)
  return (
    <main className="startup-transition-stage">
      <section className="startup-transition-modal" aria-label="ALLIN1 Preloader" aria-live="polite">
        <header className="startup-transition-header">
          <img
            className="startup-transition-brand"
            src="./allin1-logo.png"
            alt=""
            aria-hidden="true"
          />
          <span className="startup-transition-title">
            <small>STORY MODE STARTUP</small>
            <h1>ALLIN1 Preloader</h1>
            <p>Powered by Reactor V</p>
          </span>
          {status.providerConnected && (
            <button type="button" onClick={onClose} aria-label="Close ALLIN1 Preloader">×</button>
          )}
        </header>

        <section className="startup-service-panel" aria-labelledby="startup-service-heading">
          <h2 id="startup-service-heading">Services</h2>
          <ol className="startup-component-list" aria-label="Startup service checklist">
            {components.map((component) => (
              <li key={component.id} className={`startup-component ${component.state}`}>
                <span className="startup-component-indicator" aria-hidden="true">
                  {component.state === 'ready' ? '✓' : component.state === 'unavailable' ? '!' : ''}
                </span>
                <span className="startup-component-copy">
                  <strong>{component.label}</strong>
                  <small>{component.detail}</small>
                </span>
                <em>{stateLabels[component.state]}</em>
              </li>
            ))}
          </ol>
        </section>

        <section className="startup-console" aria-label="Initialization log">
          <header>
            <span>Startup log</span>
            <small>{status.console.dropped > 0 ? `${status.console.dropped} earlier events omitted` : 'Live'}</small>
          </header>
          <ol>
            {entries.map((entry) => (
              <li key={`${entry.sequence}:${entry.source}:${entry.stage}`}>
                <time dateTime={entry.timestampUtc}>{formatStartupTimestamp(entry.timestampUtc)}</time>
                <span className="startup-console-source" title={`${entry.source} / ${entry.stage}`}>
                  {readableSource(entry.source)}
                </span>
                <span className="startup-console-message" title={entry.message || undefined}>
                  {readableLogMessage(entry.message, entry.stage)}
                </span>
              </li>
            ))}
          </ol>
        </section>

        <footer>
          <span><kbd>Esc</kbd> close</span>
          <span className="startup-transition-activity"><i aria-hidden="true" /> Starting services</span>
        </footer>
      </section>
    </main>
  )
}
