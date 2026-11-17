import { describe, expect, it } from 'vitest';
import { MetricKind } from '@/types/metrics';
import { MetricDeviationDirection, MetricTileKind, MetricTileStatusRule, type MetricBoardTile, type MetricBoardTileResult } from '@/types/metricBoard';
import { SloComparison, SloGaugeAggregate, SloState } from '@/types/slo';
import {
  acceptsTileInstrument,
  acknowledgedHint,
  annotationsTruncatedHint,
  buildTileRequest,
  completeUntilHint,
  changeStatusRule,
  changeTileKind,
  defaultAggregate,
  describeRule,
  describeTile,
  emptyTileDraft,
  formatTileChange,
  formatTileValue,
  sparklinePoints,
  statusCounts,
  tileStatusClass,
  validateTileDraft,
} from './boardModel';

const valueTile: MetricBoardTile = { ...emptyTileDraft, id: 1, name: 'Volume', instrument: 'orders' };
const ratioTile: MetricBoardTile = {
  ...emptyTileDraft,
  id: 2,
  name: 'Success',
  instrument: 'payments',
  kind: MetricTileKind.Ratio,
  ratioTag: 'result',
  goodValues: 'ok',
  badValues: 'declined,error',
};

function ratioFields(): Partial<MetricBoardTile> {
  return { kind: MetricTileKind.Ratio, ratioTag: 'result', goodValues: 'ok', badValues: 'declined,error' };
}
function result(tile: MetricBoardTile, patch: Partial<MetricBoardTileResult> = {}): MetricBoardTileResult {
  return { tile, instrumentKind: MetricKind.Counter, unit: null, current: null, dayAgo: null, weekAgo: null, status: null, unacknowledgedStatus: null, annotations: [], sparkline: [], ...patch };
}

describe('formatTileValue', () => {
  it('shows a ratio as a percentage', () => {
    expect(formatTileValue(result(ratioTile), 0.9876)).toBe('98.76%');
  });

  it('keeps a gauge unit but drops a histogram one, whose value is a count', () => {
    expect(formatTileValue(result(valueTile, { instrumentKind: MetricKind.Gauge, unit: 'By' }), 12)).toBe('12 bytes');
    expect(formatTileValue(result(valueTile, { instrumentKind: MetricKind.Histogram, unit: 'ms' }), 12)).toBe('12');
  });

  it('shows a dash for nothing to show', () => {
    expect(formatTileValue(result(valueTile), null)).toBe('—');
  });
});

describe('formatTileChange', () => {
  it('reads a ratio move in points', () => {
    expect(formatTileChange(ratioTile, 0.95, 0.99)).toBe('−4.00 pts');
    expect(formatTileChange(ratioTile, 0.99, 0.95)).toBe('+4.00 pts');
    expect(formatTileChange(ratioTile, 0.99, null)).toBe('—');
  });

  it('reads an unchanged ratio as ±0 points', () => {
    expect(formatTileChange(ratioTile, 0.99, 0.99)).toBe('±0.00 pts');
  });

  it.each([
    [150, 100, '+50%'],
    [50, 100, '-50%'],
    [103, 100, '+3%'],
    [100.04, 100, '0%'],
    [100, 100, '0%'],
    [-50, -100, '+50%'],
    [5, 0, '—'],
    [null, 100, '—'],
    [100, null, '—'],
  ])('reads a value move from %s to %s as %s', (current, previous, expected) => {
    expect(formatTileChange(valueTile, current, previous)).toBe(expected);
  });
});

describe('describeTile / describeRule', () => {
  it('names the instrument, the ratio, the filter and the application', () => {
    expect(describeTile({ ...ratioTile, tagFilter: 'region=eu', application: 'shop' }))
      .toBe('payments · result: ok / declined,error · region=eu · @shop');
  });

  it('names a gauge aggregate', () => {
    expect(describeTile({ ...valueTile, gaugeAggregate: SloGaugeAggregate.Max })).toBe('orders · max');
  });

  it('reads thresholds and deviations', () => {
    expect(describeRule({ ...valueTile, statusRule: MetricTileStatusRule.Threshold, threshold: 10, comparison: SloComparison.AtMost })).toBe('≤ 10');
    expect(describeRule({ ...ratioTile, statusRule: MetricTileStatusRule.Threshold, threshold: 0.99, comparison: SloComparison.AtLeast })).toBe('≥ 99.00%');
    expect(describeRule({ ...ratioTile, statusRule: MetricTileStatusRule.Deviation, maxDeviation: 0.02, baselineDays: 7, deviationDirection: MetricDeviationDirection.Drop }))
      .toBe('drop ≤ 2.00 pts vs 7d');
    expect(describeRule({ ...valueTile, statusRule: MetricTileStatusRule.Deviation, maxDeviation: 0.3, baselineDays: 1, deviationDirection: MetricDeviationDirection.Either }))
      .toBe('± 30% vs 1d');
    expect(describeRule(valueTile)).toBeNull();
  });
});

