import { describe, expect, it } from 'vitest';
import {
  DEFAULT_JOB_LIST_PARAMS,
  jobsByTypeQueryKeys,
  jobsQueryKeys,
  parseJobListParams,
  parseMetricsDimension,
  writeJobListParams,
  writeMetricsDimension,
  type JobListParams,
} from './jobsModel';

const write = (list: JobListParams) => writeJobListParams(new URLSearchParams(), list);

describe('jobs list URL parameters', () => {
  it.each<[JobListParams, string]>([
    [{ page: 3, type: null }, 'page=3'],
    [{ page: 0, type: 'Acme.SendInvoice' }, 'type=Acme.SendInvoice'],
    [{ page: 2, type: 'Acme.Orders+Ship, Acme' }, 'type=Acme.Orders%2BShip%2C+Acme&page=2'],
  ])('round-trips %j', (list, query) => {
    expect(write(list).toString()).toBe(query);
    expect(parseJobListParams(write(list))).toEqual(list);
  });

  it('leaves the defaults out of the URL and lists every key it writes', () => {
    expect(write(DEFAULT_JOB_LIST_PARAMS).toString()).toBe('');
    expect(parseJobListParams(new URLSearchParams())).toEqual(DEFAULT_JOB_LIST_PARAMS);
    expect(new Set(write({ page: 1, type: 'T' }).keys())).toEqual(new Set(jobsQueryKeys));
  });

  it('keeps other parameters', () => {
    expect(writeJobListParams(new URLSearchParams('x=1&page=9'), { page: 0, type: 'T' }).toString()).toBe('x=1&type=T');
  });

  it.each(['page=abc', 'page=-2', 'page=1.5', 'page=', 'type=', 'type=%20%20'])('ignores the invalid %s and reads the default', (query) => {
    expect(parseJobListParams(new URLSearchParams(query))).toEqual(DEFAULT_JOB_LIST_PARAMS);
  });
});

describe('jobs-by-type metrics dimension', () => {
  it('round-trips the handler dimension and leaves the type default out', () => {
    expect(writeMetricsDimension(new URLSearchParams('page=2'), 'handler').toString()).toBe('page=2&metrics=handler');
    expect(parseMetricsDimension(writeMetricsDimension(new URLSearchParams(), 'handler'))).toBe('handler');
    expect(writeMetricsDimension(new URLSearchParams('metrics=handler'), 'type').toString()).toBe('');
    expect(parseMetricsDimension(new URLSearchParams())).toBe('type');
  });

  it('reads an unknown dimension as the default', () => {
    expect(parseMetricsDimension(new URLSearchParams('metrics=queue'))).toBe('type');
  });

  it('lists its keys', () => {
    expect(new Set([...writeMetricsDimension(new URLSearchParams('page=1'), 'handler').keys()])).toEqual(new Set(jobsByTypeQueryKeys));
  });
});
