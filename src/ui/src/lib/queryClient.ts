import { QueryClient } from '@tanstack/react-query';

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5_000,
      gcTime: 5 * 60_000,
      refetchOnWindowFocus: false,
      retry: (failureCount, error: unknown) => {
        const status = (error as { response?: { status?: number } })?.response?.status;
        if (status === 401 || status === 403 || status === 404) {
          return false;
        }

        return failureCount < 2;
      },
    },
    mutations: {
      retry: false,
    },
  },
});

export const queryKeys = {
  dashboardStatus: ['dashboard', 'status'] as const,
  dashboardStats: (range?: string) => ['dashboard', 'stats', range ?? '24h'] as const,
  statsHistory: (hours: number) => ['stats', 'history', hours] as const,
  jobs: (state: string, page: number, pageSize: number) =>
    ['jobs', state, page, pageSize] as const,
  /**
   * Intentionally not under the 'jobs' prefix — see useRetryingJobsCount. Realtime invalidation
   * sweeps queryScopes.jobs on every finalized job, and this query backs an unindexed backlog scan.
   */
  retryingJobsCount: ['jobs-retrying-count'] as const,
  failedJobsByType: (type: string, page: number, pageSize: number) =>
    ['jobs', 'failed', 'by-type', type, page, pageSize] as const,
  failedJobTypes: ['jobs', 'failed', 'types'] as const,
  job: (id: string) => ['job', id] as const,
  jobLogs: (id: string) => ['job', id, 'logs'] as const,
  messages: (state: string | undefined, page: number, pageSize: number) =>
    ['messages', state ?? 'all', page, pageSize] as const,
  messageJobs: (id: string, page: number, pageSize: number, state?: string) =>
    ['messages', id, 'jobs', state ?? 'all', page, pageSize] as const,
  messageJobCounts: (id: string) => ['messages', id, 'jobs', 'counts'] as const,
  batches: (state: string | undefined, page: number, pageSize: number) =>
    ['batches', state ?? 'all', page, pageSize] as const,
  batchJobs: (id: string, page: number, pageSize: number, state?: string) =>
    ['batches', id, 'jobs', state ?? 'all', page, pageSize] as const,
  batchJobCounts: (id: string) => ['batches', id, 'jobs', 'counts'] as const,
  recurring: (page: number, pageSize: number) => ['recurring', page, pageSize] as const,
  recurringDetail: (name: string) => ['recurring', name] as const,
  recurringJobs: (name: string, page: number, pageSize: number) =>
    ['recurring', name, 'jobs', page, pageSize] as const,
  servers: ['servers'] as const,
  serverDetail: (id: string) => ['servers', id] as const,
  serverTasks: (id: string) => ['servers', id, 'tasks'] as const,
  serverLogs: (id: string, page: number, pageSize: number, taskName?: string) =>
    ['servers', id, 'logs', taskName ?? 'all', page, pageSize] as const,
  workerDetail: (id: string) => ['workers', id] as const,
  workerLogs: (id: string, page: number, pageSize: number) =>
    ['workers', id, 'logs', page, pageSize] as const,
  counters: ['counters'] as const,
  countersHistory: (hours: number) => ['counters', 'history', hours] as const,
  concurrencyLimits: ['concurrency-limits'] as const,
  rateLimits: ['rate-limits'] as const,
  trace: (id: string) => ['trace', id] as const,
  detail: (id: string) => ['detail', id] as const,
};

/**
 * Top-level "scope" keys used by realtime invalidation. Invalidating by prefix
 * here matches every paginated/filtered variant (e.g. `['jobs', 'failed', 0, 20]`).
 */
// Liveness no longer needs this — the server sends its threshold and every dot re-derives the answer
// on the clock (lib/liveness). What still does is everything else on the row: CPU, memory, and an
// instance that started after the page was opened. Slower than the servers list's 10s because a page
// renders one detail read per application.
export const INSTANCE_ROSTER_POLL_MS = 15_000;

// A page rendering a countdown needs a live data source, not just a live label. The cross-zero
// signal (lib/dueSignal) fires once, ~1s after the instant — before ScheduledActivationInterval
// (10s) or the recurring scheduler has moved anything — and the recurring and webhook surfaces have
// no other refresh at all: no hub event touches their scopes and neither uses useRealtimeRefetch's
// safety net. Without this poll a healthy hourly cron reads "overdue by 20 minutes" and keeps
// counting, which is precisely the stopped-scheduler accusation the label is meant to reserve for a
// real fault. Only the surfaces that carry a countdown pay for it.
export const COUNTDOWN_POLL_MS = 15_000;

export const queryScopes = {
  jobs: ['jobs'] as const,
  messages: ['messages'] as const,
  batches: ['batches'] as const,
  recurring: ['recurring'] as const,
  webhooks: ['webhooks'] as const,
  servers: ['servers'] as const,
  workers: ['workers'] as const,
  counters: ['counters'] as const,
  detail: ['detail'] as const,
  dashboard: ['dashboard'] as const,
  stats: ['stats'] as const,
};
