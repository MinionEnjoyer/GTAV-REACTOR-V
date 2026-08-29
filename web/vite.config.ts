import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'

const projectRoot = fileURLToPath(new URL('.', import.meta.url))

export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir: 'dist',
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
})
