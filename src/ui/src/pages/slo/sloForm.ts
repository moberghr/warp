// Pure form logic for the SLO objective editor. Mirrors the client-checkable rules of Warp.Core.Slo.SloValidation
// for the metric kinds (6-11) so most mistakes show up before a round trip. The server stays the authority: the
// upsert's 400 message is always surfaced by the page.
import { listsOverlap, parsePattern, type ValuePattern } from '@/lib/metricPattern';
import { MetricKind, type MetricInstrument } from '@/types/metrics';
import { SloComparison, SloGaugeAggregate, SloKind, isMetricKind, type SloObjective, type SloUpsertRequest } from '@/types/slo';

export const MIN_METRIC_WINDOW_SECONDS = 600;

/** Whether an objective of `kind` can read an instrument of `instrument` kind (mirrors SloValidation.AcceptsInstrument). */
export function acceptsInstrument(kind: SloKind, instrument: MetricKind): boolean {
  if (kind === SloKind.MetricLatency) {
    return instrument === MetricKind.Histogram;
  }
  if (kind === SloKind.MetricGaugeThreshold) {
    return instrument === MetricKind.Gauge;
  }

  return instrument !== MetricKind.Gauge;
}

/**
 * Instruments that can back a metric SLO of `kind`: a latency objective needs a histogram (bucket counts), a gauge
 * threshold a gauge; the event-counting kinds exclude gauges, which have no event count.
 */
export function selectableInstruments(instruments: MetricInstrument[], kind: SloKind = SloKind.MetricThreshold): MetricInstrument[] {
  return instruments
    .filter((x) => acceptsInstrument(kind, x.kind))
    .sort((a, b) => a.name.localeCompare(b.name));
}

export const DEFAULT_LATENCY_PERCENTILE = 95;

/** Rounds away float noise so 0.9 * 100 shows as 90, not 90.00000000000001. */
export function fractionToPercent(fraction: number): number {
  return Math.round(fraction * 100 * 1e6) / 1e6;
}

export function percentToFraction(percent: number): number {
  return Math.round((percent / 100) * 1e8) / 1e8;
}

const metricFieldKeys = [
  'tagFilter',
  'secondaryMetric',
  'secondaryTagFilter',
  'ratioTag',
  'goodValues',
  'badValues',
  'baselineDays',
  'comparison',
  'gaugeAggregate',
] as const;

/** A draft with every metric-only field cleared. */
function withoutMetricFields(draft: SloUpsertRequest): SloUpsertRequest {
  const next = { ...draft };
  for (const key of metricFieldKeys) {
    delete next[key];
  }

  return next;
}

/** Switches the draft to `kind`, dropping fields the new kind does not use and applying sensible defaults. */
export function changeKind(draft: SloUpsertRequest, kind: SloKind): SloUpsertRequest {
  const wasMetric = isMetricKind(draft.kind);
  const base = { ...withoutMetricFields(draft), kind, percentile: null };

  if (kind === SloKind.WebhookDeliveryLatency) {
    // Dimension = a delivery group (endpoint) or *; target = minutes; percentile = the share delivered in time.
    return { ...base, dimension: wasMetric ? '' : base.dimension, targetValue: 5, percentile: DEFAULT_LATENCY_PERCENTILE };
  }

  if (!isMetricKind(kind)) {
    return wasMetric ? { ...base, dimension: '' } : base;
  }

  // The dimension of a metric kind is an instrument name; a job type / queue from a non-metric kind is meaningless.
  const metricBase = { ...base, dimension: wasMetric ? draft.dimension : '', windowSeconds: Math.max(base.windowSeconds, MIN_METRIC_WINDOW_SECONDS) };

  switch (kind) {
    case SloKind.MetricRatio:
      return { ...metricBase, targetValue: 0.99, tagFilter: '', ratioTag: '', goodValues: '', badValues: '', baselineDays: null };
    case SloKind.MetricThreshold:
      return { ...metricBase, targetValue: 0, tagFilter: '' };
    case SloKind.MetricVolumeFloor:
      return { ...metricBase, targetValue: 0.5, tagFilter: '', baselineDays: 7 };
    case SloKind.MetricLatency:
      return { ...metricBase, targetValue: 0, percentile: DEFAULT_LATENCY_PERCENTILE, tagFilter: '' };
    case SloKind.MetricGaugeThreshold:
      return {
        ...metricBase,
        targetValue: 0,
        percentile: DEFAULT_LATENCY_PERCENTILE,
        tagFilter: '',
        comparison: SloComparison.AtMost,
        gaugeAggregate: SloGaugeAggregate.Average,
      };
    default:
      return { ...metricBase, targetValue: 0, tagFilter: '', secondaryMetric: '', secondaryTagFilter: '' };
  }
}

