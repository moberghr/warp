// DTOs for the custom application metrics read API (Warp.Core/Models/MetricModels.cs), serialized camelCase.
// Enums are numeric on the wire (§8.11, start at 1).

export const MetricKind = {
  Counter: 1,
  UpDownCounter: 2,
  Histogram: 3,
  Gauge: 4,
} as const;
export type MetricKind = (typeof MetricKind)[keyof typeof MetricKind];

/** The resolution a series line was served at (Warp.Core.Services.MetricTier). */
export const MetricTier = {
  Fine: 1,
  Hourly: 2,
  Daily: 3,
} as const;
export type MetricTier = (typeof MetricTier)[keyof typeof MetricTier];

/** One row of GET /api/metrics. */
export interface MetricInstrument {
  name: string;
  meter: string;
  kind: MetricKind;
  unit: string | null;
  description: string | null;
  /** Histograms: bucket upper bounds; empty for every other kind. */
  bounds: number[];
  /** Tag keys observed on the instrument (keys only, never values). */
  tagKeys: string[];
  firstSeenAt: string;
  lastSeenAt: string;
  /** When the instrument last collapsed on a cardinality cap; null when it never did. */
  collapsedAt: string | null;
  collapsedTags: string[];
}

export interface MetricPoint {
  /** Bucket start (UTC). On a compare overlay it is already shifted forward to align with the current range. */
  timestamp: string;
  /** Headline value: sum (counter / up-down counter), count (histogram), average (gauge). */
  value: number;
  count: number;
  sum: number;
  min: number | null;
  max: number | null;
  /** Histograms, when a percentile was requested: upper bound of the bucket the percentile falls in. */
  percentile: number | null;
}

export interface MetricSeriesLine {
  /** Breakdown value, `{none}`, `(rest)`, or `total` when no breakdown was asked for. */
  name: string;
  /** Overlay offset label (`1d` / `7d`); null for the current range. */
  compare: string | null;
  resolution: MetricTier;
  points: MetricPoint[];
}

/** The answer to GET /api/metrics/{id}/series. */
export interface MetricSeries {
  instrument: string;
  kind: MetricKind;
  unit: string | null;
  from: string;
  to: string;
  resolution: MetricTier;
  series: MetricSeriesLine[];
}
