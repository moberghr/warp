import { useState, useEffect, useRef, useMemo } from 'react';
import { Chart, LineController, LineElement, PointElement, LinearScale, CategoryScale, Filler, Tooltip as ChartTooltip, Legend } from 'chart.js';
import { Panel, PanelHeader } from '@/components/v2/Panel';
import { LoadingState, ErrorState } from '@/components/PageState';
import { usePageStore } from '@/stores/page';
import type { CounterHistoryPoint } from '@/types';
import { useCounters, useCountersHistory } from '@/api/hooks/useCounters';

Chart.register(LineController, LineElement, PointElement, LinearScale, CategoryScale, Filler, ChartTooltip, Legend);

const builtInColors: Record<string, string> = {
  'stats:succeeded': '#15803D',
  'stats:failed': '#B91C1C',
  'stats:deleted': '#8E815F',
  'stats:requeued': '#B45309',
};

// Server hour buckets are UTC hour boundaries; build keys the same way
// (local hour starts only coincide with UTC ones for whole-hour offsets).
function latestUtcHourStart(): number {
  return Math.floor(Date.now() / 3600000) * 3600000;
}

// Deterministic color from key for addon-defined metrics. Same key → same color across reloads.
function colorFor(key: string): string {
  if (builtInColors[key]) {
    return builtInColors[key];
  }
  let hash = 0;
  for (let i = 0; i < key.length; i++) {
    hash = (hash * 31 + key.charCodeAt(i)) | 0;
  }
  const hue = Math.abs(hash) % 360;
  return `hsl(${hue}, 65%, 50%)`;
}

export default function CountersPage() {
  const [historyHours, setHistoryHours] = useState(24);
  const countersQuery = useCounters();
  const historyQuery = useCountersHistory(historyHours);

  useEffect(() => {
    usePageStore.getState().set({
      title: 'Counters',
      subtitle: 'Raw counter rows from the database',
    });
    return () => usePageStore.getState().reset();
  }, []);

  if (countersQuery.error) {
    return <ErrorState message={(countersQuery.error as Error).message} />;
  }
  if (!countersQuery.data) {
    return <LoadingState />;
  }

  const counters = countersQuery.data;
  const history = historyQuery.data ?? null;

  return (
    <div className="flex flex-col gap-3 py-5">
      <p className="text-[12.5px] text-text-mute">
        Built-in: <code className="font-mono text-foreground">stats:succeeded</code>,{' '}
        <code className="font-mono text-foreground">stats:failed</code>,{' '}
        <code className="font-mono text-foreground">stats:deleted</code>,{' '}
        <code className="font-mono text-foreground">stats:requeued</code>. Addons can write their own keys here.
      </p>

      <Panel>
        <PanelHeader
          eyebrow="Hourly history"
          action={
            <div className="flex gap-1">
              {[
                { label: '24h', hours: 24 },
                { label: '7d', hours: 168 },
              ].map(({ label, hours }) => (
                <button
                  key={label}
                  type="button"
                  onClick={() => setHistoryHours(hours)}
                  className={`px-2 py-0.5 text-[11px] font-medium rounded-md transition-colors ${
                    historyHours === hours
                      ? 'bg-foreground text-background'
                      : 'text-text-mute hover:bg-panel-2'
                  }`}
                >
                  {label}
                </button>
              ))}
            </div>
          }
        />
        <div className="p-4">
          <HistoryChart points={history} hours={historyHours} />
        </div>
      </Panel>

      {counters.length === 0 ? (
        <Panel>
          <div className="py-10 text-center text-[13px] text-text-mute">No counters</div>
        </Panel>
      ) : (
        <Panel className="overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full border-collapse">
              <thead>
                <tr className="bg-panel-2 border-b border-border">
                  <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Key</th>
                  <th className="warp-eyebrow text-right px-3.5 py-2.5 text-text-mute font-semibold w-40">Value</th>
                </tr>
              </thead>
              <tbody>
                {counters.map((c) => (
                  <tr key={c.key} className="border-b border-border last:border-b-0 hover:bg-panel-2/60">
                    <td className="px-3.5 py-2 font-mono text-[12.5px]">{c.key}</td>
                    <td className="px-3.5 py-2 text-right font-mono text-[12.5px] tabular-nums">{c.value.toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Panel>
      )}
    </div>
  );
}

function HistoryChart({ points, hours }: { points: CounterHistoryPoint[] | null; hours: number }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const chartRef = useRef<Chart | null>(null);

  // Pivot points → labels + per-key series, padding empty hours with 0.
  const data = useMemo(() => {
    if (!points) {
      return null;
    }

    const latestHour = latestUtcHourStart();

    const labels: string[] = [];
    const hourTimes: number[] = [];
    for (let i = hours - 1; i >= 0; i--) {
      const t = latestHour - i * 3600000;
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
      if (idx < 0) {
        continue;
      }

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
    if (!canvasRef.current) {
      return;
    }

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

    return () => {
      chartRef.current?.destroy();
      chartRef.current = null;
    };
  }, []);

  useEffect(() => {
    if (!chartRef.current || !data) {
      return;
    }
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
    return (
      <div style={{ height: 240 }} className="flex flex-col gap-2 p-3">
        <div className="h-4 w-32 rounded bg-panel-2 animate-pulse" />
        <div className="flex-1 rounded bg-panel-2 animate-pulse" />
      </div>
    );
  }

  if (data && data.series.length === 0) {
    return <div style={{ height: 240 }} className="flex items-center justify-center text-[13px] text-text-mute">No hourly data yet</div>;
  }

  return (
    <div style={{ height: 240 }}>
      <canvas ref={canvasRef} />
    </div>
  );
}
