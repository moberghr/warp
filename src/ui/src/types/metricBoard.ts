// DTOs for the metrics health board (#326) — Warp.Core/Models/MetricBoardModels.cs, serialized camelCase.
// Enums are numeric on the wire (§8.11, start at 1).
import type { MetricKind, MetricTier } from './metrics';
import type { SloComparison, SloGaugeAggregate, SloState } from './slo';
import type { MetricAnnotation } from './annotations';

/** What a tile shows (MetricTileKind). */
export const MetricTileKind = {
  Value: 1,
  Ratio: 2,
} as const;
export type MetricTileKind = (typeof MetricTileKind)[keyof typeof MetricTileKind];

/** How a tile picks its status colour (MetricTileStatusRule). */
export const MetricTileStatusRule = {
  None: 1,
  Threshold: 2,
  Deviation: 3,
} as const;
export type MetricTileStatusRule = (typeof MetricTileStatusRule)[keyof typeof MetricTileStatusRule];

/** Which move away from the baseline a deviation rule treats as bad (MetricDeviationDirection). */
export const MetricDeviationDirection = {
  Drop: 1,
  Rise: 2,
  Either: 3,
} as const;
export type MetricDeviationDirection = (typeof MetricDeviationDirection)[keyof typeof MetricDeviationDirection];

/** One stored tile (GET /api/metrics/board/tiles row; also the upsert body, with id 0 for a new tile). */
export interface MetricBoardTile {
  id: number;
  name: string;
  position: number;
  instrument: string;
  application: string | null;
  tagFilter: string | null;
  kind: MetricTileKind;
  ratioTag: string | null;
  goodValues: string | null;
  badValues: string | null;
  gaugeAggregate: SloGaugeAggregate | null;
  statusRule: MetricTileStatusRule;
  comparison: SloComparison | null;
  threshold: number | null;
  baselineDays: number | null;
  maxDeviation: number | null;
  deviationDirection: MetricDeviationDirection | null;
}

export interface MetricBoardSparkPoint {
  timestamp: string;
  value: number | null;
}

/** One evaluated tile of GET /api/metrics/board. */
export interface MetricBoardTileResult {
  tile: MetricBoardTile;
  /** Null when the instrument is not in the registry. */
  instrumentKind: MetricKind | null;
  unit: string | null;
  /** Events, a gauge aggregate, or a 0–1 ratio over the window; null when there is nothing to show. */
  current: number | null;
  dayAgo: number | null;
  weekAgo: number | null;
  /**
   * Null when the tile has no status rule. Acknowledged when the rule says Warning / Breaching but an acknowledged-noise
   * annotation that applies to the tile is active at the window's end (#333).
   */
  status: SloState | null;
  /** The rule's own verdict before acknowledged noise (equal to status unless that is Acknowledged). */
  unacknowledgedStatus: SloState | null;
  /** Instrument annotations that apply to the tile and overlap the window, newest first (#333). */
  annotations: MetricAnnotation[];
  sparkline: MetricBoardSparkPoint[];
}

export interface MetricBoard {
  from: string;
  to: string;
  resolution: MetricTier;
  tiles: MetricBoardTileResult[];
}
