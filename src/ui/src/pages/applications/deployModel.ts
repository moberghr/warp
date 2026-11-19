// Pure read-side model for deploy history (#325). The API returns detected deploys newest first; the
// Applications detail page shows each one as "previous → new" within its environment, so a rollout reads as
// an upgrade path instead of a bare list of versions.
import type { ApplicationDeployModel } from '@/types/applications';

export interface DeployRow extends ApplicationDeployModel {
  /** The version deployed before this one in the same environment, or null for the oldest one shown. */
  previousVersion: string | null;
}

/**
 * Pairs each deploy with the one before it in the same environment (null and a missing environment are the
 * same environment). Input order does not matter; the result is newest first, ties broken by version so the
 * order is stable.
 */
export function withPreviousVersion(deploys: ApplicationDeployModel[]): DeployRow[] {
  const ordered = [...deploys].sort(
    (a, b) => Date.parse(b.firstSeenAt) - Date.parse(a.firstSeenAt) || a.version.localeCompare(b.version),
  );

  return ordered.map((deploy, index) => {
    const previous = ordered
      .slice(index + 1)
      .find((x) => x.application === deploy.application && (x.environment ?? '') === (deploy.environment ?? ''));

    return { ...deploy, previousVersion: previous?.version ?? null };
  });
}
