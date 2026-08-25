import type { ComponentType } from 'react';
import {
  Activity,
  ArrowDownToLine,
  Briefcase,
  Bug,
  Cable,
  Gauge,
  GitBranch,
  KeyRound,
  Layers,
  LayoutDashboard,
  Mail,
  MonitorSmartphone,
  RefreshCw,
  Server,
  Timer,
  Webhook,
} from 'lucide-react';
import type { DashboardStatistics, WarpAddonsInfo } from '@/types';

export interface NavItem {
  to: string;
  label: string;
  icon: ComponentType<{ className?: string }>;
  /** Short lowercase hint rendered under the label inside a group panel. */
  hint?: string;
  /** Addon flag that has to be true for this item to appear. Core pages omit it. */
  addon?: keyof WarpAddonsInfo;
}

export interface NavGroup {
  label: string;
  items: NavItem[];
}

/**
 * Dashboard and Jobs keep their own slot on the bar; everything else lives in a
 * group. This is the single source of truth for membership — the header reads it
 * to build triggers, the page heading reads it to build the breadcrumb.
 */
export const TOP_LEVEL_NAV_ITEMS: NavItem[] = [
  { to: '/', label: 'Dashboard', icon: LayoutDashboard },
  { to: '/jobs/enqueued', label: 'Jobs', icon: Briefcase },
];

export const NAV_GROUPS: NavGroup[] = [
  {
    label: 'Workloads',
    items: [
      { to: '/messages/enqueued', label: 'Messages', icon: Mail, hint: 'pub/sub fan-out' },
      { to: '/batches/processing', label: 'Batches', icon: Layers, hint: 'grouped jobs' },
      { to: '/recurring', label: 'Recurring', icon: RefreshCw, hint: 'cron schedules' },
      { to: '/services', label: 'Services', icon: Activity, hint: 'background services' },
    ],
  },
  {
    label: 'Traffic',
    items: [
      { to: '/adapters', label: 'Adapters', icon: Cable, hint: 'outbound calls', addon: 'adapters' },
      { to: '/endpoints', label: 'Endpoints', icon: ArrowDownToLine, hint: 'inbound HTTP', addon: 'endpoints' },
      { to: '/client', label: 'Client', icon: MonitorSmartphone, hint: 'browser sessions', addon: 'client' },
      { to: '/webhooks', label: 'Webhooks', icon: Webhook, hint: 'deliveries', addon: 'webhooks' },
    ],
  },
  {
    label: 'Runtime',
    items: [
      { to: '/concurrency', label: 'Concurrency', icon: KeyRound, hint: 'mutex & semaphore', addon: 'concurrency' },
      { to: '/ratelimits', label: 'Rate Limits', icon: Timer, hint: 'fixed & sliding', addon: 'rateLimits' },
      { to: '/sagas', label: 'Sagas', icon: GitBranch, hint: 'correlated state', addon: 'sagas' },
    ],
  },
  {
    label: 'Health',
    items: [
      // Issues (error grouping §8.29) is a Core feature — always shown, not gated on an addon flag.
      { to: '/issues', label: 'Issues', icon: Bug, hint: 'grouped failures' },
      { to: '/slo', label: 'SLOs', icon: Gauge, hint: 'burn rate', addon: 'slo' },
      { to: '/counters', label: 'Counters', icon: Gauge, hint: 'time series' },
      { to: '/applications', label: 'Applications', icon: Server, hint: 'servers & instances' },
    ],
  },
];

/** Counts rendered beside a nav item or rolled onto a group trigger. Zero means "don't render". */
export interface NavBadges {
  pending: number;
  failed: number;
  neutral: number | null;
}

/** Panel geometry, kept here so the clamp is testable without a DOM. */
export const PANEL_WIDTH = 520;
export const PANEL_MARGIN = 12;

/**
 * The nav matches on the first path segment, so `/jobs/failed` keeps `Jobs`
 * active. `/` is the one route that has to match exactly or it would swallow
 * every other path.
 */
export function matchPathFor(to: string): string {
  return to.includes('/') && to !== '/' ? '/' + to.split('/')[1] : to;
}

export function isNavItemActive(to: string, pathname: string): boolean {
  if (to === '/') {
    return pathname === '/';
  }

  return pathname.startsWith(matchPathFor(to));
}

export interface ActiveLocation {
  /** The group holding the active page, or null when it's a top-level item. */
  group: NavGroup | null;
  item: NavItem | null;
}

/**
 * Resolves where the current route sits in the nav. Drives the trigger pill,
 * the label appended to the trigger, the panel check mark and the breadcrumb —
 * all derived from the pathname, never stored.
 */
