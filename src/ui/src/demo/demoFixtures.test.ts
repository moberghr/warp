import { describe, it, expect } from 'vitest';
import type { InternalAxiosRequestConfig } from 'axios';
import { demoAdapterDetails, demoAdapterCalls } from './data/adapters';
import { createDemoAdapter } from './adapter';
import { demoMetrics } from './data/metrics';
import { encodeUrlSafeId } from '@/lib/urlSafeId';
import { MetricKind } from '@/types/metrics';
import type { MetricInstrument, MetricSeries } from '@/types/metrics';
import type { WarpAddonsInfo } from '@/types';
import { MetricTileStatusRule, type MetricBoard, type MetricBoardTile } from '@/types/metricBoard';
import type { ApplicationDeployModel, DeployVerdictModel } from '@/types/applications';
import { coverageMet, demoDeploys } from './data/applications';
import { MetricAnnotationTarget, type MetricAnnotationList } from '@/types/annotations';
import { demoSlos } from './data/slo';
import { demoAnnotations, demoNoteAppliesTo, demoNoteCoversTile } from './data/annotations';
import { demoBoardTiles, demoMetricBoard } from './data/metricBoard';
import type { MetricAnnotation } from '@/types/annotations';
import { SloState } from '@/types/slo';

// Demo mode is what the docs screenshots and every "show me the dashboard" walkthrough run against,
// so a dangling reference in the fixtures reads as a broken product: the page renders "Unable to
// load call detail — make sure the Warp backend is running", which is exactly the wrong diagnosis.
// These guards check the fixtures are internally consistent, since nothing else does.
describe('demo adapter fixtures', () => {
  it('every clickable recent call has a detail fixture', () => {
    const referenced = Object.values(demoAdapterDetails).flatMap((adapter) =>
      adapter.recentCalls.map((call) => call.id));

    const missing = referenced.filter((id) => !(id in demoAdapterCalls));

    expect(referenced.length).toBeGreaterThan(0);
    expect(missing).toEqual([]);
  });

  it('each detail fixture agrees with its list row on the shared fields', () => {
    // A detail page that contradicts the row it was opened from is worse than a missing one: it
    // teaches the reader to distrust the numbers.
    const mismatches: string[] = [];

    for (const adapter of Object.values(demoAdapterDetails)) {
      for (const row of adapter.recentCalls) {
        const detail = demoAdapterCalls[row.id];
        if (!detail) {
          continue;
        }

        const compare: [string, unknown, unknown][] = [
          ['adapterName', detail.adapterName, adapter.name],
          ['operation', detail.operation, row.operation],
          ['groupName', detail.groupName, row.groupName],
          ['durationMs', detail.durationMs, row.durationMs],
          ['attempts', detail.attempts, row.attempts],
          ['outcome', detail.outcome, row.outcome],
          ['statusCode', detail.statusCode, row.statusCode],
          ['correlationId', detail.correlationId, row.correlationId],
          ['tagsJson', detail.tagsJson, row.tagsJson],
        ];

        for (const [field, detailValue, rowValue] of compare) {
          if (detailValue !== rowValue) {
            mismatches.push(`${row.id}.${field}: detail=${String(detailValue)} row=${String(rowValue)}`);
          }
        }
      }
    }

    expect(mismatches).toEqual([]);
  });

  it('no detail fixture is orphaned', () => {
    // An entry nothing links to is dead weight that drifts out of shape unnoticed.
    const referenced = new Set(Object.values(demoAdapterDetails).flatMap((adapter) =>
      adapter.recentCalls.map((call) => call.id)));

    expect(Object.keys(demoAdapterCalls).filter((id) => !referenced.has(id))).toEqual([]);
  });
});

