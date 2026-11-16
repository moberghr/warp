import { SloState, type SloObjective } from '@/types/slo';

/**
 * What an objective's attainment, budget and burn cells may show:
 * - `not-evaluated`: the evaluator has not produced a status yet;
 * - `no-data`: evaluated, but nothing matched the objective in the window — the stored budget and burn are
 *   placeholders, and rendering them would make a NoData objective look healthy;
 * - `values`: real numbers.
 */
export type SloValueDisplay = 'not-evaluated' | 'no-data' | 'values';

export function sloValueDisplay(objective: Pick<SloObjective, 'evaluated' | 'state'>): SloValueDisplay {
  if (!objective.evaluated) {
    return 'not-evaluated';
  }

  return objective.state === SloState.NoData ? 'no-data' : 'values';
}

/** True only when the attainment, budget, burn and budget bar carry real numbers. */
export function showsSloValues(objective: Pick<SloObjective, 'evaluated' | 'state'>): boolean {
  return sloValueDisplay(objective) === 'values';
}
