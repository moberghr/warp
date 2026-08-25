// Deterministic demo-mode fixtures for the inbound Endpoint Observability pages. Mirrors the shape of
// `data/adapters.ts` (endpoints are the inbound counterpart of outbound adapters, §8.21) and is anchored
// to the pinned demo clock so relative-time labels render identically across screenshot runs. Typed
// against the real DTOs so a shape drift breaks the build rather than the demo.
//
// These pages had no demo data at all before: the router had no /endpoints routes, so the list page threw
// (the unhandled-route fallback returns `{}` and the history chart maps over it) and the docs images had
// to be captured by hand — which is why they alone kept showing the pre-grouping nav.
import { FROZEN_NOW } from '@/lib/demoMode';
import { AdapterCallOutcome } from '@/types/adapters';
import type {
  EndpointListItem,
  EndpointDetail,
  EndpointCallDetail,
  EndpointHistoryPoint,
} from '@/types/endpoints';

function ago(minutes: number): string {
  return new Date(FROZEN_NOW - minutes * 60_000).toISOString();
}

// Start of the UTC hour `hours` before the pinned demo clock — the x value of a history point.
function hourAgo(hours: number): string {
  const d = new Date(FROZEN_NOW - hours * 3_600_000);
  d.setUTCMinutes(0, 0, 0);

  return d.toISOString();
}

// Deterministic 24-hour series (oldest first) — no randomness, so the chart renders identically on every
// screenshot run. Values wobble by index so the bars and the latency line have a realistic shape.
function demoHistory(baseCalls: number, errorFraction: number, baseLatencyMs: number): EndpointHistoryPoint[] {
  const hours = 24;

  return Array.from({ length: hours }, (_, i) => {
    const swell = 1 + Math.round((hours / 2 - Math.abs(hours / 2 - i)) * 0.6);
    const calls = baseCalls + swell + ((i * 7) % 13);
    const errors = Math.round(calls * errorFraction * (0.3 + ((i % 4) * 0.4)));

    return {
      hour: hourAgo(hours - 1 - i),
      calls,
      errors,
      errorRate: calls === 0 ? 0 : errors / calls,
      avgDurationMs: baseLatencyMs + ((i * 13) % 55) + (i % 5) * 6,
    };
  });
}

function totals(history: EndpointHistoryPoint[]) {
  const calls = history.reduce((sum, x) => sum + x.calls, 0);
  const errors = history.reduce((sum, x) => sum + x.errors, 0);
  const durSum = history.reduce((sum, x) => sum + x.avgDurationMs * x.calls, 0);

  return { calls, errors, errorRate: calls === 0 ? 0 : errors / calls, avgDurationMs: durSum / calls };
}

// The id is the URL-safe base64 of "{METHOD} {template}" (mirrors EndpointRouteId on the server).
// UE9TVCAvb3JkZXJz === base64('POST /orders') — the endpoint jobDetailCompleted.origin links back to.
const ORDERS_CREATE = 'UE9TVCAvb3JkZXJz';
const ORDERS_GET = 'R0VUIC9vcmRlcnMve2lkfQ';
const REPORTS_DOWNLOAD = 'R0VUIC9yZXBvcnRzL3tpZH0vZG93bmxvYWQ';
const WEBHOOKS_RECEIVE = 'UE9TVCAvd2ViaG9va3MvcmVjZWl2ZQ';

const histories: Record<string, EndpointHistoryPoint[]> = {
  [ORDERS_CREATE]: demoHistory(180, 0.012, 42),
  [ORDERS_GET]: demoHistory(420, 0.002, 11),
  [REPORTS_DOWNLOAD]: demoHistory(24, 0.06, 890),
  [WEBHOOKS_RECEIVE]: demoHistory(95, 0.031, 27),
};

function listItem(id: string, method: string, routeTemplate: string): EndpointListItem {
  const t = totals(histories[id]);

  return {
    id,
    method,
    routeTemplate,
    route: `${method} ${routeTemplate}`,
    totalCalls: t.calls,
    errorCount: t.errors,
    errorRate: t.errorRate,
    avgDurationMs: t.avgDurationMs,
  };
}

export const demoEndpoints: EndpointListItem[] = [
  listItem(ORDERS_GET, 'GET', '/orders/{id}'),
  listItem(ORDERS_CREATE, 'POST', '/orders'),
  listItem(WEBHOOKS_RECEIVE, 'POST', '/webhooks/receive'),
  listItem(REPORTS_DOWNLOAD, 'GET', '/reports/{id}/download'),
];

// Keep in sync with `jobDetailCompleted.origin.callId` in data.ts, or the Origin card's link 404s.
const ORIGIN_CALL_ID = '00000000-0000-0000-0000-0000000000aa';

type DetailExtras = Pick<EndpointDetail, 'p90DurationMs' | 'p95DurationMs' | 'p99DurationMs' | 'groups' | 'recentCalls'>;

