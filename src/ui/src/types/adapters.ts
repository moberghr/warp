// DTOs for the Adapters feature (outbound service-call observability). These mirror the
// backend models in Warp.Core/Services/IAdapterQueryService.cs, serialized camelCase.

// AdapterCallOutcome enum — numeric on the wire (§8.11, starts at 1), matching
// Warp.Core.Enums.AdapterCallOutcome.
export const AdapterCallOutcome = {
  Success: 1,
  Failed: 2,
  Throttled: 3,
  CircuitOpen: 4,
} as const;
export type AdapterCallOutcome = (typeof AdapterCallOutcome)[keyof typeof AdapterCallOutcome];

/** One row on the adapters fleet (list) page. */
export interface AdapterListItem {
  name: string;
  configSummary: string | null;
  firstSeenAt: string;
  lastSeenAt: string;
  totalCalls: number;
  errorCount: number;
  /** Errors ÷ total calls, in the range 0–1. */
  errorRate: number;
  avgDurationMs: number;
  hasPolicyConflict: boolean;
  /** Optional recent-window call-volume series for the trend sparkline (demo mode). */
  trend?: number[];
}

/** Per-operation row of the detail page operations table. */
export interface AdapterOperationStat {
  operation: string;
  calls: number;
  errors: number;
  errorRate: number;
  avgDurationMs: number;
}

/** Per-group row of the detail page groups table (shown only when the adapter carries groups). */
export interface AdapterGroupStat {
  group: string;
  calls: number;
  errors: number;
  errorRate: number;
  avgDurationMs: number;
  lastFailureAt: string | null;
}

/** One entry in the detail page recent-calls list (no captured payload bodies/headers). */
export interface AdapterCallSummary {
  id: string;
  operation: string;
  groupName: string | null;
  timestamp: string;
  durationMs: number;
  attempts: number;
  outcome: AdapterCallOutcome;
  statusCode: number | null;
  correlationId: string | null;
  tagsJson: string | null;
}

/** The adapter detail page payload. */
export interface AdapterDetail {
  name: string;
  configSummary: string | null;
  firstSeenAt: string;
  lastSeenAt: string;
  hasPolicyConflict: boolean;
  /** Display label for the group dimension (e.g. "Endpoint", "Shop"); "Group" by default. */
  groupLabel: string;
  totalCalls: number;
  errorCount: number;
  errorRate: number;
  avgDurationMs: number;
  /** 90th-percentile latency (ms) from the durable histogram; 0 when no data. */
  p90DurationMs: number;
  /** 95th-percentile latency (ms) from the durable histogram; 0 when no data. */
  p95DurationMs: number;
  /** 99th-percentile latency (ms) from the durable histogram; 0 when no data. */
  p99DurationMs: number;
  operations: AdapterOperationStat[];
  groups: AdapterGroupStat[];
  recentCalls: AdapterCallSummary[];
  /** Hourly performance time-series (durable, sampling-proof), oldest first. */
  history: AdapterHistoryPoint[];
}

/** One hourly point of an adapter's performance time-series. */
export interface AdapterHistoryPoint {
  hour: string;
  calls: number;
  errors: number;
  errorRate: number;
  avgDurationMs: number;
}

/** Full call-log row with the captured (already redacted + truncated) payloads. */
export interface AdapterCallDetail {
  id: string;
  adapterName: string;
  operation: string;
  groupName: string | null;
  timestamp: string;
  durationMs: number;
  attempts: number;
  outcome: AdapterCallOutcome;
  statusCode: number | null;
  exceptionType: string | null;
  exceptionMessage: string | null;
  requestSummary: string | null;
  requestHeaders: string | null;
  responseHeaders: string | null;
  requestBody: string | null;
  responseBody: string | null;
  machineName: string;
  traceId: string | null;
  tagsJson: string | null;
  correlationId: string | null;
}
