import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { RelativeTime } from './RelativeTime';
import { resetClockTickForTests } from '@/lib/clockTick';
import { resetDueSignalForTests } from '@/lib/dueSignal';

describe('RelativeTime', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-05-25T11:00:00Z'));
    resetClockTickForTests();
    resetDueSignalForTests();
  });

  afterEach(() => {
    resetClockTickForTests();
    resetDueSignalForTests();
    vi.useRealTimers();
  });

  it('mounts one timer for a whole table of timestamps', () => {
    const spy = vi.spyOn(globalThis, 'setInterval');
    render(
      <>
        {Array.from({ length: 50 }, (_, i) => (
          <RelativeTime key={i} date="2026-05-25T10:55:00Z" />
        ))}
      </>,
    );

    expect(spy).toHaveBeenCalledTimes(1);
  });

  it('keeps the elapsed label current', () => {
    render(<RelativeTime date="2026-05-25T10:55:00Z" />);
    expect(screen.getByText('(5 minutes ago)')).toBeTruthy();

    act(() => vi.advanceTimersByTime(60_000));

    expect(screen.getByText('(6 minutes ago)')).toBeTruthy();
  });

  it('renders a countdown past its instant as due, not as a run that happened', () => {
    render(<RelativeTime date="2026-05-25T11:00:04Z" tense="countdown" />);
    expect(screen.getByText('(in 4 seconds)')).toBeTruthy();

    act(() => vi.advanceTimersByTime(10_000));

    expect(screen.getByText('(due now)')).toBeTruthy();
  });
});
