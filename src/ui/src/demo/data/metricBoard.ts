// Demo fixtures for the metrics health board (#326). Tiles point at the demo instruments (data/metrics.ts); each tile's
// numbers and sparkline are generated for whatever window is asked for from a deterministic curve, so the board honours
// the range selector and "since last deploy" the way the real API does. Same window, same picture: nothing is random.
// Two tiles are scoped to billing-service, so the post-deploy verdicts of the demo applications differ (applications.ts).

import { MetricKind, MetricTier } from '@/types/metrics';
import { MetricDeviationDirection, MetricTileKind, MetricTileStatusRule } from '@/types/metricBoard';
import type { MetricBoard, MetricBoardSparkPoint, MetricBoardTile, MetricBoardTileResult } from '@/types/metricBoard';
import { SloComparison, SloGaugeAggregate, SloState } from '@/types/slo';
import { demoTileAnnotations } from './annotations';

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

function tile(partial: Partial<MetricBoardTile> & Pick<MetricBoardTile, 'id' | 'name' | 'instrument'>): MetricBoardTile {
  return {
    position: partial.id,
    application: null,
    tagFilter: null,
    kind: MetricTileKind.Value,
    ratioTag: null,
    goodValues: null,
    badValues: null,
    gaugeAggregate: null,
    statusRule: MetricTileStatusRule.None,
    comparison: null,
    threshold: null,
    baselineDays: null,
    maxDeviation: null,
    deviationDirection: null,
    ...partial,
  };
}

export const demoBoardTiles: MetricBoardTile[] = [
  tile({
    id: 1,
    name: 'Payment success',
    instrument: 'payment.results',
    application: 'billing-service',
    kind: MetricTileKind.Ratio,
    ratioTag: 'result',
    goodValues: 'ok',
    badValues: 'declined,error',
    statusRule: MetricTileStatusRule.Deviation,
    baselineDays: 7,
    maxDeviation: 0.02,
    deviationDirection: MetricDeviationDirection.Drop,
  }),
  tile({
    id: 2,
    name: 'Inbound requests',
    instrument: 'http.server.request.duration',
    statusRule: MetricTileStatusRule.Deviation,
    baselineDays: 1,
    maxDeviation: 0.3,
    deviationDirection: MetricDeviationDirection.Drop,
  }),
  tile({
    id: 3,
    name: 'Server errors (5xx)',
    instrument: 'http.server.request.duration',
    application: 'billing-service',
    tagFilter: 'http.response.status_code=5*',
    statusRule: MetricTileStatusRule.Threshold,
    comparison: SloComparison.AtMost,
    threshold: 25,
  }),
  tile({
    id: 4,
    name: 'Gateway errors',
    instrument: 'payment.results',
    tagFilter: 'result=error',
    statusRule: MetricTileStatusRule.Deviation,
    baselineDays: 7,
    maxDeviation: 0.5,
    deviationDirection: MetricDeviationDirection.Rise,
  }),
  tile({
    id: 5,
    name: 'DB connections (max)',
    instrument: 'db.pool.connections',
    gaugeAggregate: SloGaugeAggregate.Max,
    statusRule: MetricTileStatusRule.Threshold,
    comparison: SloComparison.AtMost,
    threshold: 80,
  }),
  tile({ id: 6, name: 'Refunds', instrument: 'payment.results', tagFilter: 'step=refund' }),
];

interface Shape {
  kind: MetricKind;
  unit: string | null;
  /** Per-hour value at the curve's mean (events), or the level (gauge / ratio). */
  base: number;
  /** Relative swing of the daily curve. */
  swing: number;
  /** The current window drifts this much from the baselines (the board then has something to colour). */
  drift: number;
}

const shapes: Record<number, Shape> = {
  1: { kind: MetricKind.Counter, unit: '1', base: 0.9, swing: 0.01, drift: -0.025 },
  2: { kind: MetricKind.Histogram, unit: 'ms', base: 2400, swing: 0.35, drift: 0.04 },
  3: { kind: MetricKind.Histogram, unit: 'ms', base: 18, swing: 0.4, drift: 0.1 },
  4: { kind: MetricKind.Counter, unit: '1', base: 30, swing: 0.3, drift: 0.2 },
  5: { kind: MetricKind.Gauge, unit: '1', base: 46, swing: 0.25, drift: 0.05 },
  6: { kind: MetricKind.Counter, unit: '1', base: 20, swing: 0.3, drift: 0 },
};

