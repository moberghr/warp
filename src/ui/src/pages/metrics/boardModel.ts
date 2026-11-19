// Pure model behind the metrics health board (#326): tile value / change / rule labels, status colours, sparkline
// points, and the tile editor's draft logic. Mirrors the client-checkable rules of Warp.Core.Metrics.MetricBoardValidation
// so most mistakes show before a round trip; the server stays the authority and its 400 message is always shown.
import { DateTime } from 'luxon';
import { MetricKind } from '@/types/metrics';
import {
  MetricDeviationDirection,
  MetricTileKind,
  MetricTileStatusRule,
  type MetricBoard,
  type MetricBoardTile,
  type MetricBoardTileResult,
} from '@/types/metricBoard';
import { SloComparison, SloGaugeAggregate, SloGaugeAggregateLabel, SloState, SloStateLabel } from '@/types/slo';
import { parseValueList, validateTagFilter, valueListsOverlap } from '@/pages/slo/sloForm';
import { withUnit } from '@/lib/metricUnit';
import { changePercent, formatChange, formatNumber } from './metricModel';
import { describeChartQuery } from './chartTileModel';

/** The most tiles a board holds (MetricBoardValidation.MaxTiles). */
export const MAX_TILES = 48;

// ---------------------------------------------------------------------------------------------------------------
// Display
// ---------------------------------------------------------------------------------------------------------------

function isRatio(tile: Pick<MetricBoardTile, 'kind'>): boolean {
  return tile.kind === MetricTileKind.Ratio;
}

/** A chart tile: the metric page's chart over the board's range, with no value and no status rule. */
export function isChartTile(tile: Pick<MetricBoardTile, 'kind'>): boolean {
  return tile.kind === MetricTileKind.Chart;
}

function percent(fraction: number, digits = 2): string {
  return `${(fraction * 100).toFixed(digits)}%`;
}

/**
 * A tile value as text: a ratio as a percentage, otherwise the number with the instrument's unit — except a histogram
 * value tile, which counts measurements, so the unit (of the measurement) would mislead.
 */
export function formatTileValue(result: Pick<MetricBoardTileResult, 'tile' | 'instrumentKind' | 'unit'>, value: number | null): string {
  if (value === null || !Number.isFinite(value)) {
    return '—';
  }

  if (isRatio(result.tile)) {
    return percent(value);
  }

  return withUnit(formatNumber(value), result.instrumentKind === MetricKind.Histogram ? null : result.unit, value);
}

/** The move from a baseline: ratio points for a ratio tile (`+1.20 pts`), a percentage change otherwise. */
export function formatTileChange(tile: Pick<MetricBoardTile, 'kind'>, current: number | null, previous: number | null): string {
  if (isRatio(tile)) {
    if (current === null || previous === null) {
      return '—';
    }

    const points = (current - previous) * 100;

    return `${points > 0 ? '+' : points < 0 ? '−' : '±'}${Math.abs(points).toFixed(2)} pts`;
  }

  return formatChange(changePercent(current, previous));
}

/** What the tile reads, in one line: `payment.results · result: ok / declined,error · region=eu · @shop`. */
export function describeTile(tile: MetricBoardTile): string {
  const parts = [tile.instrument];
  if (isChartTile(tile)) {
    const controls = describeChartQuery(tile.chartQuery);

    return controls === '' ? tile.instrument : `${tile.instrument} · ${controls}`;
  }

  if (isRatio(tile)) {
    parts.push(`${tile.ratioTag}: ${tile.goodValues} / ${tile.badValues}`);
  } else if (tile.gaugeAggregate) {
    parts.push(SloGaugeAggregateLabel[tile.gaugeAggregate]);
  }

  if (tile.tagFilter) {
    parts.push(tile.tagFilter);
  }

  if (tile.application) {
    parts.push(`@${tile.application}`);
  }

  return parts.join(' · ');
}

