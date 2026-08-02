// DTOs for SLO / error-budget (§8.31). Mirror the backend SLO query service, serialized camelCase.
// Enums are numeric on the wire (§8.11, start at 1).

export const SloKind = {
  SuccessRate: 1,
  QueueWaitLatency: 2,
  ExecutionLatency: 3,
  BacklogDepth: 4,
  DeadlineAttainment: 5,
} as const;
export type SloKind = (typeof SloKind)[keyof typeof SloKind];

export const SloKindLabel: Record<SloKind, string> = {
  [SloKind.SuccessRate]: 'Success rate',
  [SloKind.QueueWaitLatency]: 'Queue-wait latency',
  [SloKind.ExecutionLatency]: 'Execution latency',
  [SloKind.BacklogDepth]: 'Backlog depth',
  [SloKind.DeadlineAttainment]: 'Deadline attainment',
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
}

/** True for the rate/attainment kinds whose target + attainment are ratios (0..1); false for latency/depth (ms / count). */
export const isRatioKind = (kind: SloKind): boolean =>
  kind === SloKind.SuccessRate || kind === SloKind.DeadlineAttainment;
