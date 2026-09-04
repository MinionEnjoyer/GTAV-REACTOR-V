import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'
import { mkdirSync, copyFileSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { createHash } from 'node:crypto'

const projectRoot = fileURLToPath(new URL('.', import.meta.url))
const runtimeAssets = ['ragewebui-logo.png', 'fonts/BebasNeue-Regular.ttf', 'fonts/Oswald-Variable.ttf',
  'fonts/OFL-Bebas-Neue.txt', 'fonts/OFL-Oswald.txt']

// Consumer UI is an explicit, separate build, never a lazy chunk in Reactor.
// The ALLIN1 compatibility suite continues to test the existing adapter.
function standaloneBoundary(): Plugin {
  const replacements = new Map([
    ['src/menu/MenuSurface', 'src/standalone/MenuSurface.tsx'],
    ['src/menu/StartupTransitionSurface', 'src/standalone/StartupTransitionSurface.tsx'],
    ['src/startup', 'src/standalone/startup.ts'],
    ['src/gta/demoTransport', 'src/standalone/demoTransport.ts'],
    ['src/styles.css', 'src/standalone/styles.css'],
  ].map(([source, target]) => [resolve(projectRoot, source).replaceAll('\\', '/'), resolve(projectRoot, target)]))
  return {
    name: 'reactor-standalone-boundary', enforce: 'pre',
    configureServer(server) {
      server.middlewares.use((request, response, next) => {
        const path = (request.url ?? '').split('?')[0].slice(1)
        if (!runtimeAssets.includes(path)) return next()
        response.setHeader('Content-Type', path.endsWith('.png') ? 'image/png' : path.endsWith('.ttf') ? 'font/ttf' : 'text/plain')
        response.end(readFileSync(resolve(projectRoot, 'public', path)))
      })
    },
    resolveId(source, importer) {
      if (!importer || !source.startsWith('.')) return null
      const absolute = resolve(dirname(importer), source).replaceAll('\\', '/')
      return replacements.get(absolute.replace(/\.(tsx?|jsx?)$/, '')) ?? null
    },
    generateBundle(_, bundle) {
      for (const output of Object.values(bundle)) {
        if (output.type !== 'chunk') continue
        for (const module of Object.keys(output.modules)) {
          if (/\/(?:menu\/(?:Gbay|gbay)|visualHarness|gta\/demoTransport)/.test(module.replaceAll('\\', '/'))) {
            this.error(`Consumer-only module leaked into the runtime: ${module}`)
          }
        }
        if (/allin1|gbay/i.test(output.code)) this.error(`Consumer content leaked into ${output.fileName}`)
      }
    },
    writeBundle(options) {
      // An allowlist, not public-directory copying: future consumer assets cannot leak.
      for (const file of runtimeAssets) {
        const destination = resolve(options.dir!, file)
        mkdirSync(dirname(destination), { recursive: true })
        copyFileSync(resolve(projectRoot, 'public', file), destination)
      }
      const directory = options.dir!
      const files = readdirSync(directory, { recursive: true, withFileTypes: true })
        .filter((entry) => entry.isFile())
        .map((entry) => {
          const absolute = resolve(entry.parentPath, entry.name)
          const path = absolute.slice(resolve(directory).length + 1).replaceAll('\\', '/')
          return { path, sha256: createHash('sha256').update(readFileSync(absolute)).digest('hex') }
        }).sort((a, b) => a.path.localeCompare(b.path))
      writeFileSync(resolve(directory, 'reactor-ui.json'), JSON.stringify({
        schema_version: 1, profile: 'reactor-runtime', contains_consumer_content: false, files,
      }, null, 2))
    },
  }
}

export default defineConfig(({ mode }) => ({
  plugins: [react(), ...(mode === 'allin1' || mode === 'test' ? [] : [standaloneBoundary()])],
  base: './',
  publicDir: mode === 'allin1' || mode === 'test' ? 'public' : false,
  build: {
    outDir: mode === 'allin1' ? 'dist-allin1' : 'dist',
    emptyOutDir: true,
    // Production source maps are useful to developers but add almost a
    // megabyte to every installed UI payload. The source tree retains full
    // TypeScript diagnostics and tests without shipping maps to players.
    sourcemap: false,
    rollupOptions: {
      input: {
        app: `${projectRoot}index.html`,
        sdk: `${projectRoot}src/sdk.ts`,
      },
      output: {
        entryFileNames: (chunk) => chunk.name === 'sdk' ? 'ragewebui.js' : 'assets/[name]-[hash].js',
      },
    },
  },
}))
