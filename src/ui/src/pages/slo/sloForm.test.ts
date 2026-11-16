import { describe, expect, it } from 'vitest';
import { MetricKind, type MetricInstrument } from '@/types/metrics';
import { SloComparison, SloGaugeAggregate, SloKind, SloState, type SloObjective, type SloUpsertRequest } from '@/types/slo';
import {
  acceptsInstrument,
  buildUpsertRequest,
  changeKind,
  fieldsFor,
  fractionToPercent,
  isCountKind,
  objectiveToDraft,
  parseValueList,
  percentToFraction,
  selectableInstruments,
  validateMetricDraft,
  validateTagFilter,
  valueListsOverlap,
} from './sloForm';

const baseDraft: SloUpsertRequest = {
  id: 0,
  name: 'x',
  kind: SloKind.SuccessRate,
  dimension: '',
  application: null,
  targetValue: 0.99,
  percentile: null,
  windowSeconds: 3600,
  enabled: true,
};

const ratioDraft = (patch: Partial<SloUpsertRequest> = {}): SloUpsertRequest => ({
  ...changeKind(baseDraft, SloKind.MetricRatio),
  dimension: 'payments.processed',
  ratioTag: 'outcome',
  goodValues: 'ok',
  badValues: 'failed',
  ...patch,
});

function instrument(name: string, kind: number): MetricInstrument {
  return {
    name,
    meter: 'm',
    kind: kind as MetricInstrument['kind'],
    unit: null,
    description: null,
    bounds: [],
    tagKeys: [],
    firstSeenAt: '2026-01-01T00:00:00Z',
    lastSeenAt: '2026-01-01T00:00:00Z',
    collapsedAt: null,
    collapsedTags: [],
  };
}

describe('selectableInstruments', () => {
  it('excludes gauges and sorts by name', () => {
    const result = selectableInstruments([
      instrument('b.counter', MetricKind.Counter),
      instrument('a.gauge', MetricKind.Gauge),
      instrument('a.hist', MetricKind.Histogram),
      instrument('c.updown', MetricKind.UpDownCounter),
    ]);

    expect(result.map((x) => x.name)).toEqual(['a.hist', 'b.counter', 'c.updown']);
  });
});

describe('percent conversion', () => {
  it('round-trips without float noise', () => {
    expect(fractionToPercent(0.9)).toBe(90);
    expect(fractionToPercent(0.995)).toBe(99.5);
    expect(percentToFraction(99.5)).toBe(0.995);
    expect(percentToFraction(fractionToPercent(0.07))).toBe(0.07);
  });
});

describe('changeKind', () => {
  it('applies per-kind defaults', () => {
    expect(changeKind(baseDraft, SloKind.MetricRatio)).toMatchObject({ targetValue: 0.99, baselineDays: null });
    expect(changeKind(baseDraft, SloKind.MetricThreshold)).toMatchObject({ targetValue: 0, tagFilter: '' });
    expect(changeKind(baseDraft, SloKind.MetricVolumeFloor)).toMatchObject({ targetValue: 0.5, baselineDays: 7 });
    expect(changeKind(baseDraft, SloKind.MetricGap)).toMatchObject({ targetValue: 0, secondaryMetric: '' });
  });

  it('raises a short window to the metric minimum', () => {
    expect(changeKind({ ...baseDraft, windowSeconds: 60 }, SloKind.MetricThreshold).windowSeconds).toBe(600);
  });

  it('drops metric fields and the instrument when leaving a metric kind', () => {
    const next = changeKind(ratioDraft(), SloKind.SuccessRate);

    expect(next.dimension).toBe('');
    expect(next.ratioTag).toBeUndefined();
    expect(next.goodValues).toBeUndefined();
    expect(next.percentile).toBeNull();
  });

  it('clears a job-type dimension when entering a metric kind', () => {
    expect(changeKind({ ...baseDraft, dimension: 'Shop.Jobs.X' }, SloKind.MetricGap).dimension).toBe('');
  });

  it('keeps an instrument when switching between metric kinds', () => {
    expect(changeKind(ratioDraft(), SloKind.MetricThreshold).dimension).toBe('payments.processed');
  });

  it('leaves non-metric to non-metric switches alone apart from the percentile', () => {
    const next = changeKind({ ...baseDraft, dimension: 'default', percentile: 99 }, SloKind.QueueWaitLatency);

    expect(next).toMatchObject({ dimension: 'default', percentile: null, kind: SloKind.QueueWaitLatency });
  });
});

