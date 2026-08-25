import { describe, expect, it } from 'vitest';
import {
  FAMILIES,
  OVERFLOW_BUCKET,
  buildFamilySeries,
  buildFamilyTable,
  buildOutcomeRows,
  historyTokens,
  parseCounterKey,
  percentileFromBuckets,
  presentFamilies,
  type CounterEntry,
  type FamilyDef,
  type FamilyId,
} from './counterModel';

const JOB_TYPE = 'Inventhor.Api.Jobs.AccrueLeaveJob, Inventhor.Api, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null';

function family(id: FamilyId): FamilyDef {
  const found = FAMILIES.find((f) => f.id === id);
  expect(found, `no family definition for ${id}`).toBeDefined();

  return found!;
}

describe('parseCounterKey', () => {
  it('routes the global outcome family to outcomes', () => {
    expect(parseCounterKey('stats:failed-retry-exhausted')).toEqual({
      family: 'outcomes',
      application: null,
      subject: 'failed-retry-exhausted',
      token: 'count',
      bucketMs: null,
      history: false,
    });
  });

  it('splits the job-type and handler dimensions into separate families', () => {
    expect(parseCounterKey(`jobstat:type:${JOB_TYPE}:succeeded`)?.family).toBe('jobtypes');
    expect(parseCounterKey(`jobstat:handler:${JOB_TYPE}:succeeded`)?.family).toBe('handlers');
  });

  it('keeps the assembly-qualified type name intact as the subject', () => {
    // The name contains commas and equals signs but never a colon (Core sanitizes it), so the colon-delimited
    // key must survive the round trip unchanged — display shortening happens later, never in the subject.
    expect(parseCounterKey(`jobstat:type:${JOB_TYPE}:dur`)?.subject).toBe(JOB_TYPE);
  });

  it('reads the per-application slice off the -app prefixes', () => {
    const parsed = parseCounterKey(`jobstat-app:Inventhor.Api:type:${JOB_TYPE}:failed`);

    expect(parsed?.application).toBe('Inventhor.Api');
    expect(parsed?.family).toBe('jobtypes');
    expect(parsed?.token).toBe('failed');
  });

  it('marks history keys so they stay out of the lifetime table', () => {
    const parsed = parseCounterKey(`jobstat:type:${JOB_TYPE}:hist:succeeded`);

    expect(parsed?.history).toBe(true);
    expect(parsed?.token).toBe('succeeded');
  });

  it('parses latency histogram buckets into a bucket bound', () => {
    expect(parseCounterKey(`jobstat:type:${JOB_TYPE}:pct:250`)).toMatchObject({ token: 'pct', bucketMs: 250 });
  });

  it('lands queue-wait and backlog on the same queue subject', () => {
    expect(parseCounterKey('qwait:default:count')).toMatchObject({ family: 'queues', subject: 'default', token: 'count' });
    expect(parseCounterKey('qbacklog:default:depth')).toMatchObject({ family: 'queues', subject: 'default', token: 'depth' });
  });

  it('folds the adapter operation and group axes into the subject', () => {
    expect(parseCounterKey('adapter:stripe:op:CreateCharge:failed')).toMatchObject({
      family: 'adapters',
      subject: 'stripe op=CreateCharge',
      token: 'failed',
    });
    expect(parseCounterKey('endpoint:GET /orders/{id}:grp:web:success')).toMatchObject({
      family: 'endpoints',
      subject: 'GET /orders/{id} grp=web',
      token: 'success',
    });
  });

  it('parses the three client-event shapes', () => {
    expect(parseCounterKey('clientevent:total:error:count')).toMatchObject({ subject: 'type error', history: false });
    expect(parseCounterKey('clientevent:total:error:hist')).toMatchObject({ subject: 'type error', history: true });
    expect(parseCounterKey('clientevent:name:error:TypeError:count')).toMatchObject({ subject: 'error TypeError' });
    expect(parseCounterKey('clientevent:vital:LCP:dur')).toMatchObject({ subject: 'LCP', token: 'dur' });
  });

  it('parses the trend-only families', () => {
    expect(parseCounterKey('errorgroup:efc5a281242505cf81919794016a2f48')).toMatchObject({ family: 'issues', history: true });
    expect(parseCounterKey('warpsys:records-dropped:adapter')).toMatchObject({ family: 'system', token: 'dropped' });
  });

  it('returns null for keys it does not recognise rather than guessing', () => {
    expect(parseCounterKey('someaddon:whatever:1')).toBeNull();
    expect(parseCounterKey('jobstat:unknown-dimension:x:count')).toBeNull();
    expect(parseCounterKey('')).toBeNull();
  });
});

