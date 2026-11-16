// Pure read-side model for the post-deploy verdict panel on the Applications detail page. The server decides the verdict;
// this only labels, colours and orders what it returned.
import type { DeployVerdictCheck, DeployVerdictCheckState, DeployVerdictValue } from '@/types/applications';

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
