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
  /**
   * The current range's newest bucket while it is still filling (it ends after now, or within one flush interval of
   * now), so its value reads low. Always false on an overlay.
   */
  incomplete: boolean;
  /** Length of this point's bucket in seconds (an hourly row in a fine read is 3600); the divisor for a rate. */
  bucketSeconds: number;
}

/** The headline value of one range over its complete buckets (the still-filling bucket is left out, never prorated). */
export interface MetricHeadline {
  /** Overlay offset label (`1d` / `7d`); null for the current range. */
  compare: string | null;
  /** Sum (counter / up-down counter), count (histogram), average (gauge); null when no complete bucket has a measurement. */
  value: number | null;
  count: number;
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
  /** Where the complete buckets end: the headlines cover [from, completeUntil), each overlay the same span shifted back. */
  completeUntil: string;
  series: MetricSeriesLine[];
  /** The current range first, then one per overlay. */
  headlines: MetricHeadline[];
}
