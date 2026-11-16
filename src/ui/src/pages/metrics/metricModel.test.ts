import { describe, expect, it } from 'vitest';
import { MetricKind, MetricTier } from '@/types/metrics';
import type { MetricPoint, MetricSeries, MetricSeriesLine } from '@/types/metrics';
import {
  DEFAULT_CONTROLS,
  MAX_RANGE_MS,
  deployMarkers,
  fromLocalInputValue,
  latestDeploy,
  parseRangeParams,
  resolveWindow,
  sinceDeployRange,
  toLocalInputValue,
  validateCustomRange,
  writeRangeParams,
  type RangeId,
  type RangeSelection,
  REST_NAME,
  alignOverlay,
  axisTitles,
  breakdownValues,
  buildDatasets,
  buildNumeratorQuery,
  buildRatioDatasets,
  buildSeriesQuery,
  effectiveRate,
  chipFromQuery,
  chipToQuery,
  changePercent,
  chipToText,
  collapseTooltip,
  formatChange,
  formatPercentileValue,
  headlineLabel,
  headlineSpanSeconds,
  headlineSummary,
  incompleteTimes,
  isRatioActive,
  kindChartNote,
  kindLabel,
  orderedLineNames,
  parseRate,
  pointValue,
  rateParam,
  rateValue,
  ratioPercent,
  removeChip,
  resolutionLabel,
  resolutionSummary,
  seriesRefetchMs,
  supportsRate,
  toggleNumerator,
  unitLabel,
  upsertChip,
} from './metricModel';

const T0 = Date.UTC(2026, 10, 13, 10, 0, 0);
const MIN5 = 5 * 60_000;
const iso = (offset: number) => new Date(T0 + offset * MIN5).toISOString();

function point(offset: number, overrides: Partial<MetricPoint> = {}): MetricPoint {
  return {
    timestamp: iso(offset),
    value: 0,
    count: 0,
    sum: 0,
    min: null,
    max: null,
    percentile: null,
    incomplete: false,
    bucketSeconds: 300,
    ...overrides,
  };
}

function line(name: string, points: MetricPoint[], compare: string | null = null, resolution: MetricTier = MetricTier.Fine): MetricSeriesLine {
  return { name, compare, resolution, points };
}

function series(kind: MetricKind, lines: MetricSeriesLine[]): MetricSeries {
  return {
    instrument: 'payment.results',
    kind,
    unit: null,
    from: iso(0),
    to: iso(10),
    resolution: MetricTier.Fine,
    completeUntil: iso(10),
    series: lines,
    headlines: [],
  };
}

describe('labels', () => {
  it('names every kind and resolution', () => {
    expect(kindLabel(MetricKind.Counter)).toBe('Counter');
    expect(kindLabel(MetricKind.UpDownCounter)).toBe('Up-down counter');
    expect(kindLabel(MetricKind.Histogram)).toBe('Histogram');
    expect(kindLabel(MetricKind.Gauge)).toBe('Gauge');
    expect(resolutionLabel(MetricTier.Fine)).toBe('Fine');
    expect(resolutionLabel(MetricTier.Hourly)).toBe('Hourly');
    expect(resolutionLabel(MetricTier.Daily)).toBe('Daily');
  });

  it('hides the dimensionless unit and spells out bytes', () => {
    expect(unitLabel('1')).toBe('');
    expect(unitLabel(null)).toBe('');
    expect(unitLabel('By')).toBe('bytes');
    expect(unitLabel('ms')).toBe('ms');
  });

  it('words the collapse tooltip', () => {
    expect(collapseTooltip({ collapsedTags: [] })).toBe('Some tag values were collapsed into {other}');
    expect(collapseTooltip({ collapsedTags: ['gateway'] })).toContain('{other} (gateway)');
  });

  it('shows a histogram percentile as an upper bound, and a missing one as a dash', () => {
    expect(formatPercentileValue(250, 'ms')).toBe('≤ 250 ms');
    expect(formatPercentileValue(null, 'ms')).toBe('—');
  });

  it('summarises the resolution of the range and of each overlay', () => {
    const s = series(MetricKind.Counter, [
      line('total', [point(0)]),
      line('total', [point(0)], '1d', MetricTier.Hourly),
    ]);

    expect(resolutionSummary(s)).toBe('Current: Fine · 1d: Hourly');
  });
});

describe('filter chips', () => {
  // parseFilterChip's table lives in lib/metricPattern.test.ts with the rest of the shared grammar.
  it('keeps a value containing : intact', () => {
    expect(chipFromQuery('route:/a:b')).toEqual({ key: 'route', value: '/a:b' });
  });

  it('round-trips between the UI form and the API form', () => {
    const chip = { key: 'http.response.status_code', value: '5*' };

    expect(chipToText(chip)).toBe('http.response.status_code=5*');
    expect(chipToQuery(chip)).toBe('http.response.status_code:5*');
    expect(chipFromQuery(chipToQuery(chip))).toEqual(chip);
    expect(chipFromQuery('nocolon')).toBeNull();
    expect(chipFromQuery('k:')).toBeNull();
  });

  it('holds one chip per key', () => {
    const chips = upsertChip(upsertChip([], { key: 'a', value: '1' }), { key: 'b', value: '2' });

    expect(upsertChip(chips, { key: 'a', value: '9' })).toEqual([
      { key: 'b', value: '2' },
      { key: 'a', value: '9' },
    ]);
    expect(removeChip(chips, 'a')).toEqual([{ key: 'b', value: '2' }]);
  });
});

