import { useState, useEffect, useRef, useMemo } from 'react';
import { Chart, LineController, LineElement, PointElement, LinearScale, CategoryScale, Filler, Tooltip as ChartTooltip, Legend } from 'chart.js';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { LoadingState, ErrorState } from '@/components/PageState';
import { useCounters, useCountersHistory } from '@/api/hooks/useCounters';
import type { CounterHistoryPoint } from '@/types';

Chart.register(LineController, LineElement, PointElement, LinearScale, CategoryScale, Filler, ChartTooltip, Legend);

// Breakdown series tint their PARENT state's hue at reduced opacity, so fourteen keys read as three
// families (failures red, deletions grey, requeues amber) instead of fourteen unrelated lines. Without an
// entry here colorFor() hashes the key to an arbitrary hue, which would scatter the families.
const builtInColors: Record<string, string> = {
  'stats:succeeded': '#22c55e',
  'stats:unsuccessful': '#b91c1c',
  'stats:failed': '#ef4444',
  'stats:failed-retry-exhausted': '#f87171',
  'stats:failed-saga': '#fca5a5',
  'stats:deleted': '#9ca3af',
  'stats:deleted-concurrency': '#b3bac4',
  'stats:deleted-ratelimit': '#c3c9d2',
  'stats:deleted-timeout': '#d2d7de',
  'stats:deleted-saga': '#e1e5ea',
  'stats:requeued': '#f59e0b',
  'stats:requeued-retry': '#fbbf24',
  'stats:requeued-concurrency': '#fcd34d',
  'stats:requeued-ratelimit': '#fde68a',
  'stats:requeued-saga': '#fef08a',
  'stats:requeued-manual': '#d97706',
  'stats:requeued-recovery': '#b45309',
  'stats:retried-jobs': '#8b5cf6',
};

// Deterministic color from key for addon-defined metrics. Same key → same color across reloads.
function colorFor(key: string): string {
  if (builtInColors[key]) return builtInColors[key];
  let hash = 0;
  for (let i = 0; i < key.length; i++) {
    hash = (hash * 31 + key.charCodeAt(i)) | 0;
  }
  const hue = Math.abs(hash) % 360;
  return `hsl(${hue}, 65%, 50%)`;
}

// The outcome family is a hierarchy, not an alphabetical list: state totals with a reason breakdown under
// each. Rendering it flat forced the reader to reconstruct that from key names.
//
// ONLY failed and deleted nest under the unsuccessful umbrella. A success is obviously not an unsuccessful
// outcome, and a requeue is not even a terminal one — the same job will run again and land in one of the
// other three totals, so indenting either under the umbrella claims something false.
const UMBRELLA_KEY = 'stats:unsuccessful';

// `attributable` marks the states that HAVE a reason taxonomy. Succeeded does not — nothing stamps a reason
// on a success — so it is the one total whose missing breakdown is expected rather than informative.
const OUTCOME_GROUPS: { total: string; underUmbrella: boolean; attributable: boolean }[] = [
  { total: 'stats:succeeded', underUmbrella: false, attributable: false },
  { total: 'stats:failed', underUmbrella: true, attributable: true },
  { total: 'stats:deleted', underUmbrella: true, attributable: true },
  { total: 'stats:requeued', underUmbrella: false, attributable: true },
];

interface CounterRow {
  /** React key. Unique per row — derived rows namespace themselves under the group they belong to. */
  key: string;
  /** What the cell shows. Equals `key` for a real counter row. */
  label: string;
  value: number;
  depth: number;
  muted?: boolean;
  warn?: boolean;
}

