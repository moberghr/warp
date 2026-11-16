import { describe, it, expect } from 'vitest';
import type { InternalAxiosRequestConfig } from 'axios';
import { demoAdapterDetails, demoAdapterCalls } from './data/adapters';
import { createDemoAdapter } from './adapter';
import { demoMetrics } from './data/metrics';
import { encodeUrlSafeId } from '@/lib/urlSafeId';
import { MetricKind } from '@/types/metrics';
import type { MetricInstrument, MetricSeries } from '@/types/metrics';
import type { WarpAddonsInfo } from '@/types';

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

  it('answers 404 for an unknown instrument', async () => {
    await expect(series('no.such.metric')).rejects.toMatchObject({ response: { status: 404 } });
  });
});