describe('buildSeriesQuery', () => {
  const now = Date.UTC(2026, 10, 13, 12, 0, 0);

  it('sends the range and nothing else by default', () => {
    expect(buildSeriesQuery(DEFAULT_CONTROLS, MetricKind.Counter, now)).toBe(
      `from=${encodeURIComponent('2026-11-12T12:00:00.000Z')}&to=${encodeURIComponent('2026-11-13T12:00:00.000Z')}`
    );
  });

  it('repeats filter, joins compare and adds application / breakdown', () => {
    const query = buildSeriesQuery(
      {
        ...DEFAULT_CONTROLS,
        range: '1h',
        application: 'checkout-api',
        breakdown: 'result',
        filters: [{ key: 'step', value: 'auth' }, { key: 'gateway', value: 'a*' }],
        compareDay: true,
        compareWeek: true,
      },
      MetricKind.Counter,
      now
    );
    const params = new URLSearchParams(query);

    expect(params.get('application')).toBe('checkout-api');
    expect(params.get('breakdown')).toBe('result');
    expect(params.getAll('filter')).toEqual(['step:auth', 'gateway:a*']);
    expect(params.get('compare')).toBe('1d,7d');
    expect(params.get('percentile')).toBeNull();
    expect(new Date(params.get('to')!).getTime() - new Date(params.get('from')!).getTime()).toBe(3_600_000);
  });

  it('asks for a percentile only for a histogram in the values view', () => {
    const controls = { ...DEFAULT_CONTROLS, percentile: 95 };

    expect(new URLSearchParams(buildSeriesQuery(controls, MetricKind.Histogram, now)).get('percentile')).toBe('95');
    expect(new URLSearchParams(buildSeriesQuery(controls, MetricKind.Counter, now)).get('percentile')).toBeNull();
    expect(new URLSearchParams(buildSeriesQuery({ ...controls, view: 'ratio' }, MetricKind.Histogram, now)).get('percentile')).toBeNull();
  });
});

describe('pointValue', () => {
  const p = point(0, { value: 7, count: 3, sum: 21 });

  it('uses sum for counters, count for histograms and the average for gauges', () => {
    expect(pointValue(MetricKind.Counter, p)).toBe(21);
    expect(pointValue(MetricKind.UpDownCounter, p)).toBe(21);
    expect(pointValue(MetricKind.Histogram, p)).toBe(3);
    expect(pointValue(MetricKind.Gauge, p)).toBe(7);
  });
});

describe('toggleNumerator', () => {
  // matchesPattern's table lives in lib/metricPattern.test.ts with the rest of the shared grammar.
  it('picks one numerator; picking it again clears it, picking another replaces it', () => {
    expect(toggleNumerator(null, '5*')).toBe('5*');
    expect(toggleNumerator('5*', '5*')).toBeNull();
    expect(toggleNumerator('5*', '503')).toBe('503');
  });
});

describe('seriesRefetchMs', () => {
  const now = Date.UTC(2026, 10, 13, 12, 0, 0);
  const preset = (range: RangeId) => seriesRefetchMs(resolveWindow({ range, custom: null }, now));

  it('follows the display resolution the range reads at: fine 1 min, hourly 5 min, daily 30 min', () => {
    expect(preset('1h')).toBe(60_000);
    expect(preset('6h')).toBe(60_000);
    expect(preset('24h')).toBe(5 * 60_000);
    expect(preset('7d')).toBe(5 * 60_000);
    expect(preset('30d')).toBe(30 * 60_000);
  });

  it('applies the same rules to a custom window, and never refetches one that ended in the past', () => {
    expect(seriesRefetchMs(resolveWindow({ range: '24h', custom: { from: now - 3 * 3_600_000, to: null } }, now))).toBe(60_000);
    expect(seriesRefetchMs(resolveWindow({ range: '24h', custom: { from: now - 20 * 86_400_000, to: null } }, now))).toBe(30 * 60_000);
    expect(seriesRefetchMs(resolveWindow({ range: '24h', custom: { from: now - 7_200_000, to: now - 3_600_000 } }, now))).toBe(false);
  });
});

describe('resolveWindow', () => {
  const now = Date.UTC(2026, 10, 13, 12, 0, 0);

  it('ends a preset now', () => {
    expect(resolveWindow({ range: '6h', custom: null }, now)).toEqual({ from: now - 6 * 3_600_000, to: now, live: true, clamped: false });
  });

  it('uses a closed custom range as given', () => {
    const custom = { from: now - 5 * 86_400_000, to: now - 86_400_000 };

    expect(resolveWindow({ range: '24h', custom }, now)).toEqual({ ...custom, live: false, clamped: false });
  });

  it('runs an open custom range until now', () => {
    expect(resolveWindow({ range: '24h', custom: { from: now - 3_600_000, to: null } }, now)).toEqual({
      from: now - 3_600_000,
      to: now,
      live: true,
      clamped: false,
    });
  });

  it('cuts a start older than 90 days to the API limit and says so', () => {
    const span = resolveWindow({ range: '24h', custom: { from: now - 120 * 86_400_000, to: null } }, now);

    expect(span.to - span.from).toBe(MAX_RANGE_MS);
    expect(span.clamped).toBe(true);
  });

  it('keeps a start at or past the end a minute before it', () => {
    const span = resolveWindow({ range: '24h', custom: { from: now + 5_000, to: null } }, now);

    expect(span.to - span.from).toBe(60_000);
  });
});