describe('fieldsFor', () => {
  it('shows exactly each kind its own fields', () => {
    expect(fieldsFor(SloKind.MetricRatio)).toEqual({ tagFilter: true, ratio: true, baseline: 'optional', secondary: false, percentile: false });
    expect(fieldsFor(SloKind.MetricThreshold)).toEqual({ tagFilter: true, ratio: false, baseline: 'none', secondary: false, percentile: false });
    expect(fieldsFor(SloKind.MetricVolumeFloor)).toEqual({ tagFilter: true, ratio: false, baseline: 'required', secondary: false, percentile: false });
    expect(fieldsFor(SloKind.MetricGap)).toEqual({ tagFilter: true, ratio: false, baseline: 'none', secondary: true, percentile: false });
  });

  it('keeps the percentile for latency kinds only', () => {
    expect(fieldsFor(SloKind.QueueWaitLatency).percentile).toBe(true);
    expect(fieldsFor(SloKind.ExecutionLatency).percentile).toBe(true);
    expect(fieldsFor(SloKind.SuccessRate).percentile).toBe(false);
    expect(fieldsFor(SloKind.MetricRatio).percentile).toBe(false);
  });
});

describe('value lists', () => {
  it('parses literals and trailing-star prefixes', () => {
    expect(parseValueList('200, 5*').patterns).toEqual([
      { text: '200', prefix: false },
      { text: '5', prefix: true },
    ]);
  });

  it('rejects empty entries and a star that is not last', () => {
    expect(parseValueList('a,,b').error).toMatch(/empty/);
    expect(parseValueList('a*b').error).toMatch(/last character/);
  });

  it.each([
    ['500', '500', true],
    ['500', '5*', true],
    ['5*', '50*', true],
    ['*', 'anything', true],
    ['200,201', '5*', false],
    ['2*', '5*', false],
    ['ok', 'failed,error*', false],
    ['ok,5*', 'failed,500', true],
  ])('overlap(%s, %s) = %s', (good, bad, expected) => {
    expect(valueListsOverlap(good, bad)).toBe(expected);
    expect(valueListsOverlap(bad, good)).toBe(expected);
  });
});

describe('validateTagFilter', () => {
  it('accepts empty, single, multiple and prefix filters', () => {
    expect(validateTagFilter('', 'Filter')).toBeNull();
    expect(validateTagFilter(null, 'Filter')).toBeNull();
    expect(validateTagFilter('a=b', 'Filter')).toBeNull();
    expect(validateTagFilter('http.response.status_code=5*, route=/x', 'Filter')).toBeNull();
  });

  it.each([
    ['a', /missing '='/],
    ['a=b,,c=d', /empty entry/],
    ['=b', /empty key/],
    ['a*=b', /must not contain '\*'/],
    ['a=b*c', /last character/],
    ['a=b,a=c', /more than once/],
  ])('rejects %s', (filter, message) => {
    expect(validateTagFilter(filter, 'Filter')).toMatch(message);
  });
});

