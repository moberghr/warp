import { Suspense, useCallback, useEffect, useState } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { useDashboardStore } from '@/stores/dashboard';
import { useRealtimeStore } from '@/stores/realtime';
import * as api from '@/api';
import type { ExtensionManifest } from '@/extensions/types';
import WarpSidebar from '@/layouts/WarpSidebar';
import WarpTopnav from '@/layouts/WarpTopnav';
import WarpStatusbar from '@/layouts/WarpStatusbar';
import PageHeader from '@/layouts/PageHeader';
import MobileDrawer from '@/layouts/MobileDrawer';
import { buildWarpNavItems } from '@/layouts/warpNavItems';
import { useRealtimeInvalidation } from '@/hooks/useRealtimeInvalidation';
import { startRealtimeFeed, stopRealtimeFeed } from '@/lib/realtimeFeed';

export default function MainLayout({ extensions = [] }: { extensions?: ExtensionManifest[] }) {
  const error = useDashboardStore((s) => s.error);
  const location = useLocation();
  const [concurrencyAvailable, setConcurrencyAvailable] = useState(false);
  const [rateLimitsAvailable, setRateLimitsAvailable] = useState(false);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [sagasAvailable, setSagasAvailable] = useState(false);
  const [servicesAvailable, setServicesAvailable] = useState(false);

  useRealtimeInvalidation();

  // Initial fetch for first paint. Further updates arrive via SignalR push.
  useEffect(() => {
    void useDashboardStore.getState().fetchStats();
  }, []);

  useEffect(() => {
    startRealtimeFeed();
    return () => stopRealtimeFeed();
  }, []);

  // One discovery call. Replaces three speculative hide-on-404 probes that previously
  // showed as red 404s in DevTools. The result also drives the realtime hub connect
  // decision, so the dashboard makes a single addon-status round-trip per session.
  // A transient 5xx / network blip used to take down only one nav slot under the old
  // per-probe design; with a single endpoint we retry once after a short delay so a
  // momentary failure doesn't hide all addon nav and push for the rest of the session.
  useEffect(() => {
    let cancelled = false;

    const fetchAddons = async () => {
      try {
        return await api.getAddons();
      } catch {
        await new Promise((resolve) => setTimeout(resolve, 750));
        return await api.getAddons();
      }
    };

    fetchAddons()
      .then((addons) => {
        if (cancelled) return;
        setConcurrencyAvailable(addons.concurrency);
        setRateLimitsAvailable(addons.rateLimits);
        setSagasAvailable(addons.sagas);
        setServicesAvailable(addons.services);
        void useRealtimeStore.getState().connectIfEnabled(addons.push);
      })
      .catch(() => {
        if (cancelled) return;
        setConcurrencyAvailable(false);
        setRateLimitsAvailable(false);
        setSagasAvailable(false);
        setServicesAvailable(false);
        void useRealtimeStore.getState().connectIfEnabled(false);
      });

    return () => {
      cancelled = true;
    };
  }, []);

  // Close drawer on route change.
  useEffect(() => {
    setDrawerOpen(false);
  }, [location.pathname]);

  const navItems = buildWarpNavItems(
    extensions,
    concurrencyAvailable,
    rateLimitsAvailable,
    sagasAvailable,
    servicesAvailable,
  );

  const openDrawer = useCallback(() => setDrawerOpen(true), []);
  const closeDrawer = useCallback(() => setDrawerOpen(false), []);

  return (
    <div className="relative h-screen flex flex-col bg-background text-foreground overflow-hidden">
      <WarpTopnav items={navItems} onMenuClick={openDrawer} />

      <div className="flex-1 flex flex-col min-w-0 min-h-0 relative">
        {error && (
          <div
            role="alert"
            className="mx-3 sm:mx-4 lg:mx-8 mt-3 rounded-md bg-warp-red-soft ring-1 ring-warp-red/30 px-3 py-2 text-sm text-warp-red flex items-center gap-2"
          >
            <span className="font-medium">Connection lost</span>
            <span className="opacity-80">— Unable to connect to Warp API. Retrying...</span>
            <button
              type="button"
              onClick={() => window.location.reload()}
              className="ml-auto rounded border border-warp-red/40 px-2 py-0.5 text-xs font-medium hover:bg-warp-red/10"
            >
              Retry now
            </button>
          </div>
        )}

        <main className="flex-1 px-3 sm:px-6 lg:px-8 pb-4 min-w-0 overflow-auto">
          <PageHeader />
          <Suspense fallback={null}>
            <Outlet />
          </Suspense>
        </main>

        <WarpStatusbar />
      </div>

      <MobileDrawer open={drawerOpen} onOpenChange={setDrawerOpen}>
        <WarpSidebar items={navItems} mobile onNavigate={closeDrawer} />
      </MobileDrawer>
    </div>
  );
}