describe('validateCustomRange', () => {
  const now = Date.UTC(2026, 10, 13, 12, 0, 0);

  it('accepts a range up to 90 days, open-ended or closed', () => {
    expect(validateCustomRange(now - MAX_RANGE_MS, null, now)).toBeNull();
    expect(validateCustomRange(now - 2 * 86_400_000, now - 86_400_000, now)).toBeNull();
  });

  it('rejects a missing start, an unparseable end, a reversed range and one longer than 90 days', () => {
    expect(validateCustomRange(Number.NaN, null, now)).toBe('Pick a start time.');
    expect(validateCustomRange(now - 1, Number.NaN, now)).toBe('Pick an end time, or leave it empty for now.');
    expect(validateCustomRange(now, now - 1, now)).toBe('The start must be before the end.');
    expect(validateCustomRange(now + 1, null, now)).toBe('The start must be in the past.');
    expect(validateCustomRange(now - MAX_RANGE_MS - 1, null, now)).toBe('A range can span at most 90 days.');
  });
});

describe('range URL parameters', () => {
  const roundTrip = (selection: RangeSelection) => parseRangeParams(writeRangeParams(new URLSearchParams('rate=minute'), selection));

  it('round-trips a preset, the default preset, a closed custom range and an open one', () => {
    const from = Date.UTC(2026, 10, 12, 10, 42, 0);
    const to = Date.UTC(2026, 10, 13, 9, 0, 0);

    expect(roundTrip({ range: '7d', custom: null })).toEqual({ range: '7d', custom: null });
    expect(roundTrip({ range: '24h', custom: null })).toEqual({ range: '24h', custom: null });
    expect(roundTrip({ range: '24h', custom: { from, to } })).toEqual({ range: '24h', custom: { from, to } });
    expect(roundTrip({ range: '24h', custom: { from, to: null } })).toEqual({ range: '24h', custom: { from, to: null } });
  });

  it('keeps the default preset out of the URL and other parameters in it', () => {
    const params = writeRangeParams(new URLSearchParams('rate=minute&range=7d&from=x'), { range: '24h', custom: null });

    expect(params.toString()).toBe('rate=minute');
  });

  it('falls back to the preset for an unparseable start and to "until now" for an unparseable end', () => {
    expect(parseRangeParams(new URLSearchParams('range=6h&from=nonsense'))).toEqual({ range: '6h', custom: null });
    expect(parseRangeParams(new URLSearchParams('from=2026-11-12T10:42:00.000Z&to=bad')).custom).toEqual({
      from: Date.UTC(2026, 10, 12, 10, 42, 0),
      to: null,
    });
    expect(parseRangeParams(new URLSearchParams('range=2y')).range).toBe('24h');
  });

  it('converts to and from a datetime-local input value to the minute', () => {
    const ms = new Date(2026, 10, 12, 10, 42, 0).getTime();

    expect(toLocalInputValue(ms)).toBe('2026-11-12T10:42');
    expect(fromLocalInputValue(toLocalInputValue(ms))).toBe(ms);
    expect(fromLocalInputValue('')).toBeNaN();
  });
});

describe('custom range in the series query', () => {
  const now = Date.UTC(2026, 10, 13, 12, 0, 0);

  it('sends the custom window, and the overlays follow it because compare offsets the same from/to', () => {
    const custom = { from: Date.UTC(2026, 10, 12, 10, 42, 0), to: null };
    const params = new URLSearchParams(buildSeriesQuery({ ...DEFAULT_CONTROLS, custom, compareDay: true }, MetricKind.Counter, now));

    expect(params.get('from')).toBe('2026-11-12T10:42:00.000Z');
    expect(params.get('to')).toBe('2026-11-13T12:00:00.000Z');
    expect(params.get('compare')).toBe('1d');
  });
});

