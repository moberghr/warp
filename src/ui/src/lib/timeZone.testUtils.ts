import { afterAll, beforeAll, vi } from 'vitest';

/**
 * Test helper: runs the enclosing `describe` in `timeZone` and restores the suite's zone after it. Node re-reads `TZ`
 * whenever it is assigned, so local-time maths (`getHours`, `new Date('2026-03-29T02:30')`) follow it at once.
 */
export function inTimeZone(timeZone: string) {
  beforeAll(() => {
    vi.stubEnv('TZ', timeZone);
  });
  afterAll(() => {
    vi.unstubAllEnvs();
  });
}

/** The zone's offset from UTC at `ms`, in minutes (Zagreb: +60 in winter, +120 in summer). */
export function localOffsetMinutes(ms: number): number {
  return 0 - new Date(ms).getTimezoneOffset();
}
