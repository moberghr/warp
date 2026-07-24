import { Suspense, useEffect, useState } from 'react';
import { Link, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { useDashboardStore } from '@/stores/dashboard';
import * as LucideIcons from 'lucide-react';
import {
  LayoutDashboard,
  Briefcase,
  Mail,
  Layers,
  RefreshCw,
  Server,
  Moon,
  Sun,
  LogOut,
  Puzzle,
  Gauge,
  KeyRound,
  Timer,
  GitBranch,
  Activity,
  Cable,
  ArrowDownToLine,
  Webhook,
  Menu,
  X,
  ExternalLink,
} from 'lucide-react';
import { useTheme } from '@/hooks/useTheme';
import { useRealtimeInvalidation } from '@/hooks/useRealtimeInvalidation';
import { useRealtimeStore } from '@/stores/realtime';
import { startRealtimeFeed, stopRealtimeFeed } from '@/lib/realtimeFeed';
import { config } from '@/config';
import * as api from '@/api';
import type { DashboardStatistics } from '@/types';
import type { ExtensionManifest } from '@/extensions/types';

const builtInNavItems = [
  { to: '/', label: 'Dashboard', icon: LayoutDashboard },
  { to: '/jobs/enqueued', label: 'Jobs', icon: Briefcase },
  { to: '/messages/enqueued', label: 'Messages', icon: Mail },
  { to: '/batches/processing', label: 'Batches', icon: Layers },
  { to: '/recurring', label: 'Recurring', icon: RefreshCw },
  { to: '/applications', label: 'Applications', icon: Server },
  { to: '/counters', label: 'Counters', icon: Gauge },
];

const concurrencyNavItem = { to: '/concurrency', label: 'Concurrency', icon: KeyRound };
const rateLimitsNavItem = { to: '/ratelimits', label: 'Rate Limits', icon: Timer };
const sagasNavItem = { to: '/sagas', label: 'Sagas', icon: GitBranch };
const adaptersNavItem = { to: '/adapters', label: 'Adapters', icon: Cable };
const endpointsNavItem = { to: '/endpoints', label: 'Endpoints', icon: ArrowDownToLine };
const webhooksNavItem = { to: '/webhooks', label: 'Webhooks', icon: Webhook };
const servicesNavItem = { to: '/services', label: 'Services', icon: Activity };

function resolveIcon(name?: string): React.ComponentType<{ className?: string }> {
  if (!name) {
    return Puzzle;
  }

  // Convert kebab-case to PascalCase (e.g., "refresh-cw" → "RefreshCw")
  const pascalCase = name
    .split('-')
    .map((s) => s.charAt(0).toUpperCase() + s.slice(1))
    .join('');
  const icons = LucideIcons as Record<string, unknown>;

  return (icons[pascalCase] as React.ComponentType<{ className?: string }>) ?? Puzzle;
}

export default function MainLayout({ extensions = [] }: { extensions?: ExtensionManifest[] }) {
  const { stats, error, fetchStats } = useDashboardStore();
  const location = useLocation();
  const navigate = useNavigate();
  const { theme, toggle } = useTheme();
  const realtimeStatus = useRealtimeStore((s) => s.status);
  const [concurrencyAvailable, setConcurrencyAvailable] = useState(false);
  const [rateLimitsAvailable, setRateLimitsAvailable] = useState(false);
  const [sagasAvailable, setSagasAvailable] = useState(false);
  const [adaptersAvailable, setAdaptersAvailable] = useState(false);
  const [endpointsAvailable, setEndpointsAvailable] = useState(false);
  const [webhooksAvailable, setWebhooksAvailable] = useState(false);
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);

  // Close the mobile nav whenever the route changes so tapping an item dismisses it.
  useEffect(() => { setMobileMenuOpen(false); }, [location.pathname]);

  // Bridge realtime hub events into React Query invalidation for any page that
  // uses the useQuery-based hooks. Pages still on the older `useRealtimeRefetch`
  // pattern keep working in parallel until they're migrated.
  useRealtimeInvalidation();

  // Initial fetch for first paint — after this, fresh stats arrive directly via
  // the SignalR push payload on every JobFinalized / MessageEnqueued event (see
  // bridgeEvent in stores/realtime.ts) and are written straight into the dashboard
  // store. No event-driven REST refetch is needed for stats; the bus emit fired
  // by the bridge still wakes other pages (jobs, counters, etc.) to refetch their
  // own scoped views.
  useEffect(() => { fetchStats(); }, [fetchStats]);

  // Distinguish browser tabs across deployments (#241): append the host-configured instance name to
  // the tab title so prod/staging/etc. tabs aren't all just "Warp".
  useEffect(() => {
    document.title = config.instanceName ? `Warp · ${config.instanceName}` : 'Warp';
  }, []);

  // The realtime chart binds to `useDashboardStore.realtimeData` as a pure
  // renderer. The feed module owns the freshness source (SignalR push or 1 Hz
  // poll) and the 1 Hz sampler that appends delta points. Running it here
  // (rather than inside RealtimeChart) keeps the time-series accumulating
  // while the user is on other dashboard pages.
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
        setAdaptersAvailable(addons.adapters);
        setEndpointsAvailable(addons.endpoints);
        setWebhooksAvailable(addons.webhooks);
        void useRealtimeStore.getState().connectIfEnabled(addons.push);
      })
      .catch(() => {
        if (cancelled) return;
        setConcurrencyAvailable(false);
        setRateLimitsAvailable(false);
        setSagasAvailable(false);
        setAdaptersAvailable(false);
        setEndpointsAvailable(false);
        setWebhooksAvailable(false);
        void useRealtimeStore.getState().connectIfEnabled(false);
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const isJobsSection = location.pathname.startsWith('/jobs');
  const isBatchesSection = location.pathname.startsWith('/batches');
  const isMessagesSection = location.pathname.startsWith('/messages');

  const navItems = [
    ...builtInNavItems,
    ...(concurrencyAvailable ? [concurrencyNavItem] : []),
    ...(rateLimitsAvailable ? [rateLimitsNavItem] : []),
    ...(sagasAvailable ? [sagasNavItem] : []),
    ...(adaptersAvailable ? [adaptersNavItem] : []),
    ...(endpointsAvailable ? [endpointsNavItem] : []),
    ...(webhooksAvailable ? [webhooksNavItem] : []),
    servicesNavItem,
    ...extensions.flatMap((ext) =>
      ext.pages.map((page) => ({
        to: page.path,
        label: page.label,
        icon: resolveIcon(page.icon),
      }))
    ),
  ];

  const renderNavItem = (item: (typeof navItems)[number]) => {
    const Icon = item.icon;
    const matchPath = item.to.includes('/') && item.to !== '/'
      ? '/' + item.to.split('/')[1]
      : item.to;
    const isActive = item.to === '/'
      ? location.pathname === '/'
      : location.pathname.startsWith(matchPath);
    return (
      <Link
        key={item.to}
        to={item.to}
        onClick={(e) => {
          setMobileMenuOpen(false);
          if (isActive) {
            e.preventDefault();
            navigate(item.to, { replace: true, state: { refreshKey: Date.now() } });
          }
        }}
        className={`flex items-center gap-2 px-3 py-2 rounded-md text-sm font-medium transition-colors ${
          isActive
            ? 'bg-primary text-primary-foreground'
            : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground'
        }`}
      >
        <Icon className="h-4 w-4" />
        {item.label}
        {item.label === 'Jobs' && stats && stats.created > 0 && (
          <span className="ml-1 text-xs min-w-10 text-center tabular-nums px-1.5 py-0.5 rounded-full bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300">
            {stats.created}
          </span>
        )}
        {item.label === 'Jobs' && stats && stats.failed > 0 && (
          <span className="text-xs min-w-10 text-center tabular-nums px-1.5 py-0.5 rounded-full bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-300 font-bold">
            {stats.failed}
          </span>
        )}
        {item.label === 'Messages' && stats && stats.messages > 0 && (
          <span className="ml-1 text-xs min-w-10 text-center tabular-nums px-1.5 py-0.5 rounded-full bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300">
            {stats.messages}
          </span>
        )}
        {item.label === 'Messages' && stats && stats.messagesFailed > 0 && (
          <span className="text-xs min-w-10 text-center tabular-nums px-1.5 py-0.5 rounded-full bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-300 font-bold">
            {stats.messagesFailed}
          </span>
        )}
        {item.label === 'Batches' && stats && stats.batchesProcessing > 0 && (
          <span className="ml-1 text-xs min-w-10 text-center tabular-nums px-1.5 py-0.5 rounded-full bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300">
            {stats.batchesProcessing}
          </span>
        )}
        {item.label === 'Batches' && stats && stats.batchesFailed > 0 && (
          <span className="text-xs min-w-10 text-center tabular-nums px-1.5 py-0.5 rounded-full bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-300 font-bold">
            {stats.batchesFailed}
          </span>
        )}
        {item.label === 'Applications' && stats && (
          <span className="ml-1 text-xs min-w-10 text-center tabular-nums px-1.5 py-0.5 rounded-full bg-muted text-muted-foreground">{stats.servers}</span>
        )}
      </Link>
    );
  };

  return (
    <div className="min-h-screen bg-background flex flex-col overflow-x-hidden">
      {/* Top navbar */}
      <header className="border-b bg-card">
        <div className="flex h-14 items-center px-4 md:px-6">
          <Link to="/" className="flex items-center gap-2 mr-4 md:mr-8">
            {config.logoUrl
              ? <img src={config.logoUrl} alt="" className="h-6 w-auto" />
              : <span className="text-lg font-bold">Warp</span>}
            {config.instanceName && (
              <span className="rounded bg-primary/10 px-2 py-0.5 text-xs font-semibold text-primary whitespace-nowrap">{config.instanceName}</span>
            )}
          </Link>
          {/* Desktop nav — collapses to a hamburger below md so the row never
              exceeds the viewport width on phones. */}
          <nav className="hidden md:flex gap-1">
            {navItems.map(renderNavItem)}
          </nav>
          <div className="flex-1" />
          {config.portalUrl && (
            <a
              href={config.portalUrl}
              className="hidden md:flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground mr-3"
            >
              <ExternalLink className="h-4 w-4" />{config.portalLabel}
            </a>
          )}
          <RealtimeStatusIndicator status={realtimeStatus} />
          <button onClick={toggle} className="p-2 rounded-md hover:bg-accent text-muted-foreground">
            {theme === 'dark' ? <Sun className="h-4 w-4" /> : <Moon className="h-4 w-4" />}
          </button>
          {config.hasBuiltInLogin && (
            <button
              onClick={async () => {
                await fetch(`${config.apiPath}auth/logout`, { method: 'POST' });
                window.location.reload();
              }}
              className="p-2 rounded-md hover:bg-accent text-muted-foreground ml-1"
              title="Logout"
            >
              <LogOut className="h-4 w-4" />
            </button>
          )}
          <button
            onClick={() => setMobileMenuOpen((open) => !open)}
            className="md:hidden p-2 rounded-md hover:bg-accent text-muted-foreground ml-1"
            aria-label="Toggle navigation menu"
            aria-expanded={mobileMenuOpen}
          >
            {mobileMenuOpen ? <X className="h-5 w-5" /> : <Menu className="h-5 w-5" />}
          </button>
        </div>
        {mobileMenuOpen && (
          <nav className="md:hidden flex flex-col gap-1 border-t px-3 py-2 max-h-[75vh] overflow-y-auto">
            {navItems.map(renderNavItem)}
          </nav>
        )}
      </header>

      {error && (
        <div className="bg-destructive/10 border-b border-destructive/20 px-6 py-2 text-sm text-destructive flex items-center gap-2">
          <span className="font-medium">Connection lost</span>
          <span className="text-destructive/70">— Unable to connect to Warp API. Retrying...</span>
        </div>
      )}

      <div className="flex flex-1">
        {isJobsSection && <JobsSidebar stats={stats} />}
        {isBatchesSection && <BatchesSidebar stats={stats} />}
        {isMessagesSection && <MessagesSidebar stats={stats} />}

        <main className="flex-1 min-w-0 p-4 md:p-6">
          {/* Max-width + center so dashboard content is readable on ultra-wide displays
              without the cards floating off to the left of empty whitespace. 1536px
              (Tailwind's max-w-screen-2xl) matches what most modern admin dashboards
              converge on. Section sidebars live outside this wrapper so they hug the
              viewport edge. */}
          <div className="max-w-screen-2xl mx-auto">
            {/* Boundary for the code-split route pages (see App.tsx). The shell/nav
                above stays mounted while the next page's chunk loads. */}
            <Suspense fallback={null}>
              <Outlet />
            </Suspense>
          </div>
        </main>
      </div>

      {/* Footer */}
      <footer className="border-t bg-card px-4 md:px-6 py-3 text-xs text-muted-foreground flex flex-wrap items-center justify-between gap-2">
        <span>{stats?.databaseConnection ?? 'Warp Dashboard'}</span>
        <div className="flex items-center gap-4 tabular-nums">
          {stats && <span>Servers: {stats.servers} · Workers active</span>}
          <span>UTC: {new Date().toISOString().replace('T', ' ').substring(0, 19)}</span>
        </div>
      </footer>
    </div>
  );
}

function RealtimeStatusIndicator({ status }: { status: ReturnType<typeof useRealtimeStore.getState>['status'] }) {
  // 'disabled' indicator is hidden in production: when the addon is not registered
  // we don't want to imply something is wrong — polling fallback is the supported
  // path. Visible in dev to surface "did the probe actually succeed" while iterating.
  if (status === 'disabled' && !import.meta.env.DEV) {
    return null;
  }
  if (status === 'idle') {
    return null;
  }

  const styles: Record<string, { dot: string; label: string; title: string }> = {
    connected: { dot: 'bg-green-500', label: 'Live', title: 'Realtime push connected' },
    connecting: { dot: 'bg-amber-500 animate-pulse', label: 'Connecting', title: 'Connecting realtime push…' },
    reconnecting: { dot: 'bg-amber-500 animate-pulse', label: 'Reconnecting', title: 'Reconnecting realtime push…' },
    disabled: { dot: 'bg-muted-foreground/40', label: 'Polling', title: 'Realtime push disabled; using polling fallback' },
  };
  const s = styles[status];
  if (!s) return null;

  return (
    <span className="flex items-center justify-end gap-1.5 min-w-28 px-2 py-1 mr-1 text-xs text-muted-foreground" title={s.title}>
      <span className={`h-2 w-2 rounded-full ${s.dot}`} />
      <span>{s.label}</span>
    </span>
  );
}

function JobsSidebar({ stats }: { stats: DashboardStatistics | null }) {
  const location = useLocation();
  const navigate = useNavigate();

  const sidebarItems = [
    { to: '/jobs/enqueued', label: 'Enqueued', count: stats?.created ?? 0, color: 'bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300' },
    { to: '/jobs/scheduled', label: 'Scheduled', count: stats?.scheduled ?? 0, color: 'bg-yellow-100 text-yellow-700 dark:bg-yellow-900 dark:text-yellow-300' },
    { to: '/jobs/processing', label: 'Processing', count: stats?.processing ?? 0, color: 'bg-purple-100 text-purple-700 dark:bg-purple-900 dark:text-purple-300' },
    { to: '/jobs/completed', label: 'Completed', count: stats?.completed ?? 0, color: 'bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300' },
    { to: '/jobs/failed', label: 'Failed', count: stats?.failed ?? 0, color: 'bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-300' },
    { to: '/jobs/awaiting', label: 'Awaiting', count: stats?.awaiting ?? 0, color: 'bg-orange-100 text-orange-700 dark:bg-orange-900 dark:text-orange-300' },
    { to: '/jobs/deleted', label: 'Deleted', count: stats?.deleted ?? 0, color: 'bg-gray-100 text-gray-700 dark:bg-gray-900 dark:text-gray-300' },
  ];

  return (
    <aside className="hidden md:block w-64 shrink-0 border-r bg-card min-h-[calc(100vh-3.5rem)] p-4">
      <h3 className="text-xs font-semibold text-muted-foreground uppercase mb-3">Jobs</h3>
      <nav className="space-y-1">
        {sidebarItems.map((item) => {
          const isActive = location.pathname === item.to;
          return (
            <Link
              key={item.to}
              to={item.to}
              onClick={(e) => {
                if (isActive) {
                  e.preventDefault();
                  navigate(item.to, { replace: true, state: { refreshKey: Date.now() } });
                }
              }}
              className={`flex items-center justify-between px-3 py-2 rounded-md text-sm transition-colors ${
                isActive
                  ? 'bg-accent text-accent-foreground font-medium'
                  : 'text-muted-foreground hover:bg-accent/50'
              }`}
            >
              <span>{item.label}</span>
              <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${
                item.count > 0 ? item.color : 'text-muted-foreground/50'
              }`}>
                {item.count}
              </span>
            </Link>
          );
        })}
      </nav>
    </aside>
  );
}

function SidebarNav({ title, items }: { title: string; items: { to: string; label: string; count: number; color: string }[] }) {
  const location = useLocation();
  const navigate = useNavigate();

  return (
    <aside className="hidden md:block w-64 shrink-0 border-r bg-card min-h-[calc(100vh-3.5rem)] p-4">
      <h3 className="text-xs font-semibold text-muted-foreground uppercase mb-3">{title}</h3>
      <nav className="space-y-1">
        {items.map((item) => {
          const isActive = location.pathname === item.to;
          return (
            <Link
              key={item.to}
              to={item.to}
              onClick={(e) => {
                if (isActive) {
                  e.preventDefault();
                  navigate(item.to, { replace: true, state: { refreshKey: Date.now() } });
                }
              }}
              className={`flex items-center justify-between px-3 py-2 rounded-md text-sm transition-colors ${
                isActive
                  ? 'bg-accent text-accent-foreground font-medium'
                  : 'text-muted-foreground hover:bg-accent/50'
              }`}
            >
              <span>{item.label}</span>
              <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${
                item.count > 0 ? item.color : 'text-muted-foreground/50'
              }`}>
                {item.count}
              </span>
            </Link>
          );
        })}
      </nav>
    </aside>
  );
}

