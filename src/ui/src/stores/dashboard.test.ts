import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import type { DashboardStatistics } from '@/types';

// The store closes over a module-level rate-sample baseline, so reset the module per test for isolation.
vi.mock('@/api', () => ({ getStatus: vi.fn() }));

const stats = (succeeded: number, failed: number) =>
  ({ totalSucceeded: succeeded, totalFailed: failed }) as unknown as DashboardStatistics;

beforeEach(() => {
  vi.resetModules();
  vi.useFakeTimers();
  vi.setSystemTime(new Date('2026-01-01T00:00:00Z'));
});

afterEach(() => vi.useRealTimers());

describe('dashboard store: sampleRate', () => {
  it('walks baseline → delta → gap-reset', async () => {
    const { useDashboardStore } = await import('./dashboard');
    const store = useDashboardStore;

    // First sample only establishes the baseline — no point emitted.
    store.setState({ stats: stats(100, 5) });
    store.getState().sampleRate();
    expect(store.getState().realtimeData).toEqual([]);

    // 1s later, totals grew by 10 / 2 → one delta point.
    vi.setSystemTime(new Date('2026-01-01T00:00:01Z'));
    store.setState({ stats: stats(110, 7) });
    store.getState().sampleRate();
    expect(store.getState().realtimeData).toHaveLength(1);
    expect(store.getState().realtimeData[0]).toMatchObject({ succeeded: 10, failed: 2 });

    // A >2s gap (backgrounded tab / sleep) re-baselines and drops the accumulated window.
    vi.setSystemTime(new Date('2026-01-01T00:00:10Z'));
    store.setState({ stats: stats(500, 40) });
    store.getState().sampleRate();
    expect(store.getState().realtimeData).toEqual([]);
  });

  it('clamps a negative delta (e.g. counters reset) to 0', async () => {
    const { useDashboardStore } = await import('./dashboard');
    const store = useDashboardStore;

    store.setState({ stats: stats(100, 10) });
    store.getState().sampleRate(); // baseline

    vi.setSystemTime(new Date('2026-01-01T00:00:01Z'));
    store.setState({ stats: stats(90, 4) }); // totals went DOWN
    store.getState().sampleRate();

    expect(store.getState().realtimeData[0]).toMatchObject({ succeeded: 0, failed: 0 });
  });

  it('does nothing when there are no stats yet', async () => {
    const { useDashboardStore } = await import('./dashboard');
    const store = useDashboardStore;

    store.getState().sampleRate();
    expect(store.getState().realtimeData).toEqual([]);
  });

  it('fetchStats failure resets the baseline so the next sample does not spike', async () => {
    const api = await import('@/api');
    (api.getStatus as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('down'));
    const { useDashboardStore } = await import('./dashboard');
    const store = useDashboardStore;

    // Prime a baseline.
    store.setState({ stats: stats(100, 5) });
    store.getState().sampleRate();

    // A failed fetch clears the baseline + sets the error.
    await store.getState().fetchStats();
    expect(store.getState().error).toBe('Unable to connect to Warp API');

    // Next sample after recovery is treated as a fresh baseline (no giant delta), so no point emitted.
    vi.setSystemTime(new Date('2026-01-01T00:00:01Z'));
    store.setState({ stats: stats(999, 999), error: null });
    store.getState().sampleRate();
    expect(store.getState().realtimeData).toEqual([]);
  });
});
