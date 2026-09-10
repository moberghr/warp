import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { signalDue, subscribeDue, resetDueSignalForTests, DUE_SIGNAL_COALESCE_MS } from './dueSignal';

describe('dueSignal', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    resetDueSignalForTests();
  });

  afterEach(() => {
    resetDueSignalForTests();
    vi.useRealTimers();
  });

  it('coalesces a page-full of crossings into one notification', () => {
    const fn = vi.fn();
    subscribeDue(fn);

    for (let i = 0; i < 25; i++) {
      signalDue();
    }

    expect(fn).not.toHaveBeenCalled();
    vi.advanceTimersByTime(DUE_SIGNAL_COALESCE_MS);
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it('opens a fresh window after the previous one fired', () => {
    const fn = vi.fn();
    subscribeDue(fn);

    signalDue();
    vi.advanceTimersByTime(DUE_SIGNAL_COALESCE_MS);
    signalDue();
    vi.advanceTimersByTime(DUE_SIGNAL_COALESCE_MS);

    expect(fn).toHaveBeenCalledTimes(2);
  });

  it('stops notifying after unsubscribe', () => {
    const fn = vi.fn();
    const unsub = subscribeDue(fn);
    unsub();

    signalDue();
    vi.advanceTimersByTime(DUE_SIGNAL_COALESCE_MS);

    expect(fn).not.toHaveBeenCalled();
  });
});
