import { describe, expect, it } from 'vitest';
import { ErrorGroupStatus, ErrorSource } from '@/types/issues';
import { DEFAULT_ISSUES_FILTERS, parseIssuesParams, writeIssuesParams, type IssuesFilters } from './issuesModel';

const write = (filters: IssuesFilters) => writeIssuesParams(new URLSearchParams(), filters);

describe('issues URL parameters', () => {
  const cases: [keyof IssuesFilters, Partial<IssuesFilters>, string][] = [
    ['source', { source: ErrorSource.Job }, 'source=job'],
    ['source', { source: ErrorSource.Endpoint }, 'source=endpoint'],
    ['source', { source: ErrorSource.Adapter }, 'source=adapter'],
    ['source', { source: ErrorSource.Client }, 'source=client'],
    ['status', { status: ErrorGroupStatus.Resolved }, 'status=resolved'],
    ['status', { status: ErrorGroupStatus.Ignored }, 'status=ignored'],
    ['application', { application: 'shop-api' }, 'application=shop-api'],
    ['show4xx', { show4xx: true }, 'show4xx=1'],
  ];

  it.each(cases)('round-trips %s (%j)', (_field, patch, query) => {
    const filters = { ...DEFAULT_ISSUES_FILTERS, ...patch };

    expect(write(filters).toString()).toBe(query);
    expect(parseIssuesParams(write(filters))).toEqual(filters);
  });

  it('covers every filter and lists every key it writes', () => {
    expect(new Set(cases.map(([field]) => field))).toEqual(new Set(Object.keys(DEFAULT_ISSUES_FILTERS)));

    const all = write({ source: ErrorSource.Client, status: ErrorGroupStatus.Ignored, application: 'a', show4xx: true });
    expect(new Set(all.keys())).toEqual(new Set(['source', 'status', 'application', 'show4xx']));
  });

  it('leaves the defaults out of the URL', () => {
    expect(write(DEFAULT_ISSUES_FILTERS).toString()).toBe('');
    expect(parseIssuesParams(new URLSearchParams())).toEqual(DEFAULT_ISSUES_FILTERS);
    expect(write({ ...DEFAULT_ISSUES_FILTERS, status: ErrorGroupStatus.Unresolved }).toString()).toBe('');
  });

  it('keeps other parameters and drops stale filters', () => {
    const params = writeIssuesParams(new URLSearchParams('x=1&source=job&show4xx=1'), { ...DEFAULT_ISSUES_FILTERS, application: 'b' });

    expect(params.toString()).toBe('x=1&application=b');
  });

  it.each(['source=database', 'source=1', 'status=open', 'status=2', 'application=', 'application=%20', 'show4xx=yes'])(
    'ignores the invalid %s and reads the default',
    (query) => {
      expect(parseIssuesParams(new URLSearchParams(query))).toEqual(DEFAULT_ISSUES_FILTERS);
    },
  );
});
