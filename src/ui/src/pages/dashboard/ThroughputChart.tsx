import { useEffect, useRef, useState } from 'react';
import {
  createChart,
  AreaSeries,
  LineSeries,
  type IChartApi,
  type ISeriesApi,
  type UTCTimestamp,
} from 'lightweight-charts';
import { Panel } from '@/components/v2/Panel';
import { PulseDot } from '@/components/v2/PulseDot';
import { useDashboardStore } from '@/stores/dashboard';
import { ema } from '@/lib/svgPath';

type Range = '1m' | '5m' | '15m' | '1h';

const RANGE_SECONDS: Record<Range, number> = {
  '1m': 60,
  '5m': 300,
  '15m': 900,
  '1h': 3600,
};

// Rate-sampler frequency. The actual setInterval lives in `realtimeFeed`; this
// constant is kept here for header window math (samples per second).
const SAMPLE_HZ = 5;

// EMA smoothing factor. Source-side already produces 1-second moving-avg
// rates (see stores/dashboard.ts), so EMA is light here — just enough to
// polish the visual without making the line cling after activity stops.
// Effective window ≈ 1/α samples × 200ms (1m: ~1.5s, 5m: ~3s).
const EMA_ALPHA: Record<Range, number> = {
  '1m': 0.15,
  '5m': 0.07,
  '15m': 0.04,
  '1h': 0.02,
};

// Tick interval per range — used to draw a deterministic vertical grid + axis
// labels in the overlay (lightweight-charts' own tick spacing is auto-fit and
// changes with width, which makes the grid feel arbitrary).
const TICK_INTERVAL_SECONDS: Record<Range, number> = {
  '1m': 5,
  '5m': 30,
  '15m': 60,
  '1h': 300,
};

const H = 220;
const AXIS_H = 18;

export function ThroughputChart() {
  const [range, setRange] = useState<Range>('15m');
  const realtimeData = useDashboardStore((s) => s.realtimeData);

  // Sampler lives in `realtimeFeed` — single setInterval owned by MainLayout.

  const windowSec = RANGE_SECONDS[range];
  const alpha = EMA_ALPHA[range];

  // Header metrics: single pass over the visible window. Avoids
  // `.slice(-N).map(...)` plus `Math.max(...spread)`, which on a 1h × 5Hz
  // window means three full 18k-element passes plus a function-argument
  // spread that can hit the JS engine's arg cap.
  const windowSamples = windowSec * SAMPLE_HZ;
  const start = Math.max(0, realtimeData.length - windowSamples);
  const lastSecondStart = Math.max(start, realtimeData.length - SAMPLE_HZ);
  let peakVal = 0;
  let sumAll = 0;
  let countAll = 0;
  let sumLast = 0;
  let countLast = 0;
  for (let i = start; i < realtimeData.length; i++) {
    const v = realtimeData[i].succeeded;
    if (v > peakVal) {
      peakVal = v;
    }
    sumAll += v;
    countAll++;
    if (i >= lastSecondStart) {
      sumLast += v;
      countLast++;
    }
  }
  const now = countLast > 0 ? Math.round(sumLast / countLast) : 0;
  const peak = countAll > 0 ? Math.round(peakVal) : 0;
  const avg = countAll >= SAMPLE_HZ * 5 ? Math.round(sumAll / countAll) : null;

  return (
    <Panel className="flex h-full min-h-[260px] flex-col gap-2 px-4 py-3.5">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <PulseDot aria-label="Live data indicator" />
          <span className="text-[13.5px] font-semibold">Throughput</span>
          <span className="text-[11.5px] text-text-mute">
            jobs / second · {range} window
          </span>
        </div>
        <div className="flex items-center gap-3">
          <div className="mono flex gap-3 text-[11.5px]">
            <span>
              <span className="text-text-mute">now </span>
              <span className="font-semibold text-warp-green">{now}</span>
            </span>
            {avg != null && (
              <span>
                <span className="text-text-mute">avg </span>
                <span className="text-foreground">{avg}</span>
              </span>
            )}
            <span>
              <span className="text-text-mute">peak </span>
              <span className="text-foreground">{peak}</span>
            </span>
          </div>
          <div className="flex gap-0.5 rounded-md bg-panel-2 p-0.5" role="group" aria-label="Throughput time range">
            {(Object.keys(RANGE_SECONDS) as Range[]).map((r) => (
              <button
                key={r}
                onClick={() => setRange(r)}
                aria-label={`Show last ${r}`}
                aria-pressed={range === r}
                className={
                  'mono rounded px-2 py-0.5 text-[10.5px] font-semibold transition-colors ' +
                  (range === r
                    ? 'bg-warp-green-soft text-warp-green'
                    : 'text-text-dim hover:text-foreground')
                }
              >
                {r}
              </button>
            ))}
          </div>
        </div>
      </div>

      <div className="relative flex-1">
        <TVChart
          data={realtimeData}
          windowSec={windowSec}
          alpha={alpha}
          tickIntervalSec={TICK_INTERVAL_SECONDS[range]}
        />
        {realtimeData.length < 2 && (
          <div className="pointer-events-none absolute inset-0 flex items-center justify-center">
            <span className="mono text-[11px] text-text-mute">collecting samples…</span>
          </div>
        )}
      </div>
    </Panel>
  );
}

