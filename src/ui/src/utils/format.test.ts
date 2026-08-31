import { describe, it, expect, afterEach, vi } from 'vitest';
import {
  shortType, shortId, stateName, formatBytes, isServerStale,
  formatRelativeTime, formatDateTime, formatDateTimeExact, formatDateTimeMinute, DASHBOARD_LOCALE,
  stateColor, serverStatusDotColor,
  httpStatusName,
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

  it('formatRelativeTime renders English regardless of the host locale', () => {
    // luxon's toRelative defaults to the host locale; this test machine is hr-HR, which produced
    // "za 10 minuta" before the locale was pinned. The dashboard is hardcoded English everywhere
    // else, and this label is now the primary content of the next/last execution columns.
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-01-02T03:00:00.000Z'));

    expect(formatRelativeTime('2026-01-02T03:10:00.000Z')).toBe('in 10 minutes');
    expect(formatRelativeTime('2026-01-02T02:55:00.000Z')).toBe('5 minutes ago');
  });

  it('formatDateTimeMinute drops seconds and milliseconds', () => {
    // Cron occurrences are minute-aligned, so the recurring surfaces render to the minute.
    expect(formatDateTimeMinute('2026-01-02T03:04:05.678Z')).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/);
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

describe('httpStatusName', () => {
  it('names the common codes', () => {
    expect(httpStatusName(200)).toBe('OK');
    expect(httpStatusName(401)).toBe('Unauthorized');
    expect(httpStatusName(404)).toBe('Not Found');
    expect(httpStatusName(429)).toBe('Too Many Requests');
    expect(httpStatusName(500)).toBe('Internal Server Error');
    expect(httpStatusName(503)).toBe('Service Unavailable');
  });

  it('falls back to the status class for unmapped codes', () => {
    expect(httpStatusName(299)).toBe('Success');
    expect(httpStatusName(499)).toBe('Client Error');
    expect(httpStatusName(599)).toBe('Server Error');
  });

  it('is Unknown outside the status code range', () => {
    expect(httpStatusName(0)).toBe('Unknown');
    expect(httpStatusName(999)).toBe('Unknown');
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

const NEWLINE = String.fromCharCode(10);

describe('locale pinning', () => {
  it('every locale-sensitive call site passes DASHBOARD_LOCALE', () => {
    // The dashboard must render identically for every viewer, so a bare toLocaleString() /
    // toLocaleDateString() / toLocaleTimeString() — which follows the HOST locale — is a defect:
    // one machine reads "1,234" and "Mon", another "1.234" and "pon". Caught here rather than in
    // review, because the default is silent and only shows up on someone else's machine.
    //
    // Sources come from Vite's import.meta.glob rather than node:fs, so the project keeps its
    // "types": ["vite/client"] tsconfig with no @types/node dependency.
    const sources = import.meta.glob('/src/**/*.{ts,tsx}', { query: '?raw', import: 'default', eager: true }) as Record<string, string>;
    const offenders: string[] = [];

    for (const [path, source] of Object.entries(sources)) {
      if (path.includes('.test.')) {
        continue;
      }

      source.split(NEWLINE).forEach((line, index) => {
        const call = /\.toLocale(String|DateString|TimeString)\(([^)]*)\)/.exec(line);
        if (call && !call[2].includes('DASHBOARD_LOCALE')) {
          offenders.push(path + ':' + (index + 1));
        }
      });
    }

    expect(Object.keys(sources).length).toBeGreaterThan(50); // the glob actually matched something
    expect(offenders).toEqual([]);
  });

  it('pins a concrete English locale', () => {
    expect(DASHBOARD_LOCALE).toBe('en-US');
    expect((1234.5).toLocaleString(DASHBOARD_LOCALE)).toBe('1,234.5');
  });
});
