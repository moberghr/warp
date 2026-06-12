/**
 * Demo mode is active when the page is loaded with `?demo` or built with
 * `VITE_DEMO=true`. In demo mode all API calls are served from the in-memory
 * mock adapter and the clock is pinned (see main.tsx), so every surface must
 * read as a stable, deterministic snapshot.
 */
export function isDemoMode(): boolean {
  return new URLSearchParams(window.location.search).has('demo')
    || import.meta.env.VITE_DEMO === 'true';
}

/** The instant the demo clock is pinned to. All demo data and every "now" anchor
 *  resolve to this, so charts and relative-time labels are fully deterministic. */
export const FROZEN_NOW = Date.UTC(2026, 4, 25, 11, 0, 0);

/**
 * Pin the entire clock to FROZEN_NOW. Must run before the demo data module loads
 * (data.ts seeds timestamps at import time).
 *
 * Overriding only `Date.now` is not enough: `new Date()` would still read the real
 * wall-clock, so hour-bucketed charts (dashboard history, counters history) build
 * their axis around the real "now" while the seeded data is anchored at FROZEN_NOW —
 * every bucket lookup misses and the charts render empty. Freezing the constructor's
 * no-arg form too keeps producers and consumers on the same instant. Parameterised
 * `new Date(ms)` / `new Date(iso)` and all static methods are preserved.
 */
export function freezeClock(): void {
  const RealDate = Date;

  class FrozenDate extends RealDate {
    constructor(...args: unknown[]) {
      if (args.length === 0) {
        super(FROZEN_NOW);
      } else {
        // @ts-expect-error forward the real Date constructor overloads verbatim
        super(...args);
      }
    }

    static now(): number {
      return FROZEN_NOW;
    }
  }

  globalThis.Date = FrozenDate as unknown as DateConstructor;
}
