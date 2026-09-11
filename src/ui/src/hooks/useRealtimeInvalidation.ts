import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { subscribeRealtime } from '@/lib/realtimeBus';
import { subscribeDue } from '@/lib/dueSignal';
import { queryScopes } from '@/lib/queryClient';

/**
 * Single mounted bridge between the realtime event bus and React Query.
 *
 * Replaces the per-page `useRealtimeRefetch` pattern. Mounted once in
 * `MainLayout`; routes hub events to broad query invalidations so every
 * mounted page picks up fresh data without re-implementing the bridge.
 *
 * Counters are also invalidated on `JobFinalized` because the dashboard's
 * succeeded/failed counters update on every completion.
 *
 * Also bridges the countdown labels (lib/dueSignal): when a scheduled job, a retry or a webhook
 * delivery reaches its instant, the row is about to change state and nothing has asked the server
 * for it. Only the scopes that carry a countdown are swept — a label ticking over is a hint that one
 * row moved, not the broad invalidation a finalized job warrants.
 */
export function useRealtimeInvalidation() {
  const qc = useQueryClient();

  useEffect(() => {
    const onJobFinalized = () => {
      qc.invalidateQueries({ queryKey: queryScopes.jobs });
      qc.invalidateQueries({ queryKey: queryScopes.detail });
      qc.invalidateQueries({ queryKey: queryScopes.counters });
      qc.invalidateQueries({ queryKey: queryScopes.stats });
      qc.invalidateQueries({ queryKey: queryScopes.dashboard });
      qc.invalidateQueries({ queryKey: queryScopes.messages });
      qc.invalidateQueries({ queryKey: queryScopes.batches });
    };

    const onMessageEnqueued = () => {
      qc.invalidateQueries({ queryKey: queryScopes.messages });
      qc.invalidateQueries({ queryKey: queryScopes.jobs });
      qc.invalidateQueries({ queryKey: queryScopes.dashboard });
    };

    const onDue = () => {
      qc.invalidateQueries({ queryKey: queryScopes.jobs });
      qc.invalidateQueries({ queryKey: queryScopes.detail });
      qc.invalidateQueries({ queryKey: queryScopes.recurring });
      qc.invalidateQueries({ queryKey: queryScopes.webhooks });
    };

    const unsubJob = subscribeRealtime('JobFinalized', onJobFinalized);
    const unsubMsg = subscribeRealtime('MessageEnqueued', onMessageEnqueued);
    const unsubDue = subscribeDue(onDue);

    return () => {
      unsubJob();
      unsubMsg();
      unsubDue();
    };
  }, [qc]);
}
