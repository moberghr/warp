/**
 * "Something the page was counting down to just came due."
 *
 * A ticking label only relabels — crossing zero is exactly the moment the row is about to change
 * state, and nothing has refetched it. Without this the countdown reads live but the row behind it
 * is whatever the last push or poll left there, so a retry that fired sits at "due now" until the
 * next `JobFinalized` event arrives (or 30s of safety-net polling on a deployment without push).
 *
 * A module-level seam rather than a prop threaded through every table: the label component has no
 * business knowing about React Query, and the bridge already exists once in MainLayout
 * (`useRealtimeInvalidation`).
 */
export const DUE_SIGNAL_COALESCE_MS = 1_000;

const listeners = new Set<() => void>();
let timer: ReturnType<typeof setTimeout> | null = null;

export function subscribeDue(onDue: () => void): () => void {
  listeners.add(onDue);

  return () => {
    listeners.delete(onDue);
  };
}

export function signalDue(): void {
  // A page of scheduled jobs can cross zero on the same tick; one refetch answers all of them. The
  // window is trailing on purpose — the row came due this instant, and the server needs a beat to
  // activate and claim it before a refetch can show anything new.
  if (timer !== null) {
    return;
  }

  timer = setTimeout(() => {
    timer = null;
    for (const fn of listeners) {
      fn();
    }
  }, DUE_SIGNAL_COALESCE_MS);
}

/** Test seam — the coalescing timer and listener set are module singletons. */
export function resetDueSignalForTests(): void {
  if (timer !== null) {
    clearTimeout(timer);
    timer = null;
  }

  listeners.clear();
}
