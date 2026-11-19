// Demo fixtures for the metrics health board (#326). Tiles point at the demo instruments (data/metrics.ts); each tile's
// numbers and sparkline are generated for whatever window is asked for from a deterministic curve, so the board honours
// the range selector and "since last deploy" the way the real API does. Same window, same picture: nothing is random.
// Two tiles are scoped to billing-service, so the post-deploy verdicts of the demo applications differ (applications.ts).
// Three named boards (§8.34): Default (the tiles with no board), Checkout and Payments.

import { MetricKind, MetricTier } from '@/types/metrics';
import { MetricDeviationDirection, MetricTileKind, MetricTileStatusRule } from '@/types/metricBoard';
import type { MetricBoard, MetricBoardSparkPoint, MetricBoardSummary, MetricBoardTile, MetricBoardTileResult } from '@/types/metricBoard';
import { SloComparison, SloGaugeAggregate, SloState } from '@/types/slo';
import { demoNoteCoversTile, demoTileAnnotations } from './annotations';
import { demoMetrics } from './metrics';
import { demoSloDetail } from './slo';

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
const FINE_MS = 5 * MINUTE;
/** MetricFlushInterval's default: data newer than this is still being written. */
const FLUSH_MS = 30_000;

/** The server's CompleteUntil: the start of the fine bucket holding `now − flush`, clamped to the range. */
export function demoCompleteUntil(from: number, to: number, now: number): number {
  const cutoff = Math.floor((now - FLUSH_MS) / FINE_MS) * FINE_MS;

  return Math.min(to, Math.max(from, cutoff));
}

/** The demo boards' ids; the Default board owns the tiles with no board. */
export const DEMO_DEFAULT_BOARD = 1;
export const DEMO_CHECKOUT_BOARD = 2;
export const DEMO_PAYMENTS_BOARD = 3;

