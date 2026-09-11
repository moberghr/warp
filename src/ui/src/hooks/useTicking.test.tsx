import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import {
  useCountdownLabel,
  useHeartbeatStale,
  useInstanceLive,
  useLiveInstanceCount,
  useRelativeLabel,
  useServerStatusDotColor,
  useTickingValue,
} from './useTicking';
import { resetClockTickForTests } from '@/lib/clockTick';
import { resetDueSignalForTests, subscribeDue, DUE_SIGNAL_COALESCE_MS } from '@/lib/dueSignal';
import { DUE_GRACE_MS } from '@/utils/format';
import { resetLivenessForTests, setInstanceStaleGrace } from '@/lib/liveness';

const NOW = '2026-05-25T11:00:00Z';

describe('useTicking', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(NOW));
    resetClockTickForTests();
    resetDueSignalForTests();
    resetLivenessForTests();
  });

  afterEach(() => {
    resetClockTickForTests();
    resetDueSignalForTests();
    resetLivenessForTests();
    vi.useRealTimers();
  });

  it('re-labels as the clock moves', () => {
    const { result } = renderHook(() => useRelativeLabel('2026-05-25T10:59:00Z'));
    expect(result.current).toBe('1 minute ago');

    act(() => vi.advanceTimersByTime(60_000));

    expect(result.current).toBe('2 minutes ago');
  });

  it('does not re-render a label whose text has not changed', () => {
    let renders = 0;
    renderHook(() => {
      renders++;

      return useRelativeLabel('2020-01-01T00:00:00Z');
    });
    const initial = renders;

    act(() => vi.advanceTimersByTime(10_000));

    // Ten ticks, ten identical snapshots, zero re-renders: the bail-out is what makes a 1 Hz
    // ticker affordable on a hundred-row table.
    expect(renders).toBe(initial);
  });

  it('counts down, then reads "due now" rather than flipping to the past tense', () => {
    const { result } = renderHook(() => useCountdownLabel('2026-05-25T11:00:05Z'));
    expect(result.current).toBe('in 5 seconds');

    act(() => vi.advanceTimersByTime(4_000));
    expect(result.current).toBe('in 1 second');

    act(() => vi.advanceTimersByTime(2_000));
    expect(result.current).toBe('due now');
  });

  it('asks the page to refetch when a countdown reaches zero', () => {
    const onDue = vi.fn();
    subscribeDue(onDue);
    renderHook(() => useCountdownLabel('2026-05-25T11:00:03Z'));

    act(() => vi.advanceTimersByTime(4_000));
    act(() => vi.advanceTimersByTime(DUE_SIGNAL_COALESCE_MS));

    expect(onDue).toHaveBeenCalledTimes(1);
  });

  it('asks again when the grace runs out, before the label calls the row overdue', () => {
    const onDue = vi.fn();
    subscribeDue(onDue);
    renderHook(() => useCountdownLabel('2026-05-25T11:00:03Z'));

    act(() => vi.advanceTimersByTime(4_000));
    act(() => vi.advanceTimersByTime(DUE_SIGNAL_COALESCE_MS));
    expect(onDue).toHaveBeenCalledTimes(1);

    // due → overdue: the first refetch landed a second after the instant, before the server had its
    // activation interval to act, so this is the one worth re-reading.
    act(() => vi.advanceTimersByTime(DUE_GRACE_MS));
    act(() => vi.advanceTimersByTime(DUE_SIGNAL_COALESCE_MS));

    expect(onDue).toHaveBeenCalledTimes(2);
  });

  it('does not refetch for a row that was already due when it mounted', () => {
    const onDue = vi.fn();
    subscribeDue(onDue);
    renderHook(() => useCountdownLabel('2026-05-25T10:59:59Z'));

    act(() => vi.advanceTimersByTime(DUE_SIGNAL_COALESCE_MS * 3));

    expect(onDue).not.toHaveBeenCalled();
  });

  it('formats against the shared instant, not a per-call Date.now()', () => {
    const seen: number[] = [];
    renderHook(() => useTickingValue((now) => {
      seen.push(now);

      return String(now);
    }));

    expect(new Set(seen).size).toBe(1);
  });

  it('turns a heartbeat dot red on its own, without a refetch', () => {
    // 25s of silence at mount: still inside the threshold, so the row starts healthy.
    const { result } = renderHook(() => useHeartbeatStale('2026-05-25T10:59:35Z'));
    expect(result.current).toBe(false);

    act(() => vi.advanceTimersByTime(6_000));

    expect(result.current).toBe(true);
  });

  it('moves the status dot from green to red on the clock, and stays amber while paused', () => {
    const { result } = renderHook(() => useServerStatusDotColor('2026-05-25T10:59:35Z', null));
    expect(result.current).toBe('bg-green-500');

    act(() => vi.advanceTimersByTime(6_000));
    expect(result.current).toBe('bg-red-500');

    // Paused outranks stale: a paused server is not checking in, and reporting that as a fault
    // would send the operator looking for a problem they created.
    const paused = renderHook(() => useServerStatusDotColor('2020-01-01T00:00:00Z', '2026-05-25T10:00:00Z'));
    expect(paused.result.current).toBe('bg-amber-500');
  });

  it('re-derives instance liveness against the threshold the server sent', () => {
    setInstanceStaleGrace(120);
    // The API answered "live" 119s ago; one more second of silence and the same rule says otherwise.
    const { result } = renderHook(() => useInstanceLive('2026-05-25T10:58:01Z', true));
    expect(result.current).toBe(true);

    act(() => vi.advanceTimersByTime(2_000));

    expect(result.current).toBe(false);
  });

  it('trusts the API when no threshold was sent, rather than guessing one', () => {
    // A pre-6.2 backend. Guessing 30s here is what made one silent process render red on the server
    // page and green on the roster.
    const { result } = renderHook(() => useInstanceLive('2026-05-25T10:50:00Z', true));

    act(() => vi.advanceTimersByTime(60_000));

    expect(result.current).toBe(true);
  });

  it('drops the roster headcount as instances go quiet', () => {
    setInstanceStaleGrace(60);
    const instances = [
      { lastHeartbeatAt: '2026-05-25T10:59:30Z', isLive: true },
      { lastHeartbeatAt: '2026-05-25T10:59:55Z', isLive: true },
    ];
    const { result } = renderHook(() => useLiveInstanceCount(instances));
    expect(result.current).toBe(2);

    act(() => vi.advanceTimersByTime(31_000));

    expect(result.current).toBe(1);
  });
});
