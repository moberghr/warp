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
import { FAMILIES } from '@/pages/counters/counterModel';
import { config } from '@/config';

/**
 * Stable id for a built-in page, mirroring the `WarpDashboardPage` enum a host lays the nav out with
 * (`WarpDashboardPageIds` on the C# side emits exactly these strings). It is deliberately not the route:
 * several pages open on a filtered sub-route (`/jobs/enqueued`), and the SPA has to stay free to change
 * those without orphaning a host's layout.
 */
export type NavPageId =
  | 'dashboard'
  | 'jobs'
  | 'messages'
  | 'batches'
  | 'recurring'
  | 'services'
  | 'adapters'
  | 'endpoints'
  | 'client'
  | 'webhooks'
  | 'concurrency'
  | 'ratelimits'
  | 'sagas'
  | 'issues'
  | 'slo'
  | 'counters'
  | 'applications';

export interface NavItem {
  to: string;
  label: string;
  icon: ComponentType<{ className?: string }>;
  /** Short lowercase hint rendered under the label inside a group panel. */
  hint?: string;
  /** Addon flag that has to be true for this item to appear. Core pages omit it. */
  addon?: keyof WarpAddonsInfo;
  /** Join key for a host-defined layout. Only built-in pages carry one; extensions and counter families don't. */
  page?: NavPageId;
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
  { to: '/', label: 'Dashboard', icon: LayoutDashboard, page: 'dashboard' },
  { to: '/jobs/enqueued', label: 'Jobs', icon: Briefcase, page: 'jobs' },
];

/**
 * The counter families as palette targets. Each is a route (/counters/{slug}) but not a nav
 * entry — the bar has one Counters item — so the palette is the only place a user can jump
 * straight to, say, Queues. Passed to flattenNavTargets only; never rendered as a dropdown.
 */
export const COUNTER_FAMILY_GROUP: NavGroup = {
  label: 'Counters',
  items: FAMILIES.map((x) => ({ to: `/counters/${x.slug}`, label: x.label, icon: Gauge })),
};

