import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  isStale,
  statusDotColor,
  staleGraceMs,
  hasServerGrace,
  setInstanceStaleGrace,
  resetLivenessForTests,
  FALLBACK_STALE_MS,
} from './liveness';

describe('liveness', () => {
  beforeEach(() => {
    resetLivenessForTests();
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-01-01T00:00:00Z'));
  });

  afterEach(() => {
    resetLivenessForTests();
    vi.useRealTimers();
  });

  it('falls back to the dashboard guess until the server answers', () => {
    expect(hasServerGrace()).toBe(false);
    expect(staleGraceMs()).toBe(FALLBACK_STALE_MS);

    expect(isStale('2026-01-01T00:00:00Z')).toBe(false);
    expect(isStale('2025-12-31T23:59:55Z')).toBe(false); // 5s of silence
    expect(isStale('2025-12-31T23:59:00Z')).toBe(true); // 60s of silence
  });

  it('adopts the threshold the server used, so the same silence reads the same everywhere', () => {
    setInstanceStaleGrace(120);

    expect(hasServerGrace()).toBe(true);
    expect(staleGraceMs()).toBe(120_000);

    // 60s of silence was stale under the 30s guess and is live under the server's 2 minute grace.
    expect(isStale('2025-12-31T23:59:00Z')).toBe(false);
    expect(isStale('2025-12-31T23:57:00Z')).toBe(true);
  });

  it('ignores a missing or nonsensical grace rather than reading every instance as dead', () => {
    setInstanceStaleGrace(undefined);
    expect(hasServerGrace()).toBe(false);

    setInstanceStaleGrace(0);
    expect(hasServerGrace()).toBe(false);

    setInstanceStaleGrace(-5);
    expect(hasServerGrace()).toBe(false);
    expect(staleGraceMs()).toBe(FALLBACK_STALE_MS);
  });

  it('colours paused over stale', () => {
    expect(statusDotColor(true, '2026-01-01T00:00:00Z')).toBe('bg-amber-500');
    expect(statusDotColor(true, null)).toBe('bg-red-500');
    expect(statusDotColor(false, null)).toBe('bg-green-500');
  });
});
