// Pure model behind chart tiles (spec 2026-11-19 batch 1): a chart tile stores the metric page's URL controls without
// the range (one grammar for the page, a shared link and a tile), and the board's range drives every chart. These
// helpers convert between that stored query, the page's controls and links, and build the tile "Add to board" saves.
import { encodeUrlSafeId } from '@/lib/urlSafeId';
import { MetricTileKind, MetricTileStatusRule, type MetricBoardTile, type MetricBoardTileResult } from '@/types/metricBoard';
import {
  DEFAULT_CONTROLS,
  METRIC_RANGE_KEYS,
  chipToText,
  metricQueryKeys,
  parseControlParams,
  parseRate,
  percentileLabel,
  rateParam,
  writeControlParams,
  writeRangeParams,
  type MetricControls,
  type RangeSelection,
  type RateId,
} from './metricModel';

/** The most chart tiles one board holds (MetricBoardValidation.MaxChartTiles), within the 48-tile cap. */
export const MAX_CHART_TILES = 12;

/** The keys a chart tile stores: every metric page key but the range's, in the page's order. */
export const CHART_QUERY_KEYS: readonly string[] = metricQueryKeys.filter((x) => !(METRIC_RANGE_KEYS as readonly string[]).includes(x));

/**
 * The stored query of a chart made from the page's URL: only the chart keys, in the page's order, the range dropped.
 * Null when the URL carries no control (the plain chart of the instrument).
 */
export function chartQueryFromParams(params: URLSearchParams): string | null {
  const next = new URLSearchParams();
  for (const key of CHART_QUERY_KEYS) {
    for (const value of params.getAll(key)) {
      next.append(key, value);
    }
  }

  const text = next.toString();

  return text === '' ? null : text;
}

/** The stored query for a set of controls and a rate (the tile editor): what the page would write, minus the range. */
export function chartQueryFromControls(controls: MetricControls, rate: RateId): string | null {
  const params = writeControlParams(new URLSearchParams(), { ...controls, range: DEFAULT_CONTROLS.range, custom: null });
  const value = rateParam(rate);
  if (value) {
    params.set('rate', value);
  }

  return chartQueryFromParams(params);
}

/** A chart tile's controls over the board's window: the stored query read as the page reads its URL, the range the board's. */
export function chartControls(query: string | null, selection: RangeSelection): { controls: MetricControls; rate: RateId } {
  const params = new URLSearchParams(query ?? '');
  for (const key of METRIC_RANGE_KEYS) {
    params.delete(key);
  }

  return {
    controls: { ...parseControlParams(params), range: selection.range, custom: selection.custom },
    rate: parseRate(params.get('rate')),
  };
}

/** "Open in chart": the metric page with the tile's controls and the board's range, so the page shows what the tile shows. */
export function chartTileHref(instrument: string, query: string | null, selection: RangeSelection): string {
  const params = writeRangeParams(new URLSearchParams(query ?? ''), selection);
  const text = params.toString();

  return `/metrics/${encodeUrlSafeId(instrument)}${text === '' ? '' : `?${text}`}`;
}

/** What a chart tile shows, in one line: `by gateway · result=error · p95 · vs 7d`; empty for a plain chart. */
export function describeChartQuery(query: string | null): string {
  const { controls, rate } = chartControls(query, { range: DEFAULT_CONTROLS.range, custom: null });
  const parts: string[] = [];
  if (controls.breakdown) {
    parts.push(`by ${controls.breakdown}`);
  }

  for (const chip of controls.filters) {
    parts.push(chipToText(chip));
  }

  if (controls.view === 'ratio' && controls.numerator) {
    parts.push(`share ${controls.numerator}`);
  }

  if (controls.percentile !== null) {
    parts.push(percentileLabel(controls.percentile));
  }

  const compare = [controls.compareDay ? '1d' : null, controls.compareWeek ? '7d' : null].filter((x) => x !== null);
  if (compare.length > 0) {
    parts.push(`vs ${compare.join(', ')}`);
  }

  if (controls.showMinMax) {
    parts.push('min / max');
  }

  if (rate !== 'bucket') {
    parts.push(`per ${rate}`);
  }

  if (controls.application) {
    parts.push(`@${controls.application}`);
  }

  return parts.join(' · ');
}

/** The ids of the chart tiles among a board's tiles (their count is what the chart cap counts). */
export function chartTileIds(tiles: readonly Pick<MetricBoardTileResult, 'tile'>[]): number[] {
  return tiles.filter((x) => x.tile.kind === MetricTileKind.Chart).map((x) => x.tile.id);
}

/**
 * The tile "Add to board" saves from the metric page: a chart of `instrument` with the page's current controls (the
 * range left out: the board's range drives the tile), named `name`, on board `boardId` (null = Default). The query is
 * built from the controls the page PARSED, never copied from the URL: a stale or hand-edited link (`compare=30d`,
 * `view=foo`, `percentile=75`, a repeated filter key) shows the page fine, since the page ignores what it cannot read,
 * and copying it would make the save a 400 or store a control the tile cannot show.
 */
export function chartTileFromPage(instrument: string, name: string, params: URLSearchParams, boardId: number | null): MetricBoardTile {
  return {
    id: 0,
    boardId,
    name: name.trim(),
    position: 0,
    instrument,
    application: null,
    tagFilter: null,
    kind: MetricTileKind.Chart,
    chartQuery: chartQueryFromControls(parseControlParams(params), parseRate(params.get('rate'))),
    sloDefinitionId: null,
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
}

/**
 * Which tile the "Add to board" form can save, or why not: a name, and room for one more chart (and one more tile) on
 * the board. `board` is the picked board's tile and chart counts, when known. `chart` false (an SLO tile) skips the
 * chart cap.
 */
export function addToBoardError(name: string, board: { tileCount: number; chartCount: number } | null, maxTiles: number, chart = true): string | null {
  if (name.trim() === '') {
    return 'Give the tile a name.';
  }

  if (name.trim().length > 200) {
    return 'A tile name is at most 200 characters.';
  }

  if (board !== null && board.tileCount >= maxTiles) {
    return `That board already holds ${maxTiles} tiles.`;
  }

  if (chart && board !== null && board.chartCount >= MAX_CHART_TILES) {
    return `That board already holds ${MAX_CHART_TILES} charts.`;
  }

  return null;
}

/**
 * Every board's tile and chart counts from the stored tile list (GET metrics/board/tiles), keyed by board id; a tile with
 * no board counts on the Default board (`defaultId`).
 */
export function boardTileCounts(tiles: readonly MetricBoardTile[], defaultId: number | null): Map<number, { tileCount: number; chartCount: number }> {
  const counts = new Map<number, { tileCount: number; chartCount: number }>();
  for (const tile of tiles) {
    const board = tile.boardId ?? defaultId;
    if (board === null) {
      continue;
    }

    const entry = counts.get(board) ?? { tileCount: 0, chartCount: 0 };
    entry.tileCount++;
    if (tile.kind === MetricTileKind.Chart) {
      entry.chartCount++;
    }

    counts.set(board, entry);
  }

  return counts;
}