function detail(id: string, method: string, routeTemplate: string, extra: DetailExtras): EndpointDetail {
  const history = histories[id];
  const t = totals(history);

  return {
    id,
    method,
    routeTemplate,
    route: `${method} ${routeTemplate}`,
    groupLabel: 'Caller',
    totalCalls: t.calls,
    errorCount: t.errors,
    errorRate: t.errorRate,
    avgDurationMs: t.avgDurationMs,
    history,
    ...extra,
  };
}

const MAC_UA = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)';
const WIN_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)';
const IOS_UA = 'Acme-iOS/4.2.1';

export const demoEndpointDetails: Record<string, EndpointDetail> = {
  [ORDERS_CREATE]: detail(ORDERS_CREATE, 'POST', '/orders', {
    p90DurationMs: 100,
    p95DurationMs: 250,
    p99DurationMs: 1000,
    groups: [
      { group: 'storefront-web', calls: 3120, errors: 22, errorRate: 0.007, avgDurationMs: 38, lastFailureAt: ago(74) },
      { group: 'mobile-ios', calls: 1044, errors: 31, errorRate: 0.03, avgDurationMs: 61, lastFailureAt: ago(12) },
      { group: 'partner-api', calls: 388, errors: 2, errorRate: 0.005, avgDurationMs: 44, lastFailureAt: ago(610) },
    ],
    recentCalls: [
      { id: ORIGIN_CALL_ID, timestamp: ago(10), durationMs: 47, outcome: AdapterCallOutcome.Success, statusCode: 202, remoteIp: '203.0.113.24', userAgent: MAC_UA, user: 'alice', groupName: 'storefront-web' },
      { id: '00000000-0000-0000-0000-0000000000ab', timestamp: ago(12), durationMs: 1180, outcome: AdapterCallOutcome.Failed, statusCode: 500, remoteIp: '198.51.100.7', userAgent: IOS_UA, user: 'bob', groupName: 'mobile-ios' },
      { id: '00000000-0000-0000-0000-0000000000ac', timestamp: ago(18), durationMs: 39, outcome: AdapterCallOutcome.Success, statusCode: 202, remoteIp: '203.0.113.51', userAgent: WIN_UA, user: 'carol', groupName: 'storefront-web' },
      { id: '00000000-0000-0000-0000-0000000000ad', timestamp: ago(26), durationMs: 52, outcome: AdapterCallOutcome.Success, statusCode: 202, remoteIp: '203.0.113.51', userAgent: 'Acme-Partner-SDK/2.0', user: null, groupName: 'partner-api' },
      { id: '00000000-0000-0000-0000-0000000000ae', timestamp: ago(41), durationMs: 44, outcome: AdapterCallOutcome.Success, statusCode: 202, remoteIp: '203.0.113.24', userAgent: MAC_UA, user: 'alice', groupName: 'storefront-web' },
    ],
  }),
  [ORDERS_GET]: detail(ORDERS_GET, 'GET', '/orders/{id}', {
    p90DurationMs: 25,
    p95DurationMs: 50,
    p99DurationMs: 100,
    groups: [
      { group: 'storefront-web', calls: 8840, errors: 9, errorRate: 0.001, avgDurationMs: 9, lastFailureAt: ago(320) },
      { group: 'mobile-ios', calls: 2210, errors: 4, errorRate: 0.002, avgDurationMs: 14, lastFailureAt: ago(880) },
    ],
    recentCalls: [
      { id: '00000000-0000-0000-0000-0000000000ba', timestamp: ago(2), durationMs: 8, outcome: AdapterCallOutcome.Success, statusCode: 200, remoteIp: '203.0.113.24', userAgent: MAC_UA, user: 'alice', groupName: 'storefront-web' },
      { id: '00000000-0000-0000-0000-0000000000bb', timestamp: ago(4), durationMs: 11, outcome: AdapterCallOutcome.Success, statusCode: 200, remoteIp: '198.51.100.7', userAgent: IOS_UA, user: 'bob', groupName: 'mobile-ios' },
      { id: '00000000-0000-0000-0000-0000000000bc', timestamp: ago(9), durationMs: 7, outcome: AdapterCallOutcome.Success, statusCode: 404, remoteIp: '203.0.113.51', userAgent: WIN_UA, user: 'carol', groupName: 'storefront-web' },
    ],
  }),
  [WEBHOOKS_RECEIVE]: detail(WEBHOOKS_RECEIVE, 'POST', '/webhooks/receive', {
    p90DurationMs: 50,
    p95DurationMs: 100,
    p99DurationMs: 500,
    groups: [
      { group: 'stripe', calls: 1440, errors: 38, errorRate: 0.026, avgDurationMs: 24, lastFailureAt: ago(31) },
      { group: 'shippo', calls: 902, errors: 41, errorRate: 0.045, avgDurationMs: 33, lastFailureAt: ago(6) },
    ],
    recentCalls: [
      { id: '00000000-0000-0000-0000-0000000000ca', timestamp: ago(6), durationMs: 91, outcome: AdapterCallOutcome.Failed, statusCode: 500, remoteIp: '192.0.2.44', userAgent: 'Shippo-Webhook/1.0', user: null, groupName: 'shippo' },
      { id: '00000000-0000-0000-0000-0000000000cb', timestamp: ago(14), durationMs: 22, outcome: AdapterCallOutcome.Success, statusCode: 204, remoteIp: '192.0.2.19', userAgent: 'Stripe/1.0', user: null, groupName: 'stripe' },
    ],
  }),
  [REPORTS_DOWNLOAD]: detail(REPORTS_DOWNLOAD, 'GET', '/reports/{id}/download', {
    p90DurationMs: 1000,
    p95DurationMs: 2500,
    p99DurationMs: 5000,
    groups: [
      { group: 'storefront-web', calls: 512, errors: 30, errorRate: 0.059, avgDurationMs: 910, lastFailureAt: ago(48) },
    ],
    recentCalls: [
      { id: '00000000-0000-0000-0000-0000000000da', timestamp: ago(48), durationMs: 5400, outcome: AdapterCallOutcome.Failed, statusCode: 504, remoteIp: '203.0.113.24', userAgent: MAC_UA, user: 'alice', groupName: 'storefront-web' },
      { id: '00000000-0000-0000-0000-0000000000db', timestamp: ago(90), durationMs: 870, outcome: AdapterCallOutcome.Success, statusCode: 200, remoteIp: '203.0.113.24', userAgent: MAC_UA, user: 'alice', groupName: 'storefront-web' },
    ],
  }),
};

