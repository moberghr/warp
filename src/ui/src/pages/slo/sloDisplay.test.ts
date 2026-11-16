import { describe, expect, it } from 'vitest';
import { SloKind, SloState } from '@/types/slo';
import { formatObserved, formatTarget, showsSloValues, sloValueDisplay } from './sloDisplay';

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
