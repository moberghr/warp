import { describe, it, expect } from 'vitest';
import { queryKeys, queryScopes, queryClient } from './queryClient';

describe('queryKeys', () => {
  it('builds stable, structured keys', () => {
    expect(queryKeys.jobs('failed', 0, 20)).toEqual(['jobs', 'failed', 0, 20]);
    expect(queryKeys.failedJobsByType('T', 1, 50)).toEqual(['jobs', 'failed', 'by-type', 'T', 1, 50]);
    expect(queryKeys.batchJobs('b', 0, 20, 'processing')).toEqual(['batches', 'b', 'jobs', 'processing', 0, 20]);
    expect(queryKeys.trace('x')).toEqual(['trace', 'x']);
  });

  it('defaults optional segments (undefined state → "all", no range → "24h")', () => {
    expect(queryKeys.messages(undefined, 0, 20)).toEqual(['messages', 'all', 0, 20]);
    expect(queryKeys.batches(undefined, 0, 20)).toEqual(['batches', 'all', 0, 20]);
    expect(queryKeys.dashboardStats()).toEqual(['dashboard', 'stats', '24h']);
  });

  it('builds every remaining key with its expected shape', () => {
    expect(queryKeys.dashboardStatus).toEqual(['dashboard', 'status']);
    expect(queryKeys.dashboardStats('7d')).toEqual(['dashboard', 'stats', '7d']);
    expect(queryKeys.statsHistory(48)).toEqual(['stats', 'history', 48]);
    expect(queryKeys.failedJobTypes).toEqual(['jobs', 'failed', 'types']);
    expect(queryKeys.job('j')).toEqual(['job', 'j']);
    expect(queryKeys.jobLogs('j')).toEqual(['job', 'j', 'logs']);
    expect(queryKeys.messageJobs('m', 0, 20, 'processing')).toEqual(['messages', 'm', 'jobs', 'processing', 0, 20]);
    expect(queryKeys.messageJobs('m', 0, 20)).toEqual(['messages', 'm', 'jobs', 'all', 0, 20]);
    expect(queryKeys.messageJobCounts('m')).toEqual(['messages', 'm', 'jobs', 'counts']);
    expect(queryKeys.batchJobCounts('b')).toEqual(['batches', 'b', 'jobs', 'counts']);
    expect(queryKeys.recurring(0, 20)).toEqual(['recurring', 0, 20]);
    expect(queryKeys.recurringDetail(3)).toEqual(['recurring', 3]);
    expect(queryKeys.recurringJobs(3, 0, 20)).toEqual(['recurring', 3, 'jobs', 0, 20]);
    expect(queryKeys.servers).toEqual(['servers']);
    expect(queryKeys.serverDetail('s')).toEqual(['servers', 's']);
    expect(queryKeys.serverTasks('s')).toEqual(['servers', 's', 'tasks']);
    expect(queryKeys.serverLogs('s', 0, 20, 'Heartbeat')).toEqual(['servers', 's', 'logs', 'Heartbeat', 0, 20]);
    expect(queryKeys.serverLogs('s', 0, 20)).toEqual(['servers', 's', 'logs', 'all', 0, 20]);
    expect(queryKeys.workerDetail('w')).toEqual(['workers', 'w']);
    expect(queryKeys.workerLogs('w', 0, 20)).toEqual(['workers', 'w', 'logs', 0, 20]);
    expect(queryKeys.counters).toEqual(['counters']);
    expect(queryKeys.countersHistory(24)).toEqual(['counters', 'history', 24]);
    expect(queryKeys.concurrencyLimits).toEqual(['concurrency-limits']);
    expect(queryKeys.rateLimits).toEqual(['rate-limits']);
    expect(queryKeys.detail('d')).toEqual(['detail', 'd']);
  });

  it('exposes every scope as a single-segment prefix', () => {
    expect(Object.values(queryScopes).every((s) => s.length === 1)).toBe(true);
    expect(queryScopes.workers).toEqual(['workers']);
    expect(queryScopes.recurring).toEqual(['recurring']);
    expect(queryScopes.servers).toEqual(['servers']);
  });
});

describe('queryScopes', () => {
  it('are prefixes of their detailed keys, so prefix invalidation matches every variant', () => {
    const jobsKey = queryKeys.jobs('failed', 3, 50);
    expect(jobsKey.slice(0, queryScopes.jobs.length)).toEqual([...queryScopes.jobs]);

    const batchesKey = queryKeys.batches('processing', 0, 20);
    expect(batchesKey.slice(0, queryScopes.batches.length)).toEqual([...queryScopes.batches]);
  });
});

describe('queryClient retry policy', () => {
  const retry = queryClient.getDefaultOptions().queries!.retry as (n: number, e: unknown) => boolean;

  it('never retries auth/not-found responses', () => {
    for (const status of [401, 403, 404]) {
      expect(retry(0, { response: { status } })).toBe(false);
    }
  });

  it('retries other failures up to 2 attempts', () => {
    expect(retry(0, { response: { status: 500 } })).toBe(true);
    expect(retry(1, {})).toBe(true);
    expect(retry(2, {})).toBe(false);
  });
});
