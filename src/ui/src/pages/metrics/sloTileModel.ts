// Pure model behind SLO tiles on a metric board (spec 2026-11-19 batch 2): what the card shows for an objective's current
// evaluation, through the SLO page's own formatters (sloDisplay), and the tile "Add to board" saves from the SLO page.
import { MetricTileKind, MetricTileStatusRule, type MetricBoardTile, type MetricBoardTileResult } from '@/types/metricBoard';
import { SloKindLabel, SloState, SloStateLabel, type SloObjective } from '@/types/slo';
import { formatBudget, formatObserved, formatTarget, sloValueDisplay, type SloValueDisplay } from '@/pages/slo/sloDisplay';
import { chartTileFromPage } from './chartTileModel';

/** The card of an SLO tile whose objective no longer exists: it can only be removed. */
export interface MissingSloTileView {
  missing: true;
  message: string;
}

/** The card of an SLO tile: the objective's state and, when it has real numbers, attainment vs target, budget and burns. */
export interface SloTileView {
  missing: false;
  href: string;
  objectiveName: string;
  kindLabel: string;
  application: string | null;
  state: SloState;
  enabled: boolean;
  /** What the value cells may show (not evaluated yet, no data, or real numbers). */
  display: SloValueDisplay;
  /** Null unless `display` is `values`: a NoData or never-evaluated objective's stored numbers are placeholders. */
  observed: string | null;
  target: string;
  budget: string | null;
  burn: string | null;
  /** The burn exceeds 1× (the budget is being spent faster than the window allows) on either window. */
  burning: boolean;
  /** Null when never evaluated. */
  lastEvaluatedAt: string | null;
  /** "Acknowledged until …" while an acknowledgement holds, else null. */
  acknowledgedUntil: string | null;
  /** One line for the value area when there are no numbers. */
  note: string | null;
}

/** The SLO detail page of an objective. */
export function sloHref(id: number): string {
  return `/slo/${id}`;
}

/** What an SLO tile's card shows. `formatInstant` turns an ISO instant into display text (locale, time zone). */
export function sloTileView(
  result: Pick<MetricBoardTileResult, 'tile' | 'slo' | 'missing'>,
  formatInstant: (iso: string) => string,
): SloTileView | MissingSloTileView {
  const objective = result.slo;
  if (result.missing || !objective) {
    return {
      missing: true,
      message: 'The objective this tile showed was deleted. Remove the tile, or point it at another objective.',
    };
  }

  const display = sloValueDisplay(objective);
  const values = display === 'values';

  return {
    missing: false,
    href: sloHref(objective.id),
    objectiveName: objective.name,
    kindLabel: SloKindLabel[objective.kind],
    application: objective.application,
    state: objective.state,
    enabled: objective.enabled,
    display,
    observed: values ? formatObserved(objective) : null,
    target: formatTarget(objective),
    budget: values ? formatBudget(objective.budgetRemaining) : null,
    burn: values ? `${objective.burnRateShort.toFixed(2)}× / ${objective.burnRateLong.toFixed(2)}×` : null,
    burning: values && (objective.burnRateShort > 1 || objective.burnRateLong > 1),
    lastEvaluatedAt: objective.lastEvaluatedAt ? formatInstant(objective.lastEvaluatedAt) : null,
    acknowledgedUntil: objective.state === SloState.Acknowledged && objective.acknowledgedUntil
      ? `Acknowledged until ${formatInstant(objective.acknowledgedUntil)}`
      : null,
    note: display === 'not-evaluated'
      ? 'Not evaluated yet.'
      : display === 'no-data'
        ? `${SloStateLabel[SloState.NoData]}: nothing matched the objective in its window.`
        : null,
  };
}

/** The tile "Add to board" on the SLO page saves: the objective by id, labelled with the given name. */
export function sloTileFromObjective(objective: Pick<SloObjective, 'id'>, name: string, boardId: number | null): MetricBoardTile {
  return {
    id: 0,
    boardId,
    name: name.trim(),
    position: 0,
    instrument: '',
    application: null,
    tagFilter: null,
    kind: MetricTileKind.Slo,
    chartQuery: null,
    sloDefinitionId: objective.id,
    ratioTag: null,
    goodValues: null,
    badValues: null,
    gaugeAggregate: null,
    statusRule: MetricTileStatusRule.None,
    comparison: null,
    threshold: null,
    baselineDays: null,
    maxDeviation: null,
    deviationDirection: null,
  };
}

/** The objective picker's options, by name, each labelled with its application when it has one. */
export function objectiveOptions(objectives: readonly SloObjective[]): { id: number; label: string }[] {
  return [...objectives]
    .sort((a, b) => a.name.localeCompare(b.name) || a.id - b.id)
    .map((x) => ({ id: x.id, label: x.application ? `${x.name} @${x.application}` : x.name }));
}

/** What "Add to board" saves: the metric page's chart (its URL controls), or an SLO objective. */
export type AddToBoardSource =
  | { kind: 'chart'; instrument: string; params: URLSearchParams }
  | { kind: 'slo'; objective: Pick<SloObjective, 'id' | 'name'> };

/** The tile "Add to board" saves from `source` under `name` on board `boardId`. */
export function addToBoardTile(source: AddToBoardSource, name: string, boardId: number | null): MetricBoardTile {
  return source.kind === 'chart'
    ? chartTileFromPage(source.instrument, name, source.params, boardId)
    : sloTileFromObjective(source.objective, name, boardId);
}

/** The tile name "Add to board" suggests: the instrument's, or the objective's. */
export function addToBoardName(source: AddToBoardSource): string {
  return source.kind === 'chart' ? source.instrument : source.objective.name;
}
