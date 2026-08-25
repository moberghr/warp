import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import * as api from '@/api';
import { queryKeys, queryScopes } from '@/lib/queryClient';

export function useRecurringList(page: number, pageSize: number) {
  return useQuery({
    queryKey: queryKeys.recurring(page, pageSize),
    queryFn: () => api.getRecurringJobs(page, pageSize),
  });
}

export function useRecurringDetail(name: string | undefined) {
  return useQuery({
    queryKey: queryKeys.recurringDetail(name ?? ''),
    queryFn: () => api.getRecurringJob(name!),
    enabled: !!name,
  });
}

export function useRecurringJobs(name: string | undefined, page: number, pageSize: number) {
  return useQuery({
    queryKey: queryKeys.recurringJobs(name ?? '', page, pageSize),
    queryFn: () => api.getRecurringJobJobs(name!, page, pageSize),
    enabled: !!name,
  });
}

function invalidateRecurring(qc: ReturnType<typeof useQueryClient>) {
  qc.invalidateQueries({ queryKey: queryScopes.recurring });
}

export function useEnableRecurringJob() {
  const qc = useQueryClient();

  return useMutation({
    mutationFn: (name: string) => api.enableRecurringJob(name),
    onSuccess: () => {
      invalidateRecurring(qc);
      toast.success('Recurring job enabled');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

export function useDisableRecurringJob() {
  const qc = useQueryClient();

  return useMutation({
    mutationFn: (name: string) => api.disableRecurringJob(name),
    onSuccess: () => {
      invalidateRecurring(qc);
      toast.success('Recurring job disabled');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

export function useTriggerRecurringJob() {
  const qc = useQueryClient();

  return useMutation({
    mutationFn: (name: string) => api.triggerRecurringJob(name),
    onSuccess: () => {
      invalidateRecurring(qc);
      qc.invalidateQueries({ queryKey: queryScopes.jobs });
      toast.success('Recurring job triggered');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

export function useDeleteRecurringJob() {
  const qc = useQueryClient();

  return useMutation({
    mutationFn: (name: string) => api.deleteRecurringJob(name),
    onSuccess: () => {
      invalidateRecurring(qc);
      toast.success('Recurring job deleted');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}
