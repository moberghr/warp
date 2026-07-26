// DTOs for client (browser) observability (§8.27). Mirror Warp.Core/Services/IClientEventQueryService.cs,
// serialized camelCase.

// ClientEventType enum — numeric on the wire (§8.11, starts at 1).
export const ClientEventType = {
  Error: 1,
  Vital: 2,
  Log: 3,
  Event: 4,
  Request: 5,
} as const;
export type ClientEventType = (typeof ClientEventType)[keyof typeof ClientEventType];

export interface ClientNameCount {
  name: string;
  count: number;
}

export interface ClientVitalStat {
  name: string;
  sampleCount: number;
  avgValue: number;
  /** The p75 value — Google's Core Web Vitals percentile. */
  p75Value: number;
}

export interface ClientHistoryPoint {
  hour: string;
  errors: number;
  logs: number;
  events: number;
  vitals: number;
}

export interface ClientObservabilitySummary {
  application: string | null;
  errorCount: number;
  logCount: number;
  eventCount: number;
  vitalCount: number;
  errorRate: number;
  topErrors: ClientNameCount[];
  topEvents: ClientNameCount[];
  vitals: ClientVitalStat[];
  history: ClientHistoryPoint[];
}

export interface ClientEventItem {
  id: string;
  application: string | null;
  type: ClientEventType;
  name: string | null;
  level: string | null;
  message: string | null;
  value: number | null;
  url: string | null;
  traceId: string | null;
  sessionId: string | null;
  timestamp: string;
}

export interface ClientEventPage {
  items: ClientEventItem[];
  total: number;
}

export interface ClientEventDetail extends ClientEventItem {
  stack: string | null;
  release: string | null;
  userAgent: string | null;
  remoteIp: string | null;
  properties: string | null;
  breadcrumbs: string | null;
  receivedAt: string;
}

/** One row on the unified session timeline — a client event (kind 'client') or a server endpoint call (kind 'endpoint'), joined by trace id. */
export interface ClientSessionEntry {
  kind: 'client' | 'endpoint';
  timestamp: string;
  traceId: string | null;
  eventId: string | null;
  type: ClientEventType | null;
  name: string | null;
  level: string | null;
  message: string | null;
  value: number | null;
  url: string | null;
  method: string | null;
  route: string | null;
  statusCode: number | null;
  durationMs: number | null;
  outcome: string | null;
}

export interface ClientSession {
  sessionId: string;
  application: string | null;
  entries: ClientSessionEntry[];
}
