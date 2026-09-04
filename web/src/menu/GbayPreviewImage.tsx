import { useEffect, useState, type ReactNode } from 'react'
import { renderableGbayPreviewSource } from './gbay'

const reportedPreviewFailures = new Set<string>()
const maximumReportedPreviewFailures = 64

function reportFailure(source: string, reason: 'blocked' | 'load-failed') {
  const key = `${reason}:${source}`
  if (reportedPreviewFailures.has(key) ||
    reportedPreviewFailures.size >= maximumReportedPreviewFailures) return
  reportedPreviewFailures.add(key)
  globalThis.console?.warn?.(`REACTOR V GBAY preview ${reason}: ${source}`)
}

export function GbayPreviewImage({
  source,
  alt,
  fallback,
  className,
}: {
  source: string
  alt: string
  fallback: ReactNode
  className?: string
}) {
  const allowed = renderableGbayPreviewSource(source)
  const [failedSource, setFailedSource] = useState<string | null>(null)

  useEffect(() => {
    if (source && !allowed) reportFailure(source, 'blocked')
  }, [allowed, source])
  if (!allowed) return <>{fallback}</>
  if (failedSource === source) return <>{fallback}</>

  return <img
    src={source}
    alt={alt}
    className={className}
    loading="eager"
    decoding="async"
    data-reactor-gbay-preview="true"
    onError={() => {
      reportFailure(source, 'load-failed')
      setFailedSource(source)
    }}
  />
}