export const NAV_GROUPS: NavGroup[] = [
  {
    label: 'Workloads',
    items: [
      { to: '/messages/enqueued', label: 'Messages', icon: Mail, hint: 'pub/sub fan-out', page: 'messages' },
      { to: '/batches/processing', label: 'Batches', icon: Layers, hint: 'grouped jobs', page: 'batches' },
      { to: '/recurring', label: 'Recurring', icon: RefreshCw, hint: 'cron schedules', page: 'recurring' },
      { to: '/services', label: 'Services', icon: Activity, hint: 'background services', page: 'services' },
    ],
  },
  {
    label: 'Traffic',
    items: [
      { to: '/adapters', label: 'Adapters', icon: Cable, hint: 'outbound calls', addon: 'adapters', page: 'adapters' },
      { to: '/endpoints', label: 'Endpoints', icon: ArrowDownToLine, hint: 'inbound HTTP', addon: 'endpoints', page: 'endpoints' },
      { to: '/client', label: 'Client', icon: MonitorSmartphone, hint: 'browser sessions', addon: 'client', page: 'client' },
      { to: '/webhooks', label: 'Webhooks', icon: Webhook, hint: 'deliveries', addon: 'webhooks', page: 'webhooks' },
    ],
  },
  {
    label: 'Runtime',
    items: [
      { to: '/concurrency', label: 'Concurrency', icon: KeyRound, hint: 'mutex & semaphore', addon: 'concurrency', page: 'concurrency' },
      { to: '/ratelimits', label: 'Rate Limits', icon: Timer, hint: 'fixed & sliding', addon: 'rateLimits', page: 'ratelimits' },
      { to: '/sagas', label: 'Sagas', icon: GitBranch, hint: 'correlated state', addon: 'sagas', page: 'sagas' },
    ],
  },
  {
    label: 'Health',
    items: [
      // Issues (error grouping §8.29) is a Core feature — always shown, not gated on an addon flag.
      { to: '/issues', label: 'Issues', icon: Bug, hint: 'grouped failures', page: 'issues' },
      { to: '/slo', label: 'SLOs', icon: Gauge, hint: 'burn rate', addon: 'slo', page: 'slo' },
      { to: '/counters', label: 'Counters', icon: Gauge, hint: 'time series', page: 'counters' },
      { to: '/applications', label: 'Applications', icon: Server, hint: 'servers & instances', page: 'applications' },
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
 * hint — so typing "end" puts Endpoints above pages whose hint merely contains it.
 * Ties keep the nav's own order, which is the order the user already learned.
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
 * Drops addon-gated items the host hasn't registered. Before the addon probe
 * answers (`addons` still null) only Core pages show.
 */
export function gateItems(items: NavItem[], addons: WarpAddonsInfo | null): NavItem[] {
  return items.filter((x) => !x.addon || (addons?.[x.addon] ?? false));
}

/** One entry in the host's declared bar, mirroring the shapes `WarpDashboardMenu` serializes. */
export type MenuEntrySpec =
  | { kind: 'page'; page: string }
  | { kind: 'group'; label: string; pages: string[] }
  | { kind: 'divider' };

/**
 * The nav layout a host declared through `WarpDashboardOptions.ConfigureMenu`, as injected into the
 * shell. Null when the host declared none, which leaves the built-in layout untouched.
 */
export interface MenuLayoutSpec {
  entries: MenuEntrySpec[];
  overflowLabel: string;
}

/**
 * One rendered slot on the nav bar. The bar is a single ordered sequence rather than "direct pages, then
 * dropdowns", so a host can put a page between two groups and have it render there.
 */
export type NavEntry =
  | { kind: 'item'; item: NavItem }
  | { kind: 'group'; group: NavGroup }
  | { kind: 'divider' };

export function itemsOf(entries: NavEntry[]): NavItem[] {
  return entries.flatMap((x) => (x.kind === 'item' ? [x.item] : []));
}

export function groupsOf(entries: NavEntry[]): NavGroup[] {
  return entries.flatMap((x) => (x.kind === 'group' ? [x.group] : []));
}

/**
 * Lays the built-in nav out as the host declared it, joining on each item's `page` id.
 *
 * Placement is the host's, membership is not: every page the host didn't place collects in one trailing
 * overflow group, in the order the built-in nav declares them, so a layout can never make a page
 * unreachable — including a page added by a Warp upgrade the host's layout predates. Ids the SPA doesn't
 * know (a page a newer host names but this bundle doesn't ship) are skipped rather than rendered as a dead
 * entry. Addon gating runs afterwards, in gateEntries, and is unaffected by where the layout put an item.
 *
 * With no host layout this reproduces the built-in bar exactly: the direct pages, one divider, the groups.
 */
export function applyMenuLayout(
  topLevel: NavItem[],
  groups: NavGroup[],
  spec: MenuLayoutSpec | null
): NavEntry[] {
  if (!spec) {
    return [
      ...topLevel.map((item): NavEntry => ({ kind: 'item', item })),
      { kind: 'divider' },
      ...groups.map((group): NavEntry => ({ kind: 'group', group })),
    ];
  }

  const declared = [...topLevel, ...groups.flatMap((x) => x.items)];
  const byPage = new Map(declared.filter((x) => x.page).map((x) => [x.page as string, x]));
  const placed = new Set<string>();

  const take = (pages: string[]): NavItem[] =>
    pages.flatMap((page) => {
      const item = byPage.get(page);
      if (!item || placed.has(page)) {
        return [];
      }

      placed.add(page);

      return [item];
    });

  const entries = spec.entries.flatMap((entry): NavEntry[] => {
    if (entry.kind === 'divider') {
      return [{ kind: 'divider' }];
    }

    if (entry.kind === 'page') {
      return take([entry.page]).map((item): NavEntry => ({ kind: 'item', item }));
    }

    const items = take(entry.pages);

    // A group whose pages all failed to resolve would render as an empty dropdown.
    return items.length > 0 ? [{ kind: 'group', group: { label: entry.label, items } }] : [];
  });

  // An item with no page id can't be named by a host, so it can only ever reach the overflow group.
  const leftovers = declared.filter((x) => !x.page || !placed.has(x.page));
  if (leftovers.length > 0) {
    entries.push({ kind: 'group', group: { label: spec.overflowLabel || 'More', items: leftovers } });
  }

  return entries;
}

/**
 * Drops what this deployment doesn't run: addon-gated items the host hasn't registered, and any group
 * left empty by that — an empty dropdown must never get a trigger.
 *
 * Dividers are then normalised, because gating can strand one: a rule that ends up leading, trailing or
 * beside another rule has nothing left to separate and would hang off the end of the bar. This is also
 * what keeps the built-in layout honest when a host puts every page on the bar (no groups follow the
 * divider) or none on it (nothing precedes it).
 */
export function gateEntries(entries: NavEntry[], addons: WarpAddonsInfo | null): NavEntry[] {
  const gated = entries.flatMap((entry): NavEntry[] => {
    if (entry.kind === 'item') {
      return gateItems([entry.item], addons).map((item): NavEntry => ({ kind: 'item', item }));
    }

    if (entry.kind === 'group') {
      const items = gateItems(entry.group.items, addons);

      return items.length > 0 ? [{ kind: 'group', group: { ...entry.group, items } }] : [];
    }

    return [entry];
  });

  // Sequential rather than a predicate over the original: whether a rule is stranded depends on what
  // survived beside it, so a run of three that gating reduced to one has to collapse in one pass.
  const collapsed = gated.reduce<NavEntry[]>((acc, entry) => {
    if (entry.kind === 'divider' && (acc.length === 0 || acc[acc.length - 1].kind === 'divider')) {
      return acc;
    }

    acc.push(entry);

    return acc;
  }, []);

  return collapsed.slice(0, collapsed.findLastIndex((x) => x.kind !== 'divider') + 1);
}

/**
 * The built-in nav with the host's layout applied, resolved once at module load — the layout is injected
 * into the shell and cannot change for the life of the page. Shared by the header and by the page
 * heading's breadcrumb, so both name the group the host actually declared rather than the built-in one.
 * Ungated: addon gating is per-render, since it waits on the addon probe.
 */
export const NAV_ENTRIES: NavEntry[] = applyMenuLayout(TOP_LEVEL_NAV_ITEMS, NAV_GROUPS, config.menu);
