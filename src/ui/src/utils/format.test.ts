import { describe, it, expect, afterEach, vi } from 'vitest';
import {
  shortType, shortId, stateName, formatBytes, isServerStale,
  formatRelativeTime, formatDateTime, formatDateTimeExact, stateColor, serverStatusDotColor,
} from './format';
import { State } from '@/types';

describe('shortType', () => {
  it('returns the em-dash placeholder for null/empty', () => {
    expect(shortType(null)).toBe('—');
    expect(shortType(undefined)).toBe('—');
    expect(shortType('')).toBe('—');
  });

  it('extracts the class name from an assembly-qualified type', () => {
    expect(shortType('MyApp.Jobs.SyncBooking, MyApp, Version=1.0.0.0')).toBe('SyncBooking');
  });

  it('returns a bare type name unchanged', () => {
    expect(shortType('SyncBooking')).toBe('SyncBooking');
  });
});

describe('shortId', () => {
  it('takes the first 8 chars', () => {
    expect(shortId('0123456789abcdef')).toBe('01234567');
  });
});

describe('stateName', () => {
  it('names known states', () => {
    expect(stateName(State.Completed)).toBe('Completed');
    expect(stateName(State.Scheduled)).toBe('Scheduled');
  });

  it('falls back to Unknown', () => {
    expect(stateName(999 as State)).toBe('Unknown');
  });
});

describe('formatBytes', () => {
  it('formats across unit boundaries', () => {
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(2048)).toBe('2 KB');
    expect(formatBytes(5 * 1024 * 1024)).toBe('5 MB');
    expect(formatBytes(3 * 1024 * 1024 * 1024)).toBe('3.0 GB');
  });
});

describe('date formatters', () => {
  afterEach(() => vi.useRealTimers());

  it('formatDateTime / formatDateTimeExact render the yyyy-MM-dd HH:mm:ss.SSS shape', () => {
    // Zone-dependent value → assert the shape, not the exact local time (CI vs local zone differ).
    const shape = /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$/;
    expect(formatDateTime('2026-01-02T03:04:05.678Z')).toMatch(shape);
    expect(formatDateTimeExact('2026-01-02T03:04:05.678Z')).toMatch(shape);
  });

  it('formatRelativeTime is relative to the current clock', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-01-01T00:01:00Z'));
    // Locale-agnostic: 1 minute ago renders the "1" quantity in any locale (e.g. "1 minute ago",
    // "prije 1 minutu"); asserting the English word would be locale-dependent.
    const rel = formatRelativeTime('2026-01-01T00:00:00Z');
    expect(rel).toBeTruthy();
    expect(rel).toContain('1');
  });
});

describe('stateColor', () => {
  it('returns a tailwind class for every state and a default', () => {
    for (const s of [State.Enqueued, State.Awaiting, State.Processing, State.Completed, State.Failed, State.Deleted, State.Scheduled]) {
      expect(stateColor(s)).toContain('bg-');
    }
    expect(stateColor(999 as State)).toContain('bg-');
  });
});

describe('isServerStale', () => {
  afterEach(() => vi.useRealTimers());

  it('is false for a recent heartbeat and true past the 30s threshold', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-01-01T00:00:00Z'));

    expect(isServerStale('2026-01-01T00:00:00Z')).toBe(false);
    expect(isServerStale('2025-12-31T23:59:55Z')).toBe(false); // 5s ago
    expect(isServerStale('2025-12-31T23:59:00Z')).toBe(true); // 60s ago
  });
});

describe('serverStatusDotColor', () => {
  afterEach(() => vi.useRealTimers());

  it('is amber when paused (regardless of heartbeat)', () => {
    expect(serverStatusDotColor('2020-01-01T00:00:00Z', '2026-01-01T00:00:00Z')).toBe('bg-amber-500');
  });

  it('is green when the heartbeat is fresh and red when stale', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-01-01T00:00:00Z'));
    expect(serverStatusDotColor('2026-01-01T00:00:00Z', null)).toBe('bg-green-500');
    expect(serverStatusDotColor('2025-12-31T23:59:00Z', null)).toBe('bg-red-500');
  });
});
