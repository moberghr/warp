import { keepPreviousData, useQuery } from '@tanstack/react-query';
import * as api from '@/api';
import { queryKeys } from '@/lib/queryClient';

export function useBatchesList(state: string | undefined, page: number, pageSize: number) {
  return useQuery({
    queryKey: queryKeys.batches(state, page, pageSize),
    queryFn: () => api.getBatches(page, pageSize, state),
    placeholderData: keepPreviousData,
  });
}

export function useBatchJobs(batchId: string, page: number, pageSize: number, state?: string) {
  return useQuery({
    queryKey: queryKeys.batchJobs(batchId, page, pageSize, state),
    queryFn: () => api.getBatchJobs(batchId, page, pageSize, state),
    enabled: !!batchId,
  });
}

export function useBatchJobCounts(batchId: string) {
  return useQuery({
    queryKey: queryKeys.batchJobCounts(batchId),
    queryFn: () => api.getBatchJobCounts(batchId),
    enabled: !!batchId,
  });
}
