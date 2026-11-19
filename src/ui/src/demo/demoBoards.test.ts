import { describe, expect, it } from 'vitest';
import { DEMO_CHECKOUT_BOARD, DEMO_DEFAULT_BOARD, DEMO_PAYMENTS_BOARD, demoBoards, demoBoardTiles, demoMetricBoard } from './data/metricBoard';
import { demoDeployVerdict } from './data/applications';
import { demoMetrics, demoMetricSeries } from './data/metrics';
import { MetricTileKind } from '@/types/metricBoard';
import { chartControls } from '@/pages/metrics/chartTileModel';
import { buildSeriesQuery } from '@/pages/metrics/metricModel';

// Named boards (§8.34) in demo mode: three boards whose tiles read demo instruments, a board read per board, and the
// verdict's board parameter behaving as on the server.
describe('demo named boards', () => {
  const now = Date.parse('2026-11-13T12:00:00Z');
  const from = new Date(now - 6 * 3_600_000).toISOString();
  const to = new Date(now).toISOString();

  it('lists Default, Checkout and Payments with their tile counts', () => {
    expect(demoBoards.map((x) => [x.name, x.isDefault])).toEqual([['Default', true], ['Checkout', false], ['Payments', false]]);
    for (const board of demoBoards) {
      const tiles = demoBoardTiles.filter((x) => (x.boardId ?? DEMO_DEFAULT_BOARD) === board.id);
      expect(board.tileCount).toBe(tiles.length);
      expect(board.tileCount).toBeGreaterThan(0);
    }
  });

  it('points every tile at a demo instrument and a demo board', () => {
    const instruments = new Set(demoMetrics.map((x) => x.name));
    const ids = new Set(demoBoards.map((x) => x.id));
    for (const tile of demoBoardTiles) {
      expect(instruments.has(tile.instrument)).toBe(true);
      expect(tile.boardId === null || ids.has(tile.boardId)).toBe(true);
    }
  });

  it('reads one board at a time, the Default board by default, and null for an unknown board', () => {
    const fallback = demoMetricBoard(from, to, now);
    const checkout = demoMetricBoard(from, to, now, DEMO_CHECKOUT_BOARD);
    const payments = demoMetricBoard(from, to, now, DEMO_PAYMENTS_BOARD);

    expect(fallback.board?.isDefault).toBe(true);
    expect(fallback.tiles.every((x) => x.tile.boardId === null)).toBe(true);
    expect(checkout?.board?.name).toBe('Checkout');
    expect(checkout?.tiles.every((x) => x.tile.boardId === DEMO_CHECKOUT_BOARD)).toBe(true);
    expect(payments?.tiles.length).toBeGreaterThan(0);
    expect(demoMetricBoard(from, to, now, 999)).toBeNull();
  });

  it('reads every board for the verdict when no board is named', () => {
    const all = demoMetricBoard(from, to, now, 'all');

    expect(all?.board).toBeNull();
    expect(all?.tiles.length).toBe(demoBoardTiles.length);
  });

  it('narrows the verdict to a named board, ignoring case, and gives no verdict for an unknown board', () => {
    const every = demoDeployVerdict('billing-service');
    const payments = demoDeployVerdict('billing-service', undefined, undefined, 0, undefined, 'payments');
    const tileNames = new Set(demoBoardTiles.filter((x) => x.boardId === DEMO_PAYMENTS_BOARD).map((x) => x.name));

    expect(every).not.toBeNull();
    expect(payments).not.toBeNull();
    expect(every!.board).toBeNull();
    expect(payments!.board).toBe('Payments');
    const paymentTiles = payments!.checks.filter((x) => x.source === 'tile');
    expect(paymentTiles.length).toBeGreaterThan(0);
    expect(paymentTiles.every((x) => tileNames.has(x.name))).toBe(true);
    expect(every!.checks.filter((x) => x.source === 'tile').length).toBeGreaterThan(paymentTiles.length);
    expect(payments!.checks.filter((x) => x.source === 'slo')).toEqual(every!.checks.filter((x) => x.source === 'slo'));
    expect(demoDeployVerdict('billing-service', undefined, undefined, 0, undefined, 'Nope')).toBeNull();
  });

  it('puts two chart tiles on the Checkout board, read with their kind and unit and nothing computed', () => {
    const checkout = demoMetricBoard(from, to, now, DEMO_CHECKOUT_BOARD)!;
    const charts = checkout.tiles.filter((x) => x.tile.kind === MetricTileKind.Chart);

    expect(charts.map((x) => x.tile.name)).toEqual(['Payment errors by gateway', 'Request latency p95']);
    for (const chart of charts) {
      expect(chart.instrumentKind).toBe(demoMetrics.find((x) => x.name === chart.tile.instrument)!.kind);
      expect([chart.current, chart.status, chart.sparkline.length]).toEqual([null, null, 0]);
    }
  });

  it('serves every chart tile its series from the demo series API, with the tile controls applied', () => {
    const charts = demoBoardTiles.filter((x) => x.kind === MetricTileKind.Chart);

    for (const tile of charts) {
      const kind = demoMetrics.find((x) => x.name === tile.instrument)!.kind;
      const { controls } = chartControls(tile.chartQuery, { range: '6h', custom: null });

      const series = demoMetricSeries(tile.instrument, buildSeriesQuery(controls, kind, now))!;

      expect(series.series.length).toBeGreaterThan(0);
      expect(series.series.some((x) => x.points.some((p) => p.value > 0))).toBe(true);
    }

    const byGateway = demoMetricSeries('payment.results', buildSeriesQuery(chartControls(charts[0].chartQuery, { range: '6h', custom: null }).controls, null, now))!;
    expect(byGateway.series.filter((x) => x.compare === null).map((x) => x.name).sort()).toEqual(['adyen', 'checkout', 'nets']);
    expect(byGateway.series.some((x) => x.compare === '7d')).toBe(true);
  });
});
