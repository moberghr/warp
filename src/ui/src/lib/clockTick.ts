import { isDemoMode } from '@/lib/demoMode';

/**
 * One clock for every relative-time label on the page.
 *
 * A label like "5 minutes ago" is computed at render time, so without a tick it is only as fresh as
 * the last render — and on a push-enabled deployment an idle list never re-renders at all. The naive
 * cure (a `setInterval` inside the label component) puts one timer on every row: a 100-row jobs table
 * would run 100 timers to move the same "now". So the interval lives here, once, and the labels
 * subscribe to it.
 *
 * Consumers read `tickNow()` rather than calling `Date.now()` themselves. That is what makes the
 * value a *store*: every subscriber formats against the same instant, and `useSyncExternalStore` can
 * compare snapshots between ticks — a value that did not change does not re-render (see
 * `hooks/useTicking`).
 */
export const TICK_INTERVAL_MS = 1_000;

const subscribers = new Set<() => void>();
let intervalId: ReturnType<typeof setInterval> | null = null;
let visibilityBound = false;

// Materialised on first read, not at module load: demo mode pins the clock from inside `boot()`
// (main.tsx), which runs after every import has been evaluated. A `Date.now()` captured up here
// would be the real wall clock and every demo label would be months out from the frozen data.
let now = 0;

export function tickNow(): number {
  if (now === 0) {
    now = Date.now();
  }

  return now;
}

function publish(): void {
  now = Date.now();
  for (const fn of subscribers) {
    fn();
  }
}

function start(): void {
  if (intervalId !== null) {
    return;
  }

  now = Date.now();

  // Demo mode freezes Date entirely, so every tick would publish the same instant and no label could
  // ever change. Running no timer at all keeps the Playwright screenshot runs free of a background
  // interval that only ever produces no-ops.
  if (isDemoMode()) {
    return;
  }

  intervalId = setInterval(publish, TICK_INTERVAL_MS);
}

function stop(): void {
  if (intervalId !== null) {
    clearInterval(intervalId);
    intervalId = null;
  }
}

// Browsers throttle setInterval to roughly once a minute in a backgrounded tab and freeze it outright
// while the machine sleeps (the same bite `stores/dashboard.ts` documents for the rate sampler). A
// throttled ticker is worse than none: the labels drift, then snap. So the timer stops while hidden
// and republishes the moment the tab comes back, before restarting.
function onVisibilityChange(): void {
  if (document.hidden) {
    stop();

    return;
  }

  publish();

  if (subscribers.size > 0) {
    start();
  }
}

export function subscribeTick(onTick: () => void): () => void {
  if (!visibilityBound) {
    document.addEventListener('visibilitychange', onVisibilityChange);
    visibilityBound = true;
  }

  subscribers.add(onTick);

  if (!document.hidden) {
    start();
  }

  return () => {
    subscribers.delete(onTick);

    // No labels mounted, no timer. A dashboard sitting on a page with no timestamps costs nothing.
    if (subscribers.size === 0) {
      stop();
    }
  };
}

/** Test seam: the module is a singleton, so a spec that asserts on start/stop needs a clean slate. */
export function resetClockTickForTests(): void {
  stop();
  subscribers.clear();
  now = 0;
}
