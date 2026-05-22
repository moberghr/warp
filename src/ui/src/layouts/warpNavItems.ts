import {
  LayoutGrid,
  Briefcase,
  Mail,
  Layers,
  Repeat,
  Server,
  Loader,
  KeyRound,
  Timer,
  GitBranch,
  Activity,
  Puzzle,
} from 'lucide-react';
import * as LucideIcons from 'lucide-react';
import type { ExtensionManifest } from '@/extensions/types';
import type { DashboardStatistics } from '@/types';

export type BadgeKind = 'blue' | 'red';

export interface NavBadge {
  value: number;
  kind: BadgeKind;
}

export interface WarpNavItem {
  to: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  /** Pull badge values from live stats. */
  badges?: (stats: DashboardStatistics | null) => NavBadge[];
}

function nonZero(n: number | undefined | null): number {
  return typeof n === 'number' && n > 0 ? n : 0;
}

const builtInNavItems: WarpNavItem[] = [
  {
    to: '/',
    label: 'Dashboard',
    icon: LayoutGrid,
  },
  {
    to: '/jobs/enqueued',
    label: 'Jobs',
    icon: Briefcase,
    badges: (s) => {
      const blue = nonZero(s?.created);
      const red = nonZero(s?.failed);
      const out: NavBadge[] = [];
      if (blue) out.push({ value: blue, kind: 'blue' });
      if (red) out.push({ value: red, kind: 'red' });

      return out;
    },
  },
  {
    to: '/messages',
    label: 'Messages',
    icon: Mail,
    badges: (s) => {
      const blue = nonZero(s?.messagesEnqueued);
      const red = nonZero(s?.messagesFailed);
      const out: NavBadge[] = [];
      if (blue) out.push({ value: blue, kind: 'blue' });
      if (red) out.push({ value: red, kind: 'red' });

      return out;
    },
  },
  {
    to: '/batches',
    label: 'Batches',
    icon: Layers,
    badges: (s) => {
      const blue = nonZero(s?.batchesProcessing);
      const red = nonZero(s?.batchesFailed);
      const out: NavBadge[] = [];
      if (blue) out.push({ value: blue, kind: 'blue' });
      if (red) out.push({ value: red, kind: 'red' });

      return out;
    },
  },
  {
    to: '/recurring',
    label: 'Recurring',
    icon: Repeat,
  },
  {
    to: '/servers',
    label: 'Servers',
    icon: Server,
    badges: (s) => {
      const blue = nonZero(s?.servers);

      return blue ? [{ value: blue, kind: 'blue' }] : [];
    },
  },
  {
    to: '/counters',
    label: 'Counters',
    icon: Loader,
  },
];

const concurrencyNavItem: WarpNavItem = {
  to: '/concurrency',
  label: 'Concurrency',
  icon: KeyRound,
};
const rateLimitsNavItem: WarpNavItem = {
  to: '/ratelimits',
  label: 'Rate Limits',
  icon: Timer,
};
const sagasNavItem: WarpNavItem = {
  to: '/sagas',
  label: 'Sagas',
  icon: GitBranch,
};
const servicesNavItem: WarpNavItem = {
  to: '/services',
  label: 'Services',
  icon: Activity,
};

function resolveIcon(name?: string): React.ComponentType<{ className?: string }> {
  if (!name) {
    return Puzzle;
  }

  const pascalCase = name
    .split('-')
    .map((s) => s.charAt(0).toUpperCase() + s.slice(1))
    .join('');
  const icons = LucideIcons as Record<string, unknown>;

  return (icons[pascalCase] as React.ComponentType<{ className?: string }>) ?? Puzzle;
}

export function buildWarpNavItems(
  extensions: ExtensionManifest[],
  concurrencyAvailable: boolean,
  rateLimitsAvailable: boolean,
  sagasAvailable: boolean = false,
  servicesAvailable: boolean = false,
): WarpNavItem[] {
  return [
    ...builtInNavItems,
    ...(concurrencyAvailable ? [concurrencyNavItem] : []),
    ...(rateLimitsAvailable ? [rateLimitsNavItem] : []),
    ...(sagasAvailable ? [sagasNavItem] : []),
    ...(servicesAvailable ? [servicesNavItem] : []),
    ...extensions.flatMap((ext) =>
      ext.pages.map((page) => ({
        to: page.path,
        label: page.label,
        icon: resolveIcon(page.icon),
      })),
    ),
  ];
}
