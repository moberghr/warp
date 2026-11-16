// Demo fixtures for the Metrics page (custom application metrics). The instruments are the registry rows;
// series are generated on request from a deterministic traffic curve so the chart honours every control the
// real API does — range, application, breakdown tag, filters, 1d/7d overlays and a histogram percentile —
// without a table of canned responses. Same query, same picture: nothing here is random.

import { MetricKind, MetricTier } from '@/types/metrics';
import type { MetricHeadline, MetricInstrument, MetricPoint, MetricSeries, MetricSeriesLine } from '@/types/metrics';
import { matchesPattern } from '@/lib/metricPattern';

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
const FINE_MS = 5 * MINUTE;
/** MetricFlushInterval's default: a bucket is still filling until one interval after it ends. */
const FLUSH_MS = 30_000;

const ago = (ms: number) => new Date(Date.now() - ms).toISOString();

export const demoMetrics: MetricInstrument[] = [
  {
    name: 'db.pool.connections',
    meter: 'Shop.Data',
    kind: MetricKind.Gauge,
    unit: '1',
    description: 'Open database connections per pool.',
    bounds: [],
    tagKeys: ['pool'],
    firstSeenAt: ago(12 * DAY),
    lastSeenAt: ago(8_000),
    collapsedAt: null,
    collapsedTags: [],
  },
  {
    name: 'http.server.request.duration',
    meter: 'Shop.Api',
    kind: MetricKind.Histogram,
    unit: 'ms',
    description: 'Inbound HTTP request duration.',
    bounds: [5, 10, 25, 50, 100, 250, 500, 1000],
    tagKeys: ['http.route', 'http.response.status_code'],
    firstSeenAt: ago(40 * DAY),
    lastSeenAt: ago(5_000),
    collapsedAt: ago(2 * DAY),
    collapsedTags: ['http.route'],
  },
  {
    name: 'payment.results',
    meter: 'Shop.Payments',
    kind: MetricKind.Counter,
    unit: '1',
    description: 'Payment attempts by pipeline step, gateway and outcome.',
    bounds: [],
    tagKeys: ['step', 'gateway', 'result'],
    firstSeenAt: ago(40 * DAY),
    lastSeenAt: ago(20_000),
    collapsedAt: null,
    collapsedTags: [],
  },
];

type Combo = { tags: Record<string, string>; weight: number };

function cross(dimensions: Record<string, [string, number][]>): Combo[] {
  return Object.entries(dimensions).reduce<Combo[]>(
    (acc, [key, values]) => acc.flatMap((c) => values.map(([v, w]) => ({ tags: { ...c.tags, [key]: v }, weight: c.weight * w }))),
    [{ tags: {}, weight: 1 }],
  );
}

const combos: Record<string, Combo[]> = {
  'payment.results': cross({
    step: [['authorize', 10], ['capture', 8], ['refund', 1]],
    gateway: [['adyen', 5], ['checkout', 3], ['nets', 2]],
    result: [['ok', 90], ['declined', 8], ['error', 2]],
  }).map((c) => ({ ...c, weight: c.weight / 100 })),
  'http.server.request.duration': cross({
    'http.route': [['/payments', 10], ['/payments/{id}', 6], ['/refunds', 1], ['/health', 4]],
    'http.response.status_code': [['200', 90], ['201', 6], ['400', 3], ['404', 1], ['500', 0.6], ['503', 0.4]],
  }).map((c) => ({ ...c, weight: c.weight / 100 })),
  'db.pool.connections': [
    { tags: { pool: 'primary' }, weight: 14 },
    { tags: { pool: 'replica' }, weight: 8 },
  ],
};

const BOUNDS = [5, 10, 25, 50, 100, 250, 500, 1000];
const BASE_PERCENTILE_MS: Record<number, number> = { 50: 40, 90: 180, 95: 250, 99: 800 };

/** Deterministic pseudo-noise in [0, 1) from a bucket start and a salt. */
function noise(t: number, salt: string): number {
  let h = 2166136261;
  for (const ch of `${Math.floor(t / FINE_MS)}|${salt}`) {
    h = Math.imul(h ^ ch.charCodeAt(0), 16777619);
  }

  return ((h >>> 0) % 10_000) / 10_000;
}

