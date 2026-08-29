import { createRoot } from 'react-dom/client'
import { flushSync } from 'react-dom'
import App from './App'
import './styles.css'

declare global {
  interface Window {
    __reactorVPageReady?: boolean
  }
}

const rootElement = document.getElementById('root')!
const root = createRoot(rootElement)
flushSync(() => root.render(<App />))

async function publishFirstPaint() {
  const images = Array.from(document.images || [])
  const decoded = Promise.all(images.map((image) => {
    if (image.complete) return image.decode ? image.decode().catch(() => {}) : Promise.resolve()
    return new Promise<void>((resolve) => {
      image.addEventListener('load', () => resolve(), { once: true })
      image.addEventListener('error', () => resolve(), { once: true })
    })
  }))
  // A hidden WebView can throttle requestAnimationFrame indefinitely. Image
  // decode is useful cache work, but it must not hold up Story Mode startup.
  await Promise.race([
    decoded,
    new Promise<void>((resolve) => setTimeout(resolve, 500)),
  ])
  window.__reactorVPageReady = true
}

void publishFirstPaint()
