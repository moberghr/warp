import { useCallback, useEffect, useRef, useSyncExternalStore } from 'react';
import { subscribeTick, tickNow } from '@/lib/clockTick';
import { signalDue } from '@/lib/dueSignal';
import {
  countdownPhase,
  formatCountdown,
  formatRelativeTime,
  isServerStale,
  serverStatusDotColor,
  type CountdownPhase,
} from '@/utils/format';

/**
 * Re-renders on the shared clock tick, but only when the value it derives from "now" actually
 * changed. That bail-out is the whole trick: the snapshot IS the rendered string, so React's
 * `Object.is` comparison does the work no cadence heuristic could do reliably — a row reading
 * "3 hours ago" never re-renders, a row reading "in 12 seconds" re-renders every second, and neither
 * needs to know which bucket it is in.
 *
 * The cost is one format call per mounted label per tick — measured at ~24us for Luxon's
 * `toRelative`, so ~5ms/s for the ~200 labels a full jobs page carries, and none at all while the
 * tab is hidden. If that ever matters, the lever is quantising the `now` handed to the formatter by
 * distance (to the minute past an hour out), not a coarser tick: the tick has to stay at 1 Hz for
 * the countdowns to read as countdowns.
 */
export function useTickingValue<T extends string | number | boolean>(derive: (now: number) => T): T {
  return useSyncExternalStore(subscribeTick, () => derive(tickNow()));
}

/** "5 minutes ago" / "in 2 minutes" — Luxon's relative label, kept fresh. */
export function useRelativeLabel(dateString: string): string {
  return useTickingValue(useCallback((now: number) => formatRelativeTime(dateString, now), [dateString]));
}

/**
 * The countdown label for an instant still being waited on, plus the one side effect that makes a
 * countdown honest: when it reaches zero, ask the page to refetch (see lib/dueSignal).
 *
 * The phase rides its own snapshot rather than being sniffed back out of the label — it changes at
 * most twice in a countdown's life, so the effect fires on the crossings instead of on every tick.
 */
export function useCountdownLabel(dateString: string): string {
  const label = useTickingValue(useCallback((now: number) => formatCountdown(dateString, now), [dateString]));
  const phase = useTickingValue<CountdownPhase>(useCallback((now: number) => countdownPhase(dateString, now), [dateString]));
  const previousPhase = useRef<CountdownPhase | null>(null);

  useEffect(() => {
    // Null on mount, so a row that was already due when the page loaded does not ask for a refetch
    // of data it just fetched.
    if (previousPhase.current === 'future' && phase !== 'future') {
      signalDue();
    }

    previousPhase.current = phase;
  }, [phase]);

  return label;
}

// Liveness the CLIENT owns: elapsed time against the dashboard's own stale threshold. It flips
// exactly once, at the second the heartbeat goes quiet, and never again — the bail-out means a
// roomful of healthy dots costs one boolean comparison each per tick and zero re-renders.
//
// Deliberately not applied to the `isLive` the API computes for an application instance: that one
// is measured against the server's configured ApplicationInstanceStaleGrace, which the client has
// no way to know, so re-deriving it here would contradict the backend. A server-computed fact is
// refreshed by refetching it, not by re-deriving it — those pages poll instead.
export function useHeartbeatStale(lastHeartbeatTime: string): boolean {
  return useTickingValue(useCallback((now: number) => isServerStale(lastHeartbeatTime, now), [lastHeartbeatTime]));
}

/** The status dot's Tailwind colour, re-evaluated on the clock: amber paused, red stale, green live. */
export function useServerStatusDotColor(lastHeartbeatTime: string, pausedAt: string | null): string {
  return useTickingValue(
    useCallback((now: number) => serverStatusDotColor(lastHeartbeatTime, pausedAt, now), [lastHeartbeatTime, pausedAt]),
  );
}
