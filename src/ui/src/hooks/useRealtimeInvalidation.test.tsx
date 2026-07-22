import { describe, it, expect, vi } from 'vitest';
import type { ReactNode } from 'react';
import { renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useRealtimeInvalidation } from './useRealtimeInvalidation';
import { emit } from '@/lib/realtimeBus';
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
});
