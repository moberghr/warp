// Pure model behind the Metrics pages: chart datasets, compare-overlay alignment, the ratio view, (rest)
// handling, labels and filter-chip (de)serialisation. No React, no DOM, so every rule is unit-tested; the
// pages only render what comes out of here.

import { MetricKind, MetricTier } from '@/types/metrics';
import { DASHBOARD_LOCALE } from '@/utils/format';
import { unitLabel, withUnit } from '@/lib/metricUnit';
import type { FilterChip } from '@/lib/metricPattern';
import type { MetricInstrument, MetricPoint, MetricSeries, MetricSeriesLine } from '@/types/metrics';
import type { ApplicationDeployModel } from '@/types/applications';

export const REST_NAME = '(rest)';
export const TOTAL_NAME = 'total';

/**
 * The reserved key that addresses the application version as if it were a tag (#330, MetricSeriesQuery.VersionKey):
 * `breakdown={version}` splits by version and a `{version}` filter chip filters by it. Only hosts that opted in with
 * `AddMetrics(m => m.ByVersion())` write a version; every other row reads as `{none}`.
 */
export const VERSION_KEY = '{version}';

/** The breakdown / filter keys offered for an instrument: its tag keys, then the version pseudo-key. */
export function dimensionKeys(tagKeys: string[]): string[] {
  return tagKeys.includes(VERSION_KEY) ? tagKeys : [...tagKeys, VERSION_KEY];
}

/** How a breakdown / filter key reads in a picker. */
export function dimensionLabel(key: string): string {
  return key === VERSION_KEY ? 'application version' : key;
}

// ---------------------------------------------------------------------------------------------------------------
// Labels
// ---------------------------------------------------------------------------------------------------------------

const KIND_LABELS: Record<MetricKind, string> = {
  [MetricKind.Counter]: 'Counter',
  [MetricKind.UpDownCounter]: 'Up-down counter',
  [MetricKind.Histogram]: 'Histogram',
  [MetricKind.Gauge]: 'Gauge',
};

export function kindLabel(kind: MetricKind): string {
  return KIND_LABELS[kind] ?? 'Unknown';
}

/**
 * What a chart of this kind shows when that is not obvious from the kind. An up-down counter is stored as deltas, so
 * each bucket is its net change, not the level; an ObservableGauge is the instrument for a level.
 */
export function kindChartNote(kind: MetricKind): string | null {
  return kind === MetricKind.UpDownCounter
    ? 'Charted as the net change per bucket, not the current level. Record a level with an ObservableGauge.'
    : null;
}

// The one unit formatter (UCUM codes and `{annotation}` counts), re-exported for the metric pages' existing imports.
export { unitLabel, withUnit };

const RESOLUTION_LABELS: Record<MetricTier, string> = {
  [MetricTier.Fine]: 'Fine',
  [MetricTier.Hourly]: 'Hourly',
  [MetricTier.Daily]: 'Daily',
};

export function resolutionLabel(tier: MetricTier): string {
  return RESOLUTION_LABELS[tier] ?? 'Unknown';
}

export function collapseTooltip(instrument: Pick<MetricInstrument, 'collapsedTags'>): string {
  const tags = instrument.collapsedTags.length > 0 ? ` (${instrument.collapsedTags.join(', ')})` : '';

  return `Some tag values were collapsed into {other}${tags}`;
}

export function percentileLabel(percentile: number): string {
  return `p${percentile}`;
}

/** Histogram percentiles are bucket upper bounds, so they read as "at most". */
export function formatPercentileValue(value: number | null | undefined, unit: string | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return '—';
  }

  return `≤ ${withUnit(formatNumber(value), unit, value)}`;
}

export function formatNumber(value: number): string {
  if (!Number.isFinite(value)) {
    return '—';
  }

  const abs = Math.abs(value);
  if (abs >= 1000 || Number.isInteger(value)) {
    return Math.round(value).toLocaleString(DASHBOARD_LOCALE);
  }

  return abs >= 10 ? value.toFixed(1) : value.toFixed(3).replace(/\.?0+$/, '');
}

/** One line per overlay offset: "Current: Fine · 1d: Hourly" — so a reader knows an overlay is hour-resolution. */
export function resolutionSummary(series: MetricSeries): string {
  const seen = new Map<string, MetricTier>([['Current', series.resolution]]);
  for (const line of series.series) {
    if (line.compare !== null && !seen.has(line.compare)) {
      seen.set(line.compare, line.resolution);
    }
  }

  return [...seen.entries()].map(([key, tier]) => `${key}: ${resolutionLabel(tier)}`).join(' · ');
}

// ---------------------------------------------------------------------------------------------------------------
// Controls and the API query
// ---------------------------------------------------------------------------------------------------------------

