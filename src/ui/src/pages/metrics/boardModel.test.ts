import { describe, expect, it } from 'vitest';
import { MetricKind } from '@/types/metrics';
import { MetricDeviationDirection, MetricTileKind, MetricTileStatusRule, type MetricBoardTile, type MetricBoardTileResult } from '@/types/metricBoard';
import { SloComparison, SloGaugeAggregate, SloState } from '@/types/slo';
import {
  acceptsTileInstrument,
  acknowledgedHint,
  buildTileRequest,
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

  it('reads a value move as a percentage change', () => {
    expect(formatTileChange(valueTile, 150, 100)).toContain('50');
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
  it('colours a tile by its status, neutral without a rule', () => {
    expect(tileStatusClass(SloState.Breaching)).toContain('red');
    expect(tileStatusClass(SloState.Warning)).toContain('amber');
    expect(tileStatusClass(SloState.NoData)).toContain('dashed');
    expect(tileStatusClass(null)).toBe('border-border');
  });

  it('counts tiles per status', () => {
    const counts = statusCounts([
      result(valueTile, { status: SloState.Breaching }),
      result(valueTile, { status: SloState.Breaching }),
      result(valueTile, { status: SloState.NoData }),
      result(valueTile, { status: SloState.Acknowledged }),
      result(valueTile, { status: null }),
    ]);

    expect(counts).toEqual({ healthy: 0, warning: 0, breaching: 2, acknowledged: 1, noData: 1 });
  });

  it('gives an acknowledged tile its own neutral border', () => {
    expect(tileStatusClass(SloState.Acknowledged)).toContain('slate');
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

  it('validates like the server', () => {
    expect(validateTileDraft({ ...valueTile, name: ' ' }, null)).toMatch(/name/);
    expect(validateTileDraft(ratioTile, MetricKind.Gauge)).toMatch(/gauge/);
    expect(validateTileDraft({ ...valueTile, tagFilter: 'status' }, null)).toMatch(/missing '='/);
    expect(validateTileDraft({ ...ratioTile, goodValues: 'ok', badValues: 'o*' }, MetricKind.Counter)).toMatch(/overlap/);
    expect(validateTileDraft({ ...valueTile, statusRule: MetricTileStatusRule.Deviation, baselineDays: 7, maxDeviation: 1.5, deviationDirection: MetricDeviationDirection.Drop }, null))
      .toMatch(/100%/);
    expect(validateTileDraft({ ...valueTile, statusRule: MetricTileStatusRule.Deviation, baselineDays: 7, maxDeviation: 1.5, deviationDirection: MetricDeviationDirection.Rise }, null))
      .toBeNull();
    expect(validateTileDraft(ratioTile, MetricKind.Counter)).toBeNull();
  });

  it('builds a request with trimmed text, nulled blanks and only the active rule\'s fields', () => {
    const request = buildTileRequest({
      ...valueTile,
      name: ' Volume ',
      tagFilter: '  ',
      application: '',
      statusRule: MetricTileStatusRule.Threshold,
      threshold: 5,
      maxDeviation: 0.3,
    });

    expect(request.name).toBe('Volume');
    expect(request.tagFilter).toBeNull();
    expect(request.application).toBeNull();
    expect(request.comparison).toBe(SloComparison.AtMost);
    expect(request.maxDeviation).toBeNull();
  });

  it('defaults a gauge to the average', () => {
    expect(defaultAggregate(MetricKind.Gauge)).toBe(SloGaugeAggregate.Average);
    expect(defaultAggregate(MetricKind.Counter)).toBeNull();
  });
});