interface TVChartProps {
  data: { ts: number; succeeded: number; failed: number }[];
  windowSec: number;
  alpha: number;
  tickIntervalSec: number;
}

/**
 * TradingView lightweight-charts implementation. Built for live financial
 * time-series — produces buttery-smooth time-axis scrolling and animated
 * series updates out of the box. Replaces the hand-rolled SVG attempt.
 */
function TVChart({ data, windowSec, alpha, tickIntervalSec }: TVChartProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const overlayRef = useRef<SVGSVGElement>(null);
  const tooltipRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const succRef = useRef<ISeriesApi<'Area'> | null>(null);
  const failRef = useRef<ISeriesApi<'Line'> | null>(null);

  // Refs for the rAF loop so it reads fresh values without remounting the chart.
  const dataRef = useRef(data);
  const windowRef = useRef(windowSec);
  const alphaRef = useRef(alpha);
  const tickIntervalRef = useRef(tickIntervalSec);
  dataRef.current = data;
  windowRef.current = windowSec;
  alphaRef.current = alpha;
  tickIntervalRef.current = tickIntervalSec;

  // Mount the chart once.
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
      height: H - AXIS_H,
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
        // bottom: 0 keeps the zero line flush with the chart bottom and
        // prevents any negative tick labels from appearing below the data
        // (autoscaleInfoProvider on each series floors the data range at 0;
        // the margin is what was leaking into negative visual space).
        scaleMargins: { top: 0.1, bottom: 0 },
      },
      timeScale: {
        borderVisible: false,
        timeVisible: true,
        secondsVisible: true,
        rightOffset: 0,
        barSpacing: 1,
        // Hide the built-in time axis — we render our own deterministic ticks
        // in the SVG overlay so vertical-grid spacing always matches the
        // selected range (5s / 30s / 1m / 5m).
        visible: false,
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

    // Pin the y-axis to a fixed [0, MIN_MAX] range until data demands more
    // headroom. Two reasons:
    //   1. min=0 — auto-scale otherwise pads the bottom margin into negative
    //      territory whenever the series is flat at zero.
    //   2. max=MIN_MAX — reserves vertical layout space as if there were ~10
    //      jobs/s on screen, so the chart doesn't visibly resize the moment
    //      the first real sample arrives. The ceiling grows when actual data
    //      exceeds it.
    const MIN_MAX = 10;
    const floorAtZero = (original: () => { priceRange: { minValue: number; maxValue: number } } | null) => {
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
      crosshairMarkerVisible: false,
      autoscaleInfoProvider: floorAtZero,
    });
    const fail = chart.addSeries(LineSeries, {
      color: red,
      lineWidth: 2,
      priceLineVisible: false,
      lastValueVisible: false,
      crosshairMarkerVisible: false,
      autoscaleInfoProvider: floorAtZero,
    });

    chartRef.current = chart;
    succRef.current = succ;
    failRef.current = fail;

    const plotH = H - AXIS_H;
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
        param.point.y > plotH
      ) {
        tip.style.display = 'none';

        return;
      }

      const succVal = param.seriesData.get(succ) as { value?: number } | undefined;
      const failVal = param.seriesData.get(fail) as { value?: number } | undefined;
      const s = Math.round(succVal?.value ?? 0).toString();
      const f = Math.round(failVal?.value ?? 0).toString();
      const d = new Date((param.time as number) * 1000);
      const hh = String(d.getHours()).padStart(2, '0');
      const mm = String(d.getMinutes()).padStart(2, '0');
      const ss = String(d.getSeconds()).padStart(2, '0');

      tip.innerHTML =
        `<div class="mono text-[10.5px] text-text-mute mb-0.5">${hh}:${mm}:${ss}</div>` +
        `<div class="mono flex items-center gap-1.5 text-[11px]"><span class="inline-block size-1.5 rounded-full bg-warp-green"></span><span class="text-text-mute">succ</span><span class="ml-auto font-semibold text-warp-green">${s}/s</span></div>` +
        `<div class="mono flex items-center gap-1.5 text-[11px]"><span class="inline-block size-1.5 rounded-full bg-warp-red"></span><span class="text-text-mute">fail</span><span class="ml-auto font-semibold text-warp-red">${f}/s</span></div>`;
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

    // rAF loop: push EMA-smoothed data and pan the time scale to a rolling
    // window ending at `now`. Lightweight-charts interpolates point positions
    // between frames, giving a flowing live-data feel.
    let raf = 0;
    let lastDataLen = -1;
    const tick = () => {
      const d = dataRef.current;
      if (d.length >= 2) {
        // Only rebuild the dataset when the source array length changed
        // (1Hz). The rAF loop still re-pans the visible range every frame
        // for smooth scrolling between samples.
        if (d.length !== lastDataLen) {
          lastDataLen = d.length;
          const succValues = ema(d.map((p) => p.succeeded), alphaRef.current);
          const failValues = ema(d.map((p) => p.failed), alphaRef.current);
          // Lightweight-charts requires strictly-ascending unique timestamps.
          // `realtimeData` is built that way already (1Hz Unix seconds).
          const succData = d.map((p, i) => ({
            time: p.ts as UTCTimestamp,
            value: Math.max(0, succValues[i]),
          }));
          const failData = d.map((p, i) => ({
            time: p.ts as UTCTimestamp,
            value: Math.max(0, failValues[i]),
          }));
          succRef.current?.setData(succData);
          failRef.current?.setData(failData);
        }

        const nowSec = Math.floor(Date.now() / 1000) + (Date.now() % 1000) / 1000;
        const fromSec = nowSec - windowRef.current;
        chartRef.current?.timeScale().setVisibleRange({
          from: fromSec as UTCTimestamp,
          to: nowSec as UTCTimestamp,
        });

        // Use the chart's actual plot width (excludes the right price-scale
        // gutter) so vertical lines align with the series, not the panel edge.
        const plotWidth = chartRef.current?.timeScale().width() ?? container.clientWidth;
        renderTimeOverlay(
          overlayRef.current,
          fromSec,
          nowSec,
          tickIntervalRef.current,
          plotWidth,
        );
      }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);

    return () => {
      cancelAnimationFrame(raf);
      ro.disconnect();
      chart.remove();
      chartRef.current = null;
      succRef.current = null;
      failRef.current = null;
    };
  }, []);

  return (
    <div className="relative h-full w-full" style={{ height: H }}>
      <div ref={containerRef} className="absolute inset-x-0 top-0" style={{ height: H - AXIS_H }} />
      <svg
        ref={overlayRef}
        className="pointer-events-none absolute inset-0 h-full w-full"
        aria-hidden="true"
      />
      <div
        ref={tooltipRef}
        className="pointer-events-none absolute z-10 hidden min-w-[120px] rounded-md border border-border bg-panel/95 px-2 py-1.5 shadow-md backdrop-blur"
      />
    </div>
  );
}