/** Which kind-specific field groups the form shows. */
export interface KindFields {
  tagFilter: boolean;
  ratio: boolean;
  baseline: 'none' | 'optional' | 'required';
  secondary: boolean;
  percentile: boolean;
}

export function fieldsFor(kind: SloKind): KindFields {
  switch (kind) {
    case SloKind.MetricRatio:
      return { tagFilter: true, ratio: true, baseline: 'optional', secondary: false, percentile: false };
    case SloKind.MetricThreshold:
      return { tagFilter: true, ratio: false, baseline: 'none', secondary: false, percentile: false };
    case SloKind.MetricVolumeFloor:
      return { tagFilter: true, ratio: false, baseline: 'required', secondary: false, percentile: false };
    case SloKind.MetricGap:
      return { tagFilter: true, ratio: false, baseline: 'none', secondary: true, percentile: false };
    case SloKind.MetricLatency:
    case SloKind.MetricGaugeThreshold:
      return { tagFilter: true, ratio: false, baseline: 'none', secondary: false, percentile: true };
    case SloKind.QueueWaitLatency:
    case SloKind.ExecutionLatency:
    case SloKind.WebhookDeliveryLatency:
      return { tagFilter: false, ratio: false, baseline: 'none', secondary: false, percentile: true };
    default:
      return { tagFilter: false, ratio: false, baseline: 'none', secondary: false, percentile: false };
  }
}

/** Parses `k=v` style values: a CSV where each value may end in a single trailing `*`. */
export function parseValueList(csv: string | null | undefined): { patterns: ValuePattern[]; error: string | null } {
  if (!csv || csv.trim() === '') {
    return { patterns: [], error: null };
  }

  const patterns: ValuePattern[] = [];
  for (const raw of csv.split(',')) {
    const value = raw.trim();
    if (value === '') {
      return { patterns: [], error: 'Value list contains an empty entry.' };
    }
    const pattern = parsePattern(value);
    if (!pattern) {
      return { patterns: [], error: `Value '${value}' may only use '*' as its last character.` };
    }
    patterns.push(pattern);
  }

  return { patterns, error: null };
}

/** True when some value could match a pattern in both lists (equal literals, literal under a prefix, nested prefixes, bare `*`). */
export function valueListsOverlap(good: string, bad: string): boolean {
  return listsOverlap(parseValueList(good).patterns, parseValueList(bad).patterns);
}

/** Validates the `k=v[,k=v...]` filter grammar (trailing `*` allowed on values, no duplicate keys). Returns an error or null. */
export function validateTagFilter(filter: string | null | undefined, label: string): string | null {
  if (!filter || filter.trim() === '') {
    return null;
  }

  const seen = new Set<string>();
  for (const raw of filter.split(',')) {
    const pair = raw.trim();
    if (pair === '') {
      return `${label} contains an empty entry.`;
    }
    const sep = pair.indexOf('=');
    if (sep < 0) {
      return `${label} entry '${pair}' is missing '='.`;
    }
    const key = pair.slice(0, sep).trim();
    const value = pair.slice(sep + 1).trim();
    if (key === '') {
      return `${label} entry '${pair}' has an empty key.`;
    }
    if (key.includes('*')) {
      return `${label} key '${key}' must not contain '*'.`;
    }
    if (!parsePattern(value)) {
      return `${label} value '${value}' may only use '*' as its last character.`;
    }
    if (seen.has(key)) {
      return `${label} key '${key}' appears more than once.`;
    }
    seen.add(key);
  }

  return null;
}

function blank(value: string | null | undefined): boolean {
  return !value || value.trim() === '';
}

