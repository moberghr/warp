import { useRef, useEffect } from 'react';
import {
  Chart,
  BarController,
  BarElement,
  LinearScale,
  TimeScale,
  Tooltip,
  Legend,
} from 'chart.js';
import 'chartjs-adapter-luxon';

Chart.register(BarController, BarElement, LinearScale, TimeScale, Tooltip, Legend);

/** One hourly point of the webhook delivery-statistics series. */
export interface DeliveryPoint {
  hour: string;
  delivered: number;
  exhausted: number;
  pending: number;
  total: number;
}

// Delivery-statistics chart: hourly stacked bars of deliveries created that hour, split by current status
// (Delivered green / Exhausted red / Pending amber). Rebuilds imperatively when the points change (so it
// re-reads the light/dark theme). Muted placeholder until there is data.
export function WebhookDeliveryChart({ points, height = 240 }: { points: DeliveryPoint[]; height?: number }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const chartRef = useRef<Chart | null>(null);

  useEffect(() => {
    if (!canvasRef.current || points.length === 0) {
      return;
    }

    const isDark = document.documentElement.classList.contains('dark');
    const gridColor = isDark ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.08)';
    const textColor = isDark ? '#888' : '#666';

    const at = (n: keyof DeliveryPoint) => points.map((p) => ({ x: new Date(p.hour).getTime(), y: p[n] as number }));

    chartRef.current = new Chart(canvasRef.current, {
      type: 'bar',
      data: {
        datasets: [
          { label: 'Delivered', backgroundColor: 'rgba(34, 197, 94, 0.6)', data: at('delivered'), stack: 's' },
          { label: 'Pending', backgroundColor: 'rgba(245, 158, 11, 0.65)', data: at('pending'), stack: 's' },
          { label: 'Exhausted', backgroundColor: 'rgba(239, 68, 68, 0.7)', data: at('exhausted'), stack: 's' },
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
            title: { display: true, text: 'Deliveries', color: textColor, font: { size: 10 } },
            ticks: { color: textColor, font: { size: 10 }, precision: 0 },
            grid: { color: gridColor },
          },
        },
        plugins: {
          legend: { labels: { color: textColor, boxWidth: 12, font: { size: 11 } } },
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
        No deliveries recorded yet.
      </div>
    );
  }

  return (
    <div style={{ height }}>
      <canvas ref={canvasRef} />
    </div>
  );
}
