import { describe, expect, it } from 'vitest';
import { SloComparison, SloGaugeAggregate, SloKind, SloState } from '@/types/slo';
import { checkTarget, checkThreshold, formatObserved, formatTarget, showsSloValues, sloValueDisplay } from './sloDisplay';

describe('sloValueDisplay', () => {
  it('reports not-evaluated before the first evaluation, whatever the state', () => {
    expect(sloValueDisplay({ evaluated: false, state: SloState.Healthy })).toBe('not-evaluated');
    expect(showsSloValues({ evaluated: false, state: SloState.Healthy })).toBe(false);
  });

  it('hides the values of an evaluated NoData objective, so it never renders as healthy', () => {
    expect(sloValueDisplay({ evaluated: true, state: SloState.NoData })).toBe('no-data');
    expect(showsSloValues({ evaluated: true, state: SloState.NoData })).toBe(false);
  });

  it.each([SloState.Healthy, SloState.Warning, SloState.Breaching, SloState.Acknowledged])(
    'shows the values of an evaluated objective in state %s',
    (state) => {
      expect(sloValueDisplay({ evaluated: true, state })).toBe('values');
      expect(showsSloValues({ evaluated: true, state })).toBe(true);
    },
  );
});

describe('formatTarget / formatObserved', () => {
  it('shows a metric latency objective as its percentile and threshold, observed as the share within it', () => {
    const o = { kind: SloKind.MetricLatency, targetValue: 0.25, percentile: 99, attainment: 0.9812 };

    expect(formatTarget(o)).toBe('p99 ≤ 0.25');
    expect(formatObserved(o)).toBe('98.12% ≤ 0.25');
  });

  it('formats the other kinds as before', () => {
    expect(formatTarget({ kind: SloKind.SuccessRate, targetValue: 0.995, percentile: null })).toBe('99.50%');
    expect(formatObserved({ kind: SloKind.ExecutionLatency, targetValue: 500, attainment: 420.4 })).toBe('420 ms');
    expect(formatObserved({ kind: SloKind.MetricThreshold, targetValue: 0, attainment: 3 })).toBe('3');
  });
});

describe('gauge threshold display', () => {
  it('shows the aggregate, direction, threshold and compliant share', () => {
    const o = { kind: SloKind.MetricGaugeThreshold, targetValue: 5, percentile: 99, attainment: 0.875, comparison: SloComparison.AtLeast, gaugeAggregate: SloGaugeAggregate.Min };

    expect(formatTarget(o)).toBe('min ≥ 5 in 99%');
    expect(formatObserved(o)).toBe('87.50% compliant');
    expect(formatTarget({ ...o, comparison: null, gaugeAggregate: null, percentile: null })).toBe('avg ≤ 5 in 95%');
  });
});

describe('baseline kinds', () => {
  it('shows a baseline ratio objective as a drop in points, and a plain ratio as a share', () => {
    const o = { kind: SloKind.MetricRatio, targetValue: 0.05, percentile: null, attainment: 0.012, baselineDays: 7 };

    expect(formatTarget(o)).toBe('a drop of at most 5.00 pts from the 7-day baseline');
    expect(formatObserved(o)).toBe('a drop of 1.20 pts');
    expect(formatTarget({ ...o, targetValue: 0.95, baselineDays: null })).toBe('95.00%');
  });

  it('shows a volume floor as a share of its baseline', () => {
    const o = { kind: SloKind.MetricVolumeFloor, targetValue: 0.5, percentile: null, attainment: 0.83, baselineDays: 7 };

    expect(formatTarget(o)).toBe('≥ 50.00% of baseline');
    expect(formatObserved(o)).toBe('83.00% of baseline');
  });
});

describe('checkTarget / checkThreshold (the verdict check numbers)', () => {
  it.each([
    [SloKind.MetricLatency, 2, 95, 0.95, 2],
    [SloKind.MetricLatency, 0.25, 99, 0.99, 0.25],
    [SloKind.MetricGaugeThreshold, 600, 95, 0.95, 600],
    [SloKind.MetricGaugeThreshold, 600, null, 0.95, 600],
    [SloKind.SuccessRate, 0.995, null, 0.995, null],
    [SloKind.ExecutionLatency, 500, 95, 500, null],
    [SloKind.BacklogDepth, 100, null, 100, null],
    [SloKind.MetricVolumeFloor, 0.5, null, 0.5, null],
  ])('kind %s, target %s, percentile %s → target %s, threshold %s', (kind, targetValue, percentile, target, threshold) => {
    expect(checkTarget({ kind, targetValue, percentile })).toBe(target);
    expect(checkThreshold({ kind, targetValue })).toBe(threshold);
  });
});
