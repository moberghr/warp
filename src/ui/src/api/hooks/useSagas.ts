import { useQuery } from '@tanstack/react-query';
import * as api from '@/api';

export function useSagasList(
  page: number,
  pageSize: number,
  type: string | undefined,
  correlationKey: string | undefined,
) {
  return useQuery({
    queryKey: ['sagas', 'list', type ?? '', correlationKey ?? '', page, pageSize] as const,
    queryFn: () => api.listSagas(page, pageSize, type, correlationKey),
  });
}

export function useSagaTypes() {
  return useQuery({
    queryKey: ['sagas', 'types'] as const,
    queryFn: () => api.getSagaTypes(),
  });
}

export function useSagaStats() {
  return useQuery({
    queryKey: ['sagas', 'stats'] as const,
    queryFn: () => api.getSagaStats(),
  });
}

export function useSagaDetail(id: string | undefined) {
  return useQuery({
    queryKey: ['sagas', 'detail', id ?? ''] as const,
    queryFn: () => api.getSagaById(id!),
    enabled: !!id,
  });
}

export function useSagaActivity(id: string | undefined) {
  return useQuery({
    queryKey: ['sagas', 'activity', id ?? ''] as const,
    queryFn: () => api.getSagaActivity(id!),
    enabled: !!id,
  });
}
