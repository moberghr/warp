// The Jobs list pages' URL state, pure. The state is the path (`/jobs/:state`); the query string carries the rest, so
// a shared link, a refresh or a saved view reproduces the list. Defaults are left out of the URL, and an unknown
// value reads as the default. The job list API filters by state only, plus the job type on the failed list, so those
// and the page are the whole of it (no queue, application or search filter exists to keep).

export interface JobListParams {
  /** Zero-based. */
  page: number;
  /** The failed list's job-type filter; null = every type. */
  type: string | null;
}

export const DEFAULT_JOB_LIST_PARAMS: JobListParams = { page: 0, type: null };

/** Every query key the Jobs list (`/jobs/:state`) reads. Saved views (batch 3) validate a stored query against it. */
export const jobsQueryKeys = ['page', 'type'] as const;

export function parsePage(value: string | null): number {
  const parsed = Number(value);

  return value !== null && Number.isInteger(parsed) && parsed > 0 ? parsed : 0;
}

export function parseJobListParams(params: URLSearchParams): JobListParams {
  const type = params.get('type');

  return { page: parsePage(params.get('page')), type: type !== null && type.trim() !== '' ? type : null };
}

/** A copy of `params` carrying `list` (defaults left out) and every other parameter unchanged. */
export function writeJobListParams(params: URLSearchParams, list: JobListParams): URLSearchParams {
  const next = new URLSearchParams(params);
  next.delete('page');
  next.delete('type');

  if (list.type) {
    next.set('type', list.type);
  }

  if (Number.isInteger(list.page) && list.page > 0) {
    next.set('page', String(list.page));
  }

  return next;
}

/** Which execution metrics the jobs-by-type header shows: the type's own, or its handler's. */
export type JobMetricsDimension = 'type' | 'handler';

/** Every query key the jobs-by-type page (`/jobs/by-type/:type`) reads. */
export const jobsByTypeQueryKeys = ['page', 'metrics'] as const;

/** The `metrics` parameter: `handler`, else the default `type`. */
export function parseMetricsDimension(params: URLSearchParams): JobMetricsDimension {
  return params.get('metrics') === 'handler' ? 'handler' : 'type';
}

export function writeMetricsDimension(params: URLSearchParams, dimension: JobMetricsDimension): URLSearchParams {
  const next = new URLSearchParams(params);
  next.delete('metrics');
  if (dimension === 'handler') {
    next.set('metrics', 'handler');
  }

  return next;
}
