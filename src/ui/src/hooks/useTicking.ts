import { useCallback, useEffect, useRef, useSyncExternalStore } from 'react';
import { subscribeTick, tickNow } from '@/lib/clockTick';
import { signalDue } from '@/lib/dueSignal';
import { countdownPhase, formatCountdown, formatRelativeTime, type CountdownPhase } from '@/utils/format';
import { hasServerGrace, isStale, statusDotColor } from '@/lib/liveness';

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
    // Both transitions ask, not just the first: the refetch at zero lands ~1s in, before the server
    // has had its ScheduledActivationInterval to act, so the row that is still counting when the
    // grace runs out is exactly the one worth re-reading before calling it overdue. Never on mount
    // (previousPhase is null), so a row already due when the page loaded does not ask for a refetch
    // of data it just fetched. Convergence past that belongs to the pages' own poll
    // (COUNTDOWN_POLL_MS), not to a label re-arming itself.
    if (previousPhase.current !== null && previousPhase.current !== phase && phase !== 'future') {
      signalDue();
    }

    previousPhase.current = phase;
  }, [phase]);

  return label;
}

// Elapsed silence against the threshold the server sent (lib/liveness). It flips exactly once, at
// the second the grace expires, and never again — the bail-out means a roomful of healthy dots costs
// one comparison each per tick and zero re-renders.
export function useHeartbeatStale(lastHeartbeatTime: string): boolean {
  return useTickingValue(useCallback((now: number) => isStale(lastHeartbeatTime, now), [lastHeartbeatTime]));
}

/** The status dot's Tailwind colour, re-evaluated on the clock: amber paused, red stale, green live. */
export function useServerStatusDotColor(lastHeartbeatTime: string, pausedAt: string | null): string {
  return useTickingValue(
    useCallback((now: number) => statusDotColor(isStale(lastHeartbeatTime, now), pausedAt), [lastHeartbeatTime, pausedAt]),
  );
}

/**
 * Liveness for a roster row, where the API already answered once.
 *
 * With the server's threshold in hand the browser re-derives it — same rule, same number, now decaying
 * correctly as the page sits open. Without it (an older backend) the API's answer stands: guessing a
 * threshold is what made one silent process render red on one page and green on another.
 */
export function useInstanceLive(lastHeartbeatTime: string, reportedLive: boolean): boolean {
  return useTickingValue(
    useCallback(
      (now: number) => (hasServerGrace() ? !isStale(lastHeartbeatTime, now) : reportedLive),
      [lastHeartbeatTime, reportedLive],
    ),
  );
}

/**
 * "3 of 4 live" for a whole roster, on one subscription and one snapshot — the count re-renders when
 * it changes, not once per instance per second.
 */
export function useLiveInstanceCount(instances: readonly { lastHeartbeatAt: string; isLive: boolean }[]): number {
  return useTickingValue(
    useCallback(
      (now: number) =>
        instances.filter((x) => (hasServerGrace() ? !isStale(x.lastHeartbeatAt, now) : x.isLive)).length,
      [instances],
    ),
  );
}
