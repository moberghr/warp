// The Issues list's filters as URL state, so a shared link or a refresh reproduces the list. Pure: the
// page reads and writes through here. Defaults are left out of the URL, and an unknown value reads as the default.

import { ErrorGroupStatus, ErrorSource } from '@/types/issues';

export interface IssuesFilters {
  /** Null = every source. */
  source: ErrorSource | null;
  status: ErrorGroupStatus;
  /** Null = every application. */
  application: string | null;
  /** Also list the 4xx (StatusCode) groups, which are hidden by default. */
  show4xx: boolean;
}

export const DEFAULT_ISSUES_FILTERS: IssuesFilters = {
  source: null,
  status: ErrorGroupStatus.Unresolved,
  application: null,
  show4xx: false,
};

/** Every query key the Issues list reads (and owns when writing). */
const ISSUES_KEYS = ['source', 'status', 'application', 'show4xx'] as const;

const SOURCE_TOKENS: Record<ErrorSource, string> = {
  [ErrorSource.Job]: 'job',
  [ErrorSource.Endpoint]: 'endpoint',
  [ErrorSource.Adapter]: 'adapter',
  [ErrorSource.Client]: 'client',
};

const STATUS_TOKENS: Record<ErrorGroupStatus, string> = {
  [ErrorGroupStatus.Unresolved]: 'unresolved',
  [ErrorGroupStatus.Resolved]: 'resolved',
  [ErrorGroupStatus.Ignored]: 'ignored',
};

function fromToken<T extends number>(tokens: Record<T, string>, value: string | null): T | null {
  const entry = Object.entries(tokens).find(([, token]) => token === value);

  return entry ? (Number(entry[0]) as T) : null;
}

export function parseIssuesParams(params: URLSearchParams): IssuesFilters {
  const application = params.get('application');

  return {
    source: fromToken(SOURCE_TOKENS, params.get('source')),
    status: fromToken(STATUS_TOKENS, params.get('status')) ?? DEFAULT_ISSUES_FILTERS.status,
    application: application !== null && application.trim() !== '' ? application : null,
    show4xx: params.get('show4xx') === '1' || params.get('show4xx') === 'true',
  };
}

/** A copy of `params` carrying `filters` (defaults left out) and every other parameter unchanged. */
export function writeIssuesParams(params: URLSearchParams, filters: IssuesFilters): URLSearchParams {
  const next = new URLSearchParams(params);
  for (const key of ISSUES_KEYS) {
    next.delete(key);
  }

  if (filters.source !== null) {
    next.set('source', SOURCE_TOKENS[filters.source]);
  }

  if (filters.status !== DEFAULT_ISSUES_FILTERS.status) {
    next.set('status', STATUS_TOKENS[filters.status]);
  }

  if (filters.application) {
    next.set('application', filters.application);
  }

  if (filters.show4xx) {
    next.set('show4xx', '1');
  }

  return next;
}
