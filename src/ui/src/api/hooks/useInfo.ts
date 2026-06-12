import { useQuery } from '@tanstack/react-query';
import * as api from '@/api';
import type { WarpInfo } from '@/types';

/**
 * Server-supplied identity: version, provider, host, database, schema.
 * Shared by the statusbar, sidebar, and dashboard subtitle so we don't
 * fan out three separate fetches for the same payload.
 */
export function useInfo() {
  return useQuery<WarpInfo>({
    queryKey: ['warp', 'info'],
    queryFn: api.getInfo,
    staleTime: 60_000,
  });
}
