import cronstrue from 'cronstrue';
import type { RecurringJobModel } from '@/types';

// Pure read-side model for the recurring surfaces (the counterModel precedent), so the two cells
// that offer the last run — the Last Execution timestamp and the Last Result badge — can never
// disagree about whether it is openable, or about what "cleaned up" means.

// A last run is openable only while its Job row still exists. ExpirationCleanup keeps the newest 100
// RecurringJobLog rows per definition but sweeps the jobs themselves, and deleting a Job nulls
// RecurringJobLog.JobId (DeleteBehavior.SetNull) — so a cleaned-up run must render as plain text
// rather than a link into a 404 detail page, even though its outcome is still known.
export function lastRunHref(
  job: Pick<RecurringJobModel, 'lastJobId' | 'lastRunCleanedUp'>,
): string | null {
  if (!job.lastJobId || job.lastRunCleanedUp) {
    return null;
  }

  return `/detail/${job.lastJobId}`;
}

// "The run happened, its job row is gone, but ExpirationCleanup stamped the outcome before deleting
// it" — the result is displayable, just not clickable.
export function isLastRunCleanedUp(
  job: Pick<RecurringJobModel, 'hasLastRun' | 'lastRunCleanedUp'>,
): boolean {
  return job.hasLastRun && job.lastRunCleanedUp;
}

// The residual unknown: a run swept by a deployment that predates FinalState stamping. Nothing can
// recover its outcome, so it stays the bare "Cleaned up" label the UI used to show for every sweep.
export function isLastRunOutcomeUnknown(
  job: Pick<RecurringJobModel, 'hasLastRun' | 'lastState'>,
): boolean {
  return job.hasLastRun && job.lastState == null;
}

// A human-readable rendering of the cron expression, for the tooltip beside the raw one. The raw
// expression stays the primary display — it is what the definition was registered with and what an
// operator edits — so this is strictly an aid, and an expression cronstrue cannot parse (a typo, or
// syntax it does not cover) returns null rather than throwing: a bad cron must never blank the page.
// Warp accepts 5-part and 6-part (leading seconds) expressions, which cronstrue distinguishes by
// field count on its own.
export function describeCron(cron: string | null | undefined): string | null {
  if (!cron?.trim()) {
    return null;
  }

  try {
    return cronstrue.toString(cron, { throwExceptionOnParseError: true, verbose: false });
  } catch {
    return null;
  }
}
