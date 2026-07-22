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
