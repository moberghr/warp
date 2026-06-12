import { keepPreviousData, useQuery } from '@tanstack/react-query';
import * as api from '@/api';
import { queryKeys } from '@/lib/queryClient';

export function useMessagesList(state: string | undefined, page: number, pageSize: number) {
  return useQuery({
    queryKey: queryKeys.messages(state, page, pageSize),
    queryFn: () => api.getMessages(page, pageSize, state),
    placeholderData: keepPreviousData,
  });
}

export function useMessageJobs(messageId: string, page: number, pageSize: number, state?: string) {
  return useQuery({
    queryKey: queryKeys.messageJobs(messageId, page, pageSize, state),
    queryFn: () => api.getMessageJobs(messageId, page, pageSize, state),
    enabled: !!messageId,
  });
}

export function useMessageJobCounts(messageId: string) {
  return useQuery({
    queryKey: queryKeys.messageJobCounts(messageId),
    queryFn: () => api.getMessageJobCounts(messageId),
    enabled: !!messageId,
  });
}