/** The status rule as text: `≤ 10`, `≥ 99.00%`, `drop ≤ 2.00 pts vs 7d`, `± 30% vs 1d`, or null without one. */
export function describeRule(tile: MetricBoardTile): string | null {
  if (tile.statusRule === MetricTileStatusRule.Threshold && tile.threshold !== null) {
    const sign = tile.comparison === SloComparison.AtLeast ? '≥' : '≤';
    const value = isRatio(tile) ? percent(tile.threshold) : formatNumber(tile.threshold);

    return `${sign} ${value}`;
  }

  if (tile.statusRule === MetricTileStatusRule.Deviation && tile.maxDeviation !== null) {
    const amount = isRatio(tile) ? `${(tile.maxDeviation * 100).toFixed(2)} pts` : percent(tile.maxDeviation, 0);
    const direction = tile.deviationDirection ?? MetricDeviationDirection.Either;
    const verb = direction === MetricDeviationDirection.Drop ? 'drop ≤' : direction === MetricDeviationDirection.Rise ? 'rise ≤' : '±';

    return `${verb} ${amount} vs ${tile.baselineDays ?? 7}d`;
  }

  return null;
}

/** Status pill text; a tile without a rule shows none. */
export function tileStatusLabel(status: SloState | null): string | null {
  return status === null ? null : SloStateLabel[status];
}

/** The tile's border / accent colour by status (no rule = neutral). */
export function tileStatusClass(status: SloState | null): string {
  switch (status) {
    case SloState.Healthy:
      return 'border-green-500/60';
    case SloState.Warning:
      return 'border-amber-500/70';
    case SloState.Breaching:
      return 'border-red-500/80';
    case SloState.NoData:
      return 'border-dashed border-slate-400/60';
    case SloState.Acknowledged:
      return 'border-slate-500/70';
    default:
      return 'border-border';
  }
}

export interface SparkPoint {
  x: number;
  y: number;
}

/**
 * Sparkline points: buckets with a value, oldest first, a ratio as a percentage. Buckets the server left out (nothing to
 * show) stay out, so the line does not dive to zero through a gap.
 */
export function sparklinePoints(result: Pick<MetricBoardTileResult, 'tile' | 'sparkline'>): SparkPoint[] {
  const scale = isRatio(result.tile) ? 100 : 1;

  return result.sparkline
    .map((p) => ({ x: Date.parse(p.timestamp), y: p.value === null ? Number.NaN : p.value * scale }))
    .filter((p) => Number.isFinite(p.x) && Number.isFinite(p.y))
    .sort((a, b) => a.x - b.x);
}

/** Counts per status for the board header: `{ breaching: 1, warning: 0, ... }`. */
export function statusCounts(
  results: MetricBoardTileResult[],
): Record<'healthy' | 'warning' | 'breaching' | 'acknowledged' | 'noData', number> {
  const counts = { healthy: 0, warning: 0, breaching: 0, acknowledged: 0, noData: 0 };
  for (const r of results) {
    if (r.status === SloState.Healthy) counts.healthy++;
    else if (r.status === SloState.Warning) counts.warning++;
    else if (r.status === SloState.Breaching) counts.breaching++;
    else if (r.status === SloState.Acknowledged) counts.acknowledged++;
    else if (r.status === SloState.NoData) counts.noData++;
  }

  return counts;
}

/**
 * The pill's hover text for a tile read Acknowledged (#333): what the rule said before the acknowledged-noise note, or
 * null for every other status.
 */
export function acknowledgedHint(result: Pick<MetricBoardTileResult, 'status' | 'unacknowledgedStatus'>): string | null {
  if (result.status !== SloState.Acknowledged || result.unacknowledgedStatus === null || result.unacknowledgedStatus === SloState.Acknowledged) {
    return null;
  }

  return `${SloStateLabel[result.unacknowledgedStatus]} by its rule, acknowledged by a known-noise note`;
}

/**
 * The board header's note when the values stop before the window's end (#328): `values up to 14:35 (newest bucket still
 * filling)`, local time. Null when they run to the end, or when the server sent no usable instant.
 */
