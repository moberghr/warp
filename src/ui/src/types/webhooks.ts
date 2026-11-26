// DTOs for the Webhooks feature (durable outbound delivery). These mirror the backend models in
// Warp.Core/Webhooks/IWebhookQueryService.cs, serialized camelCase. The per-delivery secret never
// crosses the wire — the detail carries only a `hasSecret` flag, and Authorization-class headers
// arrive already redacted to `***`.
import { AdapterCallOutcome } from '@/types/adapters';

// WebhookDeliveryStatus — numeric on the wire (§8.11, starts at 1), matching
// Warp.Core.Enums.WebhookDeliveryStatus.
export const WebhookDeliveryStatus = {
  Pending: 1,
  Delivered: 2,
  Exhausted: 3,
  // Ran out of retries while its endpoint was failing; waits (no live job) until the endpoint recovers (8.1).
  Parked: 4,
  // An operator cancelled it while pending or parked (8.1).
  Cancelled: 5,
} as const;
export type WebhookDeliveryStatus = (typeof WebhookDeliveryStatus)[keyof typeof WebhookDeliveryStatus];

// WebhookEndpointState — health of a delivery group sent with endpoint health tracking (8.1).
export const WebhookEndpointState = {
  Healthy: 1,
  Failing: 2,
} as const;
export type WebhookEndpointState = (typeof WebhookEndpointState)[keyof typeof WebhookEndpointState];

// WebhookFailureReason — why a delivery's last attempt failed (#357), numeric on the wire.
export const WebhookFailureReason = {
  Timeout: 1,
  DnsFailure: 2,
  ConnectionRefused: 3,
  TlsError: 4,
  ConnectionFailed: 5,
  AddressNotPublic: 6,
  UnsuccessfulStatus: 7,
  Other: 8,
} as const;
export type WebhookFailureReason = (typeof WebhookFailureReason)[keyof typeof WebhookFailureReason];

// WebhookSigning — numeric on the wire, matching Warp.Core.Enums.WebhookSigning.
export const WebhookSigning = {
  None: 1,
  StandardWebhooks: 2,
  Custom: 3,
} as const;
export type WebhookSigning = (typeof WebhookSigning)[keyof typeof WebhookSigning];

/** One row on the deliveries list page (no payload, headers, or secret). */
export interface WebhookDeliveryListItem {
  id: string;
  eventType: string;
  eventId: string;
  url: string;
  groupName: string | null;
  reference: string | null;
  status: WebhookDeliveryStatus;
  signingMode: WebhookSigning;
  attemptCount: number;
  nextAttemptAt: string | null;
  createdAt: string;
  /** HTTP status of the last attempt; null without a response or before the first attempt (#357). */
  lastStatusCode: number | null;
  /** Why the last attempt failed; null after a success or before the first attempt (#357). */
  lastFailureReason: WebhookFailureReason | null;
  /** When the last attempt finished (#357). */
  lastAttemptAt: string | null;
}

/** One attempt in a delivery's timeline — projected from the delivery's AdapterCallLog rows. */
export interface WebhookAttemptItem {
  callId: string;
  timestamp: string;
  durationMs: number;
  outcome: AdapterCallOutcome;
  statusCode: number | null;
  exceptionType: string | null;
}

/** The delivery detail page payload — the self-contained contract, with secret + headers redacted. */
export interface WebhookDeliveryDetail {
  id: string;
  eventType: string;
  eventId: string;
  url: string;
  /** Per-delivery headers as a JSON object with Authorization-class values redacted to `***`. */
  headersJson: string | null;
  groupName: string | null;
  reference: string | null;
  payloadJson: string;
  signingMode: WebhookSigning;
  /** Whether a signing secret is stored; the value itself never leaves the backend. */
  hasSecret: boolean;
  /** The retry delays in seconds (empty = single attempt). */
  retryScheduleSeconds: number[];
  successCodesJson: string | null;
  status: WebhookDeliveryStatus;
  attemptCount: number;
  nextAttemptAt: string | null;
  createdAt: string;
  expireAt: string | null;
  lastStatusCode: number | null;
  lastFailureReason: WebhookFailureReason | null;
  lastAttemptAt: string | null;
  /** Whether the delivery was sent with endpoint health tracking (8.1). */
  trackEndpointHealth: boolean;
  attempts: WebhookAttemptItem[];
}

/** Summary tile counts for the webhooks dashboard. */
export interface WebhookDeliverySummary {
  total: number;
  pending: number;
  delivered: number;
  exhausted: number;
  parked: number;
  cancelled: number;
}

/** Filter for the deliveries list; empty members are ignored server-side. */
export interface WebhookDeliveryFilter {
  status?: WebhookDeliveryStatus;
  eventType?: string;
  reference?: string;
  group?: string;
  since?: string;
  until?: string;
  page?: number;
  pageSize?: number;
}

/** Dimension for the "group webhooks" summary tables. */
export type WebhookGroupBy = 'type' | 'endpoint';

/** One row of a grouped-webhooks summary table — a key with its per-status counts. */
export interface WebhookGroupModel {
  key: string;
  total: number;
  pending: number;
  delivered: number;
  exhausted: number;
  parked: number;
  cancelled: number;
  lastActivityAt: string;
  // Endpoint health (8.1) — set only when grouping by endpoint; health is null for an untracked group.
  health?: WebhookEndpointState | null;
  failingSince?: string | null;
  nextProbeAt?: string | null;
  lastSuccessAt?: string | null;
  lastFailureAt?: string | null;
  /** Delivered share of the deliveries settled in the last 24 h (0..1). */
  successRate?: number | null;
  /** p95 send-to-delivered time over the last 24 h, in milliseconds. */
  p95DeliveryMs?: number | null;
}

/** One hourly point of the delivery-statistics time-series (created that hour, by status). */
export interface WebhookDeliveryHistoryPoint {
  hour: string;
  delivered: number;
  exhausted: number;
  pending: number;
  parked: number;
  cancelled: number;
  total: number;
}