describe('deploy markers', () => {
  const deploy = (application: string, version: string, firstSeenAt: string, environment: string | null = 'prod') => ({
    id: `${application}-${version}-${environment}`,
    application,
    version,
    environment,
    firstSeenAt,
    instanceId: 'i',
    instanceName: null,
  });
  const span = { from: Date.UTC(2026, 10, 12, 0, 0, 0), to: Date.UTC(2026, 10, 13, 0, 0, 0) };

  it('keeps the deploys inside the window, oldest first, labelled by version for one application', () => {
    const markers = deployMarkers(
      [
        deploy('api', '1.2.0', '2026-11-12T18:00:00Z'),
        deploy('api', '1.1.0', '2026-11-12T06:00:00Z'),
        deploy('api', '1.0.0', '2026-11-11T06:00:00Z'),
        deploy('worker', '9.0.0', '2026-11-12T07:00:00Z'),
      ],
      span,
      'api',
    );

    expect(markers).toEqual([
      { time: Date.UTC(2026, 10, 12, 6, 0, 0), label: '1.1.0' },
      { time: Date.UTC(2026, 10, 12, 18, 0, 0), label: '1.2.0' },
    ]);
  });

  it('names the application across applications and the environment only when several are in view', () => {
    expect(deployMarkers([deploy('api', '1.1.0', '2026-11-12T06:00:00Z'), deploy('worker', '9.0.0', '2026-11-12T07:00:00Z')], span, null)
      .map((x) => x.label)).toEqual(['api 1.1.0', 'worker 9.0.0']);
    expect(deployMarkers([deploy('api', '1.1.0', '2026-11-12T06:00:00Z'), deploy('api', '1.1.0', '2026-11-12T08:00:00Z', 'staging')], span, 'api')
      .map((x) => x.label)).toEqual(['1.1.0 (prod)', '1.1.0 (staging)']);
  });

  it('picks the newest deploy for "since last deploy" and opens the range from it', () => {
    const newest = deploy('api', '1.2.0', '2026-11-12T18:00:00Z');
    const latest = latestDeploy([deploy('api', '1.1.0', '2026-11-12T06:00:00Z'), newest]);

    expect(latest).toBe(newest);
    expect(sinceDeployRange(newest)).toEqual({ from: Date.UTC(2026, 10, 12, 18, 0, 0), to: null });
    expect(latestDeploy([])).toBeNull();
    expect(latestDeploy(undefined)).toBeNull();
  });
});

describe('buildNumeratorQuery', () => {
  const now = Date.UTC(2026, 10, 13, 12, 0, 0);
  const base = { ...DEFAULT_CONTROLS, view: 'ratio' as const, breakdown: 'status', range: '1h' as const };

  it('drops the breakdown and filters on the breakdown tag, keeping the other filters and the overlays', () => {
    const q = new URLSearchParams(
      buildNumeratorQuery({ ...base, numerator: '5*', filters: [{ key: 'route', value: '/a' }], compareDay: true, application: 'shop' }, MetricKind.Counter, now)!
    );

    expect(q.get('breakdown')).toBeNull();
    expect(q.getAll('filter')).toEqual(['route:/a', 'status:5*']);
    expect(q.get('compare')).toBe('1d');
    expect(q.get('application')).toBe('shop');
    expect(q.get('percentile')).toBeNull();
  });

  it('replaces a chip already on the breakdown key (one value per key)', () => {
    const q = new URLSearchParams(buildNumeratorQuery({ ...base, numerator: '503', filters: [{ key: 'status', value: '5*' }] }, MetricKind.Counter, now)!);

    expect(q.getAll('filter')).toEqual(['status:503']);
  });

  it('is null without a breakdown or a numerator', () => {
    expect(buildNumeratorQuery({ ...base, numerator: null }, MetricKind.Counter, now)).toBeNull();
    expect(buildNumeratorQuery({ ...base, breakdown: null, numerator: '5*' }, MetricKind.Counter, now)).toBeNull();
  });
});

describe('ratioPercent', () => {
  it('divides numerator by total as a percentage', () => {
    expect(ratioPercent(1, 4)).toBe(25);
    expect(ratioPercent(0, 10)).toBe(0);
  });

  it('is null — never NaN or Infinity — when the total is zero or not finite', () => {
    expect(ratioPercent(0, 0)).toBeNull();
    expect(ratioPercent(5, 0)).toBeNull();
    expect(ratioPercent(Number.NaN, 5)).toBeNull();
    expect(ratioPercent(5, Number.POSITIVE_INFINITY)).toBeNull();
  });
});

describe('orderedLineNames / (rest)', () => {
  const s = series(MetricKind.Counter, [
    line('big', [point(0, { sum: 50 })]),
    line('small', [point(0, { sum: 2 })]),
    line(REST_NAME, [point(0, { sum: 1 })]),
    line('big', [point(0, { sum: 999 })], '1d'),
  ]);

  it('keeps the server order with (rest) last and ignores overlays', () => {
    expect(orderedLineNames(s)).toEqual(['big', 'small', REST_NAME]);
  });

  it('never re-sorts: the server ranking (by headline value, ties by value) is the order', () => {
    const serverOrder = series(MetricKind.Histogram, [
      line(REST_NAME, [point(0, { count: 1000 })]),
      line('b', [point(0, { count: 1, sum: 100 })]),
      line('a', [point(0, { count: 9, sum: 1 })]),
    ]);

    expect(orderedLineNames(serverOrder)).toEqual(['b', 'a', REST_NAME]);
  });

  it('offers only real tag values to the numerator picker', () => {
    expect(breakdownValues(s)).toEqual(['big', 'small']);
    expect(breakdownValues(null)).toEqual([]);
  });

  it('draws (rest) with the no-colour index', () => {
    const datasets = buildDatasets(s, { percentile: null, showMinMax: false });

    expect(datasets.find((x) => x.lineName === REST_NAME)?.colorIndex).toBe(-1);
    expect(datasets.find((x) => x.lineName === 'big' && x.compare === null)?.colorIndex).toBe(0);
    expect(datasets.find((x) => x.lineName === 'small')?.colorIndex).toBe(1);
  });
});

