import { useState, useEffect, useRef, useMemo } from 'react';
import { Chart, LineController, LineElement, PointElement, LinearScale, CategoryScale, Filler, Tooltip as ChartTooltip, Legend } from 'chart.js';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { LoadingState, ErrorState } from '@/components/PageState';
import { useCounters, useCountersHistory } from '@/api/hooks/useCounters';
import {
  buildFamilySeries,
  buildFamilyTable,
  buildOutcomeRows,
  historyTokens,
  parseCounterKey,
  presentFamilies,
  type CounterEntry,
  type CounterRow,
  type FamilyDef,
  type FamilyId,
  type FamilySeries,
  type MetricRow,
} from './counterModel';
import type { CounterHistoryPoint } from '@/types';
import { PageHeading } from '@/components/PageHeading';

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
  'stats:requeued-circuitbreaker': '#fef3c7',
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

// A legend of forty assembly-qualified names is not a legend. The tail is dropped from the CHART only — every
// dimension is still in the table below it — and the count of what was dropped is stated on screen.
const MAX_SERIES = 10;

const TOKEN_LABELS: Record<string, string> = {
  count: 'Count',
  succeeded: 'Succeeded',
  success: 'Success',
  failed: 'Failed',
  miss: 'Missed',
  throttled: 'Throttled',
  circuitopen: 'Circuit open',
  dropped: 'Dropped',
  depth: 'Backlog',
  oldest_age_seconds: 'Oldest',
  dur: 'Duration',
};

function tokenLabel(token: string): string {
  return TOKEN_LABELS[token] ?? token.charAt(0).toUpperCase() + token.slice(1);
}