describe('status', () => {
  it.each([
    [SloState.Healthy, 'border-green-500/60'],
    [SloState.Warning, 'border-amber-500/70'],
    [SloState.Breaching, 'border-red-500/80'],
    [SloState.NoData, 'border-dashed border-slate-400/60'],
    [SloState.Acknowledged, 'border-slate-500/70'],
    [null, 'border-border'],
  ])('colours a %s tile %s', (status, expected) => {
    expect(tileStatusClass(status)).toBe(expected);
  });

  it('counts tiles per status, every status in its own bucket and a rule-less tile in none', () => {
    const counts = statusCounts([
      result(valueTile, { status: SloState.Healthy }),
      result(valueTile, { status: SloState.Healthy }),
      result(valueTile, { status: SloState.Healthy }),
      result(valueTile, { status: SloState.Warning }),
      result(valueTile, { status: SloState.Warning }),
      result(valueTile, { status: SloState.Breaching }),
      result(valueTile, { status: SloState.NoData }),
      result(valueTile, { status: SloState.Acknowledged }),
      result(valueTile, { status: null }),
    ]);

    expect(counts).toEqual({ healthy: 3, warning: 2, breaching: 1, acknowledged: 1, noData: 1 });
    expect(statusCounts([])).toEqual({ healthy: 0, warning: 0, breaching: 0, acknowledged: 0, noData: 0 });
  });
});

describe('acknowledgedHint', () => {
  it('names the rule verdict an acknowledged-noise note turned into Acknowledged', () => {
    expect(acknowledgedHint({ status: SloState.Acknowledged, unacknowledgedStatus: SloState.Breaching })).toBe(
      'Breaching by its rule, acknowledged by a known-noise note',
    );
  });

  it('says nothing for any other status', () => {
    expect(acknowledgedHint({ status: SloState.Breaching, unacknowledgedStatus: SloState.Breaching })).toBeNull();
    expect(acknowledgedHint({ status: SloState.Acknowledged, unacknowledgedStatus: null })).toBeNull();
  });
});

// The board response's #328 cut and #333 note cap (MetricBoardModel.CompleteUntil / AnnotationsTruncated). TZ is UTC.
describe('board header hints', () => {
  const board = { from: '2026-05-25T08:00:00Z', to: '2026-05-25T14:40:12Z' };

  it('names the instant the values stop at while the newest bucket is still filling', () => {
    expect(completeUntilHint({ ...board, completeUntil: '2026-05-25T14:35:00Z' })).toBe('values up to 14:35 (newest bucket still filling)');
  });

  it('says nothing when the values run to the end of the range, or the instant is unusable', () => {
    expect(completeUntilHint({ ...board, completeUntil: board.to })).toBeNull();
    expect(completeUntilHint({ ...board, completeUntil: '' })).toBeNull();
  });

  it('says there is nothing complete yet when the cut is at the start', () => {
    expect(completeUntilHint({ ...board, completeUntil: board.from })).toBe('no complete values yet (newest bucket still filling)');
  });

  it('flags a truncated note list only when the server says so', () => {
    expect(annotationsTruncatedHint({ annotationsTruncated: true })).toBe('some notes not shown');
    expect(annotationsTruncatedHint({ annotationsTruncated: false })).toBeNull();
  });
});

describe('sparklinePoints', () => {
  it('orders points, scales a ratio to percent and leaves empty buckets out', () => {
    const points = sparklinePoints(result(ratioTile, {
      sparkline: [
        { timestamp: '2026-11-13T11:00:00Z', value: 0.5 },
        { timestamp: '2026-11-13T10:00:00Z', value: 0.25 },
        { timestamp: '2026-11-13T09:00:00Z', value: null },
      ],
    }));

    expect(points).toEqual([
      { x: Date.parse('2026-11-13T10:00:00Z'), y: 25 },
      { x: Date.parse('2026-11-13T11:00:00Z'), y: 50 },
    ]);
  });
});

