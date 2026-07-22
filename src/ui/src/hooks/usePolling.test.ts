import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { usePolling } from './usePolling';

describe('usePolling', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('invokes the callback immediately and then on each interval', () => {
    const cb = vi.fn();
    renderHook(() => usePolling(cb, 5000));

    expect(cb).toHaveBeenCalledTimes(1); // immediate
    vi.advanceTimersByTime(15_000);
    expect(cb).toHaveBeenCalledTimes(4); // + 3 ticks
  });

  it('stops polling after unmount', () => {
    const cb = vi.fn();
    const { unmount } = renderHook(() => usePolling(cb, 1000));
    vi.advanceTimersByTime(1000);
    const before = cb.mock.calls.length;

    unmount();
    vi.advanceTimersByTime(5000);
    expect(cb.mock.calls.length).toBe(before);
  });

  it('always calls the latest callback without resetting the interval', () => {
    const first = vi.fn();
    const second = vi.fn();
    const { rerender } = renderHook(({ cb }) => usePolling(cb, 1000), { initialProps: { cb: first } });

    rerender({ cb: second });
    vi.advanceTimersByTime(1000);

    expect(second).toHaveBeenCalled();
  });
});
