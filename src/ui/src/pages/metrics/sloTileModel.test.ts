import { describe, expect, it } from 'vitest';
import { MetricTileKind, MetricTileStatusRule, type MetricBoardTileResult } from '@/types/metricBoard';
import { SloKind, SloState, type SloObjective } from '@/types/slo';
import { formatObserved, formatTarget } from '@/pages/slo/sloDisplay';
import {
  buildTileRequest,
  changeTileKind,
  describeTile,
  emptyTileDraft,
  isSloTile,
  statusCounts,
  tileState,
  validateTileDraft,
} from './boardModel';
import { addToBoardError } from './chartTileModel';
import { addToBoardName, addToBoardTile, objectiveOptions, sloHref, sloTileFromObjective, sloTileView } from './sloTileModel';

const objective: SloObjective = {
  id: 8,
  name: 'Checkout p95 ≤ 0.5 s',
  kind: SloKind.MetricLatency,
  dimension: 'http.server.request.duration',
  application: 'orders-api',
  targetValue: 0.5,
  percentile: 95,
  windowSeconds: 3600,
  enabled: true,
  evaluated: true,
  attainment: 0.9712,
  budgetRemaining: 0.42,
  burnRateShort: 1.25,
  burnRateLong: 0.5,
  state: SloState.Warning,
  acknowledgedUntil: null,
  lastEvaluatedAt: '2026-11-19T11:59:00Z',
};

const sloTile = { ...emptyTileDraft, id: 13, name: 'Checkout latency', kind: MetricTileKind.Slo, sloDefinitionId: 8 };

function result(patch: Partial<MetricBoardTileResult> = {}): MetricBoardTileResult {
  return {
    tile: sloTile,
    instrumentKind: null,
    unit: null,
    current: null,
    dayAgo: null,
    weekAgo: null,
    status: null,
    unacknowledgedStatus: null,
    annotations: [],
    sparkline: [],
    slo: objective,
    missing: false,
    ...patch,
  };
}

const stamp = (iso: string) => `at ${iso}`;

describe('sloTileView', () => {
  it.each([SloState.Healthy, SloState.Warning, SloState.Breaching])('shows attainment against target, budget and burns for a %s objective', (state) => {
    const view = sloTileView(result({ slo: { ...objective, state } }), stamp);

    expect(view.missing).toBe(false);
    if (view.missing) {
      return;
    }
    expect(view.state).toBe(state);
    expect(view.display).toBe('values');
    expect(view.observed).toBe(formatObserved(objective));
    expect(view.target).toBe(formatTarget(objective));
    expect(view.observed).toBe('97.12% ≤ 0.5');
    expect(view.target).toBe('p95 ≤ 0.5');
    expect(view.budget).toBe('42%');
    expect(view.burn).toBe('1.25× / 0.50×');
    expect(view.burning).toBe(true);
    expect(view.lastEvaluatedAt).toBe('at 2026-11-19T11:59:00Z');
    expect(view.acknowledgedUntil).toBeNull();
    expect(view.note).toBeNull();
    expect(view.href).toBe('/slo/8');
  });

  it('shows an acknowledged objective with its numbers and until when', () => {
    const view = sloTileView(result({ slo: { ...objective, state: SloState.Acknowledged, acknowledgedUntil: '2026-11-19T13:00:00Z' } }), stamp);

    expect(view.missing).toBe(false);
    if (view.missing) {
      return;
    }
    expect(view.state).toBe(SloState.Acknowledged);
    expect(view.observed).toBe('97.12% ≤ 0.5');
    expect(view.acknowledgedUntil).toBe('Acknowledged until at 2026-11-19T13:00:00Z');
  });

  it('hides the placeholder numbers of a NoData objective', () => {
    const view = sloTileView(result({ slo: { ...objective, state: SloState.NoData, budgetRemaining: 1, burnRateShort: 0, burnRateLong: 0 } }), stamp);

    expect(view.missing).toBe(false);
    if (view.missing) {
      return;
    }
    expect(view.state).toBe(SloState.NoData);
    expect(view.display).toBe('no-data');
    expect([view.observed, view.budget, view.burn]).toEqual([null, null, null]);
    expect(view.burning).toBe(false);
    expect(view.target).toBe('p95 ≤ 0.5');
    expect(view.note).toContain('No data');
  });

  it('says an objective was never evaluated', () => {
    const view = sloTileView(result({ slo: { ...objective, evaluated: false, lastEvaluatedAt: null, state: SloState.Healthy } }), stamp);

    expect(view.missing).toBe(false);
    if (view.missing) {
      return;
    }
    expect(view.display).toBe('not-evaluated');
    expect(view.observed).toBeNull();
    expect(view.lastEvaluatedAt).toBeNull();
    expect(view.note).toBe('Not evaluated yet.');
  });

  it('reads a tile whose objective is gone as missing', () => {
    expect(sloTileView(result({ slo: null, missing: true }), stamp)).toEqual({ missing: true, message: expect.stringContaining('deleted') });
    expect(sloTileView(result({ slo: undefined, missing: undefined }), stamp).missing).toBe(true);
  });
});

