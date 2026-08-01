// DTOs for error grouping / the Issues dashboard (§8.29). Mirror the backend error-group query
// service, serialized camelCase. Enums are numeric on the wire (§8.11, start at 1) — same
// convention as ClientEventType in types/client.ts.

export const ErrorSource = {
  Job: 1,
  Endpoint: 2,
  Adapter: 3,
  Client: 4,
} as const;
export type ErrorSource = (typeof ErrorSource)[keyof typeof ErrorSource];

export const ErrorGroupKind = {
  Exception: 1,
  StatusCode: 2,
} as const;
export type ErrorGroupKind = (typeof ErrorGroupKind)[keyof typeof ErrorGroupKind];

export const ErrorGroupStatus = {
  Unresolved: 1,
  Resolved: 2,
  Ignored: 3,
} as const;
export type ErrorGroupStatus = (typeof ErrorGroupStatus)[keyof typeof ErrorGroupStatus];

export interface ErrorGroupSummary {
  fingerprint: string;
  source: ErrorSource;
  kind: ErrorGroupKind;
  exceptionType: string;
  title: string;
  culprit: string;
  statusCode: number | null;
  application: string | null;
  firstSeenAt: string;
  lastSeenAt: string;
  count: number;
  status: ErrorGroupStatus;
  isNew: boolean;
  isRegressed: boolean;
}

export interface ErrorGroupTrendPoint {
  hour: string;
  count: number;
}

export interface ErrorSample {
  traceId: string | null;
  timestamp: string;
  message: string | null;
  version: string | null;
}

export interface ErrorGroupDetail extends ErrorGroupSummary {
  lastSample: string | null;
  sampleTraceId: string | null;
  trend: ErrorGroupTrendPoint[];
  firstSeenVersion: string | null;
  lastSeenVersion: string | null;
  environment: string | null;
  recentSamples: ErrorSample[];
}

export interface ErrorGroupList {
  items: ErrorGroupSummary[];
  total: number;
}
