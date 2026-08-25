// Shared by the adapter and endpoint demo fixtures, which are the outbound and inbound halves of
// the same observability feature and must stay anchored to the same pinned clock. When these lived
// as a copy in each file, a change to the swell shape or the FROZEN_NOW anchor could land in one
// and not the other, and the two pages' screenshots would quietly stop lining up.
import { FROZEN_NOW } from '@/lib/demoMode';

export interface HourlyPerformancePoint {
  hour: string;
  calls: number;
  errors: number;
  errorRate: number;
  avgDurationMs: number;
}

export function ago(minutes: number): string {
  return new Date(FROZEN_NOW - minutes * 60_000).toISOString();
}

// Start of the UTC hour `hours` before the pinned demo clock — the x value of a history point.
export function hourAgo(hours: number): string {
  const d = new Date(FROZEN_NOW - hours * 3_600_000);
  d.setUTCMinutes(0, 0, 0);

  return d.toISOString();
}

// Deterministic 24-hour series (oldest first) — no randomness, so the chart renders identically on
// every screenshot run. Values wobble by index so the bars and the latency line have a realistic shape.
export function demoHistory(baseCalls: number, errorFraction: number, baseLatencyMs: number): HourlyPerformancePoint[] {
  const hours = 24;

  return Array.from({ length: hours }, (_, i) => {
    // A daytime swell: busier in the middle of the window, quieter at the edges.
    const swell = 1 + Math.round((hours / 2 - Math.abs(hours / 2 - i)) * 0.6);
    const calls = baseCalls + swell + ((i * 7) % 13);
    const errors = Math.round(calls * errorFraction * (0.3 + ((i % 4) * 0.4)));

    return {
      hour: hourAgo(hours - 1 - i),
      calls,
      errors,
      errorRate: calls === 0 ? 0 : errors / calls,
      avgDurationMs: baseLatencyMs + ((i * 13) % 55) + (i % 5) * 6,
    };
  });
}

// The fleet-wide overview chart: every per-item series summed per hour, with latency weighted by
// call count so a quiet slow item cannot drag the average of a busy fast one.
export function aggregateHistory(series: HourlyPerformancePoint[][]): HourlyPerformancePoint[] {
  const map = new Map<string, { hour: string; calls: number; errors: number; durSum: number }>();
  for (const points of series) {
    for (const p of points) {
      const g = map.get(p.hour) ?? { hour: p.hour, calls: 0, errors: 0, durSum: 0 };
      g.calls += p.calls;
      g.errors += p.errors;
      g.durSum += p.avgDurationMs * p.calls;
      map.set(p.hour, g);
    }
  }

  return [...map.values()]
    .sort((a, b) => (a.hour < b.hour ? -1 : 1))
    .map((g) => ({
      hour: g.hour,
      calls: g.calls,
      errors: g.errors,
      errorRate: g.calls === 0 ? 0 : g.errors / g.calls,
      avgDurationMs: g.calls === 0 ? 0 : g.durSum / g.calls,
    }));
}
