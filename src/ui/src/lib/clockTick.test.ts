import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { subscribeTick, tickNow, resetClockTickForTests, TICK_INTERVAL_MS } from './clockTick';

let demoMode = false;
vi.mock('@/lib/demoMode', () => ({ isDemoMode: () => demoMode }));

function setHidden(hidden: boolean) {
  Object.defineProperty(document, 'hidden', { value: hidden, configurable: true });
  document.dispatchEvent(new Event('visibilitychange'));
}

describe('clockTick', () => {
  beforeEach(() => {
    demoMode = false;
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-05-25T11:00:00Z'));
    resetClockTickForTests();
    Object.defineProperty(document, 'hidden', { value: false, configurable: true });
  });

  afterEach(() => {
    resetClockTickForTests();
    vi.useRealTimers();
  });

  it('runs one interval for many subscribers', () => {
    const spy = vi.spyOn(globalThis, 'setInterval');
    const unsubs = [vi.fn(), vi.fn(), vi.fn()].map((fn) => subscribeTick(fn));

    expect(spy).toHaveBeenCalledTimes(1);
    for (const unsub of unsubs) {
      unsub();
    }
  });

  it('notifies every subscriber on each tick and moves the shared now', () => {
    const first = vi.fn();
    const second = vi.fn();
    const unsubFirst = subscribeTick(first);
    const unsubSecond = subscribeTick(second);
    const start = tickNow();

    vi.advanceTimersByTime(TICK_INTERVAL_MS * 3);

    expect(first).toHaveBeenCalledTimes(3);
    expect(second).toHaveBeenCalledTimes(3);
    expect(tickNow() - start).toBe(TICK_INTERVAL_MS * 3);

    unsubFirst();
    unsubSecond();
  });

  it('stops the interval once the last subscriber leaves', () => {
    const fn = vi.fn();
    const unsub = subscribeTick(fn);
    vi.advanceTimersByTime(TICK_INTERVAL_MS);
    unsub();

    vi.advanceTimersByTime(TICK_INTERVAL_MS * 5);

    expect(fn).toHaveBeenCalledTimes(1);
  });

  it('pauses while the tab is hidden and catches up the moment it returns', () => {
    const fn = vi.fn();
    const unsub = subscribeTick(fn);
    const start = tickNow();

    setHidden(true);
    vi.advanceTimersByTime(TICK_INTERVAL_MS * 10);
    expect(fn).not.toHaveBeenCalled();

    setHidden(false);
    // The catch-up publish is immediate — a returning tab must not show a ten-second-old label
    // until the next tick.
    expect(fn).toHaveBeenCalledTimes(1);
    expect(tickNow() - start).toBe(TICK_INTERVAL_MS * 10);

    vi.advanceTimersByTime(TICK_INTERVAL_MS);
    expect(fn).toHaveBeenCalledTimes(2);

    unsub();
  });

  it('runs no timer in demo mode, where the clock is pinned', () => {
    demoMode = true;
    const spy = vi.spyOn(globalThis, 'setInterval');
    const fn = vi.fn();
    const unsub = subscribeTick(fn);

    vi.advanceTimersByTime(TICK_INTERVAL_MS * 10);

    expect(spy).not.toHaveBeenCalled();
    expect(fn).not.toHaveBeenCalled();
    unsub();
  });

  it('materialises now on first read rather than at module load', () => {
    // Demo mode freezes Date from inside boot(), after every module body has run.
    vi.setSystemTime(new Date('2030-01-01T00:00:00Z'));

    expect(tickNow()).toBe(Date.now());
  });
});
