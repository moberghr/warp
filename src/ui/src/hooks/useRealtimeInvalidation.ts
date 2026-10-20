import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { subscribeRealtime } from '@/lib/realtimeBus';
import { subscribeDue } from '@/lib/dueSignal';
import { queryScopes } from '@/lib/queryClient';
import { isTabHidden, onTabVisible } from '@/lib/visibility';

/**
 * Single mounted bridge between the realtime event bus and React Query.
 *
 * Replaces the per-page `useRealtimeRefetch` pattern. Mounted once in
 * `MainLayout`; routes hub events to broad query invalidations so every
 * mounted page picks up fresh data without re-implementing the bridge.
 *
 * The Counters page is NOT invalidated per event: it reads the whole counter and statistic tables,
 * and refreshes on its own 30s poll instead (useCounters).
 *
 * Also bridges the countdown labels (lib/dueSignal): when a scheduled job, a retry or a webhook
 * delivery reaches its instant, the row is about to change state and nothing has asked the server
 * for it. Only the scopes that carry a countdown are swept — a label ticking over is a hint that one
 * row moved, not the broad invalidation a finalized job warrants.
 *
 * While the tab is hidden nothing is invalidated: an invalidation refetches every active query in
 * its scope regardless of visibility, and JobFinalized fires for every job in the cluster. The
 * scopes that were hit are remembered and invalidated once when the tab is shown again.
 */
export function useRealtimeInvalidation() {
  const qc = useQueryClient();

  useEffect(() => {
    const missed = new Set<readonly unknown[]>();
    const invalidate = (...scopes: (readonly unknown[])[]) => {
      for (const scope of scopes) {
        if (isTabHidden()) {
          missed.add(scope);
        } else {
          qc.invalidateQueries({ queryKey: scope });
        }
      }
    };

    const onJobFinalized = () => invalidate(
      queryScopes.jobs,
      queryScopes.detail,
      queryScopes.stats,
      queryScopes.dashboard,
      queryScopes.messages,
      queryScopes.batches,
    );

    const onMessageEnqueued = () => invalidate(queryScopes.messages, queryScopes.jobs, queryScopes.dashboard);

    const onDue = () => invalidate(queryScopes.jobs, queryScopes.detail, queryScopes.recurring, queryScopes.webhooks);

    const unsubJob = subscribeRealtime('JobFinalized', onJobFinalized);
    const unsubMsg = subscribeRealtime('MessageEnqueued', onMessageEnqueued);
    const unsubDue = subscribeDue(onDue);
    const unsubVisible = onTabVisible(() => {
      const scopes = [...missed];
      missed.clear();
      invalidate(...scopes);
    });

    return () => {
      unsubJob();
      unsubMsg();
      unsubDue();
      unsubVisible();
    };
  }, [qc]);
}