function buildRows(counters: { key: string; value: number }[]): { outcomes: CounterRow[]; other: CounterRow[] } {
  const byKey = new Map(counters.map((c) => [c.key, c.value]));
  const claimed = new Set<string>();
  const outcomes: CounterRow[] = [];

  // Derived on read, never stored. "Not Completed" is exactly failed + deleted, and ten sites write those
  // two keys (worker cancellation, DeleteJob, BulkDelete, crash recovery, …). A stored umbrella has to be
  // maintained at every one of them or it silently under-reports — which is precisely what it did. Computing
  // it here cannot drift from the totals it sums.
  const failed = byKey.get('stats:failed');
  const deleted = byKey.get('stats:deleted');
  const umbrella = failed === undefined && deleted === undefined ? undefined : (failed ?? 0) + (deleted ?? 0);

  // Claimed so a leftover row from a build that still wrote it doesn't render twice with two values.
  claimed.add(UMBRELLA_KEY);

  let umbrellaEmitted = false;

  for (const group of OUTCOME_GROUPS) {
    const total = byKey.get(group.total);
    if (total === undefined) continue;

    if (group.underUmbrella && umbrella !== undefined && !umbrellaEmitted) {
      outcomes.push({ key: UMBRELLA_KEY, label: `${UMBRELLA_KEY} (derived: failed + deleted)`, value: umbrella, depth: 0 });
      umbrellaEmitted = true;
    }

    const depth = group.underUmbrella && umbrella !== undefined ? 1 : 0;
    outcomes.push({ key: group.total, label: group.total, value: total, depth });
    claimed.add(group.total);

    const reasons = counters
      .filter((c) => c.key.startsWith(group.total + '-'))
      .sort((a, b) => b.value - a.value);

    let attributed = 0;
    for (const reason of reasons) {
      outcomes.push({ key: reason.key, label: reason.key, value: reason.value, depth: depth + 1 });
      claimed.add(reason.key);
      attributed += reason.value;
    }

    // "Unattributed" is computed and SHOWN rather than hidden. An outcome with no attributable cause (a
    // plain handler throw with no addon involved) carries no reason, so a state total is legitimately
    // larger than the sum of its reasons. Naming the remainder beats letting someone conclude the numbers
    // are broken. The row key is namespaced by group — two groups with a remainder used to emit the same
    // React key twice.
    //
    // Gated on the group being attributable at all, NOT on some reasons having arrived. A deployment whose
    // failures are all plain handler throws has zero reason rows and a fully unattributed total — the case
    // the remainder most needs to explain — and hiding it there would show a bare total on exactly the
    // page that promises the breakdown. Succeeded is excluded because it has no reason taxonomy to be
    // missing from.
    if (group.attributable && total > attributed) {
      outcomes.push({
        key: `${group.total}#unattributed`,
        label: `unattributed (${group.total})`,
        value: total - attributed,
        depth: depth + 1,
        muted: true,
      });
    }

    // The impossible direction, surfaced rather than swallowed: a child larger than its parent means a
    // reason key was written without its state total (a write site out of step). Hiding it would render a
    // breakdown that visibly does not add up with no explanation on screen.
    if (attributed > total) {
      outcomes.push({
        key: `${group.total}#over-attributed`,
        label: `over-attributed (${group.total}) — reasons exceed the total`,
        value: attributed - total,
        depth: depth + 1,
        warn: true,
      });
    }
  }

  const distinct = byKey.get('stats:retried-jobs');
  if (distinct !== undefined) {
    outcomes.push({ key: 'stats:retried-jobs', label: 'stats:retried-jobs', value: distinct, depth: 0 });
    claimed.add('stats:retried-jobs');
  }

  const other = counters
    .filter((c) => !claimed.has(c.key))
    .map((c) => ({ key: c.key, label: c.key, value: c.value, depth: 0 }));

  return { outcomes, other };
}

function CounterTable({ title, rows }: { title: string; rows: CounterRow[] }) {
  return (
    <Card>
      <CardHeader className="pb-2">
        <CardTitle className="text-base">{title}</CardTitle>
      </CardHeader>
      <CardContent className="p-0">
        <table className="w-full text-sm">
          <thead className="border-b bg-muted/50">
            <tr>
              <th className="text-left font-semibold px-4 py-2">Key</th>
              <th className="text-right font-semibold px-4 py-2 w-40">Value</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.key} className="border-b last:border-b-0 hover:bg-muted/30">
                <td
                  className={`px-4 py-2 font-mono ${r.depth === 0 ? 'font-semibold' : ''} ${r.muted ? 'text-muted-foreground italic' : ''} ${r.warn ? 'text-destructive' : ''}`}
                  style={{ paddingLeft: `${1 + r.depth * 1.25}rem` }}
                >
                  {r.label}
                </td>
                <td
                  className={`px-4 py-2 text-right font-mono tabular-nums ${r.muted ? 'text-muted-foreground' : ''} ${r.warn ? 'text-destructive' : ''}`}
                >
                  {r.value.toLocaleString()}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </CardContent>
    </Card>
  );
}