/** A daily curve with a working-hours hump and a weekend dip. */
function traffic(t: number): number {
  const date = new Date(t);
  const hour = date.getUTCHours() + date.getUTCMinutes() / 60;
  const day = date.getUTCDay();
  const hump = 0.55 + 0.45 * Math.sin(((hour - 6) / 24) * 2 * Math.PI);
  const weekend = day === 0 || day === 6 ? 0.7 : 1;

  return Math.max(0.05, hump * weekend);
}

function tierFor(ageMs: number): { tier: MetricTier; bucketMs: number } {
  if (ageMs <= 6 * HOUR) {
    return { tier: MetricTier.Fine, bucketMs: FINE_MS };
  }

  return ageMs <= 7 * DAY ? { tier: MetricTier.Hourly, bucketMs: HOUR } : { tier: MetricTier.Daily, bucketMs: DAY };
}

// The real API's matcher (absent tag = {none}, bare * = any present value), so the demo's ratio numerator — a
// filtered request on the breakdown tag — agrees with its own breakdown lines.
function matches(combo: Combo, filters: [string, string][]): boolean {
  return filters.every(([key, wanted]) => matchesPattern(combo.tags[key] ?? null, wanted));
}

function bucketPoint(
  kind: MetricKind,
  groupName: string,
  weight: number,
  t: number,
  bucketMs: number,
  percentile: number | null,
): MetricPoint {
  const buckets = bucketMs / FINE_MS;
  const wobble = 0.85 + noise(t, groupName) * 0.3;
  const timestamp = new Date(t).toISOString();

  if (kind === MetricKind.Gauge) {
    const avg = weight * (0.7 + 0.3 * traffic(t)) * wobble;

    return { timestamp, value: avg, count: 12, sum: avg * 12, min: avg * 0.8, max: avg * 1.25, percentile: null, incomplete: false, bucketSeconds: bucketMs / 1000 };
  }

  const count = Math.round(weight * 60 * traffic(t) * wobble * buckets);

  if (kind === MetricKind.Histogram) {
    const avgMs = 55 * (0.8 + noise(t, `${groupName}|avg`) * 0.5);
    const raw = percentile === null ? null : (BASE_PERCENTILE_MS[percentile] ?? 100) * (0.8 + noise(t, `${groupName}|p${percentile}`) * 0.5);
    const bound = raw === null ? null : (BOUNDS.find((b) => b >= raw) ?? BOUNDS[BOUNDS.length - 1]);

    return { timestamp, value: count, count, sum: count * avgMs, min: 3, max: avgMs * 14, percentile: count > 0 ? bound : null, incomplete: false, bucketSeconds: bucketMs / 1000 };
  }

  return { timestamp, value: count, count, sum: count, min: null, max: null, percentile: null, incomplete: false, bucketSeconds: bucketMs / 1000 };
}

function buildLines(
  instrument: MetricInstrument,
  from: number,
  to: number,
  shiftMs: number,
  compare: string | null,
  breakdown: string | null,
  filters: [string, string][],
  keep: string[] | null,
  percentile: number | null,
  now: number,
  cutoff: number,
): MetricSeriesLine[] {
  const selected = (combos[instrument.name] ?? []).filter((c) => matches(c, filters));
  const { tier, bucketMs } = tierFor(now - (from - shiftMs));

  // Group the surviving tag combinations by the breakdown value (or one "total" group).
  const groups = new Map<string, number>();
  for (const c of selected) {
    const key = breakdown === null ? 'total' : (c.tags[breakdown] ?? '{none}');
    groups.set(key, (groups.get(key) ?? 0) + c.weight);
  }

  const ranked = [...groups.entries()].sort((a, b) => b[1] - a[1]);
  const top = keep === null ? ranked.slice(0, 10) : ranked.filter(([name]) => keep.includes(name));
  const restWeight = ranked.filter(([name]) => !top.some(([n]) => n === name)).reduce((acc, [, w]) => acc + w, 0);
  const lines: [string, number][] = restWeight > 0 ? [...top, ['(rest)', restWeight]] : top;

  const start = Math.floor((from - shiftMs) / bucketMs) * bucketMs;

  return lines.map(([name, weight]) => {
    const points: MetricPoint[] = [];
    for (let t = start; t < to - shiftMs; t += bucketMs) {
      const point = bucketPoint(instrument.kind, `${instrument.name}|${name}`, weight, t, bucketMs, percentile);
      // The current range's newest bucket is still filling: flagged, and only the elapsed share of it is in.
      const incomplete = compare === null && t >= cutoff;
      const filled = incomplete && instrument.kind !== MetricKind.Gauge ? Math.min(1, Math.max(0, (now - t) / bucketMs)) : 1;
      // The API shifts an overlay forward so it lines up with the current range.
      points.push({
        ...point,
        value: instrument.kind === MetricKind.Gauge ? point.value : Math.round(point.value * filled),
        count: instrument.kind === MetricKind.Gauge ? point.count : Math.round(point.count * filled),
        sum: point.sum * filled,
        timestamp: new Date(t + shiftMs).toISOString(),
        incomplete,
      });
    }

    return { name, compare, resolution: tier, points };
  });
}