export const RANGES = [
  { id: '1h', label: '1h', ms: 3_600_000 },
  { id: '6h', label: '6h', ms: 6 * 3_600_000 },
  { id: '24h', label: '24h', ms: 24 * 3_600_000 },
  { id: '7d', label: '7d', ms: 7 * 24 * 3_600_000 },
  { id: '30d', label: '30d', ms: 30 * 24 * 3_600_000 },
] as const;
export type RangeId = (typeof RANGES)[number]['id'];

/** The series API's limit on `to - from` (IMetricQueryService.MaxRangeDays). */
export const MAX_RANGE_MS = 90 * 24 * 3_600_000;

/** The shortest window a custom range resolves to, so a start at (or a skewed clock just past) now still reads. */
const MIN_RANGE_MS = 60_000;

/**
 * A custom window in epoch milliseconds. `to` null means "until now": the window keeps moving like a preset, which is
 * what "since last deploy" wants.
 */
export interface CustomRange {
  from: number;
  to: number | null;
}

export const PERCENTILES = [50, 90, 95, 99] as const;

/**
 * Rate units for counters and histogram counts: each bucket divided by its length. Per bucket is the stored value, and
 * changes scale with the bucket size (5 min up to 6h, 1 hour up to 7d, 1 day beyond); a rate does not.
 */
export const RATES = [
  { id: 'bucket', label: 'Per bucket', seconds: null },
  { id: 'minute', label: 'Per minute', seconds: 60 },
  { id: 'second', label: 'Per second', seconds: 1 },
] as const;
export type RateId = (typeof RATES)[number]['id'];

/** The `rate` URL parameter: anything but `minute` / `second` (or none) is per bucket, the default. */
export function parseRate(value: string | null | undefined): RateId {
  return value === 'minute' || value === 'second' ? value : 'bucket';
}

/** The `rate` URL parameter for a choice: none for the default, so a plain link stays plain. */
export function rateParam(rate: RateId): string | null {
  return rate === 'bucket' ? null : rate;
}

function rateSeconds(rate: RateId): number | null {
  return RATES.find((x) => x.id === rate)?.seconds ?? null;
}

/** Counters count events and histograms count measurements; up-down counters and gauges have no rate. */
export function supportsRate(kind: MetricKind): boolean {
  return kind === MetricKind.Counter || kind === MetricKind.Histogram;
}

/** The rate that applies: per bucket for a kind without one, and for the ratio view (the bucket length cancels). */
export function effectiveRate(kind: MetricKind, rate: RateId, ratio: boolean): RateId {
  return ratio || !supportsRate(kind) ? 'bucket' : rate;
}

/** A bucket's value as a rate: value × unit ÷ bucket length. Null when the bucket length is unknown. */
export function rateValue(value: number | null, bucketSeconds: number | null | undefined, rate: RateId): number | null {
  const unit = rateSeconds(rate);
  if (value === null || unit === null) {
    return value;
  }

  if (bucketSeconds === null || bucketSeconds === undefined || !Number.isFinite(bucketSeconds) || bucketSeconds <= 0) {
    return null;
  }

  return (value * unit) / bucketSeconds;
}

function rateSuffix(rate: RateId): string {
  return rate === 'second' ? 'per second' : 'per minute';
}

export interface MetricControls {
  range: RangeId;
  /** A custom window; when set it replaces `range`. */
  custom: CustomRange | null;
  application: string | null;
  breakdown: string | null;
  filters: FilterChip[];
  compareDay: boolean;
  compareWeek: boolean;
  /** Histograms only; null skips the percentile series. */
  percentile: number | null;
  view: 'values' | 'ratio';
  /**
   * Ratio numerator: one breakdown value or `prefix*` pattern (null until picked). One pattern, not a set, because
   * the numerator is its own series request filtered on the breakdown tag and the API holds one filter value per
   * key; summing several requests would double-count values two patterns share (`5*` and `503`).
   */
  numerator: string | null;
  /** Gauges only: also draw the min and max lines. */
  showMinMax: boolean;
}

export const DEFAULT_CONTROLS: MetricControls = {
  range: '24h',
  custom: null,
  application: null,
  breakdown: null,
  filters: [],
  compareDay: false,
  compareWeek: false,
  percentile: null,
  view: 'values',
  numerator: null,
  showMinMax: false,
};

export function rangeMs(range: RangeId): number {
  return RANGES.find((x) => x.id === range)?.ms ?? RANGES[2].ms;
}

/** The window a read covers, resolved against a clock. `live` = its end moves with now; `clamped` = cut to 90 days. */
export interface MetricWindow {
  from: number;
  to: number;
  live: boolean;
  clamped: boolean;
}

/**
 * The [from, to) a read asks for. A preset ends now; a custom range is used as given, except that its start is cut to
 * the API's 90-day limit (an old "since deploy") and kept at least a minute before its end, so the read is never a 400.
 */
