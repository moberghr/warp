// Deterministic demo-mode fixtures for the Webhooks pages. All timestamps are anchored to the pinned
// demo clock (FROZEN_NOW) so relative-time labels + chart buckets render identically across screenshot
// runs. Typed against the real DTOs so shape drift breaks the build, not the demo. The axios mock router
// in `demo/adapter.ts` serves these; production reads the real REST endpoints via `@/api`.
//
// The set is GENERATED (not hand-listed) so the graphs have rich, multi-hour shape: ~72 deliveries spread
// one every ~20 minutes across the last 24h, over 6 event types × 5 endpoints, with a deterministic status
// mix. That fills the delivery-statistics chart with 24 hourly bars, gives the group tables real counts,
// and paginates the list.
import { FROZEN_NOW } from '@/lib/demoMode';
import { AdapterCallOutcome } from '@/types/adapters';
import { WebhookDeliveryStatus, WebhookSigning } from '@/types/webhooks';
import type {
  WebhookDeliveryListItem,
  WebhookDeliveryDetail,
  WebhookDeliverySummary,
} from '@/types/webhooks';

function ago(minutes: number): string {
  return new Date(FROZEN_NOW - minutes * 60_000).toISOString();
}

function ahead(minutes: number): string {
  return new Date(FROZEN_NOW + minutes * 60_000).toISOString();
}

const EVENT_TYPES = [
  'order.completed',
  'order.shipped',
  'invoice.finalized',
  'invoice.payment_failed',
  'customer.created',
  'shipment.delivered',
];

const ENDPOINTS = [
  'https://hooks.acme.example/orders',
  'https://hooks.acme.example/customers',
  'https://hooks.globex.example/inbound',
  'https://hooks.initech.example/ship',
  'https://hooks.umbrella.example/billing',
];

const SIGNING = [WebhookSigning.StandardWebhooks, WebhookSigning.None, WebhookSigning.Custom];

const COUNT = 72;

// Deterministic status mix: ~every 11th exhausted, ~every 4th pending, rest delivered. The newest few are
// weighted toward Pending so the "in-flight" tile + latest bar look live.
function statusFor(i: number): WebhookDeliveryStatus {
  if (i >= COUNT - 3) {
    return WebhookDeliveryStatus.Pending;
  }
  if (i % 11 === 0) {
    return WebhookDeliveryStatus.Exhausted;
  }
  if (i % 4 === 0) {
    return WebhookDeliveryStatus.Pending;
  }

  return WebhookDeliveryStatus.Delivered;
}

function hex(n: number): string {
  return (n * 2654435761 % 0xffffff).toString(16).padStart(6, '0');
}

function id(i: number): string {
  return `aaaaaaaa-0000-0000-0000-${i.toString(16).padStart(12, '0')}`;
}

interface Gen {
  i: number;
  eventType: string;
  endpoint: string;
  status: WebhookDeliveryStatus;
  signing: WebhookSigning;
  createdMinutesAgo: number;
  attemptCount: number;
  reference: string | null;
}

// Oldest first (i=0) → newest (i=COUNT-1), one every ~20 min across 24h.
const generated: Gen[] = Array.from({ length: COUNT }, (_, i) => {
  const status = statusFor(i);

  return {
    i,
    eventType: EVENT_TYPES[i % EVENT_TYPES.length],
    endpoint: ENDPOINTS[(i * 3) % ENDPOINTS.length],
    status,
    signing: SIGNING[i % SIGNING.length],
    createdMinutesAgo: (COUNT - 1 - i) * 20,
    attemptCount:
      status === WebhookDeliveryStatus.Exhausted ? 5 : status === WebhookDeliveryStatus.Pending ? (i % 3) + 1 : (i % 2) + 1,
    reference: i % 5 === 0 ? null : `sub_${1000 + i}`,
  };
});

export const demoWebhooks: WebhookDeliveryListItem[] = [...generated]
  .sort((a, b) => a.createdMinutesAgo - b.createdMinutesAgo) // newest first for the list
  .map((g) => ({
    id: id(g.i),
    eventType: g.eventType,
    eventId: `evt_${hex(g.i)}`,
    url: g.endpoint,
    groupName: g.endpoint,
    reference: g.reference,
    status: g.status,
    signingMode: g.signing,
    attemptCount: g.attemptCount,
    nextAttemptAt: g.status === WebhookDeliveryStatus.Pending ? ahead((g.i % 6) + 1) : null,
    createdAt: ago(g.createdMinutesAgo),
  }));