/**
 * Draws vertical grid lines + bottom-axis labels at fixed time intervals.
 * Runs every animation frame so the marks slide smoothly with the chart pan.
 */
function renderTimeOverlay(
  svg: SVGSVGElement | null,
  fromSec: number,
  nowSec: number,
  intervalSec: number,
  width: number,
) {
  if (!svg || width <= 0) {
    return;
  }

  // Snap the first tick to a multiple of `intervalSec` >= fromSec so labels
  // sit at clean wall-clock times (e.g. 12:00:05, 12:00:10).
  const firstTick = Math.ceil(fromSec / intervalSec) * intervalSec;
  const span = nowSec - fromSec;

  const css = getComputedStyle(svg);
  const border = css.getPropertyValue('--border').trim() || '#e5e7eb';
  const muted = css.getPropertyValue('--text-mute').trim() || '#71717a';

  const labelY = svg.clientHeight - 4;
  let dom = '';
  for (let t = firstTick; t <= nowSec; t += intervalSec) {
    const x = ((t - fromSec) / span) * width;
    dom += `<line x1="${x.toFixed(1)}" y1="0" x2="${x.toFixed(1)}" y2="${(svg.clientHeight - AXIS_H).toFixed(1)}" stroke="${border}" stroke-dasharray="2 3" stroke-width="1" />`;
    dom += `<text x="${x.toFixed(1)}" y="${labelY}" fill="${muted}" font-size="10" font-family="'Geist Mono Variable', ui-monospace, monospace" text-anchor="middle">${formatTickLabel(t, intervalSec)}</text>`;
  }
  svg.innerHTML = dom;
}

function formatTickLabel(unixSec: number, intervalSec: number): string {
  const d = new Date(unixSec * 1000);
  const hh = String(d.getHours()).padStart(2, '0');
  const mm = String(d.getMinutes()).padStart(2, '0');
  const ss = String(d.getSeconds()).padStart(2, '0');
  // Sub-minute intervals need seconds; minute+ intervals don't.
  if (intervalSec < 60) {
    return `${hh}:${mm}:${ss}`;
  }

  return `${hh}:${mm}`;
}

/** Returns a CSS color with the given alpha. Handles hex / falls back to color-mix. */
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
