import { useEffect, useMemo, useRef } from 'react';
import {
  createChart,
  AreaSeries,
  LineSeries,
  type IChartApi,
  type ISeriesApi,
  type UTCTimestamp,
} from 'lightweight-charts';
import { Panel } from '@/components/v2/Panel';
import { useStatsHistory } from '@/api/hooks/useDashboard';

const H = 200;

interface HistoryPoint {
  hour: string;
  succeeded: number;
  failed: number;
}

export function HistoryChart() {
  const { data, isLoading } = useStatsHistory(24);

  const series = useMemo(() => padHistory(data ?? []), [data]);
  const totals = useMemo(() => {
    let s = 0;
    let f = 0;
    for (const p of series) {
      s += p.succeeded;
      f += p.failed;
    }

    return { succeeded: s, failed: f };
  }, [series]);

  return (
    <Panel className="flex h-full min-h-[240px] flex-col gap-2 px-4 py-3.5">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <span className="text-[13.5px] font-semibold">History</span>
          <span className="text-[11.5px] text-text-mute">completed · 24h</span>
        </div>
        <div className="mono flex gap-3 text-[11.5px]">
          <span>
            <span className="inline-block size-2 rounded-full bg-warp-green align-middle" />{' '}
            <span className="text-text-mute">succeeded </span>
            <span className="font-semibold text-warp-green">
              {totals.succeeded.toLocaleString()}
            </span>
          </span>
          <span>
            <span className="inline-block size-2 rounded-full bg-warp-red align-middle" />{' '}
            <span className="text-text-mute">failed </span>
            <span className="font-semibold text-warp-red">
              {totals.failed.toLocaleString()}
            </span>
          </span>
        </div>
      </div>

      <div className="relative flex-1">
        <TVHistoryChart data={series} />
        {isLoading && !data && (
          <div className="pointer-events-none absolute inset-0 flex items-center justify-center">
            <span className="mono text-[11px] text-text-mute">loading…</span>
          </div>
        )}
      </div>
    </Panel>
  );
}

interface TVHistoryChartProps {
  data: HistoryPoint[];
}