describe('validateMetricDraft', () => {
  it('ignores non-metric kinds', () => {
    expect(validateMetricDraft({ ...baseDraft, windowSeconds: 1 })).toBeNull();
  });

  it('accepts a valid ratio', () => {
    expect(validateMetricDraft(ratioDraft())).toBeNull();
  });

  it('requires an instrument and enforces the 10 minute window', () => {
    expect(validateMetricDraft(ratioDraft({ dimension: '' }))).toMatch(/instrument/);
    expect(validateMetricDraft(ratioDraft({ windowSeconds: 599 }))).toMatch(/10 minutes/);
    expect(validateMetricDraft(ratioDraft({ windowSeconds: 600 }))).toBeNull();
  });

  it('requires ratio tag and both value lists', () => {
    expect(validateMetricDraft(ratioDraft({ ratioTag: '' }))).toMatch(/tag/);
    expect(validateMetricDraft(ratioDraft({ goodValues: '' }))).toMatch(/both required/);
    expect(validateMetricDraft(ratioDraft({ badValues: ' ' }))).toMatch(/both required/);
  });

  it('rejects overlapping good and bad values', () => {
    expect(validateMetricDraft(ratioDraft({ goodValues: '200', badValues: '2*' }))).toMatch(/overlap/);
    expect(validateMetricDraft(ratioDraft({ goodValues: '*', badValues: 'x' }))).toMatch(/overlap/);
  });

  it('bounds the ratio target to (0,1) and checks the baseline', () => {
    expect(validateMetricDraft(ratioDraft({ targetValue: 0 }))).toMatch(/Target/);
    expect(validateMetricDraft(ratioDraft({ targetValue: 1 }))).toMatch(/Target/);
    expect(validateMetricDraft(ratioDraft({ baselineDays: 3 }))).toMatch(/Baseline/);
    expect(validateMetricDraft(ratioDraft({ baselineDays: 7, targetValue: 1.2 }))).toMatch(/drop/);
    expect(validateMetricDraft(ratioDraft({ baselineDays: 7, targetValue: 0.1 }))).toBeNull();
  });

  it('allows zero for threshold and gap but not negatives', () => {
    const threshold = { ...changeKind(baseDraft, SloKind.MetricThreshold), dimension: 'http.server.request.duration' };

    expect(validateMetricDraft(threshold)).toBeNull();
    expect(validateMetricDraft({ ...threshold, targetValue: -1 })).toMatch(/zero or greater/);
    expect(validateMetricDraft({ ...threshold, tagFilter: 'broken' })).toMatch(/missing '='/);

    const gap = { ...changeKind(baseDraft, SloKind.MetricGap), dimension: 'a', secondaryMetric: 'b' };

    expect(validateMetricDraft(gap)).toBeNull();
    expect(validateMetricDraft({ ...gap, secondaryMetric: '' })).toMatch(/secondary/);
    expect(validateMetricDraft({ ...gap, secondaryTagFilter: 'x' })).toMatch(/Secondary tag filter/);
    expect(validateMetricDraft({ ...gap, targetValue: -3 })).toMatch(/zero or greater/);
  });

  it('requires a 1 or 7 day baseline and a fraction in (0,1) for the volume floor', () => {
    const floor = { ...changeKind(baseDraft, SloKind.MetricVolumeFloor), dimension: 'a' };

    expect(validateMetricDraft(floor)).toBeNull();
    expect(validateMetricDraft({ ...floor, baselineDays: null })).toMatch(/baseline/);
    expect(validateMetricDraft({ ...floor, baselineDays: 2 })).toMatch(/baseline/);
    expect(validateMetricDraft({ ...floor, targetValue: 1 })).toMatch(/fraction/);
    expect(validateMetricDraft({ ...floor, targetValue: 0 })).toMatch(/fraction/);
  });
});

describe('buildUpsertRequest', () => {
  it('trims and nulls empty metric fields, and sends only the ratio fields for a ratio', () => {
    const request = buildUpsertRequest(ratioDraft({ goodValues: ' ok ', baselineDays: 1, secondaryMetric: 'stale' }));

    expect(request).toMatchObject({ ratioTag: 'outcome', goodValues: 'ok', badValues: 'failed', baselineDays: 1, percentile: null });
    expect(request.secondaryMetric).toBeUndefined();
    expect(request.secondaryTagFilter).toBeUndefined();
  });

  it("keeps a ratio's tag filter", () => {
    const request = buildUpsertRequest(ratioDraft({ tagFilter: ' step=authorize ' }));

    expect(request.tagFilter).toBe('step=authorize');
  });

  it('sends a null tag filter when it was left blank', () => {
    const draft = { ...changeKind(baseDraft, SloKind.MetricThreshold), dimension: ' a ' };
    const request = buildUpsertRequest(draft);

    expect(request.dimension).toBe('a');
    expect(request.tagFilter).toBeNull();
    expect(request.baselineDays).toBeUndefined();
  });

  it('sends the baseline for a volume floor and the secondary fields for a gap', () => {
    const floor = buildUpsertRequest({ ...changeKind(baseDraft, SloKind.MetricVolumeFloor), dimension: 'a', tagFilter: 'k=v' });
    const gap = buildUpsertRequest({ ...changeKind(baseDraft, SloKind.MetricGap), dimension: 'a', secondaryMetric: 'b', secondaryTagFilter: '' });

    expect(floor).toMatchObject({ baselineDays: 7, tagFilter: 'k=v' });
    expect(gap).toMatchObject({ secondaryMetric: 'b', secondaryTagFilter: null, tagFilter: null });
  });

  it('strips metric fields from a non-metric kind', () => {
    const request = buildUpsertRequest({ ...baseDraft, dimension: 'q', tagFilter: 'x=y', baselineDays: 7, percentile: 95 });

    expect(request.tagFilter).toBeUndefined();
    expect(request.baselineDays).toBeUndefined();
    expect(request.percentile).toBe(95);
  });
});

