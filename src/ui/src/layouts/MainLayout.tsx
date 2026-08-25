import { Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { useDashboardStore } from '@/stores/dashboard';
import * as LucideIcons from 'lucide-react';
import {
  Moon,
  Sun,
  LogOut,
  Puzzle,
  Menu,
  X,
  ExternalLink,
  ChevronDown,
  Check,
  Search,
} from 'lucide-react';
import { useTheme } from '@/hooks/useTheme';
import { useRealtimeInvalidation } from '@/hooks/useRealtimeInvalidation';
import { useRealtimeStore } from '@/stores/realtime';
import { startRealtimeFeed, stopRealtimeFeed } from '@/lib/realtimeFeed';
import { config } from '@/config';
import * as api from '@/api';
import type { DashboardStatistics, WarpAddonsInfo } from '@/types';
import type { ExtensionManifest } from '@/extensions/types';
import {
  COUNTER_FAMILY_GROUP,
  NAV_GROUPS,
  PANEL_WIDTH,
  TOP_LEVEL_NAV_ITEMS,
  badgesForItem,
  clampPanelLeft,
  flattenNavTargets,
  gateGroups,
  isNavItemActive,
  resolveActiveLocation,
  rollUpBadges,
  type NavGroup,
  type NavItem,
} from './navModel';
import { CommandPalette } from '@/components/CommandPalette';
import { isPaletteShortcut, shortcutHint } from '@/lib/shortcut';

// min-w keeps a changing count from jittering the bar; the full 40px is a
// luxury only the widest tier can afford.
const BADGE_BASE = 'text-xs min-w-6 xl:min-w-10 text-center tabular-nums px-1.5 py-0.5 rounded-full';
const BADGE_PENDING = `${BADGE_BASE} bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300`;
const BADGE_FAILED = `${BADGE_BASE} bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-300 font-bold`;
const BADGE_NEUTRAL = `${BADGE_BASE} bg-muted text-muted-foreground`;

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
  const [addons, setAddons] = useState<WarpAddonsInfo | null>(null);
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);
  const [openGroup, setOpenGroup] = useState<string | null>(null);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [panelLeft, setPanelLeft] = useState(0);
  const navRef = useRef<HTMLElement | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);
  const openTriggerRef = useRef<HTMLButtonElement | null>(null);

  // Close the mobile nav and any open group panel whenever the route changes, so
  // tapping an item dismisses the shell it was tapped in.
  useEffect(() => {
    setMobileMenuOpen(false);
    setOpenGroup(null);
  }, [location.pathname]);

  // The palette shortcut is global — it has to beat the browser's own Ctrl+K,
  // hence preventDefault rather than a listener on the header.
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      // e.repeat: holding the chord auto-repeats keydown, and a toggle on every repeat
      // flips the palette open and shut until the key is released.
      if (!isPaletteShortcut(e) || e.repeat) {
        return;
      }

      e.preventDefault();
      setOpenGroup(null);
      setPaletteOpen((x) => !x);
    };

    document.addEventListener('keydown', onKeyDown);

    return () => document.removeEventListener('keydown', onKeyDown);
  }, []);

  // The panel is anchored to the header, so its offset is the trigger's position
  // within that header — clamped, or the right-hand groups hang off the edge and
  // the shell's overflow-x-hidden eats their second column.
  const measurePanel = useCallback((trigger: HTMLButtonElement | null) => {
    openTriggerRef.current = trigger;
    const header = trigger?.closest('header');
    if (!trigger || !header) {
      return;
    }

    const offset = trigger.getBoundingClientRect().left - header.getBoundingClientRect().left;
    setPanelLeft(clampPanelLeft(offset, header.clientWidth));
  }, []);

  useEffect(() => {
    if (!openGroup) {
      return;
    }

    const onPointerDown = (e: MouseEvent) => {
      const target = e.target as Node;
      if (navRef.current?.contains(target) || panelRef.current?.contains(target)) {
        return;
      }

      setOpenGroup(null);
    };
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setOpenGroup(null);
        openTriggerRef.current?.focus();
      }
    };
    const onResize = () => measurePanel(openTriggerRef.current);

    document.addEventListener('mousedown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    window.addEventListener('resize', onResize);

    return () => {
      document.removeEventListener('mousedown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('resize', onResize);
    };
  }, [openGroup, measurePanel]);

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
    document.title = config.instanceName
      ? `${config.brandName} · ${config.instanceName}`
      : config.brandName;
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
      .then((info) => {
        if (cancelled) return;
        setAddons(info);
        void useRealtimeStore.getState().connectIfEnabled(info.push);
      })
      .catch(() => {
        if (cancelled) return;
        setAddons(null);
        void useRealtimeStore.getState().connectIfEnabled(false);
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const isJobsSection = location.pathname.startsWith('/jobs');
  const isBatchesSection = location.pathname.startsWith('/batches');
  const isMessagesSection = location.pathname.startsWith('/messages');

  const navGroups = useMemo(() => gateGroups(NAV_GROUPS, addons), [addons]);

  // Extension pages have no group — they keep their own top-level slot, since
  // their labels are host-supplied and can't be assigned a Warp category.
  const extensionNavItems = useMemo<NavItem[]>(
    () => extensions.flatMap((ext) =>
      ext.pages.map((page) => ({
        to: page.path,
        label: page.label,
        icon: resolveIcon(page.icon),
      }))
    ),
    [extensions]
  );

  const active = useMemo(
    () => resolveActiveLocation(location.pathname, [...TOP_LEVEL_NAV_ITEMS, ...extensionNavItems], navGroups),
    [location.pathname, extensionNavItems, navGroups]
  );
  const paletteTargets = useMemo(
    () => flattenNavTargets(TOP_LEVEL_NAV_ITEMS, [...navGroups, COUNTER_FAMILY_GROUP], extensionNavItems),
    [navGroups, extensionNavItems]
  );

  // Clicking the already-active item re-navigates with a fresh key so the page
  // refetches instead of the router treating it as a no-op.
  const handleNavClick = (item: NavItem, isActive: boolean) => (e: React.MouseEvent) => {
    setMobileMenuOpen(false);
    setOpenGroup(null);
    if (isActive) {
      e.preventDefault();
      navigate(item.to, { replace: true, state: { refreshKey: Date.now() } });
    }
  };

  // alwaysLabel is for the mobile sheet: it reuses this renderer but gives each
  // item a full row, so the bar's icon-only compression must not follow it there.
  const renderNavItem = (item: NavItem, alwaysLabel = false) => {
    const Icon = item.icon;
    const isActive = isNavItemActive(item.to, location.pathname);

    return (
      <Link
        key={item.to}
        to={item.to}
        onClick={handleNavClick(item, isActive)}
        aria-current={isActive ? 'page' : undefined}
        // Between md and lg the label is sr-only, so a sighted user gets the
        // name from the tooltip instead.
        title={item.label}
        className={`flex items-center gap-2 ${alwaysLabel ? 'px-3' : 'px-2 lg:px-3'} py-2 rounded-md text-sm font-medium transition-colors shrink-0 whitespace-nowrap ${
          isActive
            ? 'bg-primary text-primary-foreground'
            : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground'
        }`}
      >
        <Icon className="h-4 w-4 shrink-0" />
        <span className={alwaysLabel ? undefined : 'sr-only lg:not-sr-only'}>{item.label}</span>
        <NavBadges item={item} stats={stats} leading />
      </Link>
    );
  };

  const toggleGroup = (label: string, trigger: HTMLButtonElement | null) => {
    if (openGroup === label) {
      setOpenGroup(null);

      return;
    }

    measurePanel(trigger);
    setOpenGroup(label);
  };

  return (
    <div className="min-h-screen bg-background flex flex-col overflow-x-hidden">
      {/* Top navbar */}
      <header className="border-b bg-card relative">
        <div className="flex h-14 items-center px-4 md:px-6">
          <Link to="/" className="flex items-center gap-2 mr-4 md:mr-8">
            {config.logoUrl
              ? <img src={config.logoUrl} alt="" className="h-6 w-auto" />
              : <span className="text-lg font-bold">{config.brandName}</span>}
            {config.instanceName && (
              <span className="rounded bg-primary/10 px-2 py-0.5 text-xs font-semibold text-primary whitespace-nowrap">{config.instanceName}</span>
            )}
          </Link>
          {/* Desktop nav — compresses in tiers rather than collapsing early, and
              only becomes the grouped hamburger sheet at md (phone). Going down:
              xl drops the trigger's appended page name, the realtime label and
              the search's placeholder (icon-only square), and tightens padding
              and badge min-widths; lg drops the leaf labels to icons — sr-only,
              so the accessible name survives. Children stay shrink-0 (a
              shrinkable one wraps and breaks the 56px header); the nav itself is
              min-w-0 and scrolls as a last resort, so an addon-heavy deployment
              degrades by scrolling its own nav rather than clipping the row. */}
          <nav ref={navRef} className="hidden md:flex gap-1 items-center min-w-0 overflow-x-auto xl:overflow-visible [scrollbar-width:none] [&::-webkit-scrollbar]:hidden">
            {TOP_LEVEL_NAV_ITEMS.map((x) => renderNavItem(x))}
            {/* Separates the always-visible destinations from the group
                triggers. shrink-0 is load-bearing: a 1px flex child with the
                default flex-shrink collapses to nothing and silently vanishes. */}
            <div className="w-px h-6 bg-border mx-1 xl:mx-2 shrink-0" />
            {navGroups.map((group) => (
              <GroupTrigger
                key={group.label}
                group={group}
                stats={stats}
                isOpen={openGroup === group.label}
                activeItem={active.group?.label === group.label ? active.item : null}
                onToggle={toggleGroup}
              />
            ))}
            {extensionNavItems.map((x) => renderNavItem(x))}
          </nav>
          {/* Grows to take the slack, with a 24px floor so the search can never
              collapse onto the last trigger. Being the growing element here is
              what puts the search on the right edge rather than beside the nav;
              below xl it is display:none and the cluster's ml-auto does the job. */}
          <div className="hidden xl:block flex-1 shrink-0 basis-6" />
          {/* ml-auto pins this to the right edge below xl, where the spacer above
              is hidden: free space with no auto margin settles AFTER the last
              child, leaving the cluster floating inward. The wrapper is what
              makes the margin reliable, since every child here is conditional,
              and min-w-0 lets the squeeze reach the search inside it. */}
          <div className="flex items-center ml-auto min-w-0">
              {/* The one element in the row that shrinks: it wants 20rem and gives
                  width back down to 9rem as the row tightens, so the nav — not the
                  search — sets the row's minimum. */}
            <button
              type="button"
              onClick={() => setPaletteOpen(true)}
              title={`Search — ${shortcutHint()}`}
              aria-label="Search pages"
              className="hidden md:flex shrink-0 xl:shrink xl:min-w-36 items-center justify-center xl:justify-start gap-2 h-9 w-9 xl:w-80 xl:px-3 mr-3 rounded-md border border-border text-[13px] text-muted-foreground hover:bg-accent hover:text-accent-foreground transition-colors"
            >
              <Search className="h-4 w-4 shrink-0" />
              <span className="hidden xl:block flex-1 min-w-0 truncate text-left">Search pages…</span>
              <kbd className="hidden xl:block shrink-0 text-[11px] px-1.5 py-0.5 rounded-[5px] bg-muted">{shortcutHint()}</kbd>
            </button>
          {config.portalUrl && (
            <a
              href={config.portalUrl}
              title={config.portalLabel}
              className="hidden md:flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground mr-3"
            >
              <ExternalLink className="h-4 w-4" />
              <span className="hidden xl:inline">{config.portalLabel}</span>
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
        </div>
        {openGroup && (
          <GroupPanel
            ref={panelRef}
            group={navGroups.find((x) => x.label === openGroup)!}
            left={panelLeft}
            activeItem={active.group?.label === openGroup ? active.item : null}
            stats={stats}
            onNavigate={handleNavClick}
          />
        )}
        <CommandPalette
          open={paletteOpen}
          targets={paletteTargets}
          onClose={() => setPaletteOpen(false)}
        />
        {mobileMenuOpen && (
          <>
            {/* Overlay, not a block in the flow: pushing the page down reflows
                every chart and table behind the sheet, and drops the reader
                somewhere else again once it closes. */}
            <div
              className="md:hidden fixed inset-x-0 top-14 bottom-0 z-20 bg-black/20"
              aria-hidden="true"
              onClick={() => setMobileMenuOpen(false)}
            />
            <nav className="md:hidden absolute inset-x-0 top-14 z-30 flex flex-col gap-1 border-t bg-card shadow-lg px-3 py-2 max-h-[75vh] overflow-y-auto">
              {TOP_LEVEL_NAV_ITEMS.map((x) => renderNavItem(x, true))}
              {navGroups.map((group) => (
                <div key={group.label} className="flex flex-col gap-1">
                  <h3 className="text-xs font-semibold text-muted-foreground uppercase px-3 pt-3 pb-1">{group.label}</h3>
                  {group.items.map((x) => renderNavItem(x, true))}
                </div>
              ))}
              {extensionNavItems.map((x) => renderNavItem(x, true))}
            </nav>
          </>
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

function NavBadges({
  item,
  stats,
  leading,
}: {
  item: NavItem;
  stats: DashboardStatistics | null;
  /** True on the bar, where the first pill needs a gap from the label. */
  leading: boolean;
}) {
  const badges = badgesForItem(item.to, stats);

  return (
    <>
      {badges.pending > 0 && (
        <span className={`${leading ? 'ml-1 ' : ''}${BADGE_PENDING}`}>{badges.pending}</span>
      )}
      {badges.failed > 0 && <span className={BADGE_FAILED}>{badges.failed}</span>}
      {badges.neutral !== null && (
        <span className={`${leading ? 'ml-1 ' : ''}${BADGE_NEUTRAL}`}>{badges.neutral}</span>
      )}
    </>
  );
}

function GroupTrigger({
  group,
  stats,
  isOpen,
  activeItem,
  onToggle,
}: {
  group: NavGroup;
  stats: DashboardStatistics | null;
  isOpen: boolean;
  activeItem: NavItem | null;
  onToggle: (label: string, trigger: HTMLButtonElement | null) => void;
}) {
  const ref = useRef<HTMLButtonElement>(null);
  const badges = rollUpBadges(group, stats);
  const holdsActivePage = activeItem !== null;

  const state = holdsActivePage
    ? 'bg-primary text-primary-foreground'
    : isOpen
      ? 'bg-accent text-accent-foreground'
      : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground';

  return (
    <button
      ref={ref}
      type="button"
      aria-haspopup="menu"
      aria-expanded={isOpen}
      onClick={() => onToggle(group.label, ref.current)}
      className={`flex items-center gap-1.5 px-2 xl:px-3 py-2 rounded-md text-sm font-medium transition-colors shrink-0 whitespace-nowrap ${state}`}
    >
      {group.label}
      {activeItem && (
        <span className="opacity-70 hidden xl:inline-block max-w-[92px] overflow-hidden text-ellipsis whitespace-nowrap">
          · {activeItem.label}
        </span>
      )}
      {badges.pending > 0 && <span className={BADGE_PENDING}>{badges.pending}</span>}
      {badges.failed > 0 && <span className={BADGE_FAILED}>{badges.failed}</span>}
      <ChevronDown className="h-3.5 w-3.5 opacity-60" />
    </button>
  );
}

function GroupPanel({
  ref,
  group,
  left,
  activeItem,
  stats,
  onNavigate,
}: {
  ref: React.Ref<HTMLDivElement>;
  group: NavGroup;
  left: number;
  activeItem: NavItem | null;
  stats: DashboardStatistics | null;
  onNavigate: (item: NavItem, isActive: boolean) => (e: React.MouseEvent) => void;
}) {
  // React does not forward autoFocus to a plain div, so the panel never took focus and
  // its key handler never fired — arrow keys were inert after opening with the mouse.
  // Focus it on mount instead; the panel remounts on every open, so this runs each time.
  const panelRef = useRef<HTMLDivElement | null>(null);

  const attachRef = (node: HTMLDivElement | null) => {
    panelRef.current = node;
    if (typeof ref === 'function') {
      ref(node);

      return;
    }

    if (ref) {
      (ref as React.RefObject<HTMLDivElement | null>).current = node;
    }
  };

  useEffect(() => { panelRef.current?.focus(); }, []);

  // Roving focus across the two-column grid. Left/right and up/down both step by
  // one row, which is what a two-column menu reads as in practice.
  const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    const keys = ['ArrowDown', 'ArrowRight', 'ArrowUp', 'ArrowLeft', 'Home', 'End'];
    if (!keys.includes(e.key)) {
      return;
    }

    e.preventDefault();
    const rows = Array.from(e.currentTarget.querySelectorAll<HTMLElement>('[role="menuitem"]'));
    if (rows.length === 0) {
      return;
    }

    const current = rows.indexOf(document.activeElement as HTMLElement);
    const forward = e.key === 'ArrowDown' || e.key === 'ArrowRight';
    // Nothing focused yet (the panel itself has focus on open): enter at whichever
    // end the key implies rather than stepping off -1.
    const stepped = current < 0
      ? (forward ? 0 : rows.length - 1)
      : (current + (forward ? 1 : -1) + rows.length) % rows.length;
    const next = e.key === 'Home' ? 0 : e.key === 'End' ? rows.length - 1 : stepped;

    rows[next]?.focus();
  };

  return (
    <div
      ref={attachRef}
      role="menu"
      aria-label={group.label}
      tabIndex={-1}
      onKeyDown={onKeyDown}
      style={{ left, width: PANEL_WIDTH }}
      className="hidden md:block absolute top-14 z-20 box-border rounded-xl bg-popover p-3 ring-1 ring-foreground/10 shadow-lg outline-none"
    >
      <div className="text-[11px] leading-4 tracking-[0.08em] uppercase text-muted-foreground px-2 pt-1 pb-2">
        {group.label}
      </div>
      <div className="grid grid-cols-2 gap-0.5">
        {group.items.map((item) => {
          const Icon = item.icon;
          const isActive = item === activeItem;

          return (
            <Link
              key={item.to}
              to={item.to}
              role="menuitem"
              aria-current={isActive ? 'page' : undefined}
              onClick={onNavigate(item, isActive)}
              className={`flex items-center gap-2.5 p-2.5 rounded-md text-sm transition-colors hover:bg-accent outline-none focus-visible:bg-accent ${
                isActive ? 'bg-accent' : ''
              }`}
            >
              <Icon className="h-4 w-4 text-muted-foreground shrink-0" />
              <span className="flex flex-col gap-px min-w-0">
                <span className={isActive ? 'font-semibold' : 'font-medium'}>{item.label}</span>
                {item.hint && <span className="text-xs text-muted-foreground">{item.hint}</span>}
              </span>
              <span className="flex-1" />
              <NavBadges item={item} stats={stats} leading={false} />
              {isActive && <Check className="h-3.5 w-3.5" />}
            </Link>
          );
        })}
      </div>
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
    <span className="flex items-center justify-end gap-1.5 xl:min-w-28 px-2 py-1 mr-1 text-xs text-muted-foreground shrink-0" title={s.title}>
      <span className={`h-2 w-2 rounded-full ${s.dot}`} />
      <span className="hidden xl:inline">{s.label}</span>
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