describe('buildFamilyTable', () => {
  const counters: CounterEntry[] = [
    { key: `jobstat:type:${JOB_TYPE}:succeeded`, value: 8 },
    { key: `jobstat:type:${JOB_TYPE}:failed`, value: 2 },
    { key: `jobstat:type:${JOB_TYPE}:dur`, value: 500 },
    { key: `jobstat:type:${JOB_TYPE}:pct:50`, value: 9 },
    { key: `jobstat:type:${JOB_TYPE}:pct:250`, value: 1 },
    { key: `jobstat:handler:${JOB_TYPE}:succeeded`, value: 99 },
    { key: 'stats:succeeded', value: 1000 },
  ];

  it('pivots one row per dimension with the outcome tokens as columns', () => {
    const table = buildFamilyTable(counters, family('jobtypes'));

    expect(table.rows).toHaveLength(1);
    expect(table.columns).toEqual(['succeeded', 'failed']);
    expect(table.rows[0].values).toEqual({ succeeded: 8, failed: 2, dur: 500 });
  });

  it('shortens the assembly-qualified name and keeps the namespace as a subtitle', () => {
    const row = buildFamilyTable(counters, family('jobtypes')).rows[0];

    expect(row.label).toBe('AccrueLeaveJob');
    expect(row.sub).toBe('Inventhor.Api.Jobs');
    expect(row.subject).toBe(JOB_TYPE);
  });

  it('derives the average from the duration sum instead of showing it as a column', () => {
    const table = buildFamilyTable(counters, family('jobtypes'));

    // 500 ms over 10 executions — the raw 500 must never appear as a count-shaped column.
    expect(table.rows[0].avgMs).toBe(50);
    expect(table.columns).not.toContain('dur');
    expect(table.hasAvg).toBe(true);
  });

  it('derives the percentile from the histogram buckets', () => {
    const table = buildFamilyTable(counters, family('jobtypes'));

    expect(table.percentileLabels).toEqual(['p95']);
    expect(table.rows[0].percentiles.map((p) => p.ms)).toEqual([250]);
  });

  it('ignores counters belonging to other families', () => {
    expect(buildFamilyTable(counters, family('handlers')).rows).toHaveLength(1);
    expect(buildFamilyTable(counters, family('adapters')).rows).toHaveLength(0);
  });

  it('keeps the cluster-wide and per-application slices as separate rows', () => {
    const table = buildFamilyTable(
      [
        { key: `jobstat:type:${JOB_TYPE}:succeeded`, value: 8 },
        { key: `jobstat-app:Inventhor.Api:type:${JOB_TYPE}:succeeded`, value: 8 },
      ],
      family('jobtypes'),
    );

    // Merging them would double every count: the per-app slice is a slice OF the global one, not extra work.
    expect(table.hasApplication).toBe(true);
    expect(table.rows.map((r) => r.application)).toEqual([null, 'Inventhor.Api']);
  });

  it('uses only the declared count token as the average denominator', () => {
    // Queue rows carry a backlog GAUGE alongside the wait fold. Counting `depth` as an observation would
    // silently deflate the average wait.
    const table = buildFamilyTable(
      [
        { key: 'qwait:default:count', value: 4 },
        { key: 'qwait:default:dur', value: 400 },
        { key: 'qbacklog:default:depth', value: 96 },
      ],
      family('queues'),
    );

    expect(table.rows[0].avgMs).toBe(100);
    expect(table.rows[0].values.depth).toBe(96);
  });

  it('reports queues at p95 AND p99 — a queue is judged on its tail, not its middle', () => {
    const table = buildFamilyTable(
      [
        // 96 fast waits and 4 slow ones: p95 lands in the 1s rung, p99 in the 10s rung.
        { key: 'qwait:default:pct:100', value: 96 },
        { key: 'qwait:default:pct:1000', value: 3 },
        { key: 'qwait:default:pct:10000', value: 1 },
      ],
      family('queues'),
    );

    expect(table.percentileLabels).toEqual(['p95', 'p99']);
    expect(table.rows[0].percentiles).toEqual([
      { label: 'p95', ms: 100, overflow: false },
      { label: 'p99', ms: 1000, overflow: false },
    ]);
  });

  it('reports each queue percentile independently of the others', () => {
    const table = buildFamilyTable([{ key: 'qwait:default:pct:250', value: 10 }], family('queues'));

    // One rung holds everything, so both percentiles resolve to it rather than one falling through.
    expect(table.rows[0].percentiles.map((p) => p.ms)).toEqual([250, 250]);
  });

  it('reads web vitals at p75, the percentile Core Web Vitals scores on', () => {
    expect(buildFamilyTable([{ key: 'clientevent:vital:LCP:pct:2500', value: 1 }], family('client')).percentileLabels).toEqual(['p75']);
  });
});