export function resolveActiveLocation(
  pathname: string,
  topLevel: NavItem[],
  groups: NavGroup[]
): ActiveLocation {
  for (const group of groups) {
    const item = group.items.find((x) => isNavItemActive(x.to, pathname));
    if (item) {
      return { group, item };
    }
  }

  const item = topLevel.find((x) => isNavItemActive(x.to, pathname));

  return { group: null, item: item ?? null };
}

/**
 * Per-item counts. Keyed on the item's route rather than its label so a
 * relabelled page can't silently lose its badge.
 */
export function badgesForItem(to: string, stats: DashboardStatistics | null): NavBadges {
  const empty: NavBadges = { pending: 0, failed: 0, neutral: null };
  if (!stats) {
    return empty;
  }

  switch (matchPathFor(to)) {
    case '/jobs':
      return { pending: stats.created, failed: stats.failed, neutral: null };
    case '/messages':
      return { pending: stats.messages, failed: stats.messagesFailed, neutral: null };
    case '/batches':
      return { pending: stats.batchesProcessing, failed: stats.batchesFailed, neutral: null };
    case '/applications':
      return { pending: 0, failed: 0, neutral: stats.servers };
    default:
      return empty;
  }
}

/**
 * Both counts summed across the group: blue is how much work is still outstanding
 * in here, red is how much of it broke. Summed rather than maxed — the trigger is
 * meant to answer "how much is left", and the largest single item under-reports a
 * group holding work in several places. The per-item split stays in the panel.
 *
 * The neutral count (Applications' server total) is deliberately dropped: it is a
 * fleet size, not outstanding work, and adding it to a pending pill would read as
 * a backlog that never drains.
 */
export function rollUpBadges(group: NavGroup, stats: DashboardStatistics | null): NavBadges {
  return group.items.reduce<NavBadges>(
    (acc, item) => {
      const badges = badgesForItem(item.to, stats);

      return {
        pending: acc.pending + badges.pending,
        failed: acc.failed + badges.failed,
        neutral: null,
      };
    },
    { pending: 0, failed: 0, neutral: null }
  );
}

/** A nav destination plus the group it was reached through, for the palette. */
export interface NavTarget {
  item: NavItem;
  group: string | null;
}

export function flattenNavTargets(
  topLevel: NavItem[],
  groups: NavGroup[],
  extensions: NavItem[]
): NavTarget[] {
  return [
    ...topLevel.map((item) => ({ item, group: null })),
    ...groups.flatMap((group) => group.items.map((item) => ({ item, group: group.label }))),
    ...extensions.map((item) => ({ item, group: null })),
  ];
}

/**
 * Ranked substring search over label, group and hint. A label prefix wins over a
 * label hit anywhere, which wins over reaching the page through its group name or
 * hint — so typing "que" puts Queues above "Recurring / cron schedules". Ties keep
 * the nav's own order, which is the order the user already learned.
 */
export function filterNavTargets(targets: NavTarget[], query: string): NavTarget[] {
  const q = query.trim().toLowerCase();
  if (!q) {
    return targets;
  }

  return targets
    .map((target, index) => {
      const label = target.item.label.toLowerCase();
      const rank = label.startsWith(q)
        ? 0
        : label.includes(q)
          ? 1
          : (target.group?.toLowerCase().includes(q) || target.item.hint?.toLowerCase().includes(q))
            ? 2
            : 3;

      return { target, rank, index };
    })
    .filter((x) => x.rank < 3)
    .sort((a, b) => a.rank - b.rank || a.index - b.index)
    .map((x) => x.target);
}

/**
 * Raw `offsetLeft` pushes the right-hand panels past the shell edge at 1280px,
 * where `overflow-x-hidden` eats their whole second column. Clamp so a panel
 * always keeps a margin on both sides, and left-align when the shell is too
 * narrow to hold one at all.
 */
export function clampPanelLeft(
  triggerLeft: number,
  containerWidth: number,
  panelWidth = PANEL_WIDTH,
  margin = PANEL_MARGIN
): number {
  const max = containerWidth - panelWidth - margin;
  if (max <= margin) {
    return margin;
  }

  return Math.min(Math.max(margin, triggerLeft), max);
}

/**
 * Drops addon-gated items the host hasn't registered, then drops any group left
 * with nothing in it — an empty dropdown must never get a trigger. Before the
 * addon probe answers (`addons` still null) only Core pages show.
 */
export function gateGroups(groups: NavGroup[], addons: WarpAddonsInfo | null): NavGroup[] {
  return groups
    .map((group) => ({
      ...group,
      items: group.items.filter((x) => !x.addon || (addons?.[x.addon] ?? false)),
    }))
    .filter((x) => x.items.length > 0);
}
