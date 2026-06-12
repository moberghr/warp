import { describe, it, expect } from 'vitest';
import { shortType, shortId, formatBytes, stateName, stateColor, isServerStale } from './format';
import { State } from '@/types';

describe('format utils', () => {
  describe('shortType', () => {
    it('returns the last segment of a dotted type name', () => {
      expect(shortType('MyApp.Jobs.SendEmailJob')).toBe('SendEmailJob');
    });

    it('strips assembly suffix before splitting', () => {
      expect(shortType('MyApp.Jobs.SendEmailJob, MyApp.Jobs')).toBe('SendEmailJob');
    });

    it('returns em dash for null/undefined/empty', () => {
      expect(shortType(null)).toBe('—');
      expect(shortType(undefined)).toBe('—');
      expect(shortType('')).toBe('—');
    });
  });

  describe('shortId', () => {
    it('returns the first 8 chars', () => {
      expect(shortId('abcdef01-2345-6789')).toBe('abcdef01');
    });
  });

  describe('formatBytes', () => {
    it('formats small values in bytes', () => {
      expect(formatBytes(512)).toBe('512 B');
    });

    it('formats KB / MB / GB', () => {
      expect(formatBytes(2048)).toBe('2 KB');
      expect(formatBytes(5 * 1024 * 1024)).toBe('5 MB');
      expect(formatBytes(2 * 1024 * 1024 * 1024)).toBe('2.0 GB');
    });
  });

  describe('stateName / stateColor', () => {
    it('maps known states to a readable name', () => {
      expect(stateName(State.Enqueued)).toBe('Enqueued');
      expect(stateName(State.Completed)).toBe('Completed');
    });

    it('falls back to "Unknown" for unmapped values', () => {
      expect(stateName(999 as State)).toBe('Unknown');
    });

    it('returns a tailwind class string per state', () => {
      expect(stateColor(State.Failed)).toContain('text-state-failed');
      expect(stateColor(State.Completed)).toContain('text-state-completed');
    });
  });

  describe('isServerStale', () => {
    it('returns true for heartbeats older than 30s', () => {
      const old = new Date(Date.now() - 60_000).toISOString();
      expect(isServerStale(old)).toBe(true);
    });

    it('returns false for fresh heartbeats', () => {
      expect(isServerStale(new Date().toISOString())).toBe(false);
    });
  });
});
