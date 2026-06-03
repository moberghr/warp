import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { subscribeRealtime } from '@/lib/realtimeBus';
import { queryScopes } from '@/lib/queryClient';

const COALESCE_MS = 400;

/**
 * Single mounted bridge between the realtime event bus and React Query.
 *
 * Replaces the per-page `useRealtimeRefetch` pattern. Mounted once in
 * `MainLayout`; routes hub events to broad query invalidations so every
 * mounted page picks up fresh data without re-implementing the bridge.
 *
 * Bursts of `JobFinalized` (workers draining a 100-job queue in seconds)
 * are coalesced into one invalidation per scope per ~400ms. Without this,
 * the Enqueued list flips between empty and full on every event and the
 * table thrashes.
 */
export function useRealtimeInvalidation() {
  const qc = useQueryClient();

  useEffect(() => {
    const pending = new Map<string, ReturnType<typeof setTimeout>>();
    const schedule = (key: string, scopes: readonly (readonly unknown[])[]) => {
      if (pending.has(key)) {
        return;
      }
      const handle = setTimeout(() => {
        pending.delete(key);
        for (const scope of scopes) {
          qc.invalidateQueries({ queryKey: scope });
        }
      }, COALESCE_MS);
      pending.set(key, handle);
    };

    const onJobFinalized = () => {
      schedule('jobFinalized', [
        queryScopes.jobs,
        queryScopes.detail,
        queryScopes.counters,
        queryScopes.stats,
        queryScopes.dashboard,
        queryScopes.messages,
        queryScopes.batches,
      ]);
    };

    const onMessageEnqueued = () => {
      schedule('messageEnqueued', [
        queryScopes.messages,
        queryScopes.jobs,
        queryScopes.dashboard,
      ]);
    };

    const unsubJob = subscribeRealtime('JobFinalized', onJobFinalized);
    const unsubMsg = subscribeRealtime('MessageEnqueued', onMessageEnqueued);

    return () => {
      unsubJob();
      unsubMsg();
      for (const handle of pending.values()) {
        clearTimeout(handle);
      }
      pending.clear();
    };
  }, [qc]);
}