describe('buildDatasets', () => {
  it('plots the sum of a counter and labels each line with its resolution', () => {
    const s = series(MetricKind.Counter, [line('total', [point(0, { sum: 4, count: 2 }), point(1, { sum: 6, count: 3 })])]);
    const [ds] = buildDatasets(s, { percentile: null, showMinMax: false });

    expect(ds.data.map((x) => x.y)).toEqual([4, 6]);
    expect(ds.label).toBe('total · Fine');
    expect(ds.dashed).toBe(false);
    expect(ds.axis).toBe('y');
  });

  it('sorts points by time and drops unparseable timestamps', () => {
    const s = series(MetricKind.Counter, [
      line('total', [point(2, { sum: 3 }), { ...point(0), timestamp: 'garbage' }, point(1, { sum: 2 })]),
    ]);

    expect(buildDatasets(s, { percentile: null, showMinMax: false })[0].data.map((x) => x.y)).toEqual([2, 3]);
  });

  it('plots a histogram by count and adds the requested percentile on the secondary axis', () => {
    const s = series(MetricKind.Histogram, [line('total', [point(0, { count: 10, percentile: 250 }), point(1, { count: 5 })])]);
    const datasets = buildDatasets(s, { percentile: 95, showMinMax: false });

    expect(datasets.map((x) => x.role)).toEqual(['value', 'percentile']);
    expect(datasets[0].data.map((x) => x.y)).toEqual([10, 5]);
    expect(datasets[1].axis).toBe('y1');
    expect(datasets[1].label).toContain('p95');
    expect(datasets[1].data.map((x) => x.y)).toEqual([250, null]);
    expect(buildDatasets(s, { percentile: null, showMinMax: false })).toHaveLength(1);
  });

  it('plots a gauge by its average and adds min / max only when asked', () => {
    const s = series(MetricKind.Gauge, [line('total', [point(0, { value: 5, min: 1, max: 9 })])]);

    expect(buildDatasets(s, { percentile: null, showMinMax: false }).map((x) => x.role)).toEqual(['value']);

    const withRange = buildDatasets(s, { percentile: null, showMinMax: true });

    expect(withRange.map((x) => x.role)).toEqual(['value', 'min', 'max']);
    expect(withRange[0].data[0].y).toBe(5);
    expect(withRange[1].data[0].y).toBe(1);
    expect(withRange[2].data[0].y).toBe(9);
  });

  it('draws an overlay dashed, in its primary colour family, labelled with its own resolution', () => {
    const s = series(MetricKind.Counter, [
      line('ok', [point(0, { sum: 5 })]),
      line('ok', [point(0, { sum: 4 })], '1d', MetricTier.Hourly),
    ]);
    const datasets = buildDatasets(s, { percentile: null, showMinMax: false });
    const overlay = datasets.find((x) => x.compare === '1d')!;

    expect(overlay.dashed).toBe(true);
    expect(overlay.colorIndex).toBe(datasets.find((x) => x.compare === null)!.colorIndex);
    expect(overlay.label).toBe('ok (−1d) · Hourly');
    expect(datasets.map((x) => x.compare)).toEqual([null, '1d']);
  });
});

describe('alignOverlay', () => {
  const primary = [T0, T0 + MIN5, T0 + 2 * MIN5];

  it('nulls a primary timestamp the overlay has no point for, so the line breaks', () => {
    const overlay = [{ x: T0, y: 1 }, { x: T0 + 2 * MIN5, y: 3 }];

    expect(alignOverlay(overlay, primary, true)).toEqual([
      { x: T0, y: 1 },
      { x: T0 + MIN5, y: null },
      { x: T0 + 2 * MIN5, y: 3 },
    ]);
  });

  it('keeps an overlay point the primary has no bucket for', () => {
    const overlay = [{ x: T0 + 7 * MIN5, y: 9 }];

    expect(alignOverlay(overlay, primary, true).at(-1)).toEqual({ x: T0 + 7 * MIN5, y: 9 });
  });

  it('leaves a coarser-resolution overlay on its own timestamps', () => {
    const overlay = [{ x: T0, y: 1 }];

    expect(alignOverlay(overlay, primary, false)).toEqual(overlay);
  });

  it('is a no-op against an empty primary', () => {
    const overlay = [{ x: T0, y: 1 }];

    expect(alignOverlay(overlay, [], true)).toEqual(overlay);
  });

  it('gaps a same-resolution overlay missing a bucket through buildDatasets', () => {
    const s = series(MetricKind.Counter, [
      line('total', [point(0, { sum: 1 }), point(1, { sum: 2 })]),
      line('total', [point(0, { sum: 5 })], '1d'),
    ]);
    const overlay = buildDatasets(s, { percentile: null, showMinMax: false }).find((x) => x.compare === '1d')!;

    expect(overlay.data.map((x) => x.y)).toEqual([5, null]);
  });

  it('aligns a long-range overlay: the server labels both lines with the display tier, not the stored one', () => {
    // A 30-day read of fine rows and its 7d overlay of daily rows are both binned to days and both labelled Daily
    // (resolution = max(display tier, coarsest stored tier)), so the overlay is gapped onto the primary's days.
    const s = series(MetricKind.Counter, [
      line('total', [point(0, { sum: 1 }), point(1, { sum: 2 })], null, MetricTier.Daily),
      line('total', [point(1, { sum: 7 })], '7d', MetricTier.Daily),
    ]);
    const overlay = buildDatasets(s, { percentile: null, showMinMax: false }).find((x) => x.compare === '7d')!;

    expect(overlay.data.map((x) => x.y)).toEqual([null, 7]);
  });
});