// Captured headers show the redaction the real capture applies (§1.2): Authorization and Cookie come back
// as [REDACTED] from the denylist, never as the live value.
export const demoEndpointCalls: Record<string, EndpointCallDetail> = {
  [ORIGIN_CALL_ID]: {
    id: ORIGIN_CALL_ID,
    method: 'POST',
    routeTemplate: '/orders',
    operation: 'POST /orders',
    groupName: 'storefront-web',
    timestamp: ago(10),
    durationMs: 47,
    outcome: AdapterCallOutcome.Success,
    statusCode: 202,
    remoteIp: '203.0.113.24',
    userAgent: MAC_UA,
    user: 'alice',
    exceptionType: null,
    exceptionMessage: null,
    requestHeaders: JSON.stringify(
      { 'content-type': 'application/json', authorization: '[REDACTED]', cookie: '[REDACTED]', 'x-request-id': 'req-9f2c41' },
      null,
      2,
    ),
    responseHeaders: JSON.stringify({ 'content-type': 'application/json', location: '/orders/2847' }, null, 2),
    requestBody: JSON.stringify(
      {
        items: [
          { sku: 'WIDGET-001', qty: 3, price: 29.99 },
          { sku: 'GADGET-002', qty: 1, price: 149.99 },
        ],
        customerId: 'cust-42',
        shippingMethod: 'express',
      },
      null,
      2,
    ),
    responseBody: JSON.stringify({ orderId: 2847, status: 'accepted' }, null, 2),
    machineName: 'orders-api-7c9f',
    // The demo trace id, so Related jobs and the trace link resolve to the same trace the job pages use.
    traceId: '4bf92f35-77b3-4da6-a3ce-929d0e0e4736',
    tagsJson: JSON.stringify({ channel: 'web', tenant: 'acme', 'feature.flag': 'express-checkout' }),
    relatedJobs: [
      { id: 'b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e', type: 'Acme.Orders.ProcessOrderRequest', state: 4, queue: 'default' },
      { id: 'a7b8c9d0-e1f2-4345-abcd-777777777777', type: 'Acme.Billing.CalculateTaxRequest', state: 4, queue: 'default' },
    ],
  },
  '00000000-0000-0000-0000-0000000000ab': {
    id: '00000000-0000-0000-0000-0000000000ab',
    method: 'POST',
    routeTemplate: '/orders',
    operation: 'POST /orders',
    groupName: 'mobile-ios',
    timestamp: ago(12),
    durationMs: 1180,
    outcome: AdapterCallOutcome.Failed,
    statusCode: 500,
    remoteIp: '198.51.100.7',
    userAgent: IOS_UA,
    user: 'bob',
    exceptionType: 'System.InvalidOperationException',
    exceptionMessage: 'Inventory reservation failed for SKU WIDGET-001',
    requestHeaders: JSON.stringify({ 'content-type': 'application/json', authorization: '[REDACTED]' }, null, 2),
    responseHeaders: JSON.stringify({ 'content-type': 'application/problem+json' }, null, 2),
    requestBody: JSON.stringify({ items: [{ sku: 'WIDGET-001', qty: 99 }], customerId: 'cust-77' }, null, 2),
    responseBody: JSON.stringify({ type: 'about:blank', title: 'Internal Server Error', status: 500 }, null, 2),
    machineName: 'orders-api-4b2d',
    traceId: null,
    tagsJson: JSON.stringify({ channel: 'mobile', tenant: 'acme' }),
    relatedJobs: [],
  },
};

export const DEMO_ENDPOINT_IDS = {
  ordersCreate: ORDERS_CREATE,
  originCall: ORIGIN_CALL_ID,
};