function level(shape: Shape, ms: number, current: boolean): number {
  const hourOfDay = (ms % DAY) / HOUR;
  const curve = 1 + shape.swing * Math.sin(((hourOfDay - 9) / 24) * 2 * Math.PI);

  return shape.base * curve * (current ? 1 + shape.drift : 1);
}

function isEvents(shape: Shape, t: MetricBoardTile): boolean {
  return t.kind === MetricTileKind.Value && shape.kind !== MetricKind.Gauge;
}

function windowValue(shape: Shape, t: MetricBoardTile, from: number, to: number, current: boolean): number {
  const steps = 24;
  const step = (to - from) / steps;
  let total = 0;
  for (let i = 0; i < steps; i++) {
    total += level(shape, from + step * (i + 0.5), current);
  }

  const mean = total / steps;
  if (isEvents(shape, t)) {
    return Math.round((mean * (to - from)) / HOUR);
  }

  return t.kind === MetricTileKind.Ratio ? Math.min(0.999, mean) : Math.round(mean);
}

function bucketMs(span: number): number {
  return span <= 6 * HOUR ? 5 * MINUTE : span <= 7 * DAY ? HOUR : DAY;
}

function sparkline(shape: Shape, t: MetricBoardTile, from: number, to: number): MetricBoardSparkPoint[] {
  const width = bucketMs(to - from);
  const points: MetricBoardSparkPoint[] = [];
  for (let start = Math.floor(from / width) * width; start < to; start += width) {
    const value = windowValue(shape, t, start, start + width, true);
    points.push({ timestamp: new Date(start).toISOString(), value });
  }

  return points;
}

function status(t: MetricBoardTile, current: number, dayAgo: number, weekAgo: number): SloState | null {
  if (t.statusRule === MetricTileStatusRule.Threshold && t.threshold !== null) {
    const headroom = t.comparison === SloComparison.AtLeast ? current - t.threshold : t.threshold - current;
    const budget = t.threshold === 0 ? (headroom >= 0 ? 1 : -1) : headroom / Math.abs(t.threshold);

    return budget < 0 ? SloState.Breaching : budget < 0.25 ? SloState.Warning : SloState.Healthy;
  }

  if (t.statusRule === MetricTileStatusRule.Deviation && t.maxDeviation !== null) {
    const baseline = t.baselineDays === 1 ? dayAgo : weekAgo;
    const ratio = t.kind === MetricTileKind.Ratio;
    const move = ratio ? current - baseline : (current - baseline) / baseline;
    const bad = t.deviationDirection === MetricDeviationDirection.Drop ? -move : t.deviationDirection === MetricDeviationDirection.Rise ? move : Math.abs(move);
    const budget = (t.maxDeviation - bad) / t.maxDeviation;

    return budget < 0 ? SloState.Breaching : budget < 0.25 ? SloState.Warning : SloState.Healthy;
  }

  return null;
}

/** GET /metrics/board?from=&to= — every demo tile evaluated over the window. */
export function demoMetricBoard(fromIso: string | undefined, toIso: string | undefined): MetricBoard {
  const to = toIso ? Date.parse(toIso) : Date.now();
  const from = fromIso ? Date.parse(fromIso) : to - DAY;
  const span = to - from;
  const tiles: MetricBoardTileResult[] = demoBoardTiles.map((t) => {
    const shape = shapes[t.id] ?? shapes[6];
    const current = windowValue(shape, t, from, to, true);
    const dayAgo = windowValue(shape, t, from - DAY, to - DAY, false);
    const weekAgo = windowValue(shape, t, from - 7 * DAY, to - 7 * DAY, false);
    const ruled = status(t, current, dayAgo, weekAgo);
    // Notes (#333) attached as the server does; an active acknowledged-noise note turns a problem into Acknowledged.
    const annotations = demoTileAnnotations(t, from, to);
    const acknowledged = (ruled === SloState.Warning || ruled === SloState.Breaching)
      && annotations.some((a) => a.acknowledgedNoise && Date.parse(a.startsAt) < to && (a.endsAt === null || Date.parse(a.endsAt) >= to));

    return {
      tile: t,
      instrumentKind: shape.kind,
      unit: shape.unit,
      current,
      dayAgo,
      weekAgo,
      status: acknowledged ? SloState.Acknowledged : ruled,
      unacknowledgedStatus: ruled,
      annotations,
      sparkline: sparkline(shape, t, from, to),
    };
  });

  return {
    from: new Date(from).toISOString(),
    to: new Date(to).toISOString(),
    resolution: span <= 6 * HOUR ? MetricTier.Fine : span <= 7 * DAY ? MetricTier.Hourly : MetricTier.Daily,
    tiles,
  };
}