// The Metrics nav is gated on addons.metrics, so the demo has to serve the page it advertises: the registry
// and the series route, including the controls the page sends (breakdown, filters, overlays, percentile).
describe('demo metrics fixtures', () => {
  const adapter = createDemoAdapter(false);
  const get = async <T>(url: string): Promise<T> => {
    const response = await adapter({ url, baseURL: '', method: 'get' } as InternalAxiosRequestConfig);

    return response.data as T;
  };
  const series = (name: string, query = '') => get<MetricSeries>(`/metrics/${encodeUrlSafeId(name)}/series?${query}`);

  it('reports the metrics addon and the 7.5.0 version', async () => {
    const addons = await get<WarpAddonsInfo>('/addons');

    expect(addons.metrics).toBe(true);
    expect(addons.version).toBe('7.5.0');
  });

  it('serves the instrument registry', async () => {
    const list = await get<MetricInstrument[]>('/metrics');

    expect(list.map((x) => x.name)).toEqual(demoMetrics.map((x) => x.name));
    expect(list.some((x) => x.kind === MetricKind.Counter)).toBe(true);
    expect(list.some((x) => x.kind === MetricKind.Histogram)).toBe(true);
    expect(list.some((x) => x.collapsedAt !== null)).toBe(true);
  });

  it('serves a series for every instrument in the registry', async () => {
    for (const instrument of demoMetrics) {
      const result = await series(instrument.name);

      expect(result.instrument).toBe(instrument.name);
      expect(result.kind).toBe(instrument.kind);
      expect(result.series.length).toBeGreaterThan(0);
      expect(result.series[0].points.length).toBeGreaterThan(0);
    }
  });

  it('breaks a counter down by tag, with overlays dashed against the same values', async () => {
    const to = Date.now();
    const from = to - 6 * 3_600_000;
    const query = `from=${encodeURIComponent(new Date(from).toISOString())}&to=${encodeURIComponent(new Date(to).toISOString())}&breakdown=result&compare=1d,7d`;
    const result = await series('payment.results', query);
    const current = result.series.filter((x) => x.compare === null).map((x) => x.name);

    expect(current).toEqual(expect.arrayContaining(['ok', 'declined', 'error']));
    expect(result.series.filter((x) => x.compare === '1d').map((x) => x.name).sort()).toEqual([...current].sort());
    expect(result.series.some((x) => x.compare === '7d')).toBe(true);
    // an overlay is shifted forward onto the current range
    const overlay = result.series.find((x) => x.compare === '1d')!;
    const first = Date.parse(overlay.points[0].timestamp);

    expect(first).toBeGreaterThanOrEqual(from - 3_600_000);
    expect(first).toBeLessThanOrEqual(to);
  });

  it('honours a filter with a prefix value', async () => {
    const all = await series('http.server.request.duration', 'breakdown=http.response.status_code');
    const filtered = await series('http.server.request.duration', `breakdown=http.response.status_code&filter=${encodeURIComponent('http.response.status_code:5*')}`);

    expect(all.series.map((x) => x.name)).toEqual(expect.arrayContaining(['200', '500']));
    expect(filtered.series.map((x) => x.name).sort()).toEqual(['500', '503']);
  });

  it('adds a percentile to a histogram only when asked', async () => {
    const without = await series('http.server.request.duration');
    const withP95 = await series('http.server.request.duration', 'percentile=95');

    expect(without.series[0].points.every((x) => x.percentile === null)).toBe(true);
    expect(withP95.series[0].points.some((x) => x.percentile !== null)).toBe(true);
  });

  it('flags the newest bucket as still filling and leaves it out of the headlines', async () => {
    const to = Date.now();
    const query = `from=${encodeURIComponent(new Date(to - 3_600_000).toISOString())}&to=${encodeURIComponent(new Date(to).toISOString())}&compare=1d`;
    const result = await series('payment.results', query);
    const current = result.series.find((x) => x.compare === null)!;
    const overlay = result.series.find((x) => x.compare === '1d')!;
    const complete = current.points.filter((x) => !x.incomplete);

    expect(current.points[current.points.length - 1].incomplete).toBe(true);
    expect(overlay.points.every((x) => !x.incomplete)).toBe(true);
    expect(result.headlines.map((x) => x.compare)).toEqual([null, '1d']);
    expect(result.headlines[0].value).toBe(complete.reduce((acc, x) => acc + x.sum, 0));
    expect(Date.parse(result.completeUntil)).toBe(Date.parse(current.points.find((x) => x.incomplete)!.timestamp));
  });

  it('carries the bucket length of every point, so the page can chart a rate', async () => {
    const to = Date.now();
    const range = (hours: number) =>
      `from=${encodeURIComponent(new Date(to - hours * 3_600_000).toISOString())}&to=${encodeURIComponent(new Date(to).toISOString())}`;

    expect((await series('payment.results', range(1))).series[0].points.every((x) => x.bucketSeconds === 300)).toBe(true);
    expect((await series('payment.results', range(24))).series[0].points.every((x) => x.bucketSeconds === 3600)).toBe(true);
  });

  it('answers 404 for an unknown instrument', async () => {
    await expect(series('no.such.metric')).rejects.toMatchObject({ response: { status: 404 } });
  });
});

