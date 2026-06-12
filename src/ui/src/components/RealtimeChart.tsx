import { useRef, useEffect, useMemo } from 'react';
import {
  Chart,
  LineController,
  LineElement,
  PointElement,
  LinearScale,
  TimeScale,
  Filler,
} from 'chart.js';
import 'chartjs-adapter-luxon';
import { useDashboardStore } from '@/stores/dashboard';

Chart.register(LineController, LineElement, PointElement, LinearScale, TimeScale, Filler);

export function RealtimeChart({ height = 200 }: { height?: number }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const chartRef = useRef<Chart | null>(null);
  const lastRenderedTs = useRef(0);
  const rafId = useRef(0);
  const realtimeData = useDashboardStore((s) => s.realtimeData);

  // The store buffer holds up to an hour of samples; the panel claims
  // "last 60 seconds", so window the header stats to the newest sample.
  // Single memoized pass — the buffer ticks 5×/s and can hold 18k points.
  const { current, max, avg } = useMemo(() => {
    const lastTs = realtimeData.length > 0 ? realtimeData[realtimeData.length - 1].ts : 0;
    const cutoff = lastTs - 60;
    let c = 0;
    let m = 0;
    let sum = 0;
    let n = 0;
    for (const p of realtimeData) {
      if (p.ts < cutoff) {
        continue;
      }
      const v = p.succeeded + p.failed;
      c = v;
      if (v > m) {
        m = v;
      }
      sum += v;
      n++;
    }

    return {
      current: Math.round(c),
      max: Math.round(m),
      avg: n >= 5 ? Math.round(sum / n) : null,
    };
  }, [realtimeData]);

  useEffect(() => {
    if (!canvasRef.current) return;

    const isDark = document.documentElement.classList.contains('dark');
    const gridColor = isDark ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.08)';
    const textColor = isDark ? '#888' : '#666';
    const css = getComputedStyle(document.documentElement);
    const okColor = css.getPropertyValue('--state-completed').trim() || '#22c55e';
    const okBg = css.getPropertyValue('--state-completed-bg').trim() || 'rgba(34, 197, 94, 0.15)';
    const failColor = css.getPropertyValue('--state-failed').trim() || '#ef4444';
    const failBg = css.getPropertyValue('--state-failed-bg').trim() || 'rgba(239, 68, 68, 0.15)';
    const now = Date.now();

    const storeData = useDashboardStore.getState().realtimeData;
    if (storeData.length > 0) {
      lastRenderedTs.current = storeData[storeData.length - 1].ts;
    }

    // Delay data by 1s so points exist before the axis reaches them
    chartRef.current = new Chart(canvasRef.current, {
      type: 'line',
      data: {
        datasets: [
          {
            label: 'Succeeded/s',
            borderColor: okColor,
            backgroundColor: okBg,
            borderWidth: 2,
            fill: true,
            pointRadius: 0,
            tension: 0.3,
            data: storeData.map((p) => ({ x: p.ts * 1000, y: p.succeeded })),
          },
          {
            label: 'Failed/s',
            borderColor: failColor,
            backgroundColor: failBg,
            borderWidth: 2,
            fill: true,
            pointRadius: 0,
            tension: 0.3,
            data: storeData.map((p) => ({ x: p.ts * 1000, y: p.failed })),
          },
        ],
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        events: [],
        scales: {
          x: {
            type: 'time',
            time: { unit: 'second', displayFormats: { second: 'HH:mm:ss' } },
            min: now - 61000,
            max: now - 1000,
            ticks: { display: false },
            grid: { color: gridColor },
          },
          y: {
            beginAtZero: true,
            ticks: { color: textColor, font: { size: 10 }, precision: 0 },
            grid: { color: gridColor },
          },
        },
        plugins: { legend: { display: false }, tooltip: { enabled: false } },
      },
    });

    // Scroll at 30fps — enough for smooth appearance, less CPU than 60fps
    let lastFrame = 0;
    const scroll = (frameTime: number) => {
      if (!chartRef.current) return;
      if (frameTime - lastFrame >= 33) {
        lastFrame = frameTime;
        const t = Date.now();
        const xScale = chartRef.current.options.scales!.x!;
        xScale.min = t - 62000;
        xScale.max = t - 2000;
        chartRef.current.update('none');
      }
      rafId.current = requestAnimationFrame(scroll);
    };
    rafId.current = requestAnimationFrame(scroll);

    return () => {
      cancelAnimationFrame(rafId.current);
      chartRef.current?.destroy();
      chartRef.current = null;
    };
  }, []);

  // Push new data points from store
  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;

    const data = useDashboardStore.getState().realtimeData;
    const now = Date.now();

    for (const p of data) {
      if (p.ts <= lastRenderedTs.current) continue;
      (chart.data.datasets[0].data as { x: number; y: number }[]).push({ x: p.ts * 1000, y: p.succeeded });
      (chart.data.datasets[1].data as { x: number; y: number }[]).push({ x: p.ts * 1000, y: p.failed });
      lastRenderedTs.current = p.ts;
    }

    // Trim old points
    const cutoff = now - 70000;
    for (const ds of chart.data.datasets) {
      const arr = ds.data as { x: number; y: number }[];
      while (arr.length > 0 && arr[0].x < cutoff) arr.shift();
    }
  }, [realtimeData]);

  return (
    <div>
      <div className="flex gap-6 mb-2 text-sm">
        <span className="text-muted-foreground">Current: <span className="font-medium text-foreground">{current}/s</span></span>
        <span className="text-muted-foreground">Avg: <span className="font-medium text-foreground">{avg != null ? `${avg}/s` : '-'}</span></span>
        <span className="text-muted-foreground">Peak: <span className="font-medium text-foreground">{max}/s</span></span>
      </div>
      <div style={{ height }}>
        <canvas ref={canvasRef} />
      </div>
    </div>
  );
}
