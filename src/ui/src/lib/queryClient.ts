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
  recurringDetail: (id: number) => ['recurring', id] as const,
  recurringJobs: (id: number, page: number, pageSize: number) =>
    ['recurring', id, 'jobs', page, pageSize] as const,
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
export const queryScopes = {
  jobs: ['jobs'] as const,
  messages: ['messages'] as const,
  batches: ['batches'] as const,
  recurring: ['recurring'] as const,
  servers: ['servers'] as const,
  workers: ['workers'] as const,
  counters: ['counters'] as const,
  detail: ['detail'] as const,
  dashboard: ['dashboard'] as const,
  stats: ['stats'] as const,
};