export function completeUntilHint(board: Pick<MetricBoard, 'from' | 'to' | 'completeUntil'>): string | null {
  const until = Date.parse(board.completeUntil);
  if (!Number.isFinite(until) || until >= Date.parse(board.to)) {
    return null;
  }

  if (until <= Date.parse(board.from)) {
    return 'no complete values yet (newest bucket still filling)';
  }

  return `values up to ${DateTime.fromMillis(until).toFormat('HH:mm')} (newest bucket still filling)`;
}

/** The board header's note when the server capped the instrument notes it read (some tiles miss a note), else null. */
export function annotationsTruncatedHint(board: Pick<MetricBoard, 'annotationsTruncated'>): string | null {
  return board.annotationsTruncated ? 'some notes not shown' : null;
}

// ---------------------------------------------------------------------------------------------------------------
// Tile editor
// ---------------------------------------------------------------------------------------------------------------

export const emptyTileDraft: MetricBoardTile = {
  id: 0,
  boardId: null,
  name: '',
  position: 0,
  instrument: '',
  application: null,
  tagFilter: null,
  kind: MetricTileKind.Value,
  chartQuery: null,
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
};

/** Mirrors MetricBoardValidation.AcceptsInstrument: a ratio counts events, so it refuses a gauge; a chart reads every kind. */
export function acceptsTileInstrument(kind: MetricTileKind, instrument: MetricKind): boolean {
  return kind !== MetricTileKind.Ratio || instrument !== MetricKind.Gauge;
}

/**
 * Switches the draft's kind, dropping the fields the new kind does not use. A chart keeps its controls in chartQuery and
 * has no status rule, so it drops the filter, application, ratio, aggregate and rule; the other kinds drop chartQuery.
 */
export function changeTileKind(draft: MetricBoardTile, kind: MetricTileKind): MetricBoardTile {
  if (kind === MetricTileKind.Chart) {
    return {
      ...changeStatusRule(draft, MetricTileStatusRule.None),
      kind,
      application: null,
      tagFilter: null,
      ratioTag: null,
      goodValues: null,
      badValues: null,
      gaugeAggregate: null,
    };
  }

  return kind === MetricTileKind.Ratio
    ? { ...draft, kind, chartQuery: null, gaugeAggregate: null, ratioTag: draft.ratioTag ?? '', goodValues: draft.goodValues ?? '', badValues: draft.badValues ?? '' }
    : { ...draft, kind, chartQuery: null, ratioTag: null, goodValues: null, badValues: null };
}

/** Switches the status rule with sensible defaults: at most 0 / ± 30 % (2 points on a ratio) against 7 days. */
export function changeStatusRule(draft: MetricBoardTile, rule: MetricTileStatusRule): MetricBoardTile {
  const cleared = { ...draft, statusRule: rule, comparison: null, threshold: null, baselineDays: null, maxDeviation: null, deviationDirection: null };
  if (rule === MetricTileStatusRule.Threshold) {
    return { ...cleared, comparison: SloComparison.AtMost, threshold: 0 };
  }

  if (rule === MetricTileStatusRule.Deviation) {
    return {
      ...cleared,
      baselineDays: 7,
      maxDeviation: isRatio(draft) ? 0.02 : 0.3,
      deviationDirection: isRatio(draft) ? MetricDeviationDirection.Drop : MetricDeviationDirection.Either,
    };
  }

  return cleared;
}

function blank(value: string | null | undefined): boolean {
  return !value || value.trim() === '';
}