/** The demo answer to GET /api/metrics/{id}/series, or undefined for an unknown instrument. */
export function demoMetricSeries(instrumentName: string, query: string): MetricSeries | undefined {
  const instrument = demoMetrics.find((x) => x.name === instrumentName);
  if (!instrument) {
    return undefined;
  }

  const params = new URLSearchParams(query);
  const now = Date.now();
  const to = Date.parse(params.get('to') ?? '') || now;
  const from = Date.parse(params.get('from') ?? '') || to - DAY;
  const breakdown = params.get('breakdown') || null;
  const filters = params
    .getAll('filter')
    .flatMap((f): [string, string][] => {
      const i = f.indexOf(':');

      return i > 0 ? [[f.slice(0, i), f.slice(i + 1)]] : [];
    });
  const percentile = params.get('percentile') ? Number(params.get('percentile')) : null;
  const compares = (params.get('compare') ?? '').split(',').filter(Boolean);

  // The API's rule: the display bucket holding (now - flush interval) and everything after it are still filling.
  const { bucketMs } = tierFor(now - from);
  const cutoff = Math.floor((now - FLUSH_MS) / bucketMs) * bucketMs;
  const primary = buildLines(instrument, from, to, 0, null, breakdown, filters, null, percentile, now, cutoff);
  const keep = breakdown === null ? null : primary.filter((x) => x.name !== '(rest)').map((x) => x.name);
  const overlays = compares
    .filter((c) => c === '1d' || c === '7d')
    .sort()
    .flatMap((c) => buildLines(instrument, from, to, c === '1d' ? DAY : 7 * DAY, c, breakdown, filters, keep, percentile, now, cutoff));
  const offsets = [null, ...new Set(overlays.map((x) => x.compare))];

  return {
    instrument: instrument.name,
    kind: instrument.kind,
    unit: instrument.unit,
    from: new Date(from).toISOString(),
    to: new Date(to).toISOString(),
    resolution: tierFor(now - from).tier,
    completeUntil: new Date(Math.min(Math.max(cutoff, from), to)).toISOString(),
    series: [...primary, ...overlays],
    headlines: offsets.map((compare) => demoHeadline(instrument.kind, [...primary, ...overlays].filter((x) => x.compare === compare), compare, cutoff)),
  };
}

// The API's headline: every line of the range, complete buckets only (an overlay's timestamps are already shifted).
function demoHeadline(kind: MetricKind, lines: MetricSeriesLine[], compare: string | null, cutoff: number): MetricHeadline {
  const points = lines.flatMap((x) => x.points).filter((x) => Date.parse(x.timestamp) < cutoff);
  const count = points.reduce((acc, x) => acc + x.count, 0);
  const sum = points.reduce((acc, x) => acc + x.sum, 0);
  if (count === 0) {
    return { compare, value: null, count };
  }

  if (kind === MetricKind.Gauge) {
    return { compare, value: sum / count, count };
  }

  return { compare, value: kind === MetricKind.Histogram ? count : sum, count };
}