describe('tile editor', () => {
  it('accepts any instrument for a value tile and refuses a gauge for a ratio', () => {
    expect(acceptsTileInstrument(MetricTileKind.Value, MetricKind.Gauge)).toBe(true);
    expect(acceptsTileInstrument(MetricTileKind.Ratio, MetricKind.Gauge)).toBe(false);
    expect(acceptsTileInstrument(MetricTileKind.Ratio, MetricKind.Histogram)).toBe(true);
  });

  it('switching kind drops the other kind\'s fields', () => {
    expect(changeTileKind({ ...valueTile, gaugeAggregate: SloGaugeAggregate.Max }, MetricTileKind.Ratio).gaugeAggregate).toBeNull();
    const back = changeTileKind(ratioTile, MetricTileKind.Value);
    expect([back.ratioTag, back.goodValues, back.badValues]).toEqual([null, null, null]);
  });

  it('switching rule applies defaults by kind and clears the other rule', () => {
    const threshold = changeStatusRule(valueTile, MetricTileStatusRule.Threshold);
    expect([threshold.comparison, threshold.threshold]).toEqual([SloComparison.AtMost, 0]);

    const deviation = changeStatusRule(threshold, MetricTileStatusRule.Deviation);
    expect([deviation.threshold, deviation.comparison, deviation.maxDeviation, deviation.baselineDays]).toEqual([null, null, 0.3, 7]);
    expect(changeStatusRule(ratioTile, MetricTileStatusRule.Deviation).maxDeviation).toBe(0.02);
  });

  // One row per branch of validateTileDraft, which mirrors MetricBoardValidation: the first problem wins.
  const deviation = (patch: Partial<MetricBoardTile>): MetricBoardTile => ({
    ...valueTile,
    statusRule: MetricTileStatusRule.Deviation,
    baselineDays: 7,
    maxDeviation: 0.3,
    deviationDirection: MetricDeviationDirection.Either,
    ...patch,
  });
  const threshold = (value: number | null): MetricBoardTile => ({
    ...valueTile,
    statusRule: MetricTileStatusRule.Threshold,
    comparison: SloComparison.AtMost,
    threshold: value,
  });

  it.each<[string, MetricBoardTile, MetricKind | null, string | null]>([
    ['a blank name', { ...valueTile, name: ' ' }, null, 'Give the tile a name.'],
    ['a blank instrument', { ...valueTile, instrument: '  ' }, null, 'Pick the instrument this tile reads.'],
    ['a ratio over a gauge', ratioTile, MetricKind.Gauge, 'A ratio tile needs a counter or histogram: gauges have no event count.'],
    ['a value tile over a gauge', valueTile, MetricKind.Gauge, null],
    ['a ratio whose instrument kind is not known yet', ratioTile, null, null],
    ['a tag filter without =', { ...valueTile, tagFilter: 'status' }, null, "Tag filter entry 'status' is missing '='."],
    ['a tag filter with * inside a value', { ...valueTile, tagFilter: 'status=5*0' }, null, "Tag filter value '5*0' may only use '*' as its last character."],
    ['a ratio without a tag', { ...ratioTile, ratioTag: ' ' }, MetricKind.Counter, 'Pick the tag whose value marks an event good or bad.'],
    ['a ratio without good values', { ...ratioTile, goodValues: '' }, MetricKind.Counter, 'Good values and bad values are both required.'],
    ['a ratio without bad values', { ...ratioTile, badValues: null }, MetricKind.Counter, 'Good values and bad values are both required.'],
    ['a bad good-value pattern', { ...ratioTile, goodValues: 'o*k' }, MetricKind.Counter, "Good values: Value 'o*k' may only use '*' as its last character."],
    ['an empty bad-value entry', { ...ratioTile, badValues: 'declined,,error' }, MetricKind.Counter, 'Bad values: Value list contains an empty entry.'],
    ['overlapping good and bad values', { ...ratioTile, goodValues: 'ok', badValues: 'o*' }, MetricKind.Counter,
      'Good and bad values must not overlap: no value may match a pattern in both lists.'],
    ['a valid ratio', ratioTile, MetricKind.Histogram, null],
    ['a threshold without a number', threshold(null), null, 'The threshold must be a number.'],
    ['a threshold that is not finite', threshold(Number.NaN), null, 'The threshold must be a number.'],
    ['a threshold of zero', threshold(0), null, null],
    ['a negative threshold', threshold(-5), null, null],
    ['a baseline of 3 days', deviation({ baselineDays: 3 }), null, 'Compare with the same window 1 or 7 days earlier.'],
    ['no baseline', deviation({ baselineDays: null }), null, 'Compare with the same window 1 or 7 days earlier.'],
    ['a 1-day baseline', deviation({ baselineDays: 1 }), null, null],
    ['no allowed deviation', deviation({ maxDeviation: null }), null, 'The allowed deviation must be greater than zero.'],
    ['a zero deviation', deviation({ maxDeviation: 0 }), null, 'The allowed deviation must be greater than zero.'],
    ['a negative deviation', deviation({ maxDeviation: -0.1 }), null, 'The allowed deviation must be greater than zero.'],
    ['an infinite deviation', deviation({ maxDeviation: Number.POSITIVE_INFINITY }), null, 'The allowed deviation must be greater than zero.'],
    ['a value drop of 100%', deviation({ maxDeviation: 1, deviationDirection: MetricDeviationDirection.Drop }), null,
      'A drop can be at most 100% of the baseline: keep the allowance below 100%.'],
    ['a value change either way of 150%', deviation({ maxDeviation: 1.5 }), null,
      'A drop can be at most 100% of the baseline: keep the allowance below 100%.'],
    ['a value rise of 150%', deviation({ maxDeviation: 1.5, deviationDirection: MetricDeviationDirection.Rise }), null, null],
    ['a value drop of 99%', deviation({ maxDeviation: 0.99, deviationDirection: MetricDeviationDirection.Drop }), null, null],
    ['a ratio rise of 100 points', deviation({ ...ratioFields(), maxDeviation: 1, deviationDirection: MetricDeviationDirection.Rise }), MetricKind.Counter,
      'A ratio can move at most 100 points: keep the allowance below 100.'],
    ['a ratio drop of 99 points', deviation({ ...ratioFields(), maxDeviation: 0.99, deviationDirection: MetricDeviationDirection.Drop }), MetricKind.Counter, null],
  ])('validates %s', (_, draft, kind, expected) => {
    expect(validateTileDraft(draft, kind)).toBe(expected);
  });

  it('builds a threshold request with trimmed text, nulled blanks and only the threshold fields', () => {
    const request = buildTileRequest({
      ...valueTile,
      name: ' Volume ',
      instrument: ' orders ',
      tagFilter: '  ',
      application: '',
      gaugeAggregate: SloGaugeAggregate.Max,
      statusRule: MetricTileStatusRule.Threshold,
      threshold: 5,
      maxDeviation: 0.3,
      baselineDays: 7,
      deviationDirection: MetricDeviationDirection.Drop,
    });

    expect(request).toEqual({
      ...valueTile,
      name: 'Volume',
      instrument: 'orders',
      tagFilter: null,
      application: null,
      gaugeAggregate: SloGaugeAggregate.Max,
      statusRule: MetricTileStatusRule.Threshold,
      comparison: SloComparison.AtMost,
      threshold: 5,
      maxDeviation: null,
      baselineDays: null,
      deviationDirection: null,
    });
  });

  it('builds a deviation request with only the deviation fields, Either by default', () => {
    const request = buildTileRequest({
      ...valueTile,
      application: ' shop ',
      tagFilter: ' region=eu ',
      statusRule: MetricTileStatusRule.Deviation,
      comparison: SloComparison.AtLeast,
      threshold: 3,
      baselineDays: 1,
      maxDeviation: 0.25,
      deviationDirection: null,
    });

    expect(request).toMatchObject({
      application: 'shop',
      tagFilter: 'region=eu',
      comparison: null,
      threshold: null,
      baselineDays: 1,
      maxDeviation: 0.25,
      deviationDirection: MetricDeviationDirection.Either,
    });
    expect(buildTileRequest({ ...request, deviationDirection: MetricDeviationDirection.Rise }).deviationDirection).toBe(MetricDeviationDirection.Rise);
  });

  it('builds a ratio request with trimmed ratio fields and no gauge aggregate', () => {
    const request = buildTileRequest({ ...ratioTile, ratioTag: ' result ', goodValues: ' ok ', badValues: ' declined,error ', gaugeAggregate: SloGaugeAggregate.Min });

    expect(request).toMatchObject({ ratioTag: 'result', goodValues: 'ok', badValues: 'declined,error', gaugeAggregate: null });
  });

  it('drops the ratio fields of a value tile and every rule field without a rule', () => {
    const request = buildTileRequest({
      ...valueTile,
      ratioTag: 'result',
      goodValues: 'ok',
      badValues: 'x',
      comparison: SloComparison.AtMost,
      threshold: 1,
      baselineDays: 7,
      maxDeviation: 0.1,
      deviationDirection: MetricDeviationDirection.Drop,
    });

    expect([request.ratioTag, request.goodValues, request.badValues]).toEqual([null, null, null]);
    expect([request.comparison, request.threshold, request.baselineDays, request.maxDeviation, request.deviationDirection]).toEqual([null, null, null, null, null]);
  });

  it('defaults a gauge to the average', () => {
    expect(defaultAggregate(MetricKind.Gauge)).toBe(SloGaugeAggregate.Average);
    expect(defaultAggregate(MetricKind.Counter)).toBeNull();
  });
});