describe('buildRatioDatasets', () => {
  // The primary (breakdown) response: 5xx are low volume, so they fell into (rest) and no named line holds them.
  const primary = series(MetricKind.Counter, [
    line('200', [point(0, { sum: 90 }), point(1, { sum: 0 })]),
    line('404', [point(0, { sum: 6 }), point(1, { sum: 0 })]),
    line(REST_NAME, [point(0, { sum: 4 })]),
  ]);
  // The numerator request: no breakdown, filter status:5*.
  const numerator = series(MetricKind.Counter, [line('total', [point(0, { sum: 4 })])]);

  it('is empty until a numerator is chosen and its series has loaded', () => {
    expect(buildRatioDatasets(primary, numerator, null)).toEqual([]);
    expect(buildRatioDatasets(primary, undefined, '5*')).toEqual([]);
  });

  it('counts numerator values that exist only in (rest) — no false 0%', () => {
    const [ds] = buildRatioDatasets(primary, numerator, '5*');

    // 4 / (90 + 6 + 4) at bucket 0: the 5xx live only in (rest) of the breakdown response.
    expect(ds.data[0].y).toBeCloseTo(4, 5);
    expect(ds.role).toBe('ratio');
    expect(ds.label).toContain('5* ÷ total');
  });

  it('gives null, not NaN, for a bucket whose total is zero', () => {
    const [ds] = buildRatioDatasets(primary, numerator, '5*');

    expect(ds.data[1].y).toBeNull();
    expect(ds.data.every((x) => x.y === null || Number.isFinite(x.y))).toBe(true);
  });

  it('reads 0% for a bucket the numerator series has no point for', () => {
    const [ds] = buildRatioDatasets(primary, series(MetricKind.Counter, [line('total', [])]), '5*');

    expect(ds.data[0].y).toBe(0);
  });

  it('draws an overlay per compare offset, dashed, each over its own total', () => {
    const withOverlay = series(MetricKind.Counter, [
      line('ok', [point(0, { sum: 8 })]),
      line(REST_NAME, [point(0, { sum: 2 })]),
      line('ok', [point(0, { sum: 5 })], '1d'),
      line(REST_NAME, [point(0, { sum: 5 })], '1d'),
    ]);
    const bad = series(MetricKind.Counter, [line('total', [point(0, { sum: 2 })]), line('total', [point(0, { sum: 5 })], '1d')]);
    const datasets = buildRatioDatasets(withOverlay, bad, 'bad');

    expect(datasets.map((x) => x.compare)).toEqual([null, '1d']);
    expect(datasets[0].data[0].y).toBe(20);
    expect(datasets[1].data[0].y).toBe(50);
    expect(datasets[1].dashed).toBe(true);
  });

  it('uses the histogram count', () => {
    const h = series(MetricKind.Histogram, [
      line('a', [point(0, { count: 3, sum: 999 })]),
      line(REST_NAME, [point(0, { count: 1, sum: 999 })]),
    ]);
    const b = series(MetricKind.Histogram, [line('total', [point(0, { count: 1, sum: 999 })])]);

    expect(buildRatioDatasets(h, b, 'b')[0].data[0].y).toBe(25);
  });
});

describe('isRatioActive', () => {
  it('needs the ratio view, a ratio-capable kind and a breakdown', () => {
    const on = { ...DEFAULT_CONTROLS, view: 'ratio' as const, breakdown: 'result' };

    expect(isRatioActive(on, MetricKind.Counter)).toBe(true);
    expect(isRatioActive(on, MetricKind.Histogram)).toBe(true);
    expect(isRatioActive(on, MetricKind.Gauge)).toBe(false);
    expect(isRatioActive(on, MetricKind.UpDownCounter)).toBe(false);
    expect(isRatioActive({ ...on, breakdown: null }, MetricKind.Counter)).toBe(false);
    expect(isRatioActive({ ...on, view: 'values' }, MetricKind.Counter)).toBe(false);
  });
});

describe('axisTitles', () => {
  it('labels an up-down counter as net change, never as a level or a sum', () => {
    expect(axisTitles(MetricKind.UpDownCounter, null, false).y).toBe('Net change');
    expect(axisTitles(MetricKind.UpDownCounter, 'By', false).y).toBe('Net change (bytes)');
  });

  it('keeps sum, measurements and average for the other kinds', () => {
    expect(axisTitles(MetricKind.Counter, 'ms', false)).toEqual({ y: 'Sum (ms)', y1: '' });
    expect(axisTitles(MetricKind.Histogram, 'ms', false)).toEqual({ y: 'Measurements', y1: 'Percentile (ms)' });
    expect(axisTitles(MetricKind.Gauge, null, false)).toEqual({ y: 'Average', y1: '' });
    expect(axisTitles(MetricKind.Counter, null, true)).toEqual({ y: 'Share of total (%)', y1: '' });
  });
});

