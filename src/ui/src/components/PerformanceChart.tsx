import { useRef, useEffect } from 'react';
import {
  Chart,
  BarController,
  BarElement,
  LineController,
  LineElement,
  PointElement,
  LinearScale,
  TimeScale,
  Tooltip,
  Legend,
} from 'chart.js';
import 'chartjs-adapter-luxon';

Chart.register(BarController, BarElement, LineController, LineElement, PointElement, LinearScale, TimeScale, Tooltip, Legend);

/** One hourly point of a performance time-series (shared shape for adapters + endpoints). */
export interface PerformancePoint {
  hour: string;
  calls: number;
  errors: number;
  errorRate: number;
  avgDurationMs: number;
}

// Sentry-style performance chart: stacked hourly volume bars (successes green, errors red) with an
// average-latency line on a secondary axis. Rebuilds imperatively when the points change (the detail page
// refetches on events) so it also re-reads the current light/dark theme. Renders a muted placeholder until
// there is data.
export function PerformanceChart({ points, height = 240 }: { points: PerformancePoint[]; height?: number }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const chartRef = useRef<Chart | null>(null);

  useEffect(() => {
    if (!canvasRef.current || points.length === 0) {
      return;
    }

    const isDark = document.documentElement.classList.contains('dark');
    const gridColor = isDark ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.08)';
    const textColor = isDark ? '#888' : '#666';

    const success = points.map((p) => ({ x: new Date(p.hour).getTime(), y: Math.max(0, p.calls - p.errors) }));
    const errors = points.map((p) => ({ x: new Date(p.hour).getTime(), y: p.errors }));
    const latency = points.map((p) => ({ x: new Date(p.hour).getTime(), y: Math.round(p.avgDurationMs) }));

    chartRef.current = new Chart(canvasRef.current, {
      data: {
        datasets: [
          {
            type: 'bar',
            label: 'Succeeded',
            backgroundColor: 'rgba(34, 197, 94, 0.55)',
            data: success,
            stack: 'calls',
            yAxisID: 'y',
            order: 2,
          },
          {
            type: 'bar',
            label: 'Errors',
            backgroundColor: 'rgba(239, 68, 68, 0.65)',
            data: errors,
            stack: 'calls',
            yAxisID: 'y',
            order: 2,
          },
          {
            type: 'line',
            label: 'Avg latency (ms)',
            borderColor: '#6366f1',
            backgroundColor: '#6366f1',
            borderWidth: 2,
            pointRadius: 2,
            tension: 0.3,
            data: latency,
            yAxisID: 'y1',
            order: 1,
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
            type: 'time',
            time: { unit: 'hour', displayFormats: { hour: 'MMM d, HH:mm' } },
            grid: { color: gridColor },
            ticks: { color: textColor, font: { size: 10 }, maxRotation: 0, autoSkip: true },
          },
          y: {
            stacked: true,
            beginAtZero: true,
            position: 'left',
            title: { display: true, text: 'Calls', color: textColor, font: { size: 10 } },
            ticks: { color: textColor, font: { size: 10 }, precision: 0 },
            grid: { color: gridColor },
          },
          y1: {
            beginAtZero: true,
            position: 'right',
            title: { display: true, text: 'Latency (ms)', color: textColor, font: { size: 10 } },
            ticks: { color: textColor, font: { size: 10 } },
            grid: { drawOnChartArea: false },
          },
        },
        plugins: {
          legend: { labels: { color: textColor, boxWidth: 12, font: { size: 11 } } },
          tooltip: {
            callbacks: {
              afterBody: (items) => {
                const point = points[items[0].dataIndex];

                return point ? `Error rate: ${(point.errorRate * 100).toFixed(point.errorRate < 0.1 ? 1 : 0)}%` : '';
              },
            },
          },
        },
      },
    });

    return () => {
      chartRef.current?.destroy();
      chartRef.current = null;
    };
  }, [points]);

  if (points.length === 0) {
    return (
      <div className="flex items-center justify-center text-sm text-muted-foreground" style={{ height }}>
        No traffic recorded yet.
      </div>
    );
  }

  return (
    <div style={{ height }}>
      <canvas ref={canvasRef} />
    </div>
  );
}
