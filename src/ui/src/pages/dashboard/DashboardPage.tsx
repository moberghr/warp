import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  Chart,
  LineController,
  LineElement,
  PointElement,
  LinearScale,
  CategoryScale,
  Filler,
  Tooltip as ChartTooltip,
  Legend,
} from 'chart.js';
import { Briefcase, Clock, Layers, Loader, Mail, XCircle } from 'lucide-react';
import { StatCard } from '@/components/v2/StatCard';
import { DashboardSkeleton } from '@/components/skeletons/DashboardSkeleton';
import { useDashboardStore } from '@/stores/dashboard';
import { usePageStore } from '@/stores/page';
import { useInfo } from '@/api/hooks/useInfo';
import { RealtimeChart } from '@/components/RealtimeChart';
import { getStatsHistory } from '@/api';
import type { StatsHistoryPoint } from '@/types';

Chart.register(LineController, LineElement, PointElement, LinearScale, CategoryScale, Filler, ChartTooltip, Legend);

function padHistory(data: StatsHistoryPoint[], hours: number) {
  const now = new Date();
  now.setMinutes(0, 0, 0);
  const dataMap = new Map(data.map((d) => [new Date(d.hour).getTime(), d]));

  if (hours <= 24) {
    const result = [];
    for (let i = hours - 1; i >= 0; i--) {
      const hourDate = new Date(now.getTime() - i * 3600000);
      const point = dataMap.get(hourDate.getTime());
      result.push({
        label: hourDate.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false }),
        succeeded: point?.succeeded ?? 0,
        failed: point?.failed ?? 0,
      });
    }
    return result;
  }

  const days = Math.ceil(hours / 24);
  const result = [];
  for (let d = days - 1; d >= 0; d--) {
    const dayStart = new Date(now.getTime() - d * 86400000);
    dayStart.setHours(0, 0, 0, 0);
    let succeeded = 0;
    let failed = 0;
    for (let h = 0; h < 24; h++) {
      const hourKey = new Date(dayStart.getTime() + h * 3600000).getTime();
      const point = dataMap.get(hourKey);
      if (point) {
        succeeded += point.succeeded;
        failed += point.failed;
      }
    }
    result.push({
      label: `${dayStart.toLocaleDateString([], { weekday: 'short' })} ${String(dayStart.getDate()).padStart(2, '0')}.${String(dayStart.getMonth() + 1).padStart(2, '0')}`,
      succeeded,
      failed,
    });
  }
  return result;
}

