import { describe, it, expect } from 'vitest';
import { lastRunHref, isLastRunCleanedUp, isLastRunOutcomeUnknown, describeCron } from './recurringModel';
import { State } from '@/types';

describe('lastRunHref', () => {
  it('links to the job detail while the job row is live', () => {
    expect(lastRunHref({ lastJobId: 'abc', lastRunCleanedUp: false })).toBe('/detail/abc');
  });

  it('does not link a cleaned-up run, even though its outcome is known', () => {
    // ExpirationCleanup stamped FinalState, but the Job row — and its detail page — is gone.
    expect(lastRunHref({ lastJobId: null, lastRunCleanedUp: true })).toBeNull();
  });

  it('does not link on either signal alone — a stale id must never reach a missing job', () => {
    expect(lastRunHref({ lastJobId: 'abc', lastRunCleanedUp: true })).toBeNull();
    expect(lastRunHref({ lastJobId: null, lastRunCleanedUp: false })).toBeNull();
  });
});

describe('isLastRunCleanedUp', () => {
  it('is true when a run happened and its job row was swept', () => {
    expect(isLastRunCleanedUp({ hasLastRun: true, lastRunCleanedUp: true })).toBe(true);
  });

  it('is false when the definition never fired', () => {
    expect(isLastRunCleanedUp({ hasLastRun: false, lastRunCleanedUp: true })).toBe(false);
  });

  it('is false while the job row is still there', () => {
    expect(isLastRunCleanedUp({ hasLastRun: true, lastRunCleanedUp: false })).toBe(false);
  });
});

describe('isLastRunOutcomeUnknown', () => {
  it('is true only for a run swept before FinalState stamping existed', () => {
    expect(isLastRunOutcomeUnknown({ hasLastRun: true, lastState: null })).toBe(true);
  });

  it('is false once the outcome was preserved', () => {
    expect(isLastRunOutcomeUnknown({ hasLastRun: true, lastState: State.Completed })).toBe(false);
  });

  it('is false when the definition never fired', () => {
    expect(isLastRunOutcomeUnknown({ hasLastRun: false, lastState: null })).toBe(false);
  });
});

describe('describeCron', () => {
  it('reads the documented 5-part forms in plain English', () => {
    expect(describeCron('* * * * *')).toBe('Every minute');
    expect(describeCron('0 9 * * *')).toBe('At 09:00 AM');
    expect(describeCron('*/5 * * * *')).toBe('Every 5 minutes');
    expect(describeCron('0 9 * * 1-5')).toBe('At 09:00 AM, Monday through Friday');
    expect(describeCron('0 0 1 * *')).toBe('At 12:00 AM, on day 1 of the month');
  });

  it('handles the 6-part form with leading seconds', () => {
    expect(describeCron('30 0 9 * * *')).toBe('At 09:00:30 AM');
  });

  it('returns null instead of throwing on an unparseable expression', () => {
    // A bad cron must never blank the page — the raw expression is still displayed beside this.
    expect(describeCron('not a cron')).toBeNull();
    expect(describeCron('99 99 99 99 99')).toBeNull();
  });

  it('returns null for a missing or blank expression', () => {
    expect(describeCron(null)).toBeNull();
    expect(describeCron(undefined)).toBeNull();
    expect(describeCron('   ')).toBeNull();
  });
});
