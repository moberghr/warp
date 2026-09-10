import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import * as api from '@/api';
import { queryKeys, queryScopes } from '@/lib/queryClient';
import type { JobModel, PagedList } from '@/types';

type StateFetcher = (page: number, pageSize: number) => Promise<PagedList<JobModel>>;

const stateEndpoints: Record<string, StateFetcher> = {
  enqueued: api.getEnqueuedJobs,
  processing: api.getProcessingJobs,
  scheduled: api.getScheduledJobs,
  retrying: api.getRetryingJobs,
  completed: api.getCompletedJobs,
  failed: api.getFailedJobs,
  awaiting: api.getAwaitingJobs,
  deleted: api.getDeletedJobs,
};

export function useJobsList(state: string, page: number, pageSize: number) {
  return useQuery({
    queryKey: queryKeys.jobs(state, page, pageSize),
    queryFn: () => {
      const fetcher = stateEndpoints[state];
      if (!fetcher) {
        return Promise.resolve<PagedList<JobModel>>({ items: [], totalCount: 0, pageCount: 0 });
      }

      return fetcher(page, pageSize);
    },
    enabled: state in stateEndpoints,
  });
}

/**
 * Count for the Retrying sidebar badge, taken from the list endpoint's totalCount rather than from
 * dashboard stats. Deliberate: the predicate is a metadata scan over the live backlog (~24ms warm,
 * see docs/perf-results.md), and DashboardStatistics is refetched on a timer for every open
 * dashboard. Here it rides the jobs section only — the sidebar renders under /jobs — with a long
 * staleTime so switching tabs inside the section doesn't refire the scan.
 */
export function useRetryingJobsCount(enabled: boolean) {
  return useQuery({
    // Deliberately OUTSIDE the ['jobs'] scope. useRealtimeInvalidation invalidates that whole prefix
    // on every JobFinalized event, and invalidateQueries refetches ACTIVE queries immediately,
    // ignoring staleTime — this query is active on every /jobs/* page, so a ['jobs', ...] key would
    // fire the backlog scan on every completion (~10/sec under push's 100ms coalesce window). That is
    // the same mistake as putting it on the dashboard poll, only worse.
    queryKey: queryKeys.retryingJobsCount,
    queryFn: () => api.getRetryingJobsCount(),
    staleTime: 30_000,
    enabled,
  });
}

export function useFailedJobsByType(type: string, page: number, pageSize: number, enabled: boolean) {
  return useQuery({
    queryKey: queryKeys.failedJobsByType(type, page, pageSize),
    queryFn: () => api.getFailedJobsByType(type, page, pageSize),
    enabled,
  });
}

export function useJobsByType(type: string, page: number, pageSize: number, state?: string) {
  return useQuery({
    queryKey: ['jobsByType', type, state ?? null, page, pageSize],
    queryFn: () => api.getJobsByType(type, page, pageSize, state),
    enabled: !!type,
  });
}

export function useFailedJobTypes(enabled: boolean) {
  return useQuery({
    queryKey: queryKeys.failedJobTypes,
    queryFn: () => api.getFailedJobTypes(),
    enabled,
  });
}

export function useJobDetail(id: string | undefined) {
  return useQuery({
    queryKey: queryKeys.detail(id ?? ''),
    queryFn: () => api.getDetail(id!),
    enabled: !!id,
  });
}

function invalidateJobs(qc: ReturnType<typeof useQueryClient>) {
  qc.invalidateQueries({ queryKey: queryScopes.jobs });
  qc.invalidateQueries({ queryKey: queryScopes.detail });
  qc.invalidateQueries({ queryKey: queryScopes.dashboard });
}

export function useRequeueJob() {
  const qc = useQueryClient();

  return useMutation({
    mutationFn: (id: string) => api.requeueJob(id),
    onSuccess: () => {
      invalidateJobs(qc);
      toast.success('Job requeued');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

export function useDeleteJob() {
  const qc = useQueryClient();

  return useMutation({
    mutationFn: (id: string) => api.deleteJob(id),
    onSuccess: () => {
      invalidateJobs(qc);
      toast.success('Job deleted');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

export function useBulkRequeueJobs() {
  const qc = useQueryClient();

  return useMutation({
    mutationFn: (ids: string[]) => api.bulkRequeueJobs(ids),
    onSuccess: () => {
      invalidateJobs(qc);
      toast.success('Jobs requeued');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

export function useBulkDeleteJobs() {
  const qc = useQueryClient();

  return useMutation({
    mutationFn: (ids: string[]) => api.bulkDeleteJobs(ids),
    onSuccess: () => {
      invalidateJobs(qc);
      toast.success('Jobs deleted');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

export function useRequeueFailedJobsByType() {
  const qc = useQueryClient();

  return useMutation({
    mutationFn: (type: string) => api.requeueFailedJobsByType(type),
    onSuccess: () => {
      invalidateJobs(qc);
      toast.success('Failed jobs requeued');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

export function useDeleteFailedJobsByType() {
  const qc = useQueryClient();

  return useMutation({
    mutationFn: (type: string) => api.deleteFailedJobsByType(type),
    onSuccess: () => {
      invalidateJobs(qc);
      toast.success('Failed jobs deleted');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}
