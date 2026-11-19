import { describe, expect, it } from 'vitest';
import { MetricTileKind, MetricTileStatusRule, type MetricBoardTile } from '@/types/metricBoard';
import { SloComparison } from '@/types/slo';
import {
  CHART_QUERY_KEYS,
  MAX_CHART_TILES,
  addToBoardError,
  boardTileCounts,
  chartControls,
  chartQueryFromControls,
  chartQueryFromParams,
  chartTileFromPage,
  chartTileHref,
  chartTileIds,
  describeChartQuery,
} from './chartTileModel';
import { buildTileRequest, changeTileKind, describeTile, emptyTileDraft, validateTileDraft } from './boardModel';
import { DEFAULT_CONTROLS, METRIC_RANGE_KEYS, metricQueryKeys, parseControlParams, writeControlParams } from './metricModel';

const day = { range: '24h', custom: null } as const;

describe('chart tile query keys', () => {
  it('stores every metric page key but the range keys, in the page order', () => {
    expect(CHART_QUERY_KEYS).toEqual(['application', 'breakdown', 'filter', 'compare', 'percentile', 'view', 'numerator', 'minmax', 'rate']);
    expect(METRIC_RANGE_KEYS.every((x) => (metricQueryKeys as readonly string[]).includes(x))).toBe(true);
  });
});

describe('chart tile query ⇄ controls', () => {
  it('round-trips every control through the stored query', () => {
    const controls = {
      ...DEFAULT_CONTROLS,
      application: 'shop-api',
      breakdown: 'http.response.status_code',
      filters: [{ key: 'http.route', value: 'api/checkout*' }, { key: '{version}', value: '2.*' }],
      compareDay: true,
      compareWeek: true,
      percentile: 95,
      view: 'ratio' as const,
      numerator: '5*',
    };

    const query = chartQueryFromControls(controls, 'minute');
    const back = chartControls(query, day);

    expect(back.controls).toEqual(controls);
    expect(back.rate).toBe('minute');
    expect(chartQueryFromControls(back.controls, back.rate)).toBe(query);
  });

  it('writes the same keys and values as the metric page, without the range', () => {
    const controls = { ...DEFAULT_CONTROLS, range: '7d' as const, breakdown: 'gateway', filters: [{ key: 'result', value: 'error' }], compareWeek: true, showMinMax: true };

    const query = chartQueryFromControls(controls, 'bucket');

    const page = writeControlParams(new URLSearchParams(), controls);
    page.delete('range');
    expect(query).toBe(page.toString());
    expect(new URLSearchParams(query!).has('range')).toBe(false);
  });

  it('reads a server-canonical query (percent-encoded `:` `*` `,`) like the page reads its URL', () => {
    const { controls, rate } = chartControls('application=shop%20api&filter=outcome%3Aerror%2A&compare=1d%2C7d&percentile=95&minmax=1&rate=second', day);

    expect(controls.application).toBe('shop api');
    expect(controls.filters).toEqual([{ key: 'outcome', value: 'error*' }]);
    expect([controls.compareDay, controls.compareWeek]).toEqual([true, true]);
    expect(controls.percentile).toBe(95);
    expect(controls.showMinMax).toBe(true);
    expect(rate).toBe('second');
  });

  it('takes the board range, never a range left in the query', () => {
    const custom = { from: Date.parse('2026-11-19T08:00:00Z'), to: null };

    const { controls } = chartControls('range=30d&from=2026-01-01T00:00:00.000Z&breakdown=a', { range: '24h', custom });

    expect(controls.range).toBe('24h');
    expect(controls.custom).toEqual(custom);
    expect(controls.breakdown).toBe('a');
  });

  it('is null for a plain chart with no controls', () => {
    expect(chartQueryFromControls(DEFAULT_CONTROLS, 'bucket')).toBeNull();
    expect(chartControls(null, day).controls).toEqual(DEFAULT_CONTROLS);
  });
});

describe('chartQueryFromParams / chartTileFromPage (Add to board)', () => {
  it('keeps the chart keys of the page URL and drops the range and anything else', () => {
    const params = new URLSearchParams('range=7d&from=2026-11-01T00:00:00.000Z&breakdown=gateway&filter=result:error&filter=step:capture&board=x&rate=minute');

    expect(chartQueryFromParams(params)).toBe('breakdown=gateway&filter=result%3Aerror&filter=step%3Acapture&rate=minute');
  });

  it('builds a chart tile from the current URL, on the picked board, under the given name', () => {
    const params = new URLSearchParams('range=1h&breakdown=gateway&percentile=95&compare=7d');

    const tile = chartTileFromPage('payment.results', '  Payments by gateway ', params, 7);

    expect(tile).toEqual({
      ...emptyTileDraft,
      boardId: 7,
      name: 'Payments by gateway',
      instrument: 'payment.results',
      kind: MetricTileKind.Chart,
      chartQuery: 'breakdown=gateway&compare=7d&percentile=95',
    });
    expect(parseControlParams(new URLSearchParams(tile.chartQuery!)).percentile).toBe(95);
  });

  it.each([
    ['compare=30d', null],
    ['view=foo', null],
    ['rate=hour', null],
    ['percentile=75', null],
    ['filter=status:5*0', null],
    ['filter=noseparator', null],
    ['minmax=yes', null],
    ['breakdown=gateway&compare=30d,7d&view=foo&percentile=75', 'breakdown=gateway&compare=7d'],
    ['filter=status:5*&filter=status:4*', 'filter=status%3A4*'],
    ['rate=second&percentile=99', 'percentile=99&rate=second'],
  ])('saves only what the page shows from a stale URL (%s)', (query, expected) => {
    const tile = chartTileFromPage('c', 'c', new URLSearchParams(`range=7d&${query}`), null);

    expect(tile.chartQuery).toBe(expected);
  });

  it('builds a plain chart (null query) from a URL with the range only', () => {
    expect(chartTileFromPage('c', 'c', new URLSearchParams('range=7d'), null).chartQuery).toBeNull();
  });
});

