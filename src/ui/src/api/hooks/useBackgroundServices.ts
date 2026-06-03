import { useQuery } from '@tanstack/react-query';
import * as api from '@/api';

export function useBackgroundServices() {
  return useQuery({
    queryKey: ['background-services'] as const,
    queryFn: () => api.getBackgroundServices(),
    refetchInterval: 2_000,
  });
}

export function useBackgroundServiceDetail(name: string | undefined) {
  return useQuery({
    queryKey: ['background-services', name ?? ''] as const,
    queryFn: () => api.getBackgroundService(name!),
    enabled: !!name,
    refetchInterval: 2_000,
  });
}