export function resolveWindow(controls: Pick<MetricControls, 'range' | 'custom'>, now: number): MetricWindow {
  if (controls.custom === null) {
    return { from: now - rangeMs(controls.range), to: now, live: true, clamped: false };
  }

  const to = controls.custom.to ?? now;
  const from = Math.min(Math.max(controls.custom.from, to - MAX_RANGE_MS), to - MIN_RANGE_MS);

  return { from, to, live: controls.custom.to === null || controls.custom.to > now, clamped: from > controls.custom.from };
}

/**
 * Why a custom range cannot be applied, or null when it can. `to` null is "until now". The limits are the API's: the
 * start before the end, at most 90 days apart.
 */
export function validateCustomRange(from: number, to: number | null, now: number): string | null {
  if (!Number.isFinite(from)) {
    return 'Pick a start time.';
  }

  if (to !== null && !Number.isFinite(to)) {
    return 'Pick an end time, or leave it empty for now.';
  }

  const end = to ?? now;
  if (from >= end) {
    return to === null ? 'The start must be in the past.' : 'The start must be before the end.';
  }

  return end - from > MAX_RANGE_MS ? 'A range can span at most 90 days.' : null;
}

/**
 * How often a series read refetches: as often as its display resolution can change. The server picks the
 * resolution from the window length (up to 6 hours fine, up to 7 days hourly, longer daily), so a fine chart refetches
 * every minute, an hourly one every 5 minutes and a daily one every 30. A window that ended in the past never changes,
 * so it is not refetched at all.
 */
export function seriesRefetchMs(span: MetricWindow): number | false {
  if (!span.live) {
    return false;
  }

  const ms = span.to - span.from;
  if (ms <= 6 * 3_600_000) {
    return 60_000;
  }

  return ms <= 7 * 24 * 3_600_000 ? 5 * 60_000 : 30 * 60_000;
}

// The range lives in the URL so a shared link reproduces the view: `range=<preset>` (left out for the default),
// or `from=<ISO>[&to=<ISO>]` for a custom window, with no `to` meaning "until now".

export interface RangeSelection {
  range: RangeId;
  custom: CustomRange | null;
}

function parseInstant(value: string | null): number | null {
  if (!value) {
    return null;
  }

  const ms = Date.parse(value);

  return Number.isFinite(ms) ? ms : null;
}

/** Reads the range from the URL. An unparseable `from` falls back to the preset, an unparseable `to` to "until now". */
export function parseRangeParams(params: URLSearchParams): RangeSelection {
  const preset = RANGES.find((x) => x.id === params.get('range'))?.id ?? DEFAULT_CONTROLS.range;
  const from = parseInstant(params.get('from'));
  if (from === null) {
    return { range: preset, custom: null };
  }

  return { range: preset, custom: { from, to: parseInstant(params.get('to')) } };
}

/** A copy of `params` carrying `selection` and every other parameter unchanged. */
export function writeRangeParams(params: URLSearchParams, selection: RangeSelection): URLSearchParams {
  const next = new URLSearchParams(params);
  next.delete('range');
  next.delete('from');
  next.delete('to');

  if (selection.custom !== null) {
    next.set('from', new Date(selection.custom.from).toISOString());
    if (selection.custom.to !== null) {
      next.set('to', new Date(selection.custom.to).toISOString());
    }

    return next;
  }

  if (selection.range !== DEFAULT_CONTROLS.range) {
    next.set('range', selection.range);
  }

  return next;
}

function pad(value: number): string {
  return String(value).padStart(2, '0');
}

