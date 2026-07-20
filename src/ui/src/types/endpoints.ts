// DTOs for the inbound Endpoint Observability feature (requests to Warp-exposed HTTP endpoints).
// Mirror the backend models in Warp.Core/Services/IEndpointQueryService.cs, serialized camelCase.

import { AdapterCallOutcome } from './adapters';

// Inbound reuses AdapterCallOutcome (only Success=1 / Failed=2 apply).
export { AdapterCallOutcome };

/** One row on the endpoints (list) page. Identity is the HTTP method + route template. */
export interface EndpointListItem {
  /** URL-safe id for the detail route (encodes method + route template). */
  id: string;
  method: string;
  routeTemplate: string;
  /** Display identity, e.g. "GET /orders/{id}". */
  route: string;
  totalCalls: number;
  errorCount: number;
  /** Errors ÷ total calls, in the range 0–1. */
  errorRate: number;
  avgDurationMs: number;
}

/** Per-caller (group) row of the detail page callers table (shown only when calls carry a group). */
export interface EndpointGroupStat {
  group: string;
  calls: number;
  errors: number;
  errorRate: number;
  avgDurationMs: number;
  lastFailureAt: string | null;
}

/** One entry in the detail page recent-calls list (no captured payload bodies/headers). */
export interface EndpointCallSummary {
  id: string;
  timestamp: string;
  durationMs: number;
  outcome: AdapterCallOutcome;
  statusCode: number | null;
  remoteIp: string | null;
  userAgent: string | null;
  user: string | null;
  groupName: string | null;
}

/** The endpoint detail page payload. */
export interface EndpointDetail {
  id: string;
  method: string;
  routeTemplate: string;
  route: string;
  /** Display label for the group dimension (e.g. "Caller", "Channel"); "Caller" by default. */
  groupLabel: string;
  totalCalls: number;
  errorCount: number;
  errorRate: number;
  avgDurationMs: number;
  groups: EndpointGroupStat[];
  recentCalls: EndpointCallSummary[];
}

/** Full call-log row with the captured (already redacted + truncated) payloads and caller metadata. */
export interface EndpointCallDetail {
  id: string;
  method: string;
  routeTemplate: string;
  operation: string;
  groupName: string | null;
  timestamp: string;
  durationMs: number;
  outcome: AdapterCallOutcome;
  statusCode: number | null;
  remoteIp: string | null;
  userAgent: string | null;
  user: string | null;
  exceptionType: string | null;
  exceptionMessage: string | null;
  requestHeaders: string | null;
  responseHeaders: string | null;
  requestBody: string | null;
  responseBody: string | null;
  machineName: string;
  traceId: string | null;
}
