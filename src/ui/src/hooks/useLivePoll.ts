import { useEffect, useRef } from 'react';
import { isTabHidden, onTabVisible } from '@/lib/visibility';

/**
 * Calls `poll` every `intervalMs` while `active`, skipping ticks while the tab is hidden and
 * polling once as soon as it becomes visible again, so a background tab left open on a running
 * job costs nothing until someone looks at it.
 *
 * Does NOT poll on mount or when `active` changes; the consumer owns those fetches, the same
 * contract as useRealtimeRefetch.
 */
export function useLivePoll(poll: () => void, active: boolean, intervalMs: number) {
  const savedPoll = useRef(poll);
  useEffect(() => {
    savedPoll.current = poll;
  }, [poll]);

  useEffect(() => {
    if (!active) return;

    const id = setInterval(() => {
      if (!isTabHidden()) savedPoll.current();
    }, intervalMs);
    const unsubVisible = onTabVisible(() => savedPoll.current());

    return () => {
      clearInterval(id);
      unsubVisible();
    };
  }, [active, intervalMs]);
}