export const demoWebhookSummary: WebhookDeliverySummary = {
  total: generated.length,
  pending: generated.filter((g) => g.status === WebhookDeliveryStatus.Pending).length,
  delivered: generated.filter((g) => g.status === WebhookDeliveryStatus.Delivered).length,
  exhausted: generated.filter((g) => g.status === WebhookDeliveryStatus.Exhausted).length,
};

// Build a plausible attempt timeline for a delivery from its status.
function attemptsFor(g: Gen): WebhookDeliveryDetail['attempts'] {
  const base = g.createdMinutesAgo;
  if (g.status === WebhookDeliveryStatus.Delivered) {
    // Sometimes a single success; sometimes a transient failure then success.
    if (g.i % 3 === 0) {
      return [
        { callId: `bbbbbbbb-0000-0000-0000-${(g.i * 10 + 1).toString(16).padStart(12, '0')}`, timestamp: ago(base), durationMs: 420 + (g.i % 5) * 33, outcome: AdapterCallOutcome.Failed, statusCode: 503, exceptionType: null },
        { callId: `bbbbbbbb-0000-0000-0000-${(g.i * 10 + 2).toString(16).padStart(12, '0')}`, timestamp: ago(base - 1), durationMs: 120 + (g.i % 7) * 11, outcome: AdapterCallOutcome.Success, statusCode: 200, exceptionType: null },
      ];
    }

    return [
      { callId: `bbbbbbbb-0000-0000-0000-${(g.i * 10 + 1).toString(16).padStart(12, '0')}`, timestamp: ago(base), durationMs: 90 + (g.i % 9) * 14, outcome: AdapterCallOutcome.Success, statusCode: 200, exceptionType: null },
    ];
  }

  if (g.status === WebhookDeliveryStatus.Exhausted) {
    return Array.from({ length: 5 }, (_, k) => ({
      callId: `bbbbbbbb-0000-0000-0000-${(g.i * 10 + k).toString(16).padStart(12, '0')}`,
      timestamp: ago(base - k * 2),
      durationMs: 190 + k * 12,
      outcome: AdapterCallOutcome.Failed,
      statusCode: k === 1 ? null : 500 + (k % 3),
      exceptionType: k === 1 ? 'System.Threading.Tasks.TaskCanceledException' : null,
    }));
  }

  // Pending: the attempts made so far, all failed, awaiting the next.
  return Array.from({ length: g.attemptCount }, (_, k) => ({
    callId: `bbbbbbbb-0000-0000-0000-${(g.i * 10 + k).toString(16).padStart(12, '0')}`,
    timestamp: ago(base - k * 3),
    durationMs: 300 + k * 40,
    outcome: AdapterCallOutcome.Failed,
    statusCode: 503,
    exceptionType: null,
  }));
}

const payloads: Record<string, string> = {
  'order.completed': '{"id":"order_8842","total":42.5,"currency":"usd","items":3}',
  'order.shipped': '{"id":"order_8842","carrier":"ups","tracking":"1Z…"}',
  'invoice.finalized': '{"id":"in_2087","total":11800,"currency":"eur"}',
  'invoice.payment_failed': '{"id":"in_5521","amount_due":1999,"attempt":2}',
  'customer.created': '{"id":"cus_7781","email":"[redacted]"}',
  'shipment.delivered': '{"id":"ship_9930","status":"delivered"}',
};

export const demoWebhookDetails: Record<string, WebhookDeliveryDetail> = Object.fromEntries(
  generated.map((g) => {
    const signed = g.signing !== WebhookSigning.None;

    return [
      id(g.i),
      {
        id: id(g.i),
        eventType: g.eventType,
        eventId: `evt_${hex(g.i)}`,
        url: g.endpoint,
        headersJson: signed
          ? '{"Content-Type":"application/json","Authorization":"***"}'
          : '{"Content-Type":"application/json"}',
        groupName: g.endpoint,
        reference: g.reference,
        payloadJson: payloads[g.eventType] ?? '{}',
        signingMode: g.signing,
        hasSecret: signed,
        retryScheduleSeconds: g.attemptCount === 0 ? [] : [60, 600, 3600, 21600],
        successCodesJson: g.i % 4 === 0 ? '[200,202]' : null,
        status: g.status,
        attemptCount: g.attemptCount,
        nextAttemptAt: g.status === WebhookDeliveryStatus.Pending ? ahead((g.i % 6) + 1) : null,
        createdAt: ago(g.createdMinutesAgo),
        expireAt: ahead(60 * 24 * 30),
        attempts: attemptsFor(g),
      } satisfies WebhookDeliveryDetail,
    ];
  }),
);