function BatchesSidebar({ stats }: { stats: DashboardStatistics | null }) {
  return (
    <SidebarNav title="Batches" items={[
      { to: '/batches/processing', label: 'Processing', count: stats?.batchesProcessing ?? 0, color: 'bg-purple-100 text-purple-700 dark:bg-purple-900 dark:text-purple-300' },
      { to: '/batches/awaiting', label: 'Awaiting', count: stats?.batchesAwaiting ?? 0, color: 'bg-orange-100 text-orange-700 dark:bg-orange-900 dark:text-orange-300' },
      { to: '/batches/completed', label: 'Completed', count: stats?.batchesCompleted ?? 0, color: 'bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300' },
      { to: '/batches/failed', label: 'Failed', count: stats?.batchesFailed ?? 0, color: 'bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-300' },
      { to: '/batches/deleted', label: 'Deleted', count: stats?.batchesDeleted ?? 0, color: 'bg-gray-100 text-gray-700 dark:bg-gray-900 dark:text-gray-300' },
    ]} />
  );
}

function MessagesSidebar({ stats }: { stats: DashboardStatistics | null }) {
  return (
    <SidebarNav title="Messages" items={[
      { to: '/messages/enqueued', label: 'Enqueued', count: stats?.messagesEnqueued ?? 0, color: 'bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300' },
      { to: '/messages/processing', label: 'Processing', count: stats?.messagesProcessing ?? 0, color: 'bg-purple-100 text-purple-700 dark:bg-purple-900 dark:text-purple-300' },
      { to: '/messages/completed', label: 'Completed', count: stats?.messagesCompleted ?? 0, color: 'bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300' },
      { to: '/messages/failed', label: 'Failed', count: stats?.messagesFailed ?? 0, color: 'bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-300' },
    ]} />
  );
}
