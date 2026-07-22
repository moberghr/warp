import { describe, it, expect, afterEach, vi } from 'vitest';
import { shortType, shortId, stateName, formatBytes, isServerStale } from './format';
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