// The health board (#326) is a tab of the Metrics page, so demo mode serves it too: every tile reads a demo instrument
// and the board honours the window it is asked for.
describe('demo metrics board fixtures', () => {
  const adapter = createDemoAdapter(false);
  const call = async <T>(method: string, url: string, params?: Record<string, string>): Promise<T> => {
    const response = await adapter({ url, baseURL: '', method, params } as InternalAxiosRequestConfig);

    return response.data as T;
  };

  it('every tile reads an instrument of the demo registry', async () => {
    const tiles = await call<MetricBoardTile[]>('get', '/metrics/board/tiles');
    const names = new Set(demoMetrics.map((x) => x.name));

    expect(tiles.length).toBeGreaterThan(0);
    expect(tiles.filter((x) => !names.has(x.instrument))).toEqual([]);
  });

  it('evaluates every tile over the requested window, with a coloured tile and a sparkline inside it', async () => {
    const to = Date.now();
    const from = to - 6 * 3_600_000;
    const board = await call<MetricBoard>('get', '/metrics/board', { from: new Date(from).toISOString(), to: new Date(to).toISOString() });

    expect(Date.parse(board.from)).toBe(from);
    expect(board.tiles.every((x) => x.current !== null && x.dayAgo !== null && x.weekAgo !== null)).toBe(true);
    expect(board.tiles.some((x) => x.status !== null)).toBe(true);
    expect(board.tiles.every((x) => x.sparkline.length > 0)).toBe(true);
    expect(board.tiles.flatMap((x) => x.sparkline).every((p) => Date.parse(p.timestamp) < to)).toBe(true);
  });

  it('cuts the values at the still-filling bucket like the server, and runs the sparklines to the end', () => {
    const now = Date.UTC(2026, 4, 25, 14, 37, 10);
    const live = demoMetricBoard(new Date(now - 6 * 3_600_000).toISOString(), new Date(now).toISOString(), now);
    const past = demoMetricBoard(new Date(now - 30 * 3_600_000).toISOString(), new Date(now - 24 * 3_600_000).toISOString(), now);
    const fresh = demoMetricBoard(new Date(now - 60_000).toISOString(), new Date(now).toISOString(), now);

    expect(live.completeUntil).toBe('2026-05-25T14:35:00.000Z');
    expect(live.annotationsTruncated).toBe(false);
    expect(live.tiles.every((x) => x.current !== null)).toBe(true);
    expect(live.tiles.every((x) => Date.parse(x.sparkline[x.sparkline.length - 1].timestamp) >= Date.parse(live.completeUntil))).toBe(true);
    expect(past.completeUntil).toBe(past.to);
    // Nothing complete yet: no values, and a tile with a rule reads NoData.
    expect(fresh.completeUntil).toBe(fresh.from);
    expect(fresh.tiles.every((x) => x.current === null && x.dayAgo === null && x.weekAgo === null)).toBe(true);
    expect(fresh.tiles.every((x) => x.status === (x.tile.statusRule === MetricTileStatusRule.None ? null : SloState.NoData))).toBe(true);
  });

  it('accepts tile upserts and deletes', async () => {
    await expect(call('post', '/metrics/board/tiles')).resolves.toEqual({ id: 0 });
    await expect(call('delete', '/metrics/board/tiles/1')).resolves.toEqual({});
  });
});

