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
} as const;
export type WebhookDeliveryStatus = (typeof WebhookDeliveryStatus)[keyof typeof WebhookDeliveryStatus];

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
  attempts: WebhookAttemptItem[];
}

/** Summary tile counts for the webhooks dashboard. */
export interface WebhookDeliverySummary {
  total: number;
  pending: number;
  delivered: number;
  exhausted: number;
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
  lastActivityAt: string;
}
