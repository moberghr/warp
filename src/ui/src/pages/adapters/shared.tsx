// Shared presentational helpers for the Adapters pages (fleet + detail). Kept here so the
// health pill, outcome badge, sparkline, and numeric formatters read identically on both.
// This is a leaf helper module (formatters + tiny presentational atoms), not an HMR
// component boundary, so the fast-refresh single-export rule doesn't apply.
/* eslint-disable react-refresh/only-export-components */
import type { ReactNode } from 'react';
import { AdapterCallOutcome } from '@/types/adapters';
import { httpStatusName } from '@/utils/format';
import { Hint } from '@/components/ui/tooltip';

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

// Status code with the reason phrase on hover ("401 Unauthorized"); em-dash when the call has no
// status (e.g. an exception escaped before the response completed on an older recording).
export function HttpStatus({ code, className }: { code: number | null | undefined; className?: string }) {
  if (code == null) {
    return <>—</>;
  }

  return (
    <Hint text={`${code} ${httpStatusName(code)}`}>
      <span className={`cursor-help ${className ?? ''}`.trim()}>
        {code}
      </span>
    </Hint>
  );
}

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

// Bordered titled section used across the call-detail pages (metadata, exception, payload panes).
export function Pane({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="rounded-md border p-3">
      <div className="text-sm font-medium mb-2">{title}</div>
      {children}
    </div>
  );
}

// Label/value row in a metadata grid.
export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex gap-2">
      <span className="text-muted-foreground min-w-24">{label}</span>
      <span>{children}</span>
    </div>
  );
}

// Request/response pane: an optional one-line summary, then the captured (already redacted +
// truncated) headers and body. Shows "Not captured." when the tier stored nothing.
export function PayloadPane({
  title,
  summary,
  headers,
  body,
}: {
  title: string;
  summary?: string | null;
  headers: string | null;
  body: string | null;
}) {
  const hasAny = !!summary || !!headers || !!body;

  return (
    <Pane title={title}>
      {!hasAny && <div className="text-xs text-muted-foreground">Not captured.</div>}
      {summary && <div className="font-mono text-xs mb-2 break-words">{summary}</div>}
      {headers && (
        <div className="mb-2">
          <div className="text-xs text-muted-foreground mb-0.5">Headers</div>
          <pre className="whitespace-pre-wrap break-words rounded-md bg-muted/50 p-2 font-mono text-xs">{headers}</pre>
        </div>
      )}
      {body && (
        <div>
          <div className="text-xs text-muted-foreground mb-0.5">Body</div>
          <pre className="whitespace-pre-wrap break-words rounded-md bg-muted/50 p-2 font-mono text-xs max-h-72 overflow-auto">{body}</pre>
        </div>
      )}
    </Pane>
  );
}

// Redacted, truncated tags come from the recorder as a JSON object of string→string. Parse
// defensively — a malformed or non-object payload yields no pairs rather than a crash.
export function parseTags(tagsJson: string | null): [string, string][] {
  if (!tagsJson) {
    return [];
  }
  try {
    const parsed: unknown = JSON.parse(tagsJson);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return [];
    }

    return Object.entries(parsed as Record<string, unknown>).map(([key, value]) => [key, String(value)]);
  } catch {
    return [];
  }
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
