import { describe, it, expect, afterEach, vi } from 'vitest';
import {
  shortType, shortId, stateName, formatBytes, isServerStale,
  formatRelativeTime, formatDateTime, formatDateTimeExact, formatDateTimeMinute, formatDateTimeSecond,
  absoluteLabel, DASHBOARD_LOCALE,
  formatCountdown, countdownPhase, formatDurationRough, DUE_GRACE_MS,
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

  it('formatDateTimeSecond drops milliseconds but keeps seconds', () => {
    expect(formatDateTimeSecond('2026-01-02T03:04:05.678Z')).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/);
  });

  it('absoluteLabel defaults to second precision', () => {
    // The default drives every RelativeTime in the app, so a regression here silently puts
    // milliseconds back into every table column.
    expect(absoluteLabel('2026-01-02T03:04:05.678Z')).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/);
    expect(absoluteLabel('2026-01-02T03:04:05.678Z', 'exact')).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$/);
    expect(absoluteLabel('2026-01-02T03:04:05.678Z', 'minute')).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/);
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

describe('formatDurationRough', () => {
  it('picks the largest whole unit and pluralises it', () => {
    expect(formatDurationRough(1_000)).toBe('1 second');
    expect(formatDurationRough(45_000)).toBe('45 seconds');
    expect(formatDurationRough(90_000)).toBe('1 minute');
    expect(formatDurationRough(3 * 3_600_000)).toBe('3 hours');
    expect(formatDurationRough(50 * 3_600_000)).toBe('2 days');
  });

  it('reads the same on both sides of zero', () => {
    expect(formatDurationRough(-45_000)).toBe(formatDurationRough(45_000));
  });
});

describe('countdown labels', () => {
  const base = Date.UTC(2026, 4, 25, 11, 0, 0);
  const at = (offsetMs: number) => new Date(base + offsetMs).toISOString();

  it('counts down while the instant is still ahead', () => {
    expect(formatCountdown(at(120_000), base)).toBe('in 2 minutes');
    expect(formatCountdown(at(1_000), base)).toBe('in 1 second');
  });

  it('reads "due now" from the crossing until the grace expires', () => {
    // A scheduled job sits past its instant while ScheduledJobActivation flips it and a worker
    // claims it — that is waiting, not a run that already happened.
    expect(formatCountdown(at(0), base)).toBe('due now');
    expect(formatCountdown(at(-1), base)).toBe('due now');
    expect(formatCountdown(at(-DUE_GRACE_MS + 1_000), base)).toBe('due now');
  });

  it('calls out a real overrun once the grace is spent', () => {
    expect(formatCountdown(at(-DUE_GRACE_MS), base)).toBe('overdue by 1 minute');
    expect(formatCountdown(at(-5 * 60_000), base)).toBe('overdue by 5 minutes');
  });

  it('covers the worst-case healthy latency before crying overdue', () => {
    // ScheduledActivationInterval (10s) + a peer worker's MaxPollingInterval backoff (30s).
    expect(formatCountdown(at(-40_000), base)).toBe('due now');
  });

  it('degrades to empty on an unparseable timestamp instead of rendering NaN', () => {
    expect(formatCountdown('not-a-timestamp', base)).toBe('');
    expect(countdownPhase('not-a-timestamp', base)).toBe('future');
  });

  it('exposes the same three phases the cross-zero refetch keys on', () => {
    expect(countdownPhase(at(60_000), base)).toBe('future');
    expect(countdownPhase(at(-1_000), base)).toBe('due');
    expect(countdownPhase(at(-10 * 60_000), base)).toBe('overdue');
  });
});

describe('formatRelativeTime', () => {
  it('formats against a supplied instant so every label on the page shares one now', () => {
    const base = Date.UTC(2026, 4, 25, 11, 0, 0);
    expect(formatRelativeTime(new Date(base - 300_000).toISOString(), base)).toBe('5 minutes ago');
    expect(formatRelativeTime(new Date(base + 600_000).toISOString(), base)).toBe('in 10 minutes');
  });
});