/** First client-side problem with a draft, or null. `instrumentKind` is the picked instrument's kind when known. */
export function validateTileDraft(draft: MetricBoardTile, instrumentKind: MetricKind | null): string | null {
  if (blank(draft.name)) {
    return 'Give the tile a name.';
  }
  if (blank(draft.instrument)) {
    return 'Pick the instrument this tile reads.';
  }
  if (instrumentKind !== null && !acceptsTileInstrument(draft.kind, instrumentKind)) {
    return 'A ratio tile needs a counter or histogram: gauges have no event count.';
  }

  // A chart's controls come from the metric page's own pickers, so they parse; the server checks the query anyway.
  if (isChartTile(draft)) {
    return draft.statusRule === MetricTileStatusRule.None ? null : 'A chart tile has no status colour.';
  }

  const filterError = validateTagFilter(draft.tagFilter, 'Tag filter');
  if (filterError) {
    return filterError;
  }

  if (isRatio(draft)) {
    if (blank(draft.ratioTag)) {
      return 'Pick the tag whose value marks an event good or bad.';
    }
    if (blank(draft.goodValues) || blank(draft.badValues)) {
      return 'Good values and bad values are both required.';
    }
    const good = parseValueList(draft.goodValues);
    const bad = parseValueList(draft.badValues);
    if (good.error || bad.error) {
      return `${good.error ? 'Good' : 'Bad'} values: ${good.error ?? bad.error}`;
    }
    if (valueListsOverlap(draft.goodValues!, draft.badValues!)) {
      return 'Good and bad values must not overlap: no value may match a pattern in both lists.';
    }
  }

  if (draft.statusRule === MetricTileStatusRule.Threshold && (draft.threshold === null || !Number.isFinite(draft.threshold))) {
    return 'The threshold must be a number.';
  }
  // A ratio reads a 0–1 share (entered as a percentage): a bound outside it could never be met, or never be missed.
  if (draft.statusRule === MetricTileStatusRule.Threshold && isRatio(draft) && (draft.threshold! < 0 || draft.threshold! > 1)) {
    return 'A ratio threshold is a percentage between 0% and 100%.';
  }

  if (draft.statusRule === MetricTileStatusRule.Deviation) {
    const max = draft.maxDeviation;
    if (draft.baselineDays !== 1 && draft.baselineDays !== 7) {
      return 'Compare with the same window 1 or 7 days earlier.';
    }
    if (max === null || !Number.isFinite(max) || max <= 0) {
      return 'The allowed deviation must be greater than zero.';
    }
    if ((isRatio(draft) || draft.deviationDirection !== MetricDeviationDirection.Rise) && max >= 1) {
      return isRatio(draft) ? 'A ratio can move at most 100 points: keep the allowance below 100.' : 'A drop can be at most 100% of the baseline: keep the allowance below 100%.';
    }
  }

  return null;
}

/** The request to send: text fields trimmed, empty ones nulled, fields of another kind or rule left out. */
export function buildTileRequest(draft: MetricBoardTile): MetricBoardTile {
  const clean = (value: string | null | undefined) => (blank(value) ? null : value!.trim());
  if (isChartTile(draft)) {
    return {
      ...changeTileKind(draft, MetricTileKind.Chart),
      name: draft.name.trim(),
      instrument: draft.instrument.trim(),
      chartQuery: clean(draft.chartQuery),
    };
  }

  const ratio = isRatio(draft);
  const threshold = draft.statusRule === MetricTileStatusRule.Threshold;
  const deviation = draft.statusRule === MetricTileStatusRule.Deviation;

  return {
    ...draft,
    name: draft.name.trim(),
    instrument: draft.instrument.trim(),
    chartQuery: null,
    application: clean(draft.application),
    tagFilter: clean(draft.tagFilter),
    ratioTag: ratio ? clean(draft.ratioTag) : null,
    goodValues: ratio ? clean(draft.goodValues) : null,
    badValues: ratio ? clean(draft.badValues) : null,
    gaugeAggregate: ratio ? null : draft.gaugeAggregate,
    comparison: threshold ? (draft.comparison ?? SloComparison.AtMost) : null,
    threshold: threshold ? draft.threshold : null,
    baselineDays: deviation ? draft.baselineDays : null,
    maxDeviation: deviation ? draft.maxDeviation : null,
    deviationDirection: deviation ? (draft.deviationDirection ?? MetricDeviationDirection.Either) : null,
  };
}

/** Gauges pick an aggregate; the default is the sample-weighted average. */
export function defaultAggregate(kind: MetricKind | null): SloGaugeAggregate | null {
  return kind === MetricKind.Gauge ? SloGaugeAggregate.Average : null;
}