export default function DashboardPage() {
  const stats = useDashboardStore((s) => s.stats);
  const error = useDashboardStore((s) => s.error);
  const { data: info } = useInfo();

  const [history, setHistory] = useState<StatsHistoryPoint[]>([]);
  const [historyHours, setHistoryHours] = useState(24);

  useEffect(() => {
    getStatsHistory(historyHours).then(setHistory).catch(() => {});
    const id = setInterval(() => {
      getStatsHistory(historyHours).then(setHistory).catch(() => {});
    }, 60000);

    return () => clearInterval(id);
  }, [historyHours]);

  useEffect(() => {
    const parts: string[] = [];
    if (info?.schema) {
      parts.push(info.schema);
    }
    if (info?.provider) {
      parts.push(info.provider);
    }
    parts.push('live');

    usePageStore.getState().set({
      title: 'Dashboard',
      subtitle: parts.join(' · '),
      right: undefined,
    });

    return () => {
      usePageStore.getState().reset();
    };
  }, [info?.schema, info?.provider]);

  if (!stats) {
    if (error) {
      return (
        <div className="p-6">
          <div className="max-w-xl rounded-lg border border-warp-red/40 bg-warp-red-soft px-4 py-3 text-[13px] text-warp-red">
            <div className="font-semibold mb-1">Dashboard unavailable</div>
            <div className="opacity-90">{error}</div>
          </div>
        </div>
      );
    }

    return <DashboardSkeleton />;
  }

  const dbStatus = stats.databaseConnection;
  const dbHealthy = !dbStatus || dbStatus === 'Healthy' || dbStatus === 'Open';

  return (
    <div className="flex flex-col gap-3">
      {error && (
        <div className="rounded-lg border border-warp-amber/40 bg-warp-amber-soft px-3 py-2 text-[12px] text-warp-amber">
          {error} — showing last known data.
        </div>
      )}
      {dbStatus && !dbHealthy && (
        <div className="rounded-lg border border-warp-red/40 bg-warp-red-soft px-3 py-2 text-[12px] text-warp-red">
          Database: {dbStatus}
        </div>
      )}
      {/* 6-up stat row */}
      <div className="grid grid-cols-2 gap-2.5 md:grid-cols-3 lg:grid-cols-6">
        <StatCard
          label="Enqueued"
          value={stats.created}
          icon={Briefcase}
          href="/jobs/enqueued"
          as={Link}
        />
        <StatCard
          label="Processing"
          value={stats.processing}
          icon={Loader}
          accentClass={stats.processing > 0 ? 'text-warp-purple' : undefined}
          accentColor={stats.processing > 0 ? 'var(--warp-purple)' : undefined}
          href="/jobs/processing"
          as={Link}
        />
        <StatCard
          label="Scheduled"
          value={stats.scheduled}
          icon={Clock}
          href="/jobs/scheduled"
          as={Link}
        />
        <StatCard
          label="Failed"
          value={stats.failed}
          icon={XCircle}
          accentClass={stats.failed > 0 ? 'text-warp-red' : undefined}
          accentColor={stats.failed > 0 ? 'var(--warp-red)' : undefined}
          href="/jobs/failed"
          as={Link}
        />
        <StatCard
          label="Messages"
          value={stats.messages}
          icon={Mail}
          href="/messages/enqueued"
          as={Link}
          sub={
            stats.messagesFailed > 0
              ? `${stats.messagesFailed} failed`
              : undefined
          }
        />
        <StatCard
          label="Batches"
          value={stats.batchesProcessing}
          icon={Layers}
          href="/batches/processing"
          as={Link}
          sub={
            stats.batchesProcessing > 0
              ? `${stats.batchesProcessing} in progress`
              : undefined
          }
        />
      </div>

      <div className="rounded-xl border border-warp-border bg-warp-surface p-4">
        <div className="mb-2 text-[13px] font-medium text-foreground">Realtime — last 60 seconds</div>
        <RealtimeChart />
      </div>

      <div className="rounded-xl border border-warp-border bg-warp-surface p-4">
        <div className="mb-2 flex items-center justify-between">
          <div className="text-[13px] font-medium text-foreground">History</div>
          <div className="flex gap-1">
            {[
              { label: '24h', hours: 24 },
              { label: '7d', hours: 168 },
            ].map(({ label, hours }) => (
              <button
                key={label}
                type="button"
                onClick={() => {
                  setHistoryHours(hours);
                  getStatsHistory(hours).then(setHistory).catch(() => {});
                }}
                className={`rounded-md px-2 py-0.5 text-xs transition-colors ${
                  historyHours === hours
                    ? 'bg-primary text-primary-foreground'
                    : 'text-muted-foreground hover:bg-accent'
                }`}
              >
                {label}
              </button>
            ))}
          </div>
        </div>
        <HistoryChart data={padHistory(history, historyHours)} />
      </div>
    </div>
  );
}

function HistoryChart({ data }: { data: { label: string; succeeded: number; failed: number }[] }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const chartRef = useRef<Chart | null>(null);

  useEffect(() => {
    if (!canvasRef.current) {
      return;
    }

    const isDark = document.documentElement.classList.contains('dark');
    const gridColor = isDark ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.08)';
    const textColor = isDark ? '#888' : '#666';

    chartRef.current = new Chart(canvasRef.current, {
      type: 'line',
      data: {
        labels: [],
        datasets: [
          {
            label: 'Succeeded',
            data: [],
            borderColor: '#22c55e',
            backgroundColor: 'rgba(34, 197, 94, 0.15)',
            borderWidth: 2,
            fill: true,
            pointRadius: 0,
            pointHitRadius: 10,
            tension: 0.3,
          },
          {
            label: 'Failed',
            data: [],
            borderColor: '#ef4444',
            backgroundColor: 'rgba(239, 68, 68, 0.15)',
            borderWidth: 2,
            fill: true,
            pointRadius: 0,
            pointHitRadius: 10,
            tension: 0.3,
          },
        ],
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        interaction: { mode: 'index', intersect: false },
        scales: {
          x: {
            ticks: { color: textColor, font: { size: 10 }, maxRotation: 0, autoSkip: true, maxTicksLimit: 24 },
            grid: { color: gridColor },
          },
          y: {
            beginAtZero: true,
            ticks: { color: textColor, font: { size: 10 }, precision: 0 },
            grid: { color: gridColor },
          },
        },
        plugins: {
          legend: { display: false },
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
    if (!chartRef.current) {
      return;
    }

    chartRef.current.data.labels = data.map((d) => d.label);
    chartRef.current.data.datasets[0].data = data.map((d) => d.succeeded);
    chartRef.current.data.datasets[1].data = data.map((d) => d.failed);
    chartRef.current.update();
  }, [data]);

  return (
    <div style={{ height: 200 }}>
      <canvas ref={canvasRef} />
    </div>
  );
}
