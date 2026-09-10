import { describe, it, expect, afterEach, vi } from 'vitest';
import type { ReactNode } from 'react';
import { renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useRealtimeInvalidation } from './useRealtimeInvalidation';
import { emit } from '@/lib/realtimeBus';
import { signalDue, resetDueSignalForTests, DUE_SIGNAL_COALESCE_MS } from '@/lib/dueSignal';
import { queryScopes } from '@/lib/queryClient';

function setup() {
  const qc = new QueryClient();
  const spy = vi.spyOn(qc, 'invalidateQueries');
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );
  renderHook(() => useRealtimeInvalidation(), { wrapper });
  return spy;
}

describe('useRealtimeInvalidation', () => {
  it('invalidates job/detail/counter/stats/dashboard/message/batch scopes on JobFinalized', () => {
    const spy = setup();
    emit('JobFinalized');

    for (const scope of [queryScopes.jobs, queryScopes.detail, queryScopes.counters, queryScopes.stats, queryScopes.dashboard, queryScopes.messages, queryScopes.batches]) {
      expect(spy).toHaveBeenCalledWith({ queryKey: scope });
    }
  });

  it('invalidates only messages/jobs/dashboard on MessageEnqueued', () => {
    const spy = setup();
    emit('MessageEnqueued');

    expect(spy).toHaveBeenCalledWith({ queryKey: queryScopes.messages });
    expect(spy).toHaveBeenCalledWith({ queryKey: queryScopes.jobs });
    expect(spy).toHaveBeenCalledWith({ queryKey: queryScopes.dashboard });
    expect(spy).not.toHaveBeenCalledWith({ queryKey: queryScopes.counters });
  });

  describe('countdown crossings', () => {
    afterEach(() => {
      resetDueSignalForTests();
      vi.useRealTimers();
    });

    it('refetches the scopes that carry a countdown when one comes due', () => {
      vi.useFakeTimers();
      const spy = setup();

      signalDue();
      vi.advanceTimersByTime(DUE_SIGNAL_COALESCE_MS);

      for (const scope of [queryScopes.jobs, queryScopes.detail, queryScopes.recurring, queryScopes.webhooks]) {
        expect(spy).toHaveBeenCalledWith({ queryKey: scope });
      }

      // A label ticking over says one row moved — it is not the broad sweep a finalized job warrants.
      expect(spy).not.toHaveBeenCalledWith({ queryKey: queryScopes.stats });
    });
  });
});
