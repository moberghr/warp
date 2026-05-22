/// <reference types="vitest" />
import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'
import fs from 'fs'
import { execSync } from 'child_process'

function gitInfo() {
  const safe = (cmd: string, fallback = '') => {
    try { return execSync(cmd, { stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim() }
    catch { return fallback }
  }
  // Tag (with `v` prefix stripped) or short sha as fallback.
  const raw = safe('git describe --tags --abbrev=0', '') || safe('git rev-parse --short HEAD', 'dev')
  const version = raw.replace(/^v/, '')
  const commit = safe('git rev-parse --short HEAD', 'dev')
  const buildDate = safe('git log -1 --format=%cd --date=short', new Date().toISOString().slice(0, 10))
  return { version, commit, buildDate }
}

/**
 * In demo mode, serves UI extension JS files from their source location
 * so Playwright screenshot tests can load them via dynamic import().
 */
function serveExtensions(): Plugin {
  const extDir = path.resolve(__dirname, '../core/Warp.UI/Extensions')
  return {
    name: 'serve-extensions',
    // Use enforce: 'pre' so this runs before Vite's transform pipeline
    enforce: 'pre',
    configureServer(server) {
      // Pre-hook: runs before Vite's SPA fallback so extension JS is served directly
      server.middlewares.use((req, res, next) => {
          // Strip Vite's ?import query parameter
          const rawUrl = req.url ?? ''
          const url = rawUrl.split('?')[0]
          const prefix = '/warp/_ext/'
          if (!url.startsWith(prefix)) {
            return next()
          }

          // Map /_ext/{name}/file.js → Extensions/{Name}/dist/file.js
          const relPath = url.slice(prefix.length)
          const [extName, ...rest] = relPath.split('/')
          const filePath = path.join(extDir, extName.charAt(0).toUpperCase() + extName.slice(1), 'dist', ...rest)

          if (fs.existsSync(filePath)) {
            res.setHeader('Content-Type', 'application/javascript')
            fs.createReadStream(filePath).pipe(res)
          } else {
            next()
          }
        })
    },
  }
}

export default defineConfig(({ mode }) => {
  const isDemo = mode === 'demo'
  const git = gitInfo()
  // Vite exposes any process.env var prefixed with VITE_ on import.meta.env
  // at build/dev time. This is the supported escape hatch when define-based
  // substitution misbehaves with JSX/TSX (Vite v8 + plugin-react).
  process.env.VITE_APP_VERSION = git.version
  process.env.VITE_APP_COMMIT = git.commit
  process.env.VITE_APP_BUILD_DATE = git.buildDate

  return {
    plugins: [react(), tailwindcss(), serveExtensions()],
    base: './',
    resolve: {
      alias: {
        '@': path.resolve(__dirname, './src'),
      },
    },
    build: {
      outDir: '../core/Warp.UI/dist',
      emptyOutDir: true,
    },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/test/setup.ts'],
      include: ['src/**/*.{test,spec}.{ts,tsx}'],
      css: false,
    },
    server: isDemo
      ? { allowedHosts: ['.trycloudflare.com'] }
      : {
          allowedHosts: ['.trycloudflare.com'],
          proxy: {
            '/warp/api': {
              target: 'http://localhost:5104',
              ws: true,
            },
          },
        },
  }
})
