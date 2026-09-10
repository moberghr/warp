import { useEffect, useRef } from 'react';
import { subscribeRealtime } from '@/lib/realtimeBus';
import { subscribeDue } from '@/lib/dueSignal';
import type { RealtimeEvent } from '@/api/realtime';

/**
 * Subscribes `refetch` to one or more realtime hub events with a single safety-net
 * interval (default 30 s). Pass an array of events to share one interval across
 * subscriptions — important when a page needs the same fetcher invalidated by
 * multiple event kinds (e.g. messages page reacts to both `MessageEnqueued` and
 * `JobFinalized`), otherwise each call would stack its own interval and halve the
 * effective cadence.
 *
 * A countdown on the page reaching zero (lib/dueSignal) refetches too. The React Query bridge in
 * MainLayout cannot cover these pages — they own their own fetch, and the job detail page's
 * ['detail'] scope has no live query behind it — and `JobFinalized` is the wrong event to wait for:
 * a Scheduled job crossing its instant becomes Enqueued, which finalizes nothing. Pages with no
 * countdown mounted never see the signal, since only a rendered countdown label raises it.
 *
 * The hook does NOT fetch on mount — consumers own their initial fetch via their
 * existing `useEffect(() => fetchData(), [deps])`. Mount-fetching here would cause
 * a double fetch on every page render and a race with the consumer's own effect.
 */
export function useRealtimeRefetch(
  events: RealtimeEvent | RealtimeEvent[],
  refetch: () => void,
  safetyMs: number = 30_000,
) {
  const savedRefetch = useRef(refetch);
  useEffect(() => {
    savedRefetch.current = refetch;
  }, [refetch]);

  const eventList = Array.isArray(events) ? events : [events];
  const eventsKey = eventList.join(',');

  useEffect(() => {
    const fire = () => savedRefetch.current();
    const unsubs = eventList.map((event) => subscribeRealtime(event, fire));
    const unsubDue = subscribeDue(fire);
    const id = setInterval(fire, safetyMs);

    return () => {
      for (const unsub of unsubs) {
        unsub();
      }

      unsubDue();
      clearInterval(id);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [eventsKey, safetyMs]);
}