// Annotations (#333) show on the metric chart, the board tiles and the SLO page, so demo mode serves them too: every
// note points at a demo instrument or objective, a read honours its target, and the board attaches them to tiles.
describe('demo annotation fixtures', () => {
  const adapter = createDemoAdapter(false);
  const call = async <T>(method: string, url: string, params?: Record<string, string | number>): Promise<T> => {
    const response = await adapter({ url, baseURL: '', method, params } as InternalAxiosRequestConfig);

    return response.data as T;
  };

  it('every note points at a demo instrument or a demo objective', async () => {
    const all = await call<MetricAnnotationList>('get', '/annotations');
    const instruments = new Set(demoMetrics.map((x) => x.name));
    const objectives = new Set(demoSlos().items.map((x) => x.id));

    expect(all.items.length).toBeGreaterThan(0);
    expect(all.items.filter((x) => x.target === MetricAnnotationTarget.Instrument && !instruments.has(x.instrument ?? ''))).toEqual([]);
    expect(all.items.filter((x) => x.target === MetricAnnotationTarget.Slo && !objectives.has(x.sloDefinitionId ?? 0))).toEqual([]);
  });

  it('reads one target at a time, newest first', async () => {
    const slo = await call<MetricAnnotationList>('get', '/annotations', { slo: 1 });
    const http = await call<MetricAnnotationList>('get', '/annotations', { instrument: 'http.server.request.duration' });

    expect(slo.items.length).toBeGreaterThan(0);
    expect(slo.items.every((x) => x.target === MetricAnnotationTarget.Slo && x.sloDefinitionId === 1)).toBe(true);
    expect(http.items.every((x) => x.instrument === 'http.server.request.duration')).toBe(true);
  });

  it('attaches notes to board tiles and accepts note upserts and deletes', async () => {
    const to = Date.now();
    const board = await call<MetricBoard>('get', '/metrics/board', { from: new Date(to - 24 * 3_600_000).toISOString(), to: new Date(to).toISOString() });

    expect(board.tiles.every((x) => Array.isArray(x.annotations))).toBe(true);
    expect(board.tiles.some((x) => x.annotations.length > 0)).toBe(true);
    await expect(call('post', '/annotations')).resolves.toEqual({ id: 0 });
    await expect(call('delete', '/annotations/1')).resolves.toEqual({});
  });

  // MetricAnnotationMatch.Covers: overlap is enough to show a note on a tile, only covering acknowledges it.
  it('shows every overlapping note on a tile but acknowledges only with a covering one', () => {
    const tile = (tagFilter: string | null): MetricBoardTile => ({ ...demoBoardTiles[1], instrument: 'payment.results', tagFilter });
    const note = (tagFilter: string | null, instrument = 'payment.results'): MetricAnnotation => ({
      ...demoAnnotations(0)[0],
      instrument,
      tagFilter,
    });

    expect(demoNoteAppliesTo(note('result=error'), tile(null))).toBe(true);
    expect(demoNoteCoversTile(note('result=error'), tile(null))).toBe(false);
    expect(demoNoteAppliesTo(note('code=503'), tile('code=5*'))).toBe(true);
    expect(demoNoteCoversTile(note('code=503'), tile('code=5*'))).toBe(false);
    expect(demoNoteCoversTile(note('code=5*'), tile('code=503,region=eu'))).toBe(true);
    expect(demoNoteCoversTile(note(null), tile('code=503'))).toBe(true);
    expect(demoNoteAppliesTo(note('code=4*'), tile('code=5*'))).toBe(false);
    expect(demoNoteAppliesTo(note(null, 'other'), tile(null))).toBe(false);
    // An unparseable tile filter: the note still shows, but nothing proves it covers the tile.
    expect(demoNoteAppliesTo(note('code=5*'), tile('code'))).toBe(true);
    expect(demoNoteCoversTile(note('code=5*'), tile('code'))).toBe(false);
    expect(demoNoteAppliesTo(note('code'), tile(null))).toBe(false);
  });

  it('never acknowledges a board tile with a note that only overlaps it', async () => {
    const to = Date.now();
    const board = await call<MetricBoard>('get', '/metrics/board', { from: new Date(to - 24 * 3_600_000).toISOString(), to: new Date(to).toISOString() });
    const unfiltered = board.tiles.find((x) => x.tile.instrument === 'http.server.request.duration' && x.tile.tagFilter === null)!;

    // The 5xx acknowledged-noise note is shown on the unfiltered requests tile but is about part of it only.
    expect(unfiltered.annotations.some((a) => a.acknowledgedNoise && a.tagFilter !== null)).toBe(true);
    expect(unfiltered.annotations.some((a) => demoNoteCoversTile(a, unfiltered.tile))).toBe(false);
    expect(unfiltered.status).toBe(unfiltered.unacknowledgedStatus);
    for (const result of board.tiles.filter((x) => x.status === SloState.Acknowledged)) {
      expect(result.annotations.some((a) => a.acknowledgedNoise && demoNoteCoversTile(a, result.tile))).toBe(true);
    }
  });
});

