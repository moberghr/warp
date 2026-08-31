import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { isDemoMode, freezeClock } from '@/lib/demoMode'
import { TooltipProvider } from '@/components/ui/tooltip'

async function boot() {
  if (isDemoMode()) {
    // Pin the clock so demo data, "X minutes ago" labels, and hour-bucketed charts
    // are deterministic. Must run BEFORE the demo module loads — data.ts seeds
    // timestamps at import time, so a later override wouldn't reach those seeds.
    freezeClock()

    const { setupDemo } = await import('@/demo')
    setupDemo()
  }

  // Note: realtime probe + hub connection is NOT started here. It runs from
  // MainLayout's useEffect so that page-level useRealtimeRefetch subscribers
  // are guaranteed to have registered before the post-connect drain emits.
  // Triggering it at module load races React's useEffect cycle and the drain
  // fires before subscribers exist, leaving the dashboard stale until the 30s
  // safety-net interval.

  // TooltipProvider owns the shared hover delay for every Tooltip in the app — in this version of
  // Base UI the delay lives on the provider, not on the individual Tooltip, so mounting it here
  // means a tooltip anywhere gets the same 300ms rather than opening instantly.
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <TooltipProvider>
        <App />
      </TooltipProvider>
    </StrictMode>,
  )
}

boot()
