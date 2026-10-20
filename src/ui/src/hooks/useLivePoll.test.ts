import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useLivePoll } from './useLivePoll';
import { setTabVisibility } from '@/lib/visibility.testUtils';

describe('useLivePoll', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    setTabVisibility('visible');
  });

  afterEach(() => {
    vi.useRealTimers();
    setTabVisibility('visible');
  });

  it('polls on the interval while active', () => {
    const poll = vi.fn();
    renderHook(() => useLivePoll(poll, true, 2_000));

    vi.advanceTimersByTime(6_000);

    expect(poll).toHaveBeenCalledTimes(3);
  });

  it('does not poll while inactive', () => {
    const poll = vi.fn();
    renderHook(() => useLivePoll(poll, false, 2_000));

    vi.advanceTimersByTime(10_000);
    setTabVisibility('visible');

    expect(poll).not.toHaveBeenCalled();
  });

  it('skips ticks while the tab is hidden and polls once when it becomes visible', () => {
    const poll = vi.fn();
    renderHook(() => useLivePoll(poll, true, 2_000));

    setTabVisibility('hidden');
    vi.advanceTimersByTime(10_000);
    expect(poll).not.toHaveBeenCalled();

    setTabVisibility('visible');
    expect(poll).toHaveBeenCalledTimes(1);
  });

  it('stops polling when it becomes inactive', () => {
    const poll = vi.fn();
    const { rerender } = renderHook(({ active }) => useLivePoll(poll, active, 2_000), {
      initialProps: { active: true },
    });

    rerender({ active: false });
    vi.advanceTimersByTime(10_000);

    expect(poll).not.toHaveBeenCalled();
  });
});
