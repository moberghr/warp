import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'

async function boot() {
  const isDemoMode = new URLSearchParams(window.location.search).has('demo')
    || import.meta.env.VITE_DEMO === 'true'

  if (isDemoMode) {
    // Pin the clock so demo data + "X minutes ago" labels are deterministic.
    // Must run BEFORE the demo module loads — `data.ts` reads `Date.now()`
    // at the top level into a `const NOW`, so a later override wouldn't
    // reach that seed.
    const FROZEN_NOW = Date.UTC(2026, 4, 25, 11, 0, 0)
    Date.now = () => FROZEN_NOW

    const { setupDemo } = await import('@/demo')
    setupDemo()
  }

  // Note: realtime probe + hub connection is NOT started here. It runs from
  // MainLayout's useEffect so that page-level useRealtimeRefetch subscribers
  // are guaranteed to have registered before the post-connect drain emits.
  // Triggering it at module load races React's useEffect cycle and the drain
  // fires before subscribers exist, leaving the dashboard stale until the 30s
  // safety-net interval.

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  )
}

boot()
