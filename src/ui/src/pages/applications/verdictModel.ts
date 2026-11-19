// Pure read-side model for the post-deploy verdict panel on the Applications detail page. The server decides the verdict;
// this only labels, colours and orders what it returned.
import { errorMessage, errorStatus } from '@/lib/apiError';
import type { DeployVerdictCheck, DeployVerdictCheckState, DeployVerdictModel, DeployVerdictValue } from '@/types/applications';

const VERDICT_LABEL: Record<DeployVerdictValue, string> = {
  pass: 'Pass',
  warn: 'Warn',
  fail: 'Fail',
  pending: 'Pending',
  noData: 'No data',
};

const STATE_LABEL: Record<DeployVerdictCheckState, string> = {
  healthy: 'Healthy',
  warning: 'Warning',
  breaching: 'Breaching',
  acknowledged: 'Acknowledged',
  noData: 'No data',
};

// Worst first: what made the verdict, then what was waved through, then what had nothing to say.
const STATE_RANK: Record<DeployVerdictCheckState, number> = {
  breaching: 0,
  warning: 1,
  acknowledged: 2,
  healthy: 3,
  noData: 4,
};

export function verdictLabel(verdict: DeployVerdictValue): string {
  return VERDICT_LABEL[verdict] ?? verdict;
}

/** The verdict badge colours (light + dark), keyed like the board's status pills. */
export function verdictClass(verdict: DeployVerdictValue): string {
  switch (verdict) {
    case 'pass':
      return 'bg-green-500/15 text-green-700 dark:text-green-400';
    case 'warn':
      return 'bg-amber-500/15 text-amber-700 dark:text-amber-400';
    case 'fail':
      return 'bg-red-500/15 text-red-700 dark:text-red-400';
    default:
      return 'bg-muted text-muted-foreground';
  }
}

export function checkStateLabel(state: DeployVerdictCheckState): string {
  return STATE_LABEL[state] ?? state;
}

/** The checks worst first; ties keep the server's order (board tiles, then SLOs by name). */
export function orderChecks(checks: DeployVerdictCheck[]): DeployVerdictCheck[] {
  return checks
    .map((check, index) => ({ check, index }))
    .sort((a, b) => (STATE_RANK[a.check.state] ?? 5) - (STATE_RANK[b.check.state] ?? 5) || a.index - b.index)
    .map((x) => x.check);
}

/** The deploy as "previous → version (environment)". */
export function describeDeploy(deploy: { version: string; previousVersion: string | null; environment: string | null }): string {
  const versions = deploy.previousVersion ? `${deploy.previousVersion} → ${deploy.version}` : deploy.version;

  return deploy.environment ? `${versions} (${deploy.environment})` : versions;
}

/** The verdict panel polls while pending (until settled) and slowly otherwise; never faster than the board. */
export function verdictRefetchMs(verdict: DeployVerdictValue | undefined): number {
  return verdict === 'pending' ? 30_000 : 120_000;
}

/** The coverage line: how many checks had data. A pass resting on few of them is weak evidence. */
export function coverageLabel(verdict: Pick<DeployVerdictModel, 'checksWithData' | 'checksTotal'>): string {
  if (verdict.checksTotal === 0) {
    return 'No board tile with a rule and no SLO covers this application';
  }

  return `${verdict.checksWithData} of ${verdict.checksTotal} ${verdict.checksTotal === 1 ? 'check' : 'checks'} had data`;
}

/**
 * What the verdict card shows for the state of its read: nothing while there is no deploy to judge or the read is in
 * flight, "No matching deploy" for a 404, an error line for any other failure, else the verdict (with a note when a
 * refetch failed after an earlier answer, so a stale verdict is not taken for a fresh one).
 */
export type VerdictPanel =
  | { kind: 'hidden' }
  | { kind: 'noMatch' }
  | { kind: 'error'; message: string }
  | { kind: 'verdict'; verdict: DeployVerdictModel; staleError: string | null };

export function verdictPanel(read: { data: DeployVerdictModel | undefined; isError: boolean; error: unknown }): VerdictPanel {
  if (read.isError) {
    if (errorStatus(read.error) === 404) {
      return { kind: 'noMatch' };
    }

    const message = errorMessage(read.error, 'Unable to load the verdict.');

    return read.data ? { kind: 'verdict', verdict: read.data, staleError: message } : { kind: 'error', message };
  }

  return read.data ? { kind: 'verdict', verdict: read.data, staleError: null } : { kind: 'hidden' };
}

/**
 * Whether to ask for the verdict: once the deploy history shows a deploy, or when that history could not be read (the
 * verdict endpoint answers on its own, with a 404 when there is no deploy). Never for an unknown application.
 */
export function shouldLoadVerdict(application: string | undefined, deploys: { isError: boolean; count: number }): boolean {
  return !!application && (deploys.isError || deploys.count > 0);
}