export default function CountersPage() {
  const [historyHours, setHistoryHours] = useState(24);
  const { data: counters, isLoading, isError } = useCounters();
  const { data: history } = useCountersHistory(historyHours);
  const rows = useMemo(() => buildRows(counters ?? []), [counters]);

  if (isError) return <ErrorState message="Unable to load counters" />;
  if (isLoading || !counters) return <LoadingState />;

  return (
    <div>
      <h1 className="text-2xl font-bold mb-2">Counters</h1>
      <p className="text-sm text-muted-foreground mb-4">
        Raw counter rows from the database. Built-in: <code>stats:succeeded</code>,{' '}
        <code>stats:failed</code>, <code>stats:deleted</code>, <code>stats:requeued</code>, per-reason
        breakdowns such as <code>stats:failed-retry-exhausted</code>, and <code>stats:retried-jobs</code>.
        These are recorded events and only ever increase &mdash; a requeue never rewrites history. The{' '}
        <code>stats:unsuccessful</code> row is derived here as <code>failed + deleted</code>, not stored, so
        it can never drift from the totals it sums. For what is happening <em>right now</em>, see the
        Dashboard. Addons can write their own keys here.
      </p>

      <Card className="mb-6">
        <CardHeader className="pb-2 flex-row items-center justify-between space-y-0">
          <CardTitle className="text-base">Hourly history</CardTitle>
          <div className="flex gap-1">
            {[
              { label: '24h', hours: 24 },
              { label: '7d', hours: 168 },
            ].map(({ label, hours }) => (
              <button
                key={label}
                onClick={() => setHistoryHours(hours)}
                className={`px-2 py-0.5 text-xs rounded-md transition-colors ${
                  historyHours === hours
                    ? 'bg-primary text-primary-foreground'
                    : 'text-muted-foreground hover:bg-accent'
                }`}
              >
                {label}
              </button>
            ))}
          </div>
        </CardHeader>
        <CardContent>
          <HistoryChart points={history ?? null} hours={historyHours} />
        </CardContent>
      </Card>

      {counters.length === 0 ? (
        <Card>
          <CardContent className="py-8 text-center text-muted-foreground">
            No counters
          </CardContent>
        </Card>
      ) : (
        <div className="flex flex-col gap-6">
          {rows.outcomes.length > 0 && <CounterTable title="Outcomes" rows={rows.outcomes} />}
          {rows.other.length > 0 && <CounterTable title="Other" rows={rows.other} />}
        </div>
      )}
    </div>
  );
}

function HistoryChart({ points, hours }: { points: CounterHistoryPoint[] | null; hours: number }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const chartRef = useRef<Chart | null>(null);

  // Pivot points → labels + per-key series, padding empty hours with 0.
  const data = useMemo(() => {
    if (!points) return null;

    const now = new Date();
    now.setMinutes(0, 0, 0);

    const labels: string[] = [];
    const hourTimes: number[] = [];
    for (let i = hours - 1; i >= 0; i--) {
      const t = now.getTime() - i * 3600000;
      hourTimes.push(t);
      const d = new Date(t);
      labels.push(
        hours <= 24
          ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
          : `${d.toLocaleDateString([], { weekday: 'short' })} ${String(d.getDate()).padStart(2, '0')}`
      );
    }

    const seriesMap = new Map<string, number[]>();
    for (const p of points) {
      const t = new Date(p.hour).getTime();
      const idx = hourTimes.indexOf(t);
      if (idx < 0) continue;

      let series = seriesMap.get(p.key);
      if (!series) {
        series = new Array(hours).fill(0);
        seriesMap.set(p.key, series);
      }
      series[idx] = p.value;
    }

    const series = [...seriesMap.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([key, values]) => ({ key, values, color: colorFor(key) }));

    return { labels, series };
  }, [points, hours]);

  useEffect(() => {
    if (!canvasRef.current) return;

    const isDark = document.documentElement.classList.contains('dark');
    const gridColor = isDark ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.08)';
    const textColor = isDark ? '#888' : '#666';

    chartRef.current = new Chart(canvasRef.current, {
      type: 'line',
      data: { labels: [], datasets: [] },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        interaction: { mode: 'index', intersect: false },
        scales: {
          x: { ticks: { color: textColor, font: { size: 10 }, maxRotation: 0, autoSkip: true, maxTicksLimit: 24 }, grid: { color: gridColor } },
          y: { beginAtZero: true, ticks: { color: textColor, font: { size: 10 }, precision: 0 }, grid: { color: gridColor } },
        },
        plugins: {
          legend: {
            display: true,
            position: 'top' as const,
            labels: { color: textColor, font: { size: 11 }, boxWidth: 12, boxHeight: 12 },
          },
          tooltip: {
            backgroundColor: isDark ? '#1f1f23' : '#fff',
            titleColor: isDark ? '#e4e4e7' : '#18181b',
            bodyColor: isDark ? '#a1a1aa' : '#52525b',
            borderColor: isDark ? '#27272a' : '#e4e4e7',
            borderWidth: 1,
          },
        },
      },
    });

    return () => { chartRef.current?.destroy(); chartRef.current = null; };
  }, []);

  useEffect(() => {
    if (!chartRef.current || !data) return;
    chartRef.current.data.labels = data.labels;
    chartRef.current.data.datasets = data.series.map(s => ({
      label: s.key,
      data: s.values,
      borderColor: s.color,
      backgroundColor: s.color + '22',
      borderWidth: 2,
      fill: false,
      pointRadius: 0,
      pointHitRadius: 10,
      tension: 0.3,
    }));
    chartRef.current.update();
  }, [data]);

  if (!points) {
    return <div style={{ height: 240 }} className="flex items-center justify-center text-sm text-muted-foreground">Loading...</div>;
  }

  if (data && data.series.length === 0) {
    return <div style={{ height: 240 }} className="flex items-center justify-center text-sm text-muted-foreground">No hourly data yet</div>;
  }

  return (
    <div style={{ height: 240 }}>
      <canvas ref={canvasRef} />
    </div>
  );
}