/** First client-side problem with a metric-kind draft, or null. Non-metric kinds return null (the server validates those). */
export function validateMetricDraft(draft: SloUpsertRequest): string | null {
  if (!isMetricKind(draft.kind)) {
    return null;
  }

  if (blank(draft.dimension)) {
    return 'Pick the instrument this objective measures.';
  }
  if (draft.windowSeconds < MIN_METRIC_WINDOW_SECONDS) {
    return `Window must be at least ${MIN_METRIC_WINDOW_SECONDS / 60} minutes.`;
  }
  if (!Number.isFinite(draft.targetValue)) {
    return 'Target must be a number.';
  }

  const filterError = validateTagFilter(draft.tagFilter, 'Tag filter');
  if (filterError) {
    return filterError;
  }

  switch (draft.kind) {
    case SloKind.MetricRatio:
      return validateRatio(draft);
    case SloKind.MetricVolumeFloor:
      if (draft.baselineDays !== 1 && draft.baselineDays !== 7) {
        return 'Volume floor needs a baseline of 1 or 7 days.';
      }
      if (draft.targetValue <= 0 || draft.targetValue >= 1) {
        return 'Minimum fraction must be between 0% and 100% (exclusive).';
      }

      return null;
    case SloKind.MetricGap: {
      if (blank(draft.secondaryMetric)) {
        return 'Pick the secondary instrument that is subtracted from the primary one.';
      }
      const secondaryError = validateTagFilter(draft.secondaryTagFilter, 'Secondary tag filter');
      if (secondaryError) {
        return secondaryError;
      }

      return draft.targetValue < 0 ? 'Max gap must be zero or greater.' : null;
    }
    case SloKind.MetricLatency:
      return validateLatency(draft);
    case SloKind.MetricGaugeThreshold:
      return validateGauge(draft);
    default:
      return draft.targetValue < 0 ? 'Max events must be zero or greater.' : null;
  }
}

function validateLatency(draft: SloUpsertRequest): string | null {
  return validatePercentile(draft, 'Percentile') ?? (draft.targetValue < 0 ? 'Threshold must be zero or greater.' : null);
}

function validatePercentile(draft: SloUpsertRequest, label: string): string | null {
  const percentile = draft.percentile ?? DEFAULT_LATENCY_PERCENTILE;

  return !Number.isInteger(percentile) || percentile < 1 || percentile > 99 ? `${label} must be a whole number between 1 and 99.` : null;
}

function validateGauge(draft: SloUpsertRequest): string | null {
  const comparisons: number[] = Object.values(SloComparison);
  const aggregates: number[] = Object.values(SloGaugeAggregate);
  if (draft.comparison != null && !comparisons.includes(draft.comparison)) {
    return 'Pick whether the gauge must stay at most or at least the threshold.';
  }
  if (draft.gaugeAggregate != null && !aggregates.includes(draft.gaugeAggregate)) {
    return 'Pick the aggregate (average, min or max) compared with the threshold.';
  }

  return validatePercentile(draft, 'Compliant share');
}

function validateRatio(draft: SloUpsertRequest): string | null {
  if (blank(draft.ratioTag)) {
    return 'Pick the tag whose value marks an event good or bad.';
  }
  if (blank(draft.goodValues) || blank(draft.badValues)) {
    return 'Good values and bad values are both required.';
  }
  const good = parseValueList(draft.goodValues);
  if (good.error) {
    return `Good values: ${good.error}`;
  }
  const bad = parseValueList(draft.badValues);
  if (bad.error) {
    return `Bad values: ${bad.error}`;
  }
  if (listsOverlap(good.patterns, bad.patterns)) {
    return 'Good and bad values must not overlap: no value may match a pattern in both lists.';
  }
  if (draft.baselineDays != null && draft.baselineDays !== 1 && draft.baselineDays !== 7) {
    return 'Baseline must be 1 or 7 days.';
  }
  if (draft.targetValue <= 0 || draft.targetValue >= 1) {
    return draft.baselineDays == null
      ? 'Target must be between 0% and 100% (exclusive).'
      : 'Maximum drop must be between 0 and 100 points (exclusive).';
  }

  return null;
}

