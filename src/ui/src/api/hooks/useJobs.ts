import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import * as api from '@/api';
import { queryKeys, queryScopes } from '@/lib/queryClient';
import type { JobModel, PagedList } from '@/types';

type StateFetcher = (page: number, pageSize: number) => Promise<PagedList<JobModel>>;

const stateEndpoints: Record<string, StateFetcher> = {
  enqueued: api.getEnqueuedJobs,
  processing: api.getProcessingJobs,
  scheduled: api.getScheduledJobs,
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
    placeholderData: keepPreviousData,
  });
}

export function useFailedJobsByType(type: string, page: number, pageSize: number, enabled: boolean) {
  return useQuery({
    queryKey: queryKeys.failedJobsByType(type, page, pageSize),
    queryFn: () => api.getFailedJobsByType(type, page, pageSize),
    enabled,
    placeholderData: keepPreviousData,
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