function tile(partial: Partial<MetricBoardTile> & Pick<MetricBoardTile, 'id' | 'name' | 'instrument'>): MetricBoardTile {
  return {
    boardId: null,
    position: partial.id,
    application: null,
    tagFilter: null,
    kind: MetricTileKind.Value,
    chartQuery: null,
    sloDefinitionId: null,
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
  tile({
    id: 7,
    boardId: DEMO_CHECKOUT_BOARD,
    position: 1,
    name: 'Checkout requests',
    instrument: 'http.server.request.duration',
    tagFilter: 'http.route=api/checkout*',
  }),
  tile({
    id: 8,
    boardId: DEMO_CHECKOUT_BOARD,
    position: 2,
    name: 'Checkout 5xx',
    instrument: 'http.server.request.duration',
    application: 'orders-api',
    tagFilter: 'http.route=api/checkout*,http.response.status_code=5*',
    statusRule: MetricTileStatusRule.Threshold,
    comparison: SloComparison.AtMost,
    threshold: 200,
  }),
  tile({
    id: 9,
    boardId: DEMO_PAYMENTS_BOARD,
    position: 1,
    name: 'Authorisation rate',
    instrument: 'payment.results',
    application: 'billing-service',
    kind: MetricTileKind.Ratio,
    ratioTag: 'result',
    goodValues: 'ok',
    badValues: 'declined,error',
    statusRule: MetricTileStatusRule.Threshold,
    comparison: SloComparison.AtLeast,
    threshold: 0.9,
  }),
  tile({ id: 10, boardId: DEMO_PAYMENTS_BOARD, position: 2, name: 'Declines', instrument: 'payment.results', tagFilter: 'result=declined' }),
  // Chart tiles (spec 2026-11-19 batch 1): the metric page's chart with stored controls, over the board's range. The
  // series come from the demo series API (data/metrics.ts), like the metric page's.
  tile({
    id: 11,
    boardId: DEMO_CHECKOUT_BOARD,
    position: 3,
    name: 'Payment errors by gateway',
    instrument: 'payment.results',
    kind: MetricTileKind.Chart,
    chartQuery: 'breakdown=gateway&filter=result%3Aerror&compare=7d',
  }),
  tile({
    id: 12,
    boardId: DEMO_CHECKOUT_BOARD,
    position: 4,
    name: 'Request latency p95',
    instrument: 'http.server.request.duration',
    kind: MetricTileKind.Chart,
    chartQuery: 'percentile=95',
  }),
  // SLO tiles (spec 2026-11-19 batch 2): an objective's current evaluation from the demo SLOs (data/slo.ts). With a
  // board, the verdict counts only these objectives (resolved question 1).
  tile({ id: 13, boardId: DEMO_CHECKOUT_BOARD, position: 5, name: 'Checkout p95 ≤ 0.5 s', instrument: '', kind: MetricTileKind.Slo, sloDefinitionId: 8 }),
  tile({ id: 14, boardId: DEMO_PAYMENTS_BOARD, position: 3, name: 'Payment success ratio', instrument: '', kind: MetricTileKind.Slo, sloDefinitionId: 4 }),
];

const boardOf = (t: MetricBoardTile) => t.boardId ?? DEMO_DEFAULT_BOARD;

/** GET /metrics/boards — the three demo boards in picker order, with their tile counts. */
export const demoBoards: MetricBoardSummary[] = [
  { id: DEMO_DEFAULT_BOARD, name: 'Default', description: null, application: null, isDefault: true },
  { id: DEMO_CHECKOUT_BOARD, name: 'Checkout', description: 'The checkout funnel of the shop.', application: 'orders-api', isDefault: false },
  { id: DEMO_PAYMENTS_BOARD, name: 'Payments', description: 'Card authorisations and declines.', application: 'billing-service', isDefault: false },
].map((x, i) => ({
  ...x,
  position: i,
  createdBy: x.isDefault ? null : 'demo',
  tileCount: demoBoardTiles.filter((t) => boardOf(t) === x.id).length,
}));

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
  7: { kind: MetricKind.Histogram, unit: 'ms', base: 640, swing: 0.4, drift: 0.06 },
  8: { kind: MetricKind.Histogram, unit: 'ms', base: 3, swing: 0.5, drift: 0.3 },
  9: { kind: MetricKind.Counter, unit: '1', base: 0.94, swing: 0.01, drift: -0.005 },
  10: { kind: MetricKind.Counter, unit: '1', base: 42, swing: 0.3, drift: 0.1 },
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

/** The share of the threshold budget left (SloMath.EvaluateBound, simplified for the demo). */
function thresholdBudget(t: MetricBoardTile, threshold: number, current: number): number {
  const atLeast = t.comparison === SloComparison.AtLeast;
  if (atLeast && t.kind === MetricTileKind.Ratio) {
    // On a 0–1 ratio the headroom is measured against the allowed bad share, the rate budget (obs − t)/(1 − t), so
    // "success ≥ 98%" at 99.5% is Healthy. A target of 100% leaves no bad share: met or not, all or nothing.
    return threshold >= 1 ? (current >= threshold ? 1 : current - threshold) : (current - threshold) / (1 - threshold);
  }

  const headroom = atLeast ? current - threshold : threshold - current;

  return threshold === 0 ? (headroom >= 0 ? 1 : -1) : headroom / Math.abs(threshold);
}

/** A demo tile's rule verdict from its current value and baselines; null without a rule. */
export function demoTileStatus(t: MetricBoardTile, current: number, dayAgo: number, weekAgo: number): SloState | null {
  if (t.statusRule === MetricTileStatusRule.Threshold && t.threshold !== null) {
    const budget = thresholdBudget(t, t.threshold, current);

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

/**
 * GET /metrics/board?from=&to=&board= — the tiles of one demo board (the Default board when none is given), or of every
 * board for 'all' (what the server's verdict reads without a board), evaluated over the window. As on the server, the
 * values and their baselines stop at `completeUntil` (the newest bucket is still filling) while the sparklines run to
 * `to`. Null for a board id that does not exist.
 */
export function demoMetricBoard(fromIso: string | undefined, toIso: string | undefined, now?: number): MetricBoard;
export function demoMetricBoard(fromIso: string | undefined, toIso: string | undefined, now: number | undefined, board: number | 'all'): MetricBoard | null;
export function demoMetricBoard(fromIso: string | undefined, toIso: string | undefined, now = Date.now(), board: number | 'all' = DEMO_DEFAULT_BOARD): MetricBoard | null {
  const summary = board === 'all' ? null : demoBoards.find((x) => x.id === board);
  if (summary === undefined) {
    return null;
  }
  const to = toIso ? Date.parse(toIso) : now;
  const from = fromIso ? Date.parse(fromIso) : to - DAY;
  const span = to - from;
  const complete = demoCompleteUntil(from, to, now);
  const tiles: MetricBoardTileResult[] = demoBoardTiles.filter((t) => summary === null || boardOf(t) === summary.id).map((t) => {
    if (t.kind === MetricTileKind.Slo) {
      // As on the server: the objective with its current evaluation, or missing when it no longer exists.
      const objective = demoSloDetail(t.sloDefinitionId ?? 0) ?? null;

      return {
        tile: t,
        instrumentKind: null,
        unit: null,
        current: null,
        dayAgo: null,
        weekAgo: null,
        status: null,
        unacknowledgedStatus: null,
        annotations: [],
        sparkline: [],
        slo: objective,
        missing: objective === null,
      };
    }

    if (t.kind === MetricTileKind.Chart) {
      // As on the server: a chart tile carries its instrument's kind and unit and nothing computed (the page reads the series).
      const instrument = demoMetrics.find((x) => x.name === t.instrument);

      return {
        tile: t,
        instrumentKind: instrument?.kind ?? null,
        unit: instrument?.unit ?? null,
        current: null,
        dayAgo: null,
        weekAgo: null,
        status: null,
        unacknowledgedStatus: null,
        annotations: [],
        sparkline: [],
      };
    }

    const shape = shapes[t.id] ?? shapes[6];
    const empty = complete <= from;
    const current = empty ? null : windowValue(shape, t, from, complete, true);
    const dayAgo = empty ? null : windowValue(shape, t, from - DAY, complete - DAY, false);
    const weekAgo = empty ? null : windowValue(shape, t, from - 7 * DAY, complete - 7 * DAY, false);
    const ruled = current === null || dayAgo === null || weekAgo === null
      ? (t.statusRule === MetricTileStatusRule.None ? null : SloState.NoData)
      : demoTileStatus(t, current, dayAgo, weekAgo);
    // Notes (#333) attached as the server does: every overlapping note is shown, but only an active acknowledged-noise
    // note that COVERS the tile (about everything it reads) turns a problem into Acknowledged.
    const annotations = demoTileAnnotations(t, from, to);
    const acknowledged = (ruled === SloState.Warning || ruled === SloState.Breaching)
      && annotations.some((a) => a.acknowledgedNoise && demoNoteCoversTile(a, t) && Date.parse(a.startsAt) < to && (a.endsAt === null || Date.parse(a.endsAt) >= to));

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
    board: summary,
    from: new Date(from).toISOString(),
    to: new Date(to).toISOString(),
    resolution: span <= 6 * HOUR ? MetricTier.Fine : span <= 7 * DAY ? MetricTier.Hourly : MetricTier.Daily,
    tiles,
    completeUntil: new Date(complete).toISOString(),
    annotationsTruncated: false,
  };
}