describe('chartTileHref', () => {
  it('opens the metric page with the tile controls and the board range', () => {
    const href = chartTileHref('payment.results', 'breakdown=gateway', { range: '7d', custom: null });

    const [path, search] = href.split('?');
    expect(path).toMatch(/^\/metrics\/[A-Za-z0-9_-]+$/);
    const params = new URLSearchParams(search);
    expect(params.get('breakdown')).toBe('gateway');
    expect(params.get('range')).toBe('7d');
  });

  it('carries a custom board range as from/to', () => {
    const from = Date.parse('2026-11-19T08:00:00Z');

    const params = new URLSearchParams(chartTileHref('c', null, { range: '24h', custom: { from, to: null } }).split('?')[1]);

    expect(params.get('from')).toBe(new Date(from).toISOString());
    expect(params.has('to')).toBe(false);
  });
});

describe('describeChartQuery', () => {
  it('summarises the controls in one line', () => {
    expect(describeChartQuery('application=shop&breakdown=gateway&filter=result:error&compare=7d&percentile=95&rate=minute'))
      .toBe('by gateway · result=error · p95 · vs 7d · per minute · @shop');
    expect(describeChartQuery(null)).toBe('');
  });

  it('is part of a chart tile description', () => {
    const tile = { ...emptyTileDraft, instrument: 'payment.results', kind: MetricTileKind.Chart, chartQuery: 'breakdown=gateway' };

    expect(describeTile(tile)).toBe('payment.results · by gateway');
    expect(describeTile({ ...tile, chartQuery: null })).toBe('payment.results');
  });
});

describe('chart tiles in the tile editor', () => {
  const ruled: MetricBoardTile = {
    ...emptyTileDraft,
    name: 'Errors',
    instrument: 'http',
    tagFilter: 'status=5*',
    application: 'api',
    gaugeAggregate: null,
    statusRule: MetricTileStatusRule.Threshold,
    comparison: SloComparison.AtMost,
    threshold: 5,
  };

  it('switching to a chart drops the filter, application, ratio and rule', () => {
    const chart = changeTileKind({ ...ruled, ratioTag: 'r', goodValues: 'a', badValues: 'b' }, MetricTileKind.Chart);

    expect(chart).toMatchObject({
      kind: MetricTileKind.Chart,
      tagFilter: null,
      application: null,
      ratioTag: null,
      goodValues: null,
      badValues: null,
      statusRule: MetricTileStatusRule.None,
      threshold: null,
      comparison: null,
    });
  });

  it('switching away from a chart drops its query', () => {
    const chart = { ...emptyTileDraft, kind: MetricTileKind.Chart, chartQuery: 'breakdown=a' };

    expect(changeTileKind(chart, MetricTileKind.Value).chartQuery).toBeNull();
    expect(changeTileKind(chart, MetricTileKind.Ratio).chartQuery).toBeNull();
  });

  it('sends a chart with its query and nothing of the other kinds', () => {
    const request = buildTileRequest({ ...ruled, kind: MetricTileKind.Chart, chartQuery: ' breakdown=a ' });

    expect(request.chartQuery).toBe('breakdown=a');
    expect(request.tagFilter).toBeNull();
    expect(request.statusRule).toBe(MetricTileStatusRule.None);
    expect(request.threshold).toBeNull();
    expect(buildTileRequest({ ...ruled, chartQuery: 'breakdown=a' }).chartQuery).toBeNull();
  });

  it('rejects a chart with a status rule and accepts one without', () => {
    expect(validateTileDraft({ ...ruled, kind: MetricTileKind.Chart }, null)).toBe('A chart tile has no status colour.');
    expect(validateTileDraft({ ...ruled, kind: MetricTileKind.Chart, statusRule: MetricTileStatusRule.None }, null)).toBeNull();
  });
});

describe('board counts for Add to board', () => {
  const tiles: MetricBoardTile[] = [
    { ...emptyTileDraft, id: 1, boardId: null },
    { ...emptyTileDraft, id: 2, boardId: null, kind: MetricTileKind.Chart },
    { ...emptyTileDraft, id: 3, boardId: 5, kind: MetricTileKind.Chart },
  ];

  it('counts tiles and charts per board, no board id on the Default board', () => {
    const counts = boardTileCounts(tiles, 1);

    expect(counts.get(1)).toEqual({ tileCount: 2, chartCount: 1 });
    expect(counts.get(5)).toEqual({ tileCount: 1, chartCount: 1 });
  });

  it('lists the chart tile ids of a board read', () => {
    expect(chartTileIds(tiles.map((tile) => ({ tile })))).toEqual([2, 3]);
  });

  it('refuses a blank name, a full board and a board full of charts', () => {
    expect(addToBoardError(' ', null, 48)).toBe('Give the tile a name.');
    expect(addToBoardError('x', { tileCount: 48, chartCount: 0 }, 48)).toBe('That board already holds 48 tiles.');
    expect(addToBoardError('x', { tileCount: 20, chartCount: MAX_CHART_TILES }, 48)).toBe(`That board already holds ${MAX_CHART_TILES} charts.`);
    expect(addToBoardError('x', { tileCount: 20, chartCount: MAX_CHART_TILES - 1 }, 48)).toBeNull();
  });
});