describe('percentileFromBuckets', () => {
  it('returns the smallest bound whose cumulative count reaches the percentile', () => {
    expect(percentileFromBuckets(new Map([[50, 9], [250, 1]]), 0.95)).toEqual({ ms: 250, overflow: false });
    expect(percentileFromBuckets(new Map([[50, 9], [250, 1]]), 0.5)).toEqual({ ms: 50, overflow: false });
  });

  it('reports the catch-all rung as an overflow above the last real bound', () => {
    // int.MaxValue is a bucket bound, not a latency — rendering it as one would claim a 24-day job.
    expect(percentileFromBuckets(new Map([[50, 1], [OVERFLOW_BUCKET, 9]]), 0.95)).toEqual({ ms: 50, overflow: true });
  });

  it('returns null when nothing was measured', () => {
    expect(percentileFromBuckets(new Map(), 0.95)).toEqual({ ms: null, overflow: false });
  });
});

describe('buildOutcomeRows', () => {
  it('derives the unsuccessful umbrella from failed + deleted', () => {
    const rows = buildOutcomeRows([
      { key: 'stats:failed', value: 3 },
      { key: 'stats:deleted', value: 4 },
    ]);

    expect(rows.find((r) => r.key === 'stats:unsuccessful')?.value).toBe(7);
  });

  it('shows the unattributed remainder rather than a total that does not add up', () => {
    const rows = buildOutcomeRows([
      { key: 'stats:failed', value: 10 },
      { key: 'stats:failed-retry-exhausted', value: 4 },
    ]);

    expect(rows.find((r) => r.key === 'stats:failed#unattributed')?.value).toBe(6);
  });

  it('surfaces the impossible direction when reasons exceed their total', () => {
    const rows = buildOutcomeRows([
      { key: 'stats:failed', value: 1 },
      { key: 'stats:failed-retry-exhausted', value: 4 },
    ]);

    expect(rows.find((r) => r.key === 'stats:failed#over-attributed')).toMatchObject({ value: 3, warn: true });
  });

  it('still renders a stats key no group claims', () => {
    // A new outcome key added in Core must not vanish just because this page has not been taught about it.
    const rows = buildOutcomeRows([{ key: 'stats:something-new', value: 5 }]);

    expect(rows).toEqual([{ key: 'stats:something-new', label: 'stats:something-new', value: 5, depth: 0 }]);
  });
});

describe('history series', () => {
  const points = [
    { hour: '2026-08-12T06:00:00Z', key: `jobstat:type:${JOB_TYPE}:hist:succeeded`, value: 2 },
    { hour: '2026-08-12T07:00:00Z', key: `jobstat:type:${JOB_TYPE}:hist:succeeded`, value: 3 },
    { hour: '2026-08-12T06:00:00Z', key: `jobstat:type:${JOB_TYPE}:hist:dur`, value: 600000 },
  ];

  it('offers each recorded metric separately so counts never share an axis with a duration sum', () => {
    expect(historyTokens(points, 'jobtypes')).toEqual(['succeeded', 'dur']);
  });

  it('builds one series per dimension for the selected metric only', () => {
    const series = buildFamilySeries(points, family('jobtypes'), 'succeeded');

    expect(series).toHaveLength(1);
    expect(series[0].label).toBe('AccrueLeaveJob');
    expect(series[0].total).toBe(5);
    expect(series[0].byHour.get(Date.parse('2026-08-12T06:00:00Z'))).toBe(2);
  });

  it('colors outcome series by their full key so the built-in palette still matches', () => {
    const series = buildFamilySeries([{ hour: '2026-08-12T06:00:00Z', key: 'stats:failed', value: 1 }], family('outcomes'), 'count');

    expect(series[0].colorKey).toBe('stats:failed');
  });
});

describe('presentFamilies', () => {
  it('lists only families that have data, in display order', () => {
    const families = presentFamilies(
      [{ key: 'stats:succeeded', value: 1 }, { key: 'qwait:default:count', value: 1 }],
      ['errorgroup:abc'],
    );

    expect(families.map((f) => f.id)).toEqual(['outcomes', 'queues', 'issues']);
  });

  it('routes unrecognised keys to the other family so nothing is hidden', () => {
    expect(presentFamilies([{ key: 'someaddon:thing', value: 1 }], []).map((f) => f.id)).toEqual(['other']);
  });
});