function formatMs(ms: number | null): string {
  if (ms === null) return '—';
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(2)}s`;

  return `${(ms / 60000).toFixed(1)}m`;
}

function formatSeconds(seconds: number): string {
  if (seconds <= 0) return '—';
  if (seconds < 60) return `${Math.round(seconds)}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${Math.round(seconds % 60)}s`;

  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

function formatCell(token: string, value: number | undefined): string {
  if (value === undefined) return '—';
  if (token === 'oldest_age_seconds') return formatSeconds(value);

  return value.toLocaleString();
}

export default function CountersPage() {
  const [historyHours, setHistoryHours] = useState(24);
  const [tab, setTab] = useState<FamilyId>('outcomes');
  const [metricByFamily, setMetricByFamily] = useState<Record<string, string>>({});
  const [filter, setFilter] = useState('');
  const { data: counters, isLoading, isError } = useCounters();
  const { data: history } = useCountersHistory(historyHours);

  const families = useMemo(
    () => presentFamilies(counters ?? [], (history ?? []).map((p) => p.key)),
    [counters, history],
  );

  // The active tab is derived rather than corrected in an effect: a family can disappear between refetches
  // (its last counter aged out), and falling back to the first present family keeps the page rendering.
  const family = families.find((f) => f.id === tab) ?? families[0];

  if (isError) return <ErrorState message="Unable to load counters" />;
  if (isLoading || !counters) return <LoadingState />;

  return (
    <div>
      <PageHeading className="mb-2">Counters</PageHeading>
      <p className="text-sm text-muted-foreground mb-4">
        Every durable metric Warp folds through <code>Counter</code> &rarr; <code>Statistic</code>, grouped by the
        subsystem that wrote it. These are recorded events and only ever increase &mdash; a requeue never rewrites
        history &mdash; and they survive the cleanup of the rows they were derived from. For what is happening{' '}
        <em>right now</em>, see the Dashboard.
      </p>

      {family === undefined ? (
        <Card>
          <CardContent className="py-8 text-center text-muted-foreground">No counters</CardContent>
        </Card>
      ) : (
        <>
          <div className="flex flex-wrap gap-1 mb-4 border-b pb-2">
            {families.map((f) => (
              <button
                key={f.id}
                onClick={() => { setTab(f.id); setFilter(''); }}
                className={`px-3 py-1 text-sm rounded-md transition-colors ${
                  family.id === f.id ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-accent'
                }`}
              >
                {f.label}
              </button>
            ))}
          </div>

          <p className="text-sm text-muted-foreground mb-4">{family.description}</p>

          <FamilyChart
            family={family}
            points={history ?? null}
            hours={historyHours}
            onHoursChange={setHistoryHours}
            metric={metricByFamily[family.id]}
            onMetricChange={(token) => setMetricByFamily((prev) => ({ ...prev, [family.id]: token }))}
          />

          <FamilyBody family={family} counters={counters} filter={filter} onFilterChange={setFilter} />
        </>
      )}
    </div>
  );
}

function FamilyBody({
  family,
  counters,
  filter,
  onFilterChange,
}: {
  family: FamilyDef;
  counters: CounterEntry[];
  filter: string;
  onFilterChange: (value: string) => void;
}) {
  const needle = filter.trim().toLowerCase();

  const outcomes = useMemo(
    () => (family.id === 'outcomes' ? buildOutcomeRows(counters.filter((c) => c.key.startsWith('stats:'))) : []),
    [family.id, counters],
  );

  // Unrecognised keys are the ONLY thing the `other` family holds, so they are matched by parse failure rather
  // than by a prefix — an addon key nobody taught this page about still renders, raw.
  const unparsed = useMemo(
    () => (family.id === 'other' ? counters.filter((c) => parseCounterKey(c.key) === null) : []),
    [family.id, counters],
  );

  const table = useMemo(
    () => (family.id === 'outcomes' || family.id === 'other' ? null : buildFamilyTable(counters, family)),
    [family, counters],
  );

  if (family.id === 'outcomes' || family.id === 'other') {
    const rows: CounterRow[] =
      family.id === 'outcomes'
        ? outcomes
        : unparsed.map((c) => ({ key: c.key, label: c.key, value: c.value, depth: 0 }));
    const visible = needle ? rows.filter((r) => r.label.toLowerCase().includes(needle)) : rows;

    return (
      <Card>
        <TableHeaderBar count={visible.length} filter={filter} onFilterChange={onFilterChange} />
        <CardContent className="p-0">
          {visible.length === 0 ? (
            <EmptyBody filtered={needle.length > 0} />
          ) : (
            <table className="w-full text-sm">
              <thead className="border-b bg-muted/50">
                <tr>
                  <th className="text-left font-semibold px-4 py-2">Key</th>
                  <th className="text-right font-semibold px-4 py-2 w-40">Value</th>
                </tr>
              </thead>
              <tbody>
                {visible.map((r) => (
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
          )}
        </CardContent>
      </Card>
    );
  }

  if (table === null) return null;

  const visible = needle
    ? table.rows.filter((r) => r.subject.toLowerCase().includes(needle) || r.label.toLowerCase().includes(needle))
    : table.rows;

  return (
    <Card>
      <TableHeaderBar count={visible.length} filter={filter} onFilterChange={onFilterChange} />
      <CardContent className="p-0">
        {visible.length === 0 ? (
          <EmptyBody filtered={needle.length > 0} chartOnly={table.rows.length === 0} />
        ) : (
          <table className="w-full text-sm">
            <thead className="border-b bg-muted/50">
              <tr>
                <th className="text-left font-semibold px-4 py-2">Name</th>
                {table.hasApplication && <th className="text-left font-semibold px-4 py-2 w-40">Application</th>}
                {table.columns.map((token) => (
                  <th key={token} className="text-right font-semibold px-4 py-2 w-28">
                    {tokenLabel(token)}
                  </th>
                ))}
                {table.hasAvg && <th className="text-right font-semibold px-4 py-2 w-24">Avg</th>}
                {table.hasPercentile && table.percentileLabels.map((label) => (
                  <th key={label} className="text-right font-semibold px-4 py-2 w-24">{label}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {visible.map((row) => (
                <MetricTableRow key={row.id} row={row} columns={table.columns} hasApplication={table.hasApplication} hasAvg={table.hasAvg} hasPercentile={table.hasPercentile} />
              ))}
            </tbody>
          </table>
        )}
      </CardContent>
    </Card>
  );
}

function MetricTableRow({
  row,
  columns,
  hasApplication,
  hasAvg,
  hasPercentile,
}: {
  row: MetricRow;
  columns: string[];
  hasApplication: boolean;
  hasAvg: boolean;
  hasPercentile: boolean;
}) {
  return (
    <tr className="border-b last:border-b-0 hover:bg-muted/30">
      <td className="px-4 py-2" title={row.subject}>
        <div className="font-mono font-medium">{row.label}</div>
        {row.sub && <div className="font-mono text-xs text-muted-foreground">{row.sub}</div>}
      </td>
      {hasApplication && (
        <td className="px-4 py-2 font-mono text-xs text-muted-foreground">{row.application ?? 'all'}</td>
      )}
      {columns.map((token) => (
        <td key={token} className="px-4 py-2 text-right font-mono tabular-nums">
          {formatCell(token, row.values[token])}
        </td>
      ))}
      {hasAvg && <td className="px-4 py-2 text-right font-mono tabular-nums">{formatMs(row.avgMs)}</td>}
      {hasPercentile && row.percentiles.map((p) => (
        <td key={p.label} className="px-4 py-2 text-right font-mono tabular-nums">
          {p.overflow ? `>${formatMs(p.ms)}` : formatMs(p.ms)}
        </td>
      ))}
    </tr>
  );
}

function TableHeaderBar({
  count,
  filter,
  onFilterChange,
}: {
  count: number;
  filter: string;
  onFilterChange: (value: string) => void;
}) {
  return (
    <CardHeader className="pb-2 flex-row items-center justify-between space-y-0 gap-4">
      <CardTitle className="text-base">
        {count.toLocaleString()} {count === 1 ? 'row' : 'rows'}
      </CardTitle>
      <input
        value={filter}
        onChange={(e) => onFilterChange(e.target.value)}
        placeholder="Filter…"
        className="h-8 w-56 rounded-md border bg-background px-2 text-sm"
      />
    </CardHeader>
  );
}

function EmptyBody({ filtered, chartOnly }: { filtered: boolean; chartOnly?: boolean }) {
  return (
    <div className="py-8 text-center text-sm text-muted-foreground">
      {filtered
        ? 'No rows match the filter'
        : chartOnly
          ? 'This family records an hourly trend only — see the chart above.'
          : 'No counters'}
    </div>
  );
}

function FamilyChart({
  family,
  points,
  hours,
  onHoursChange,
  metric,
  onMetricChange,
}: {
  family: FamilyDef;
  points: CounterHistoryPoint[] | null;
  hours: number;
  onHoursChange: (hours: number) => void;
  metric: string | undefined;
  onMetricChange: (token: string) => void;
}) {
  const tokens = useMemo(() => historyTokens(points ?? [], family.id), [points, family.id]);
  const selected = metric !== undefined && tokens.includes(metric) ? metric : tokens[0];

  const series = useMemo(
    () => (points === null || selected === undefined ? [] : buildFamilySeries(points, family, selected)),
    [points, family, selected],
  );

  const shown = series.slice(0, MAX_SERIES);
  const hidden = series.length - shown.length;

  return (
    <Card className="mb-6">
      <CardHeader className="pb-2 flex-row items-center justify-between space-y-0 gap-4">
        <CardTitle className="text-base">
          Hourly history
          {selected === 'dur' && (
            <span className="ml-2 text-xs font-normal text-muted-foreground">total milliseconds per hour</span>
          )}
        </CardTitle>
        <div className="flex items-center gap-3">
          {tokens.length > 1 && (
            <div className="flex gap-1">
              {tokens.map((token) => (
                <button
                  key={token}
                  onClick={() => onMetricChange(token)}
                  className={`px-2 py-0.5 text-xs rounded-md transition-colors ${
                    selected === token ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-accent'
                  }`}
                >
                  {tokenLabel(token)}
                </button>
              ))}
            </div>
          )}
          <div className="flex gap-1">
            {[
              { label: '24h', hours: 24 },
              { label: '7d', hours: 168 },
            ].map(({ label, hours: h }) => (
              <button
                key={label}
                onClick={() => onHoursChange(h)}
                className={`px-2 py-0.5 text-xs rounded-md transition-colors ${
                  hours === h ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-accent'
                }`}
              >
                {label}
              </button>
            ))}
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <HistoryChart series={shown} hours={hours} loading={points === null} />
        {hidden > 0 && (
          <p className="mt-2 text-xs text-muted-foreground">
            Showing the {MAX_SERIES} largest of {series.length}. All {series.length} are in the table below.
          </p>
        )}
      </CardContent>
    </Card>
  );
}

function HistoryChart({ series, hours, loading }: { series: FamilySeries[]; hours: number; loading: boolean }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const chartRef = useRef<Chart | null>(null);

  // Pivot each series onto the hour slots of the window, padding empty hours with 0.
  const data = useMemo(() => {
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

    return {
      labels,
      series: series.map((s) => ({
        label: s.label,
        color: colorFor(s.colorKey),
        values: hourTimes.map((t) => s.byHour.get(t) ?? 0),
      })),
    };
  }, [series, hours]);

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
            // A counter series is zero for most hours, so an unfiltered index tooltip listed every dimension
            // in the chart with ": 0" and buried the one or two that actually moved in that hour. Dropping the
            // zeros and sorting by value makes the hover answer "what happened at 06:00" directly. An hour where
            // nothing moved shows no tooltip at all, which is the honest answer.
            filter: (item) => (item.parsed.y ?? 0) !== 0,
            itemSort: (a, b) => (b.parsed.y ?? 0) - (a.parsed.y ?? 0),
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
    if (!chartRef.current) return;
    chartRef.current.data.labels = data.labels;
    chartRef.current.data.datasets = data.series.map(s => ({
      label: s.label,
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

  // The canvas stays mounted through loading and empty states, and the message overlays it. Swapping it for a
  // placeholder div meant the mount-once chart-creation effect could fire while the canvas was absent and never
  // run again, leaving a permanently blank chart on any tab whose first render had no data yet.
  const overlay = loading ? 'Loading...' : series.length === 0 ? 'No hourly data yet' : null;

  return (
    <div style={{ height: 240 }} className="relative">
      <canvas ref={canvasRef} />
      {overlay !== null && (
        <div className="absolute inset-0 flex items-center justify-center bg-card text-sm text-muted-foreground">
          {overlay}
        </div>
      )}
    </div>
  );
}
