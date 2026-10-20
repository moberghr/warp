import { useQuery } from '@tanstack/react-query';
import * as api from '@/api';
import { queryKeys } from '@/lib/queryClient';

// Polled rather than invalidated on every JobFinalized: both reads scan the counter and statistic
// tables, and a completion fires up to ~once a second. Pauses in a hidden tab (React Query default).
export const COUNTERS_POLL_MS = 30_000;

export function useCounters() {
  return useQuery({
    queryKey: queryKeys.counters,
    queryFn: () => api.getCounters(),
    refetchInterval: COUNTERS_POLL_MS,
  });
}

export function useCountersHistory(hours: number) {
  return useQuery({
    queryKey: queryKeys.countersHistory(hours),
    queryFn: () => api.getCountersHistory(hours),
    refetchInterval: COUNTERS_POLL_MS,
  });
}