function TVHistoryChart({ data }: TVHistoryChartProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const tooltipRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const succRef = useRef<ISeriesApi<'Area'> | null>(null);
  const failRef = useRef<ISeriesApi<'Line'> | null>(null);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) {
      return;
    }

    const css = getComputedStyle(document.documentElement);
    const green = css.getPropertyValue('--warp-green').trim() || '#22c55e';
    const red = css.getPropertyValue('--warp-red').trim() || '#ef4444';
    const muted = css.getPropertyValue('--text-mute').trim() || '#71717a';
    const border = css.getPropertyValue('--border').trim() || '#e5e7eb';

    const chart = createChart(container, {
      width: container.clientWidth,
      height: H,
      layout: {
        background: { color: 'transparent' },
        textColor: muted,
        fontSize: 10,
        fontFamily: '"Geist Mono Variable", ui-monospace, monospace',
        attributionLogo: false,
      },
      grid: {
        vertLines: { visible: false },
        horzLines: { color: border, style: 1 },
      },
      rightPriceScale: {
        borderVisible: false,
        scaleMargins: { top: 0.15, bottom: 0 },
      },
      timeScale: {
        borderVisible: false,
        timeVisible: true,
        secondsVisible: false,
        rightOffset: 1,
        barSpacing: 16,
        fixLeftEdge: true,
        fixRightEdge: true,
      },
      crosshair: {
        mode: 1,
        vertLine: { color: muted, width: 1, style: 2, labelVisible: false },
        horzLine: { color: muted, width: 1, style: 2, labelVisible: false },
      },
      handleScale: false,
      handleScroll: false,
      autoSize: false,
    });

    const MIN_MAX = 10;
    const floorAtZero = (
      original: () => { priceRange: { minValue: number; maxValue: number } } | null,
    ) => {
      const r = original();
      if (r) {
        r.priceRange.minValue = 0;
        if (r.priceRange.maxValue < MIN_MAX) {
          r.priceRange.maxValue = MIN_MAX;
        }

        return r;
      }

      return { priceRange: { minValue: 0, maxValue: MIN_MAX } };
    };

    const succ = chart.addSeries(AreaSeries, {
      lineColor: green,
      topColor: withAlpha(green, 0.42),
      bottomColor: withAlpha(green, 0),
      lineWidth: 2,
      priceLineVisible: false,
      lastValueVisible: false,
      crosshairMarkerVisible: true,
      crosshairMarkerRadius: 4,
      autoscaleInfoProvider: floorAtZero,
    });
    const fail = chart.addSeries(LineSeries, {
      color: red,
      lineWidth: 2,
      priceLineVisible: false,
      lastValueVisible: false,
      crosshairMarkerVisible: true,
      crosshairMarkerRadius: 4,
      autoscaleInfoProvider: floorAtZero,
    });

    chartRef.current = chart;
    succRef.current = succ;
    failRef.current = fail;

    chart.subscribeCrosshairMove((param) => {
      const tip = tooltipRef.current;
      if (!tip) {
        return;
      }
      if (
        !param.point ||
        param.time === undefined ||
        param.point.x < 0 ||
        param.point.y < 0 ||
        param.point.x > container.clientWidth ||
        param.point.y > H
      ) {
        tip.style.display = 'none';

        return;
      }

      const succVal = param.seriesData.get(succ) as { value?: number } | undefined;
      const failVal = param.seriesData.get(fail) as { value?: number } | undefined;
      const s = Math.round(succVal?.value ?? 0);
      const f = Math.round(failVal?.value ?? 0);
      const d = new Date((param.time as number) * 1000);
      const hh = String(d.getHours()).padStart(2, '0');
      const label = `${hh}:00`;

      tip.innerHTML =
        `<div class="mono text-[10.5px] text-text-mute mb-0.5">${label}</div>` +
        `<div class="mono flex items-center gap-1.5 text-[11px]"><span class="inline-block size-1.5 rounded-full bg-warp-green"></span><span class="text-text-mute">succeeded</span><span class="ml-auto font-semibold text-warp-green">${s.toLocaleString()}</span></div>` +
        `<div class="mono flex items-center gap-1.5 text-[11px]"><span class="inline-block size-1.5 rounded-full bg-warp-red"></span><span class="text-text-mute">failed</span><span class="ml-auto font-semibold text-warp-red">${f.toLocaleString()}</span></div>`;
      tip.style.display = 'block';

      const w = tip.offsetWidth;
      const cw = container.clientWidth;
      let left = param.point.x + 12;
      if (left + w > cw - 4) {
        left = param.point.x - w - 12;
      }
      tip.style.left = `${Math.max(4, left)}px`;
      tip.style.top = '4px';
    });

    const ro = new ResizeObserver(() => {
      const w = container.clientWidth;
      if (w > 0 && chartRef.current) {
        chartRef.current.applyOptions({ width: w });
      }
    });
    ro.observe(container);

    return () => {
      ro.disconnect();
      chart.remove();
      chartRef.current = null;
      succRef.current = null;
      failRef.current = null;
    };
  }, []);

  useEffect(() => {
    if (!succRef.current || !failRef.current || data.length === 0) {
      return;
    }

    const succData = data.map((p) => ({
      time: Math.floor(new Date(p.hour).getTime() / 1000) as UTCTimestamp,
      value: p.succeeded,
    }));
    const failData = data.map((p) => ({
      time: Math.floor(new Date(p.hour).getTime() / 1000) as UTCTimestamp,
      value: p.failed,
    }));
    succRef.current.setData(succData);
    failRef.current.setData(failData);
    chartRef.current?.timeScale().fitContent();
  }, [data]);

  return (
    <div className="relative h-full w-full" style={{ height: H }}>
      <div ref={containerRef} className="h-full w-full" />
      <div
        ref={tooltipRef}
        className="pointer-events-none absolute z-10 hidden min-w-[140px] rounded-md border border-border bg-panel/95 px-2 py-1.5 shadow-md backdrop-blur"
      />
    </div>
  );
}

function withAlpha(color: string, alpha: number): string {
  const trimmed = color.trim();
  if (trimmed.startsWith('#')) {
    const hex = trimmed.slice(1);
    if (hex.length === 6) {
      const r = parseInt(hex.slice(0, 2), 16);
      const g = parseInt(hex.slice(2, 4), 16);
      const b = parseInt(hex.slice(4, 6), 16);
      return `rgba(${r},${g},${b},${alpha})`;
    }
  }

  return `color-mix(in srgb, ${trimmed} ${Math.round(alpha * 100)}%, transparent)`;
}

function padHistory(data: HistoryPoint[]): HistoryPoint[] {
  const now = new Date();
  now.setMinutes(0, 0, 0);
  const map = new Map(data.map((d) => [new Date(d.hour).getTime(), d]));
  const out: HistoryPoint[] = [];
  for (let i = 23; i >= 0; i--) {
    const h = new Date(now.getTime() - i * 3600000);
    const p = map.get(h.getTime());
    out.push({
      hour: h.toISOString(),
      succeeded: p?.succeeded ?? 0,
      failed: p?.failed ?? 0,
    });
  }

  return out;
}
