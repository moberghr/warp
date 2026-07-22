import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useRealtimeRefetch } from './useRealtimeRefetch';
import { emit } from '@/lib/realtimeBus';

describe('useRealtimeRefetch', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

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
});
