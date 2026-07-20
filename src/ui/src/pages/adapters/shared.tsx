// Shared presentational helpers for the Adapters pages (fleet + detail). Kept here so the
// health pill, outcome badge, sparkline, and numeric formatters read identically on both.
// This is a leaf helper module (formatters + tiny presentational atoms), not an HMR
// component boundary, so the fast-refresh single-export rule doesn't apply.
/* eslint-disable react-refresh/only-export-components */
import { AdapterCallOutcome } from '@/types/adapters';

export function formatPercent(rate: number): string {
  if (rate <= 0) {
    return '0%';
  }

  const pct = rate * 100;

  return `${pct < 1 ? pct.toFixed(1) : pct.toFixed(pct < 10 ? 1 : 0)}%`;
}

export function formatMs(ms: number): string {
  if (ms <= 0) {
    return '—';
  }
  if (ms < 1) {
    return '<1 ms';
  }
  if (ms < 1000) {
    return `${Math.round(ms)} ms`;
  }

  return `${(ms / 1000).toFixed(2)} s`;
}

export type AdapterHealth = 'healthy' | 'degraded' | 'unhealthy' | 'idle';

export function adapterHealth(item: { totalCalls: number; errorRate: number }): AdapterHealth {
  if (item.totalCalls === 0) {
    return 'idle';
  }
  if (item.errorRate >= 0.2) {
    return 'unhealthy';
  }
  if (item.errorRate >= 0.05) {
    return 'degraded';
  }

  return 'healthy';
}

const healthStyles: Record<AdapterHealth, { dot: string; label: string }> = {
  healthy: { dot: 'bg-green-500', label: 'Healthy' },
  degraded: { dot: 'bg-amber-500', label: 'Degraded' },
  unhealthy: { dot: 'bg-red-500', label: 'Unhealthy' },
  idle: { dot: 'bg-muted-foreground/40', label: 'Idle' },
};

export function HealthPill({ health }: { health: AdapterHealth }) {
  const style = healthStyles[health];

  return (
    <span className="inline-flex items-center gap-1.5 text-sm text-muted-foreground">
      <span className={`h-2 w-2 rounded-full ${style.dot}`} />
      <span>{style.label}</span>
    </span>
  );
}

const outcomeStyles: Record<AdapterCallOutcome, { label: string; cls: string }> = {
  [AdapterCallOutcome.Success]: {
    label: 'Success',
    cls: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400',
  },
  [AdapterCallOutcome.Failed]: {
    label: 'Failed',
    cls: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400',
  },
  [AdapterCallOutcome.Throttled]: {
    label: 'Throttled',
    cls: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400',
  },
  [AdapterCallOutcome.CircuitOpen]: {
    label: 'Circuit open',
    cls: 'bg-orange-100 text-orange-800 dark:bg-orange-900/30 dark:text-orange-400',
  },
};

export function OutcomeBadge({ outcome }: { outcome: AdapterCallOutcome }) {
  const style = outcomeStyles[outcome] ?? {
    label: 'Unknown',
    cls: 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-400',
  };

  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${style.cls}`}>
      {style.label}
    </span>
  );
}

// Neutral, dependency-free trend sparkline. Renders nothing when there's no series (production
// list rows carry no per-adapter time series — that lives in OTel); demo fixtures supply one.
export function Sparkline({ values, width = 96, height = 22 }: { values?: number[]; width?: number; height?: number }) {
  if (!values || values.length < 2) {
    return <span className="text-muted-foreground/40">—</span>;
  }

  const max = Math.max(...values);
  const min = Math.min(...values);
  const span = max - min || 1;
  const step = width / (values.length - 1);
  const points = values
    .map((v, i) => {
      const x = i * step;
      const y = height - ((v - min) / span) * (height - 2) - 1;

      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(' ');

  return (
    <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} className="text-muted-foreground" aria-hidden="true">
      <polyline points={points} fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}