describe('edit round-trip (objective -> draft -> request)', () => {
  const objective = (patch: Partial<SloObjective>): SloObjective => ({
    id: 7,
    name: 'o',
    kind: SloKind.MetricRatio,
    dimension: 'payments.processed',
    application: 'shop',
    targetValue: 0.1,
    percentile: null,
    windowSeconds: 3600,
    enabled: true,
    evaluated: true,
    attainment: 1,
    budgetRemaining: 1,
    burnRateShort: 0,
    burnRateLong: 0,
    state: SloState.Healthy,
    acknowledgedUntil: null,
    lastEvaluatedAt: null,
    ...patch,
  });

  const metricFields = (kind: SloKind): Partial<SloObjective> => {
    switch (kind) {
      case SloKind.MetricRatio:
        return { tagFilter: 'step=authorize', ratioTag: 'outcome', goodValues: 'ok', badValues: 'failed,error*', baselineDays: 7 };
      case SloKind.MetricThreshold:
        return { tagFilter: 'http.response.status_code=5*' };
      case SloKind.MetricVolumeFloor:
        return { tagFilter: 'route=/checkout', baselineDays: 1 };
      case SloKind.MetricLatency:
        // A non-default percentile: a round-trip that fell back to 95 would pass with the default.
        return { tagFilter: 'http.route=/checkout', percentile: 99 };
      case SloKind.MetricGaugeThreshold:
        // Neither the default comparison (AtMost) nor the default aggregate (Average), for the same reason.
        return { tagFilter: 'pool=main', percentile: 90, comparison: SloComparison.AtLeast, gaugeAggregate: SloGaugeAggregate.Max };
      default:
        return { tagFilter: 'side=in', secondaryMetric: 'orders.shipped', secondaryTagFilter: 'side=out' };
    }
  };

  it.each([
    SloKind.MetricRatio,
    SloKind.MetricThreshold,
    SloKind.MetricVolumeFloor,
    SloKind.MetricGap,
    SloKind.MetricLatency,
    SloKind.MetricGaugeThreshold,
  ])(
    'preserves every metric field of kind %s',
    (kind) => {
      const fields = metricFields(kind);
      const request = buildUpsertRequest(objectiveToDraft(objective({ kind, ...fields })));

      expect(request).toMatchObject({ id: 7, kind, dimension: 'payments.processed', application: 'shop', targetValue: 0.1, windowSeconds: 3600, ...fields });
    },
  );
});

describe('isCountKind', () => {
  it('treats threshold, gap and backlog as counts', () => {
    expect(isCountKind(SloKind.MetricThreshold)).toBe(true);
    expect(isCountKind(SloKind.MetricGap)).toBe(true);
    expect(isCountKind(SloKind.BacklogDepth)).toBe(true);
    expect(isCountKind(SloKind.MetricRatio)).toBe(false);
    expect(isCountKind(SloKind.ExecutionLatency)).toBe(false);
  });
});

describe('metric latency', () => {
  const latencyDraft = (patch: Partial<SloUpsertRequest> = {}): SloUpsertRequest => ({
    ...changeKind(baseDraft, SloKind.MetricLatency),
    dimension: 'http.server.request.duration',
    targetValue: 0.25,
    ...patch,
  });

  it('limits the instrument picker to histograms', () => {
    const result = selectableInstruments(
      [
        instrument('c', MetricKind.Counter),
        instrument('h', MetricKind.Histogram),
        instrument('g', MetricKind.Gauge),
        instrument('u', MetricKind.UpDownCounter),
      ],
      SloKind.MetricLatency,
    );

    expect(result.map((x) => x.name)).toEqual(['h']);
    expect(acceptsInstrument(SloKind.MetricThreshold, MetricKind.Histogram)).toBe(true);
    expect(acceptsInstrument(SloKind.MetricLatency, MetricKind.Counter)).toBe(false);
  });

  it('defaults the percentile to 95 and shows the percentile and tag filter fields', () => {
    const draft = changeKind(baseDraft, SloKind.MetricLatency);

    expect(draft.percentile).toBe(95);
    expect(draft.windowSeconds).toBeGreaterThanOrEqual(600);
    expect(fieldsFor(SloKind.MetricLatency)).toMatchObject({ tagFilter: true, percentile: true, ratio: false, secondary: false });
  });

  it('clears the percentile when switching to another metric kind', () => {
    expect(changeKind(latencyDraft({ percentile: 99 }), SloKind.MetricThreshold).percentile).toBeNull();
  });

  it.each([0, 100, 95.5])('rejects percentile %s', (percentile) => {
    expect(validateMetricDraft(latencyDraft({ percentile }))).toMatch(/Percentile/);
  });

  it('rejects a negative threshold and accepts a valid draft', () => {
    expect(validateMetricDraft(latencyDraft({ targetValue: -1 }))).toMatch(/Threshold/);
    expect(validateMetricDraft(latencyDraft())).toBeNull();
  });

  it('keeps the percentile and tag filter in the request, unlike the other metric kinds', () => {
    const request = buildUpsertRequest(latencyDraft({ percentile: 99, tagFilter: ' http.route=/x ' }));

    expect(request.percentile).toBe(99);
    expect(request.tagFilter).toBe('http.route=/x');
    expect(buildUpsertRequest({ ...latencyDraft({ percentile: 99 }), kind: SloKind.MetricThreshold }).percentile).toBeNull();
  });

  it('is neither a count nor a ratio kind', () => {
    expect(isCountKind(SloKind.MetricLatency)).toBe(false);
  });
});