// The post-deploy verdict (#325 follow-up) and the deploy history are on the Applications detail page, and the deploys
// also mark the metric charts, so demo mode serves both. A verdict check must name a tile or objective the reader can
// find on the Board and SLOs pages, and must follow the server's rules (scope, superseded, coverage).
describe('demo deploy and verdict fixtures', () => {
  const adapter = createDemoAdapter(false);
  const call = async <T>(method: string, url: string, params?: Record<string, string>): Promise<T> => {
    const response = await adapter({ url, baseURL: '', method, params } as InternalAxiosRequestConfig);

    return response.data as T;
  };
  const verdictOf = (application: string, extra: Record<string, string> = {}) =>
    call<DeployVerdictModel>('get', '/applications/verdict', { application, ...extra });

  it('filters the deploys by application and a [from, to) window, newest first', async () => {
    const all = await call<ApplicationDeployModel[]>('get', '/applications/deploys');
    const orders = await call<ApplicationDeployModel[]>('get', '/applications/deploys', { application: 'orders-api' });
    const newest = Date.parse(orders[0].firstSeenAt);
    const window = await call<ApplicationDeployModel[]>('get', '/applications/deploys', {
      application: 'orders-api',
      from: new Date(newest).toISOString(),
      to: new Date(newest + 1).toISOString(),
    });
    const byId = await call<ApplicationDeployModel[]>('get', `/applications/${encodeUrlSafeId('orders-api')}/deploys`);

    expect(all.length).toBeGreaterThan(orders.length);
    expect(orders.every((x) => x.application === 'orders-api')).toBe(true);
    expect(orders.map((x) => Date.parse(x.firstSeenAt))).toEqual([...orders.map((x) => Date.parse(x.firstSeenAt))].sort((a, b) => b - a));
    expect(window.map((x) => x.id)).toEqual([orders[0].id]);
    expect(byId).toEqual(orders);
  });

  it('every check names a demo tile with a status rule or an enabled demo objective, in scope for the application', async () => {
    const tiles = await call<MetricBoardTile[]>('get', '/metrics/board/tiles');
    const objectives = demoSlos().items;
    const problems: string[] = [];

    for (const application of ['orders-api', 'checkout-worker', 'billing-service']) {
      const verdict = await verdictOf(application);
      for (const check of verdict.checks) {
        const source = check.source === 'tile'
          ? tiles.find((x) => x.id === check.id && x.statusRule !== MetricTileStatusRule.None)
          : objectives.find((x) => x.id === check.id && x.enabled);
        if (!source || source.name !== check.name || source.application !== check.application) {
          problems.push(`${application}: ${check.source} ${check.id} "${check.name}"`);
        }
        if (check.application !== null && check.application !== application) {
          problems.push(`${application}: ${check.source} ${check.id} is scoped to ${check.application}`);
        }
      }
    }

    expect(problems).toEqual([]);
  });

  it('gives the demo applications different verdicts, with the coverage counted from the checks', async () => {
    const verdicts = await Promise.all(['orders-api', 'checkout-worker', 'billing-service'].map((x) => verdictOf(x)));

    expect(new Set(verdicts.map((x) => x.verdict)).size).toBe(3);
    for (const verdict of verdicts) {
      expect(verdict.checksTotal).toBe(verdict.checks.length);
      expect(verdict.checksWithData).toBe(verdict.checks.filter((x) => x.state !== 'noData').length);
      expect(verdict.summary).toContain(`(${verdict.checksWithData} of ${verdict.checksTotal} checks had data)`);
    }
  });

  it('judges a superseded deploy over its own lifetime, with tile checks and no SLO checks', async () => {
    const verdict = await verdictOf('orders-api', { version: '2.3.0' });

    expect(verdict.deploy.supersededBy).toBe('2.3.1');
    expect(verdict.window.to).toBe(demoDeploys.find((x) => x.id === 'dep-oa-231')!.firstSeenAt);
    expect(verdict.window.settled).toBe(true);
    expect(verdict.checks.length).toBeGreaterThan(0);
    expect(verdict.checks.every((x) => x.source === 'tile')).toBe(true);
  });

  it('turns a thin pass into noData under minCoverage, but never hides a failure', async () => {
    const pass = await verdictOf('orders-api');
    const fail = await verdictOf('billing-service');

    expect(pass.checksWithData).toBeLessThan(pass.checksTotal);
    expect((await verdictOf('orders-api', { minCoverage: '1' })).verdict).toBe('noData');
    expect((await verdictOf('orders-api', { minCoverage: '0.5' })).verdict).toBe(pass.verdict);
    expect(fail.verdict).toBe('fail');
    expect((await verdictOf('billing-service', { minCoverage: '1' })).verdict).toBe('fail');
  });

  it('compares the coverage as a share with an epsilon, so an exact share is never lost to floating point', () => {
    expect(coverageMet(14, 25, 0.56)).toBe(true);
    expect(coverageMet(7, 25, 0.28)).toBe(true);
    expect(coverageMet(13, 25, 0.56)).toBe(false);
    expect(coverageMet(6, 25, 0.28)).toBe(false);
    expect(coverageMet(0, 0, 0.5)).toBe(false);
  });

  it('answers 404 with the server { error } body when no deploy matches', async () => {
    await expect(verdictOf('orders-api', { version: '9.9.9' })).rejects.toMatchObject({
      response: { status: 404, data: { error: expect.stringContaining('No recorded deploy') } },
    });
    await expect(verdictOf('no-such-app')).rejects.toMatchObject({ response: { status: 404 } });
  });
});