/** The request to send: metric fields trimmed, empty ones nulled, and fields of other kinds omitted. */
export function buildUpsertRequest(draft: SloUpsertRequest): SloUpsertRequest {
  const clean = (value: string | null | undefined) => (blank(value) ? null : value!.trim());
  const base = withoutMetricFields(draft);

  if (!isMetricKind(draft.kind)) {
    return { ...base, dimension: draft.dimension.trim() };
  }

  // Every metric kind takes a (primary) tag filter: dropping it on any kind silently widens the objective on edit.
  const usesPercentile = draft.kind === SloKind.MetricLatency || draft.kind === SloKind.MetricGaugeThreshold;
  const percentile = usesPercentile ? (draft.percentile ?? DEFAULT_LATENCY_PERCENTILE) : null;
  const request: SloUpsertRequest = { ...base, dimension: draft.dimension.trim(), percentile, tagFilter: clean(draft.tagFilter) };
  if (draft.kind === SloKind.MetricRatio) {
    request.ratioTag = clean(draft.ratioTag);
    request.goodValues = clean(draft.goodValues);
    request.badValues = clean(draft.badValues);
    request.baselineDays = draft.baselineDays ?? null;
  }
  if (draft.kind === SloKind.MetricVolumeFloor) {
    request.baselineDays = draft.baselineDays ?? null;
  }
  if (draft.kind === SloKind.MetricGap) {
    request.secondaryMetric = clean(draft.secondaryMetric);
    request.secondaryTagFilter = clean(draft.secondaryTagFilter);
  }
  if (draft.kind === SloKind.MetricGaugeThreshold) {
    request.comparison = draft.comparison ?? SloComparison.AtMost;
    request.gaugeAggregate = draft.gaugeAggregate ?? SloGaugeAggregate.Average;
  }

  return request;
}

/** The editable draft for an existing objective: every metric field is carried over so an edit never drops one. */
export function objectiveToDraft(o: SloObjective): SloUpsertRequest {
  return {
    id: o.id,
    name: o.name,
    kind: o.kind,
    dimension: o.dimension,
    application: o.application,
    targetValue: o.targetValue,
    percentile: o.percentile,
    windowSeconds: o.windowSeconds,
    enabled: o.enabled,
    tagFilter: o.tagFilter ?? null,
    secondaryMetric: o.secondaryMetric ?? null,
    secondaryTagFilter: o.secondaryTagFilter ?? null,
    ratioTag: o.ratioTag ?? null,
    goodValues: o.goodValues ?? null,
    badValues: o.badValues ?? null,
    baselineDays: o.baselineDays ?? null,
    comparison: o.comparison ?? null,
    gaugeAggregate: o.gaugeAggregate ?? null,
  };
}

/** One-line explanation of what a metric kind measures, for the detail page. */
export function describeMetricKind(kind: SloKind): string | null {
  switch (kind) {
    case SloKind.MetricRatio:
      return 'Ratio of good to good + bad events; events whose tag matches neither list are ignored.';
    case SloKind.MetricThreshold:
      return 'Matching events in the window must not exceed the limit (count ≤ N).';
    case SloKind.MetricVolumeFloor:
      return 'Matching events must stay at or above the target fraction of the same window in the baseline period.';
    case SloKind.MetricGap:
      return 'Primary event count minus secondary event count must not exceed the limit (count ≤ N).';
    case SloKind.MetricGaugeThreshold:
      return 'The gauge\'s per-bucket aggregate must stay at most / at least the threshold in at least the compliant share of the window\'s observed time. Samples from several processes and matching series are averaged (or their min / max taken), never summed.';
    case SloKind.WebhookDeliveryLatency:
      return 'At least the percentile share of the deliveries to the endpoint (delivery group, or * for all) created in the window must be delivered within the target minutes of being sent. Exhausted, parked, late and still-pending deliveries count against it; cancelled ones and those younger than the target do not.';
    case SloKind.MetricLatency:
      return 'At least the percentile share of matching histogram events must land in buckets whose upper bound is at most the threshold (in the instrument\'s unit). Thresholds are compared with bucket upper bounds, so resolution is the bucket ladder.';
    default:
      return null;
  }
}

/** "1d" / "7d" label for a baseline, or null. */
export function baselineLabel(days: number | null | undefined): string | null {
  return days == null ? null : `${days} day${days === 1 ? '' : 's'} earlier`;
}

/** True for the kinds whose observed value and target are plain counts (no unit, no %). */
export function isCountKind(kind: SloKind): boolean {
  return kind === SloKind.MetricThreshold || kind === SloKind.MetricGap || kind === SloKind.BacklogDepth;
}