describe('kindChartNote', () => {
  it('explains the up-down counter chart and points at ObservableGauge for a level', () => {
    expect(kindChartNote(MetricKind.UpDownCounter)).toContain('net change per bucket');
    expect(kindChartNote(MetricKind.UpDownCounter)).toContain('ObservableGauge');
  });

  it('says nothing for the kinds whose chart is self-explanatory', () => {
    expect(kindChartNote(MetricKind.Counter)).toBeNull();
    expect(kindChartNote(MetricKind.Histogram)).toBeNull();
    expect(kindChartNote(MetricKind.Gauge)).toBeNull();
  });
});

describe('the still-filling bucket', () => {
  it('carries the flag onto the chart points of the current lines', () => {
    const s = series(MetricKind.Histogram, [
      line('total', [point(0, { count: 9, percentile: 100 }), point(1, { count: 2, percentile: 50, incomplete: true })]),
    ]);
    const datasets = buildDatasets(s, { percentile: 95, showMinMax: false });

    expect(datasets.map((x) => x.data.map((y) => y.incomplete))).toEqual([
      [false, true],
      [false, true],
    ]);
  });

  it('leaves overlay points unflagged', () => {
    const s = series(MetricKind.Counter, [
      line('total', [point(0, { sum: 5 }), point(1, { sum: 1, incomplete: true })]),
      line('total', [point(0, { sum: 4 }), point(1, { sum: 6 })], '1d'),
    ]);
    const overlay = buildDatasets(s, { percentile: null, showMinMax: false }).find((x) => x.compare === '1d')!;

    expect(overlay.data.map((x) => x.incomplete)).toEqual([false, false]);
  });

  it('collects the flagged timestamps of the current range only', () => {
    const s = series(MetricKind.Counter, [
      line('a', [point(0), point(1, { incomplete: true })]),
      line('b', [point(1, { incomplete: true })]),
      line('a', [point(1, { incomplete: true })], '1d'),
    ]);

    expect([...incompleteTimes(s)]).toEqual([T0 + MIN5]);
  });

  it('flags the matching ratio bucket of the current range, not of an overlay', () => {
    const primary = series(MetricKind.Counter, [
      line('ok', [point(0, { sum: 8 }), point(1, { sum: 1, incomplete: true })]),
      line('ok', [point(0, { sum: 5 }), point(1, { sum: 5 })], '1d'),
    ]);
    const numerator = series(MetricKind.Counter, [
      line('total', [point(0, { sum: 2 }), point(1, { sum: 1, incomplete: true })]),
      line('total', [point(0, { sum: 1 }), point(1, { sum: 1 })], '1d'),
    ]);
    const datasets = buildRatioDatasets(primary, numerator, 'ok');

    expect(datasets.find((x) => x.compare === null)!.data.map((x) => x.incomplete)).toEqual([false, true]);
    expect(datasets.find((x) => x.compare === '1d')!.data.map((x) => x.incomplete ?? false)).toEqual([false, false]);
  });
});

describe('headline', () => {
  it('names the headline per kind', () => {
    expect(headlineLabel(MetricKind.Counter)).toBe('Total');
    expect(headlineLabel(MetricKind.UpDownCounter)).toBe('Net change');
    expect(headlineLabel(MetricKind.Histogram)).toBe('Measurements');
    expect(headlineLabel(MetricKind.Gauge)).toBe('Average');
  });

  it('computes the change against a baseline, and nothing against a missing or zero one', () => {
    expect(changePercent(120, 100)).toBeCloseTo(20, 5);
    expect(changePercent(-50, -100)).toBeCloseTo(50, 5);
    expect(changePercent(5, 0)).toBeNull();
    expect(changePercent(null, 10)).toBeNull();
    expect(changePercent(10, null)).toBeNull();
  });

  it('formats a change with its sign', () => {
    expect(formatChange(20)).toBe('+20%');
    expect(formatChange(-3.14)).toBe('-3.1%');
    expect(formatChange(0)).toBe('0%');
    expect(formatChange(null)).toBe('—');
  });

  it('uses the server headlines (complete buckets only), never a sum of the drawn points', () => {
    const s = {
      ...series(MetricKind.Counter, [
        line('total', [point(0, { sum: 30 }), point(1, { sum: 3, incomplete: true })]),
        line('total', [point(0, { sum: 20 }), point(1, { sum: 20 })], '1d'),
      ]),
      headlines: [
        { compare: null, value: 30, count: 6 },
        { compare: '1d', value: 20, count: 4 },
      ],
    };
    const summary = headlineSummary(s)!;

    expect(summary.label).toBe('Total');
    expect(summary.value).toBe(30);
    expect(summary.excludesIncomplete).toBe(true);
    expect(summary.comparisons).toEqual([{ compare: '1d', value: 20, changePercent: 50 }]);
  });

  it('is null when the response carries no headline', () => {
    expect(headlineSummary(series(MetricKind.Counter, [line('total', [point(0)])]))).toBeNull();
  });

  it('does not claim to exclude anything when every bucket is complete', () => {
    const s = { ...series(MetricKind.Counter, [line('total', [point(0, { sum: 4 })])]), headlines: [{ compare: null, value: 4, count: 1 }] };

    expect(headlineSummary(s)!.excludesIncomplete).toBe(false);
  });
});