describe('SLO tiles in the board model', () => {
  it('describes and recognises an SLO tile', () => {
    expect(isSloTile(sloTile)).toBe(true);
    expect(describeTile(sloTile)).toBe('SLO #8');
    expect(sloHref(8)).toBe('/slo/8');
  });

  it('counts an SLO tile by its objective state, a disabled, unevaluated or missing one not at all', () => {
    expect(tileState(result())).toBe(SloState.Warning);
    expect(tileState(result({ slo: { ...objective, enabled: false } }))).toBeNull();
    expect(tileState(result({ slo: { ...objective, evaluated: false } }))).toBeNull();
    expect(tileState(result({ slo: null, missing: true }))).toBeNull();
    expect(statusCounts([result(), result({ slo: { ...objective, state: SloState.Breaching } })])).toEqual({
      healthy: 0,
      warning: 1,
      breaching: 1,
      acknowledged: 0,
      noData: 0,
    });
  });

  it('turns a value tile into an SLO tile, dropping the instrument, filter and rule', () => {
    const value = {
      ...emptyTileDraft,
      name: 'Errors',
      instrument: 'orders',
      tagFilter: 'status=5*',
      application: 'shop',
      statusRule: MetricTileStatusRule.Threshold,
      threshold: 3,
    };

    const slo = changeTileKind(value, MetricTileKind.Slo);

    expect(slo).toMatchObject({
      kind: MetricTileKind.Slo,
      instrument: '',
      tagFilter: null,
      application: null,
      statusRule: MetricTileStatusRule.None,
      threshold: null,
      sloDefinitionId: null,
    });
    expect(changeTileKind({ ...slo, sloDefinitionId: 4 }, MetricTileKind.Value).sloDefinitionId).toBeNull();
  });

  it('asks for an objective, and never for an instrument', () => {
    expect(validateTileDraft({ ...sloTile, sloDefinitionId: null }, null)).toBe('Pick the objective this tile shows.');
    expect(validateTileDraft(sloTile, null)).toBeNull();
    expect(validateTileDraft({ ...sloTile, statusRule: MetricTileStatusRule.Threshold }, null)).toContain('no status colour');
  });

  it('sends the objective and nothing of another kind', () => {
    const request = buildTileRequest({ ...sloTile, name: '  Checkout  ', instrument: 'stale', chartQuery: 'percentile=95', tagFilter: 'a=b' });

    expect(request).toMatchObject({ name: 'Checkout', kind: MetricTileKind.Slo, sloDefinitionId: 8, instrument: '', chartQuery: null, tagFilter: null });
  });
});

describe('Add to board from the SLO page', () => {
  it('builds an SLO tile of the objective on the picked board', () => {
    expect(sloTileFromObjective(objective, '  Checkout latency ', 3)).toEqual({
      ...emptyTileDraft,
      boardId: 3,
      name: 'Checkout latency',
      kind: MetricTileKind.Slo,
      sloDefinitionId: 8,
    });
  });

  it('is not held back by a board full of charts, only by a full board', () => {
    expect(addToBoardError('Checkout', { tileCount: 20, chartCount: 12 }, 48, false)).toBeNull();
    expect(addToBoardError('Checkout', { tileCount: 20, chartCount: 12 }, 48)).toContain('12 charts');
    expect(addToBoardError('Checkout', { tileCount: 48, chartCount: 0 }, 48, false)).toContain('48 tiles');
  });

  it('lists the objectives by name, with their application', () => {
    const options = objectiveOptions([
      { ...objective, id: 2, name: 'b', application: null },
      { ...objective, id: 1, name: 'a', application: 'shop' },
    ]);

    expect(options).toEqual([{ id: 1, label: 'a @shop' }, { id: 2, label: 'b' }]);
  });

  it('builds the tile and the suggested name from either source', () => {
    const params = new URLSearchParams('breakdown=gateway&range=7d');

    expect(addToBoardName({ kind: 'slo', objective })).toBe(objective.name);
    expect(addToBoardName({ kind: 'chart', instrument: 'payment.results', params })).toBe('payment.results');
    expect(addToBoardTile({ kind: 'slo', objective }, 'x', null)).toMatchObject({ kind: MetricTileKind.Slo, sloDefinitionId: 8 });
    expect(addToBoardTile({ kind: 'chart', instrument: 'payment.results', params }, 'x', null)).toMatchObject({
      kind: MetricTileKind.Chart,
      chartQuery: 'breakdown=gateway',
      sloDefinitionId: null,
    });
  });
});
