import { defineConfig, type Plugin, type PluginOption } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'
import fs from 'fs'

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

export default defineConfig(async ({ mode }) => {
  const isDemo = mode === 'demo'

  // Bundle analysis is opt-in: `ANALYZE=1 npm run build` emits bundle-stats.html.
  // Loaded via dynamic import so normal/CI builds never touch the devDependency.
  const plugins: PluginOption[] = [react(), tailwindcss(), serveExtensions()]
  if (process.env.ANALYZE) {
    const { visualizer } = await import('rollup-plugin-visualizer')
    plugins.push(
      visualizer({ filename: 'bundle-stats.html', gzipSize: true, brotliSize: true, template: 'treemap' }) as PluginOption,
    )
  }

  return {
    plugins,
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
    server: isDemo
      ? {}
      : {
          proxy: {
            '/warp': 'http://localhost:5104',
          },
        },
  }
})
