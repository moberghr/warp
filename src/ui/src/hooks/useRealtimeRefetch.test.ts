import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useRealtimeRefetch } from './useRealtimeRefetch';
import { emit } from '@/lib/realtimeBus';
import { signalDue, resetDueSignalForTests, DUE_SIGNAL_COALESCE_MS } from '@/lib/dueSignal';

describe('useRealtimeRefetch', () => {
  beforeEach(() => vi.useFakeTimers());

  afterEach(() => {
    resetDueSignalForTests();
    vi.useRealTimers();
  });

  it('refetches when the subscribed event fires', () => {
    const refetch = vi.fn();
    renderHook(() => useRealtimeRefetch('JobFinalized', refetch));

    emit('JobFinalized');
    expect(refetch).toHaveBeenCalledOnce();
  });

  it('does not fetch on mount', () => {
    const refetch = vi.fn();
    renderHook(() => useRealtimeRefetch('JobFinalized', refetch));
    expect(refetch).not.toHaveBeenCalled();
  });

  it('fires the safety-net interval', () => {
    const refetch = vi.fn();
    renderHook(() => useRealtimeRefetch('JobFinalized', refetch, 30_000));
    vi.advanceTimersByTime(60_000);
    expect(refetch).toHaveBeenCalledTimes(2);
  });

  it('shares ONE safety interval across an array of events', () => {
    const refetch = vi.fn();
    renderHook(() => useRealtimeRefetch(['JobFinalized', 'MessageEnqueued'], refetch, 10_000));

    // Both events route to the same fetcher...
    emit('JobFinalized');
    emit('MessageEnqueued');
    expect(refetch).toHaveBeenCalledTimes(2);

    // ...and the interval is single, not one-per-event (would be 2 ticks/10s otherwise).
    refetch.mockClear();
    vi.advanceTimersByTime(10_000);
    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it('unsubscribes and clears the interval on unmount', () => {
    const refetch = vi.fn();
    const { unmount } = renderHook(() => useRealtimeRefetch('JobFinalized', refetch));
    unmount();

    emit('JobFinalized');
    vi.advanceTimersByTime(60_000);
    expect(refetch).not.toHaveBeenCalled();
  });

  it('refetches when a countdown on the page reaches zero', () => {
    // These pages own their own fetch, so the MainLayout React Query bridge cannot reach them —
    // and JobFinalized never fires for a Scheduled job crossing its instant.
    const refetch = vi.fn();
    renderHook(() => useRealtimeRefetch('JobFinalized', refetch));

    signalDue();
    vi.advanceTimersByTime(DUE_SIGNAL_COALESCE_MS);

    expect(refetch).toHaveBeenCalledOnce();
  });

  it('stops listening for due crossings after unmount', () => {
    const refetch = vi.fn();
    const { unmount } = renderHook(() => useRealtimeRefetch('JobFinalized', refetch));
    unmount();

    signalDue();
    vi.advanceTimersByTime(DUE_SIGNAL_COALESCE_MS);

    expect(refetch).not.toHaveBeenCalled();
  });
});