/** An instant as a `<input type="datetime-local">` value, in the browser's time zone, to the minute. */
export function toLocalInputValue(ms: number): string {
  const d = new Date(ms);

  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** A `<input type="datetime-local">` value as an instant (NaN when empty or invalid). */
export function fromLocalInputValue(value: string): number {
  return value ? new Date(value).getTime() : Number.NaN;
}

/** The custom window as text: `13 Nov, 10:42 → now`. */
export function customRangeLabel(custom: CustomRange): string {
  const format = (ms: number) =>
    new Date(ms).toLocaleString(DASHBOARD_LOCALE, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

  return `${format(custom.from)} → ${custom.to === null ? 'now' : format(custom.to)}`;
}

// ---------------------------------------------------------------------------------------------------------------
// Deploy markers (#325 deploy history on the chart)
// ---------------------------------------------------------------------------------------------------------------

export interface DeployMarker {
  time: number;
  label: string;
}

/**
 * Vertical chart markers for the deploys inside the window, oldest first. With one application in view the label is
 * the version; across applications it names the application too. The environment is added only when the deploys in
 * view span more than one, so the common single-environment chart stays terse.
 */
export function deployMarkers(
  deploys: ApplicationDeployModel[],
  span: Pick<MetricWindow, 'from' | 'to'>,
  application: string | null,
): DeployMarker[] {
  const inView = deploys
    .map((x) => ({ deploy: x, time: Date.parse(x.firstSeenAt) }))
    .filter((x) => Number.isFinite(x.time) && x.time >= span.from && x.time <= span.to)
    .filter((x) => application === null || x.deploy.application === application)
    .sort((a, b) => a.time - b.time);
  const environments = new Set(inView.map((x) => x.deploy.environment ?? ''));

  return inView.map(({ deploy, time }) => {
    const name = application === null ? `${deploy.application} ${deploy.version}` : deploy.version;
    const label = environments.size > 1 && deploy.environment ? `${name} (${deploy.environment})` : name;

    return { time, label };
  });
}

/**
 * The deploy "since last deploy" starts from: the newest of `deploys`, which the caller has already scoped to the
 * selected application, or to every application when none is selected. Null when there is none.
 */
export function latestDeploy(deploys: ApplicationDeployModel[] | undefined): ApplicationDeployModel | null {
  let latest: ApplicationDeployModel | null = null;
  for (const deploy of deploys ?? []) {
    if (latest === null || Date.parse(deploy.firstSeenAt) > Date.parse(latest.firstSeenAt)) {
      latest = deploy;
    }
  }

  return latest;
}

/**
 * The "Since last deploy" tooltip. A failed deploy read says so rather than "no deploy", which would claim the
 * application was never deployed. `scoped` = the read was limited to the selected application.
 */
export function sinceDeployHint(deploy: ApplicationDeployModel | null, failed: boolean, scoped: boolean): string {
  if (failed) {
    return 'Unable to load deploys';
  }

  if (deploy) {
    return `From the ${deploy.application} ${deploy.version} deploy`;
  }

  return scoped ? 'No deploy recorded for this selection' : 'No deploy recorded';
}

/** The custom range "since last deploy" selects: from the deploy, open-ended to now. */
export function sinceDeployRange(deploy: ApplicationDeployModel): CustomRange {
  return { from: Date.parse(deploy.firstSeenAt), to: null };
}

export function supportsRatio(kind: MetricKind): boolean {
  return kind === MetricKind.Counter || kind === MetricKind.Histogram;
}

export function supportsPercentile(kind: MetricKind): boolean {
  return kind === MetricKind.Histogram;
}

/** Ratio needs a breakdown to take a numerator from. */
export function isRatioActive(controls: MetricControls, kind: MetricKind): boolean {
  return controls.view === 'ratio' && supportsRatio(kind) && controls.breakdown !== null;
}

/**
 * The query string for GET /api/metrics/{id}/series. Built by hand rather than through axios `params`
 * because `filter` repeats and axios would serialise the array as `filter[]=`.
 */
export function buildSeriesQuery(controls: MetricControls, kind: MetricKind | null, now: number): string {
  const span = resolveWindow(controls, now);
  const parts: [string, string][] = [
    ['from', new Date(span.from).toISOString()],
    ['to', new Date(span.to).toISOString()],
  ];

  if (controls.application) {
    parts.push(['application', controls.application]);
  }

  if (controls.breakdown) {
    parts.push(['breakdown', controls.breakdown]);
  }

  for (const chip of controls.filters) {
    parts.push(['filter', chipToQuery(chip)]);
  }

  const compare = [controls.compareDay ? '1d' : null, controls.compareWeek ? '7d' : null].filter((x) => x !== null);
  if (compare.length > 0) {
    parts.push(['compare', compare.join(',')]);
  }

  if (controls.percentile !== null && kind === MetricKind.Histogram && controls.view === 'values') {
    parts.push(['percentile', String(controls.percentile)]);
  }

  return parts.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`).join('&');
}

/**
 * The ratio numerator's own series request: the current controls with no breakdown, plus
 * `filter=<breakdown>:<pattern>`, so values the primary read folded into `(rest)` still count. Null when the ratio
 * has no breakdown or no numerator. A filter chip already on the breakdown key is replaced (one value per key).
 */
export function buildNumeratorQuery(controls: MetricControls, kind: MetricKind | null, now: number): string | null {
  if (controls.breakdown === null || controls.numerator === null) {
    return null;
  }

  return buildSeriesQuery(
    {
      ...controls,
      breakdown: null,
      filters: upsertChip(controls.filters, { key: controls.breakdown, value: controls.numerator }),
      percentile: null,
      view: 'values',
    },
    kind,
    now
  );
}

// ---------------------------------------------------------------------------------------------------------------
// Query keys: every input a request reads is in its key, so a change of window, filter or numerator refetches instead
// of reusing the previous window's data. metricModel.test.ts pins that for every control.
// ---------------------------------------------------------------------------------------------------------------

/** The series request's key: every control buildSeriesQuery reads (the percentile only where it is sent). */
export function seriesQueryKey(name: string | null, controls: MetricControls, kind: MetricKind | null) {
  return ['metrics', 'series', name, controls.range, controls.custom, controls.application, controls.breakdown, controls.filters,
    controls.compareDay, controls.compareWeek, controls.view === 'values' ? controls.percentile : null, kind] as const;
}

/** The ratio numerator's key: every control buildNumeratorQuery reads, the custom window included. */
export function numeratorQueryKey(name: string | null, controls: MetricControls, kind: MetricKind | null) {
  return ['metrics', 'series', 'numerator', name, controls.range, controls.custom, controls.application, controls.breakdown,
    controls.filters, controls.compareDay, controls.compareWeek, controls.numerator, kind] as const;
}

/** The deploys drawn as chart markers: the selected application (or all) inside the window. */
export function deploysRequest(controls: Pick<MetricControls, 'range' | 'custom' | 'application'>, now: number) {
  const w = resolveWindow(controls, now);

  return { application: controls.application ?? undefined, from: new Date(w.from).toISOString(), to: new Date(w.to + 1).toISOString() };
}

export function deploysQueryKey(controls: Pick<MetricControls, 'range' | 'custom' | 'application'>) {
  return ['metrics', 'deploys', controls.application, controls.range, controls.custom] as const;
}

/** The instrument's notes overlapping the window, whatever their filter. */
export function annotationsRequest(name: string, controls: Pick<MetricControls, 'range' | 'custom'>, now: number) {
  const w = resolveWindow(controls, now);

  return { instrument: name, from: new Date(w.from).toISOString(), to: new Date(w.to + 1).toISOString() };
}

export function annotationsQueryKey(name: string | null, controls: Pick<MetricControls, 'range' | 'custom'>) {
  return ['annotations', 'instrument', name, controls.range, controls.custom] as const;
}

// ---------------------------------------------------------------------------------------------------------------
// Filter chips: `key=value` in the UI (parsed by lib/metricPattern), `key:value` on the wire
// ---------------------------------------------------------------------------------------------------------------

export function chipToText(chip: FilterChip): string {
  return `${chip.key}=${chip.value}`;
}

export function chipToQuery(chip: FilterChip): string {
  return `${chip.key}:${chip.value}`;
}

export function chipFromQuery(text: string): FilterChip | null {
  const separator = text.indexOf(':');
  if (separator <= 0 || separator === text.length - 1) {
    return null;
  }

  return { key: text.slice(0, separator), value: text.slice(separator + 1) };
}

/** The API holds one value per key, so adding a chip replaces an existing one on the same key. */
export function upsertChip(chips: FilterChip[], chip: FilterChip): FilterChip[] {
  return [...chips.filter((x) => x.key !== chip.key), chip];
}

export function removeChip(chips: FilterChip[], key: string): FilterChip[] {
  return chips.filter((x) => x.key !== key);
}

// ---------------------------------------------------------------------------------------------------------------
// Numerator (pattern matching itself lives in lib/metricPattern, the mirror of the server's MetricValuePattern)
// ---------------------------------------------------------------------------------------------------------------

/** Picking the selected numerator again clears it; picking another replaces it. */
export function toggleNumerator(numerator: string | null, value: string): string | null {
  return numerator === value ? null : value;
}

// ---------------------------------------------------------------------------------------------------------------
// Series -> datasets
// ---------------------------------------------------------------------------------------------------------------

export type DatasetRole = 'value' | 'percentile' | 'min' | 'max' | 'ratio';

export interface ChartPoint {
  x: number;
  y: number | null;
  /** The current range's still-filling bucket: drawn faded and dashed, and left out of the headline. */
  incomplete?: boolean;
}

export interface DatasetSpec {
  id: string;
  label: string;
  role: DatasetRole;
  lineName: string;
  compare: string | null;
  resolution: MetricTier;
  /** Index into the page palette; -1 for `(rest)`. Overlays share their primary's index (same colour family). */
  colorIndex: number;
  dashed: boolean;
  axis: 'y' | 'y1';
  data: ChartPoint[];
}

function isRest(name: string): boolean {
  return name === REST_NAME;
}

/** The y value of a point for the instrument kind: sum, count or average. */
export function pointValue(kind: MetricKind, point: MetricPoint): number {
  switch (kind) {
    case MetricKind.Counter:
    case MetricKind.UpDownCounter:
      return point.sum;
    case MetricKind.Histogram:
      return point.count;
    case MetricKind.Gauge:
      return point.value;
    default:
      return point.value;
  }
}

function toMs(timestamp: string): number {
  return new Date(timestamp).getTime();
}

function sortedPoints(line: MetricSeriesLine): MetricPoint[] {
  return line.points
    .filter((x) => Number.isFinite(toMs(x.timestamp)))
    .sort((a, b) => toMs(a.timestamp) - toMs(b.timestamp));
}

/**
 * Breakdown values of the current range in the SERVER's order (ranked by |headline value over the range|, ties by
 * ordinal value — the same ranking that picked the top 10), `(rest)` last. Excludes overlays. Not re-sorted here, so
 * the page and the API can never disagree about which value is "biggest". This is the order lines are coloured and
 * listed in, so an overlay can borrow its primary's colour by name.
 */
export function orderedLineNames(series: MetricSeries): string[] {
  const current = series.series.filter((x) => x.compare === null);
  const named = [...new Set(current.filter((x) => !isRest(x.name)).map((x) => x.name))];

  return current.some((x) => isRest(x.name)) ? [...named, REST_NAME] : named;
}

/** The breakdown values a ratio numerator can be picked from: real tag values, never `(rest)` or `total`. */
export function breakdownValues(series: MetricSeries | null | undefined): string[] {
  if (!series) {
    return [];
  }

  return orderedLineNames(series).filter((x) => !isRest(x) && x !== TOTAL_NAME);
}

function colorIndexFor(names: string[], name: string): number {
  if (isRest(name)) {
    return -1;
  }

  const index = names.filter((x) => !isRest(x)).indexOf(name);

  return index < 0 ? names.filter((x) => !isRest(x)).length : index;
}

/** Legend text: name, overlay offset, optional series suffix, and the resolution the line was served at. */
function lineLabel(line: MetricSeriesLine, suffix?: string): string {
  const base = line.compare ? `${line.name} (−${line.compare})` : line.name;

  return `${base}${suffix ? ` ${suffix}` : ''} · ${resolutionLabel(line.resolution)}`;
}

/**
 * Overlay alignment. The API already shifts an overlay's timestamps forward by its offset, so an overlay
 * point lines up with a primary point at the same instant. This verifies that: when the overlay was served
 * at the same resolution as the primary, a primary timestamp the overlay has no point for becomes a null so
 * the line breaks instead of drawing a straight segment across missing data. An overlay served at a coarser
 * tier (hourly against fine) keeps its own timestamps — snapping those onto the primary grid would invent
 * points.
 */
export function alignOverlay(
  overlayPoints: ChartPoint[],
  primaryTimestamps: number[],
  sameResolution: boolean
): ChartPoint[] {
  if (!sameResolution || primaryTimestamps.length === 0) {
    return overlayPoints;
  }

  const byTime = new Map(overlayPoints.map((x) => [x.x, x]));
  const aligned: ChartPoint[] = primaryTimestamps.map((t) => byTime.get(t) ?? { x: t, y: null });
  const primarySet = new Set(primaryTimestamps);
  const extras = overlayPoints.filter((x) => !primarySet.has(x.x));

  return [...aligned, ...extras].sort((a, b) => a.x - b.x);
}

function primaryTimestampsOf(series: MetricSeries, name: string): number[] {
  const line = series.series.find((x) => x.compare === null && x.name === name);

  return line ? sortedPoints(line).map((x) => toMs(x.timestamp)) : [];
}

function primaryResolutionOf(series: MetricSeries, name: string): MetricTier | null {
  return series.series.find((x) => x.compare === null && x.name === name)?.resolution ?? null;
}

export interface DatasetOptions {
  /** Histograms: draw the percentile the response carries. */
  percentile: number | null;
  /** Gauges: also draw min and max. */
  showMinMax: boolean;
  /** Counters and histogram counts: divide each bucket by its length. Per bucket when absent. */
  rate?: RateId;
}

/** One dataset per line (top 10 + `(rest)`), plus overlays dashed in the same colour, plus percentile / min / max. */
export function buildDatasets(series: MetricSeries, options: DatasetOptions): DatasetSpec[] {
  const names = orderedLineNames(series);
  const rank = (line: MetricSeriesLine) => {
    const index = names.indexOf(line.name);

    return index < 0 ? names.length : index;
  };
  const lines = [...series.series].sort(
    (a, b) => Number(a.compare !== null) - Number(b.compare !== null) || (a.compare ?? '').localeCompare(b.compare ?? '') || rank(a) - rank(b)
  );

  return lines.flatMap((line) => {
    const colorIndex = colorIndexFor(names, line.name);
    const dashed = line.compare !== null;
    const points = sortedPoints(line);
    const sameResolution = line.compare === null || primaryResolutionOf(series, line.name) === line.resolution;
    const primaryTimes = line.compare === null ? [] : primaryTimestampsOf(series, line.name);
    const idBase = `${line.compare ?? 'now'}|${line.name}`;

    const build = (role: DatasetRole, pick: (p: MetricPoint) => number | null, axis: 'y' | 'y1', suffix?: string) => {
      const data = alignOverlay(
        points.map((p) => {
          const y = pick(p);

          return { x: toMs(p.timestamp), y: y !== null && Number.isFinite(y) ? y : null, incomplete: p.incomplete === true };
        }),
        primaryTimes,
        sameResolution
      );
      const spec: DatasetSpec = {
        id: `${idBase}|${role}`,
        label: lineLabel(line, suffix),
        role,
        lineName: line.name,
        compare: line.compare,
        resolution: line.resolution,
        colorIndex,
        dashed,
        axis,
        data,
      };

      return spec;
    };

    const rate = effectiveRate(series.kind, options.rate ?? 'bucket', false);
    const result: DatasetSpec[] = [build('value', (p) => rateValue(pointValue(series.kind, p), p.bucketSeconds, rate), 'y')];

    if (series.kind === MetricKind.Histogram && options.percentile !== null) {
      result.push(build('percentile', (p) => p.percentile, 'y1', percentileLabel(options.percentile)));
    }

    if (series.kind === MetricKind.Gauge && options.showMinMax && line.compare === null) {
      result.push(build('min', (p) => p.min, 'y', 'min'), build('max', (p) => p.max, 'y', 'max'));
    }

    return result;
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Ratio: numerator / total per bucket
// ---------------------------------------------------------------------------------------------------------------

/**
 * numerator ÷ total per bucket, as a percentage. A bucket whose total is zero (or not finite) is null —
 * never NaN or Infinity — so the chart leaves a gap instead of drawing a lie.
 */
export function ratioPercent(numerator: number, total: number): number | null {
  if (!Number.isFinite(numerator) || !Number.isFinite(total) || total === 0) {
    return null;
  }

  const ratio = (numerator / total) * 100;

  return Number.isFinite(ratio) ? ratio : null;
}

/** The bucket timestamps the current range flags as still filling (any line). */
export function incompleteTimes(series: MetricSeries): Set<number> {
  return new Set(
    series.series
      .filter((x) => x.compare === null)
      .flatMap((x) => x.points)
      .filter((x) => x.incomplete === true)
      .map((x) => toMs(x.timestamp))
      .filter((x) => Number.isFinite(x))
  );
}

/** Per-bucket sum of every line of one compare offset (null = the current range). */
function sumByBucket(series: MetricSeries, compare: string | null): Map<number, number> {
  const sums = new Map<number, number>();
  for (const line of series.series.filter((x) => x.compare === compare)) {
    for (const point of line.points) {
      const t = toMs(point.timestamp);
      if (Number.isFinite(t)) {
        sums.set(t, (sums.get(t) ?? 0) + pointValue(series.kind, point));
      }
    }
  }

  return sums;
}

/**
 * The ratio datasets: for the current range and each overlay, numerator ÷ total at each bucket.
 *
 * - The TOTAL is the primary (breakdown) response summed across every line, `(rest)` included — the breakdown
 *   partitions the filtered rows, so its lines add up to the series total and no extra request is needed.
 * - The NUMERATOR is `numeratorSeries`, the separate no-breakdown request filtered on `breakdown:<pattern>`
 *   (`buildNumeratorQuery`). It is NOT picked out of the breakdown lines: a low-volume value (the 5xx in "5* over
 *   total") lands in `(rest)` and would read 0% — a false green.
 *
 * A bucket with a zero total is null, never NaN; a bucket the numerator read has no point for counts 0.
 */
export function buildRatioDatasets(series: MetricSeries, numeratorSeries: MetricSeries | null | undefined, numerator: string | null): DatasetSpec[] {
  if (numerator === null || !numeratorSeries) {
    return [];
  }

  const compares = [...new Set(series.series.map((x) => x.compare))].sort((a, b) => (a ?? '').localeCompare(b ?? ''));

  return compares.flatMap((compare) => {
    const lines = series.series.filter((x) => x.compare === compare);
    if (lines.length === 0) {
      return [];
    }

    const filling = compare === null ? incompleteTimes(series) : new Set<number>();
    const totals = sumByBucket(series, compare);
    const numerators = sumByBucket(numeratorSeries, compare);
    for (const t of numerators.keys()) {
      if (!totals.has(t)) {
        totals.set(t, 0);
      }
    }

    const resolution = Math.max(...lines.map((x) => x.resolution)) as MetricTier;
    const primary = series.series.filter((x) => x.compare === null);
    const primaryTimes = [...new Set(primary.flatMap((l) => l.points.map((p) => toMs(p.timestamp))))]
      .filter((x) => Number.isFinite(x))
      .sort((a, b) => a - b);
    const points: ChartPoint[] = [...totals.entries()]
      .sort((a, b) => a[0] - b[0])
      .map(([x, total]) => ({ x, y: ratioPercent(numerators.get(x) ?? 0, total), incomplete: filling.has(x) }));
    const spec: DatasetSpec = {
      id: `${compare ?? 'now'}|ratio`,
      label: `${numerator} ÷ total${compare ? ` (−${compare})` : ''} · ${resolutionLabel(resolution)}`,
      role: 'ratio',
      lineName: numerator,
      compare,
      resolution,
      colorIndex: 0,
      dashed: compare !== null,
      axis: 'y',
      data: alignOverlay(
        points,
        compare === null ? [] : primaryTimes,
        compare === null || (primary.length > 0 && Math.max(...primary.map((x) => x.resolution)) === resolution)
      ),
    };

    return [spec];
  });
}

/** y-axis titles: what the left and right axes mean for this kind / view. */
export function axisTitles(
  kind: MetricKind,
  unit: string | null | undefined,
  ratio: boolean,
  rate: RateId = 'bucket'
): { y: string; y1: string } {
  const suffix = unitLabel(unit);
  if (ratio) {
    return { y: 'Share of total (%)', y1: '' };
  }

  if (effectiveRate(kind, rate, ratio) !== 'bucket') {
    const per = rateSuffix(rate);
    if (kind === MetricKind.Histogram) {
      return { y: `Measurements ${per}`, y1: suffix ? `Percentile (${suffix})` : 'Percentile' };
    }

    return { y: suffix ? `Sum ${per} (${suffix})` : `Sum ${per}`, y1: '' };
  }

  switch (kind) {
    case MetricKind.Histogram:
      return { y: 'Measurements', y1: suffix ? `Percentile (${suffix})` : 'Percentile' };
    case MetricKind.Gauge:
      return { y: suffix ? `Average (${suffix})` : 'Average', y1: '' };
    case MetricKind.UpDownCounter:
      // Stored as deltas, so a bucket is the net change inside it, never the level (an in-flight count hovers near 0).
      return { y: suffix ? `Net change (${suffix})` : 'Net change', y1: '' };
    default:
      return { y: suffix ? `Sum (${suffix})` : 'Sum', y1: '' };
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Headline: the range value over its complete buckets, and the 1d / 7d comparisons
// ---------------------------------------------------------------------------------------------------------------

export interface HeadlineComparison {
  compare: string;
  value: number | null;
  /** Change of the current value against this overlay, in percent; null when either side has no value or it is 0. */
  changePercent: number | null;
}

export interface HeadlineSummary {
  label: string;
  value: number | null;
  comparisons: HeadlineComparison[];
  /** The current range has a still-filling bucket, which the value leaves out. */
  excludesIncomplete: boolean;
}

/** What the headline of this kind is: the server sums counters, counts histograms and averages gauges. */
export function headlineLabel(kind: MetricKind): string {
  switch (kind) {
    case MetricKind.UpDownCounter:
      return 'Net change';
    case MetricKind.Histogram:
      return 'Measurements';
    case MetricKind.Gauge:
      return 'Average';
    default:
      return 'Total';
  }
}

/** (current - previous) / |previous| in percent; null when it is not defined (a missing side, or a zero baseline). */
export function changePercent(current: number | null, previous: number | null): number | null {
  if (current === null || previous === null || !Number.isFinite(current) || !Number.isFinite(previous) || previous === 0) {
    return null;
  }

  return ((current - previous) / Math.abs(previous)) * 100;
}

export function formatChange(percent: number | null): string {
  if (percent === null) {
    return '—';
  }

  const rounded = Math.abs(percent) >= 10 ? Math.round(percent) : Math.round(percent * 10) / 10;

  return `${rounded > 0 ? '+' : ''}${rounded}%`;
}

/**
 * The headline strip from the server's headlines (complete buckets only — the still-filling bucket is left out, never
 * prorated, and each overlay leaves out the bucket aligned with it). Null when the response carries none.
 */
export function headlineSummary(series: MetricSeries, rate: RateId = 'bucket'): HeadlineSummary | null {
  const headlines = series.headlines ?? [];
  const current = headlines.find((x) => x.compare === null);
  if (!current) {
    return null;
  }

  const effective = effectiveRate(series.kind, rate, false);
  const span = effective === 'bucket' ? null : headlineSpanSeconds(series);
  const scale = (value: number | null) => (effective === 'bucket' ? value : rateValue(value, span, effective));
  const value = scale(current.value);

  return {
    label: effective === 'bucket' ? headlineLabel(series.kind) : `Average ${rateSuffix(effective)}`,
    value,
    comparisons: headlines
      .filter((x): x is typeof x & { compare: string } => x.compare !== null)
      .map((x) => {
        const previous = scale(x.value);

        return { compare: x.compare, value: previous, changePercent: changePercent(value, previous) };
      }),
    excludesIncomplete: incompleteTimes(series).size > 0,
  };
}

/**
 * The span a headline covers, for its rate: from the range start (or the first bucket's start, which floors to the
 * bucket and can sit before it) to where the complete buckets end. Every overlay covers the same span shifted back.
 * Null when it is not positive.
 */
export function headlineSpanSeconds(series: MetricSeries): number | null {
  const firstPoint = Math.min(
    ...series.series
      .filter((x) => x.compare === null)
      .flatMap((x) => x.points)
      .map((x) => toMs(x.timestamp))
      .filter((x) => Number.isFinite(x))
  );
  const start = Math.min(toMs(series.from), firstPoint);
  const end = toMs(series.completeUntil);
  const seconds = (end - start) / 1000;

  return Number.isFinite(seconds) && seconds > 0 ? seconds : null;
}