describe('metric gauge threshold', () => {
  const gaugeDraft = (patch: Partial<SloUpsertRequest> = {}): SloUpsertRequest => ({
    ...changeKind(baseDraft, SloKind.MetricGaugeThreshold),
    dimension: 'queue.depth',
    targetValue: 500,
    ...patch,
  });

  it('limits the instrument picker to gauges', () => {
    const result = selectableInstruments(
      [instrument('c', MetricKind.Counter), instrument('h', MetricKind.Histogram), instrument('g', MetricKind.Gauge)],
      SloKind.MetricGaugeThreshold,
    );

    expect(result.map((x) => x.name)).toEqual(['g']);
  });

  it('defaults to at most, average and 95% compliant', () => {
    const draft = changeKind(baseDraft, SloKind.MetricGaugeThreshold);

    expect(draft.comparison).toBe(SloComparison.AtMost);
    expect(draft.gaugeAggregate).toBe(SloGaugeAggregate.Average);
    expect(draft.percentile).toBe(95);
    expect(fieldsFor(SloKind.MetricGaugeThreshold)).toMatchObject({ tagFilter: true, percentile: true });
  });

  it('drops the gauge options when switching to another kind', () => {
    const next = changeKind(gaugeDraft({ comparison: SloComparison.AtLeast }), SloKind.MetricThreshold);

    expect(next.comparison).toBeUndefined();
    expect(next.gaugeAggregate).toBeUndefined();
  });

  it('accepts a negative threshold and rejects a bad compliant share or option', () => {
    expect(validateMetricDraft(gaugeDraft({ targetValue: -5 }))).toBeNull();
    expect(validateMetricDraft(gaugeDraft({ percentile: 100 }))).toMatch(/Compliant share/);
    expect(validateMetricDraft(gaugeDraft({ comparison: 7 as SloComparison }))).toMatch(/at most or at least/);
    expect(validateMetricDraft(gaugeDraft({ gaugeAggregate: 9 as SloGaugeAggregate }))).toMatch(/aggregate/);
  });

  it('sends the comparison, aggregate and percentile, and only for this kind', () => {
    const request = buildUpsertRequest(gaugeDraft({ comparison: SloComparison.AtLeast, gaugeAggregate: SloGaugeAggregate.Min, percentile: 90 }));

    expect(request).toMatchObject({ comparison: SloComparison.AtLeast, gaugeAggregate: SloGaugeAggregate.Min, percentile: 90 });
    const threshold = buildUpsertRequest({ ...gaugeDraft({ comparison: SloComparison.AtLeast }), kind: SloKind.MetricThreshold });
    expect(threshold.comparison).toBeUndefined();
    expect(threshold.percentile).toBeNull();
  });

  it('carries the gauge options into an edit draft', () => {
    const draft = objectiveToDraft({
      id: 9,
      name: 'depth',
      kind: SloKind.MetricGaugeThreshold,
      dimension: 'queue.depth',
      application: null,
      targetValue: 500,
      percentile: 95,
      windowSeconds: 3600,
      enabled: true,
      evaluated: false,
      attainment: 0,
      budgetRemaining: 1,
      burnRateShort: 0,
      burnRateLong: 0,
      state: SloState.Healthy,
      acknowledgedUntil: null,
      lastEvaluatedAt: null,
      comparison: SloComparison.AtLeast,
      gaugeAggregate: SloGaugeAggregate.Max,
    });

    expect(draft.comparison).toBe(SloComparison.AtLeast);
    expect(draft.gaugeAggregate).toBe(SloGaugeAggregate.Max);
  });
});
