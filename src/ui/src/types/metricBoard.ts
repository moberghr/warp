// DTOs for the metrics health board (#326) — Warp.Core/Models/MetricBoardModels.cs, serialized camelCase.
// Enums are numeric on the wire (§8.11, start at 1).
import type { MetricKind, MetricTier } from './metrics';
import type { SloComparison, SloGaugeAggregate, SloObjective, SloState } from './slo';
import type { MetricAnnotation } from './annotations';

/** What a tile shows (MetricTileKind). */
export const MetricTileKind = {
  Value: 1,
  Ratio: 2,
  /** The metric page's chart with the stored controls (chartQuery) over the board's range; no status rule. */
  Chart: 3,
  /** An SLO objective's current evaluation (sloDefinitionId); no status rule of its own, no instrument. */
  Slo: 4,
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

/** One named board (GET /api/metrics/boards row). Boards are always shared; the Default board cannot be deleted. */
export interface MetricBoardSummary {
  id: number;
  name: string;
  description: string | null;
  position: number;
  /** The application the board is scoped to by default; it does not filter the tiles. */
  application: string | null;
  /** True for the Default board, which owns every tile without a board (boardId null). */
  isDefault: boolean;
  tileCount: number;
  createdBy: string | null;
}

/** The create / rename body of POST /api/metrics/boards (id 0 creates). */
export interface MetricBoardUpsert {
  id: number;
  name: string;
  description: string | null;
  position?: number;
  application?: string | null;
}

/** One stored tile (GET /api/metrics/board/tiles row; also the upsert body, with id 0 for a new tile). */
export interface MetricBoardTile {
  id: number;
  /** The board holding the tile; null is the Default board. Changing it moves the tile. */
  boardId: number | null;
  name: string;
  position: number;
  instrument: string;
  application: string | null;
  tagFilter: string | null;
  kind: MetricTileKind;
  /**
   * A chart tile's metric-page controls: the page's URL query without the range (`breakdown=…&filter=k:v&compare=7d`),
   * stored canonical by the server. Null for every other kind (and for a chart with no controls).
   */
  chartQuery: string | null;
  /** An SLO tile's objective (a soft reference); null for every other kind. Optional so older drafts still type. */
  sloDefinitionId?: number | null;
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

/** One evaluated tile of GET /api/metrics/board. A chart tile carries its kind and unit only: the page reads its series. */
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
   * annotation that covers the tile (its filter is empty or implied by the tile's, so it is about everything the tile
   * reads) is active at the window's end (#333). A note that merely overlaps the tile is listed but does not acknowledge.
   */
  status: SloState | null;
  /** The rule's own verdict before acknowledged noise (equal to status unless that is Acknowledged). */
  unacknowledgedStatus: SloState | null;
  /** Instrument annotations that apply to the tile and overlap the window, newest first (#333). */
  annotations: MetricAnnotation[];
  sparkline: MetricBoardSparkPoint[];
  /** An SLO tile's objective with its current evaluation (one objective read for the board); null otherwise or when missing. */
  slo?: SloObjective | null;
  /** True for an SLO tile whose objective no longer exists. */
  missing?: boolean;
}

export interface MetricBoard {
  /** The board read; null only for a read across every board (the server's verdict, never this page). */
  board: MetricBoardSummary | null;
  from: string;
  to: string;
  resolution: MetricTier;
  tiles: MetricBoardTileResult[];
  /**
   * Where the tile values and their 1d / 7d baselines stop (#328, ISO instant): the start of the fine bucket still being
   * written. Equal to `to` for a range that ended earlier, to `from` when nothing in it is complete yet. The sparklines
   * still run to `to`.
   */
  completeUntil: string;
  /** True when more instrument notes overlap the range than the board reads: some are left off the tiles. */
  annotationsTruncated: boolean;
}
