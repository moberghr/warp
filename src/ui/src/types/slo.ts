// DTOs for SLO / error-budget (§8.31). Mirror the backend SLO query service, serialized camelCase.
// Enums are numeric on the wire (§8.11, start at 1).

export const SloKind = {
  SuccessRate: 1,
  QueueWaitLatency: 2,
  ExecutionLatency: 3,
  BacklogDepth: 4,
  DeadlineAttainment: 5,
  MetricRatio: 6,
  MetricThreshold: 7,
  MetricVolumeFloor: 8,
  MetricGap: 9,
  MetricLatency: 10,
} as const;
export type SloKind = (typeof SloKind)[keyof typeof SloKind];

export const SloKindLabel: Record<SloKind, string> = {
  [SloKind.SuccessRate]: 'Success rate',
  [SloKind.QueueWaitLatency]: 'Queue-wait latency',
  [SloKind.ExecutionLatency]: 'Execution latency',
  [SloKind.BacklogDepth]: 'Backlog depth',
  [SloKind.DeadlineAttainment]: 'Deadline attainment',
  [SloKind.MetricRatio]: 'Metric ratio',
  [SloKind.MetricThreshold]: 'Metric threshold',
  [SloKind.MetricVolumeFloor]: 'Metric volume floor',
  [SloKind.MetricGap]: 'Metric gap',
  [SloKind.MetricLatency]: 'Metric latency',
};

export const SloState = {
  Healthy: 1,
  Warning: 2,
  Breaching: 3,
  Acknowledged: 4,
  NoData: 5,
} as const;
export type SloState = (typeof SloState)[keyof typeof SloState];

export const SloStateLabel: Record<SloState, string> = {
  [SloState.Healthy]: 'Healthy',
  [SloState.Warning]: 'Warning',
  [SloState.Breaching]: 'Breaching',
  [SloState.Acknowledged]: 'Acknowledged',
  [SloState.NoData]: 'No data',
};

export interface SloObjective {
  id: number;
  name: string;
  kind: SloKind;
  dimension: string;
  application: string | null;
  targetValue: number;
  percentile: number | null;
  windowSeconds: number;
  enabled: boolean;
  evaluated: boolean;
  attainment: number;
  budgetRemaining: number;
  burnRateShort: number;
  burnRateLong: number;
  state: SloState;
  acknowledgedUntil: string | null;
  lastEvaluatedAt: string | null;
  /** Metric kinds only (6-10); null/absent otherwise. Mirrors SloObjectiveModel. */
  tagFilter?: string | null;
  secondaryMetric?: string | null;
  secondaryTagFilter?: string | null;
  ratioTag?: string | null;
  goodValues?: string | null;
  badValues?: string | null;
  baselineDays?: number | null;
}

export interface SloList {
  items: SloObjective[];
}

export interface SloUpsertRequest {
  id: number;
  name: string;
  kind: SloKind;
  dimension: string;
  application: string | null;
  targetValue: number;
  percentile: number | null;
  windowSeconds: number;
  enabled: boolean;
  tagFilter?: string | null;
  secondaryMetric?: string | null;
  secondaryTagFilter?: string | null;
  ratioTag?: string | null;
  goodValues?: string | null;
  badValues?: string | null;
  baselineDays?: number | null;
}

/** True for the metric kinds (6-10), whose Dimension is an instrument name. */
export const isMetricKind = (kind: SloKind): boolean =>
  kind === SloKind.MetricRatio ||
  kind === SloKind.MetricThreshold ||
  kind === SloKind.MetricVolumeFloor ||
  kind === SloKind.MetricGap ||
  kind === SloKind.MetricLatency;

/** True for the kinds whose target + attainment are ratios (0..1); false for latency/depth/count (ms / count). */
export const isRatioKind = (kind: SloKind): boolean =>
  kind === SloKind.SuccessRate ||
  kind === SloKind.DeadlineAttainment ||
  kind === SloKind.MetricRatio ||
  kind === SloKind.MetricVolumeFloor;