describe('rate', () => {
  it('reads the URL parameter, defaulting to per bucket', () => {
    expect(parseRate('minute')).toBe('minute');
    expect(parseRate('second')).toBe('second');
    expect(parseRate(null)).toBe('bucket');
    expect(parseRate('hour')).toBe('bucket');
    expect(rateParam('bucket')).toBeNull();
    expect(rateParam('second')).toBe('second');
  });

  it('applies to counters and histogram counts only, and never to the ratio view', () => {
    expect(supportsRate(MetricKind.Counter)).toBe(true);
    expect(supportsRate(MetricKind.Histogram)).toBe(true);
    expect(supportsRate(MetricKind.UpDownCounter)).toBe(false);
    expect(supportsRate(MetricKind.Gauge)).toBe(false);
    expect(effectiveRate(MetricKind.Gauge, 'minute', false)).toBe('bucket');
    expect(effectiveRate(MetricKind.Counter, 'minute', true)).toBe('bucket');
    expect(effectiveRate(MetricKind.Counter, 'second', false)).toBe('second');
  });

  it('divides a bucket by its length', () => {
    expect(rateValue(600, 300, 'minute')).toBe(120);
    expect(rateValue(600, 300, 'second')).toBe(2);
    expect(rateValue(600, 3600, 'minute')).toBe(10);
    expect(rateValue(600, 300, 'bucket')).toBe(600);
    expect(rateValue(null, 300, 'minute')).toBeNull();
    expect(rateValue(600, 0, 'minute')).toBeNull();
    expect(rateValue(600, undefined, 'minute')).toBeNull();
  });

  it('charts each point per its own bucket length, so an hourly point and a fine one read on one scale', () => {
    const s = series(MetricKind.Counter, [
      line('total', [point(0, { sum: 3600, bucketSeconds: 3600 }), point(12, { sum: 300, bucketSeconds: 300 })]),
    ]);

    expect(buildDatasets(s, { percentile: null, showMinMax: false, rate: 'minute' })[0].data.map((x) => x.y)).toEqual([60, 60]);
    expect(buildDatasets(s, { percentile: null, showMinMax: false })[0].data.map((x) => x.y)).toEqual([3600, 300]);
  });

  it('rates a histogram count but leaves its percentile alone', () => {
    const s = series(MetricKind.Histogram, [line('total', [point(0, { count: 600, percentile: 250 })])]);
    const [value, percentile] = buildDatasets(s, { percentile: 95, showMinMax: false, rate: 'second' });

    expect(value.data[0].y).toBe(2);
    expect(percentile.data[0].y).toBe(250);
  });

  it('ignores the rate for a gauge and an up-down counter', () => {
    const gauge = series(MetricKind.Gauge, [line('total', [point(0, { value: 7 })])]);
    const upDown = series(MetricKind.UpDownCounter, [line('total', [point(0, { sum: -4 })])]);

    expect(buildDatasets(gauge, { percentile: null, showMinMax: false, rate: 'second' })[0].data[0].y).toBe(7);
    expect(buildDatasets(upDown, { percentile: null, showMinMax: false, rate: 'second' })[0].data[0].y).toBe(-4);
  });

  it('puts the unit on the axis', () => {
    expect(axisTitles(MetricKind.Counter, null, false, 'minute').y).toBe('Sum per minute');
    expect(axisTitles(MetricKind.Counter, 'By', false, 'second').y).toBe('Sum per second (bytes)');
    expect(axisTitles(MetricKind.Histogram, 'ms', false, 'minute')).toEqual({ y: 'Measurements per minute', y1: 'Percentile (ms)' });
    expect(axisTitles(MetricKind.Gauge, null, false, 'minute').y).toBe('Average');
    expect(axisTitles(MetricKind.UpDownCounter, null, false, 'minute').y).toBe('Net change');
    expect(axisTitles(MetricKind.Counter, null, true, 'minute').y).toBe('Share of total (%)');
  });

  it('turns the headline into an average rate over the span it covers, overlays over the same span', () => {
    // 10 complete five-minute buckets: from iso(0) to completeUntil iso(10) is 50 minutes.
    const s = {
      ...series(MetricKind.Counter, [line('total', [point(0, { sum: 1 }), point(10, { sum: 1, incomplete: true })])]),
      headlines: [
        { compare: null, value: 500, count: 500 },
        { compare: '1d', value: 250, count: 250 },
      ],
    };

    expect(headlineSpanSeconds(s)).toBe(3000);

    const summary = headlineSummary(s, 'minute')!;

    expect(summary.label).toBe('Average per minute');
    expect(summary.value).toBe(10);
    expect(summary.comparisons).toEqual([{ compare: '1d', value: 5, changePercent: 100 }]);
    expect(headlineSummary(s, 'bucket')!.value).toBe(500);
  });

  it('measures the headline span from the first bucket when it floors to before the range start', () => {
    const s = { ...series(MetricKind.Counter, [line('total', [point(-1, { sum: 1 })])]), completeUntil: iso(1) };

    expect(headlineSpanSeconds(s)).toBe(600);
  });

  it('keeps the gauge headline an average whatever the rate', () => {
    const s = { ...series(MetricKind.Gauge, [line('total', [point(0)])]), headlines: [{ compare: null, value: 7, count: 2 }] };
    const summary = headlineSummary(s, 'second')!;

    expect(summary.label).toBe('Average');
    expect(summary.value).toBe(7);
  });
});
