import { SloComparison, SloGaugeAggregate, SloGaugeAggregateLabel, SloKind, SloState, isRatioKind, type SloObjective } from '@/types/slo';
import { isCountKind } from './sloForm';

/** Formats a target/observed value per kind: ratio kinds as a %, latency as ms, depth as a count. */
export function formatObjectiveValue(kind: SloKind, value: number): string {
  if (isRatioKind(kind)) {
    return `${(value * 100).toFixed(2)}%`;
  }
  if (isCountKind(kind)) {
    return `${Math.round(value)}`;
  }
  return `${Math.round(value)} ms`;
}

export function formatBudget(budget: number): string {
  return `${Math.round(budget * 100)}%`;
}

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

function plain(value: number): string {
  return `${Number(value.toPrecision(7))}`;
}

function percent(fraction: number): string {
  return `${(fraction * 100).toFixed(2)}%`;
}

/**
 * The target cell. A metric latency objective shows its percentile and threshold (in the instrument's unit, which the
 * objective does not know, so no unit is printed); every other kind formats its target value.
 */
export function formatTarget(o: Pick<SloObjective, 'kind' | 'targetValue' | 'percentile' | 'comparison' | 'gaugeAggregate'>): string {
  if (o.kind === SloKind.MetricGaugeThreshold) {
    const aggregate = SloGaugeAggregateLabel[o.gaugeAggregate ?? SloGaugeAggregate.Average];
    const sign = o.comparison === SloComparison.AtLeast ? '≥' : '≤';

    return `${aggregate} ${sign} ${plain(o.targetValue)} in ${o.percentile ?? 95}%`;
  }
  if (o.kind === SloKind.MetricLatency) {
    return `p${o.percentile ?? 95} ≤ ${plain(o.targetValue)}`;
  }

  return formatObjectiveValue(o.kind, o.targetValue);
}

/**
 * The observed cell. A metric latency objective's attainment is the share of events within the threshold; a gauge
 * threshold's is the compliant share of the window's observed time.
 */
export function formatObserved(o: Pick<SloObjective, 'kind' | 'targetValue' | 'attainment'>): string {
  if (o.kind === SloKind.MetricGaugeThreshold) {
    return `${percent(o.attainment)} compliant`;
  }
  if (o.kind === SloKind.MetricLatency) {
    return `${percent(o.attainment)} ≤ ${plain(o.targetValue)}`;
  }

  return formatObjectiveValue(o.kind, o.attainment);
}
