// Deterministic demo-mode fixtures for the Applications pages (§8.19 multi-app observability — the
// renamed Servers surface). All timestamps are anchored to the pinned demo clock (FROZEN_NOW) so
// relative-time labels render identically across screenshot runs. Typed against the real DTOs so a
// shape drift breaks the build, not the demo.
//
// NOTE: the axios mock router lives in `demo/adapter.ts` (routeApplications). These fixtures are
// exported for that router to serve; production reads the real REST endpoints via `@/api`.
//
// Server instances reuse the existing dashboard server ids (IDS.server1 / IDS.server2) so an
// instance row that drills into /servers/{id} is consistent with the servers demo data.
import { FROZEN_NOW } from '@/lib/demoMode';
import { IDS } from '@/demo/data';
import { ApplicationInstanceEventType } from '@/types/applications';
import type {
  ApplicationSummaryModel,
  ApplicationDetailModel,
  ApplicationInstanceDetailModel,
  InstanceView,
  JobExecutionMetricsModel,
} from '@/types/applications';

function ago(minutes: number): string {
  return new Date(FROZEN_NOW - minutes * 60_000).toISOString();
}

// Non-server instance ids — server instances reuse IDS.server1 / IDS.server2.
const ORDERS_API_1 = 'aa000000-0000-4000-a000-0000000000a1';
const ORDERS_API_2 = 'aa000000-0000-4000-a000-0000000000a2';
const BILLING_SERVER = 'bb000000-0000-4000-a000-0000000000b1';
const BILLING_PROC = 'bb000000-0000-4000-a000-0000000000b2';

// ============================================================
// Roster (GET /applications)
// ============================================================

export const demoApplications: ApplicationSummaryModel[] = [
  {
    name: 'orders-api',
    instanceCount: 2,
    liveInstanceCount: 1,
    totalCpuUsagePercent: 3.2,
    totalMemoryWorkingSetBytes: 98_000_000,
    versions: ['2.3.0', '2.3.1'],
    environments: ['production'],
  },
  {
    name: 'checkout-worker',
    instanceCount: 2,
    liveInstanceCount: 2,
    totalCpuUsagePercent: 17.2,
    totalMemoryWorkingSetBytes: 429_000_000,
    versions: ['2.3.1'],
    environments: ['production'],
  },
  {
    name: 'billing-service',
    instanceCount: 2,
    liveInstanceCount: 2,
    totalCpuUsagePercent: 9.5,
    totalMemoryWorkingSetBytes: 274_000_000,
    versions: ['2.2.0'],
    environments: ['production', 'staging'],
  },
];

// ============================================================
// Per-application instance detail (GET /applications/{id})
// ============================================================

const ordersApiInstances: InstanceView[] = [
  {
    id: ORDERS_API_1,
    application: 'orders-api',
    machineName: 'orders-api-7c9f',
    startedAt: ago(60 * 6),
    lastHeartbeatAt: ago(0.1),
    cpuUsagePercent: 3.2,
    memoryWorkingSetBytes: 98_000_000,
    isServer: false,
    version: '2.3.1',
    environment: 'production',
    isLive: true,
  },
  {
    id: ORDERS_API_2,
    application: 'orders-api',
    machineName: 'orders-api-4b2d',
    startedAt: ago(60 * 20),
    lastHeartbeatAt: ago(9),
    cpuUsagePercent: null,
    memoryWorkingSetBytes: null,
    isServer: false,
    version: '2.3.0',
    environment: 'production',
    isLive: false,
  },
];

const checkoutWorkerInstances: InstanceView[] = [
  {
    id: IDS.server1,
    application: 'checkout-worker',
    machineName: 'warp-prod-server-1',
    startedAt: ago(120),
    lastHeartbeatAt: ago(0.05),
    cpuUsagePercent: 12.4,
    memoryWorkingSetBytes: 287_000_000,
    isServer: true,
    version: '2.3.1',
    environment: 'production',
    isLive: true,
  },
  {
    id: IDS.server2,
    application: 'checkout-worker',
    machineName: 'warp-prod-server-2',
    startedAt: ago(60),
    lastHeartbeatAt: ago(0.08),
    cpuUsagePercent: 4.8,
    memoryWorkingSetBytes: 142_000_000,
    isServer: true,
    version: '2.3.1',
    environment: 'production',
    isLive: true,
  },
];

const billingServiceInstances: InstanceView[] = [
  {
    id: BILLING_SERVER,
    application: 'billing-service',
    machineName: 'warp-billing-1',
    startedAt: ago(60 * 4),
    lastHeartbeatAt: ago(0.06),
    cpuUsagePercent: 8.1,
    memoryWorkingSetBytes: 210_000_000,
    isServer: true,
    version: '2.2.0',
    environment: 'production',
    isLive: true,
  },
  {
    id: BILLING_PROC,
    application: 'billing-service',
    machineName: 'billing-cron-2',
    startedAt: ago(60 * 3),
    lastHeartbeatAt: ago(0.4),
    cpuUsagePercent: 1.4,
    memoryWorkingSetBytes: 64_000_000,
    isServer: false,
    version: '2.2.0',
    environment: 'staging',
    isLive: true,
  },
];

export const demoApplicationDetails: Record<string, ApplicationDetailModel> = {
  'orders-api': {
    name: 'orders-api',
    instances: ordersApiInstances,
    versions: ['2.3.0', '2.3.1'],
    environments: ['production'],
  },
  'checkout-worker': {
    name: 'checkout-worker',
    instances: checkoutWorkerInstances,
    versions: ['2.3.1'],
    environments: ['production'],
  },
  'billing-service': {
    name: 'billing-service',
    instances: billingServiceInstances,
    versions: ['2.2.0'],
    environments: ['production', 'staging'],
  },
};

// ============================================================
// Single-instance detail (GET /applications/{id}/instances/{instanceId})
// Only the non-server instances need this — server rows drill into /servers/{id} instead.
// ============================================================

export const demoInstanceDetails: Record<string, ApplicationInstanceDetailModel> = {
  [ORDERS_API_1]: {
    instance: ordersApiInstances[0],
    recentEvents: [
      { id: 'evt-oa1-2', instanceId: ORDERS_API_1, applicationName: 'orders-api', timestamp: ago(60 * 6), eventType: ApplicationInstanceEventType.Registered, message: 'Instance registered (orders-api-7c9f)' },
    ],
  },
  [ORDERS_API_2]: {
    instance: ordersApiInstances[1],
    recentEvents: [
      { id: 'evt-oa2-3', instanceId: ORDERS_API_2, applicationName: 'orders-api', timestamp: ago(9), eventType: ApplicationInstanceEventType.HeartbeatLost, message: 'No heartbeat within the liveness window' },
      { id: 'evt-oa2-2', instanceId: ORDERS_API_2, applicationName: 'orders-api', timestamp: ago(60 * 12), eventType: ApplicationInstanceEventType.Recovered, message: 'Heartbeat resumed' },
      { id: 'evt-oa2-1', instanceId: ORDERS_API_2, applicationName: 'orders-api', timestamp: ago(60 * 20), eventType: ApplicationInstanceEventType.Registered, message: 'Instance registered (orders-api-4b2d)' },
    ],
  },
  [BILLING_PROC]: {
    instance: billingServiceInstances[1],
    recentEvents: [
      { id: 'evt-bp-1', instanceId: BILLING_PROC, applicationName: 'billing-service', timestamp: ago(60 * 3), eventType: ApplicationInstanceEventType.Registered, message: 'Instance registered (billing-cron-2)' },
    ],
  },
};

// ============================================================
// Per-application execution metrics (GET /applications/{id}/jobstats).
// Percentiles are 0 for a per-application slice (the app family carries no latency histogram) — the
// detail page only aggregates executedCount / errorCount into the activity tiles anyway.
// ============================================================

export const demoApplicationJobStats: Record<string, JobExecutionMetricsModel> = {
  'orders-api': { byType: [], byHandler: [] },
  'checkout-worker': {
    byType: [
      { identifier: 'Acme.Orders.ProcessOrderRequest', executedCount: 41280, errorCount: 96, errorRate: 96 / 41280, avgDurationMs: 214.6, p95DurationMs: 0, p99DurationMs: 0 },
      { identifier: 'Acme.Shipping.ShipItemRequest', executedCount: 38215, errorCount: 310, errorRate: 310 / 38215, avgDurationMs: 158.2, p95DurationMs: 0, p99DurationMs: 0 },
      { identifier: 'Acme.Billing.CalculateTaxRequest', executedCount: 12904, errorCount: 44, errorRate: 44 / 12904, avgDurationMs: 91.4, p95DurationMs: 0, p99DurationMs: 0 },
    ],
    byHandler: [
      { identifier: 'Acme.Orders.ProcessOrderHandler', executedCount: 41280, errorCount: 96, errorRate: 96 / 41280, avgDurationMs: 214.6, p95DurationMs: 0, p99DurationMs: 0 },
      { identifier: 'Acme.Shipping.ShipItemHandler', executedCount: 38215, errorCount: 310, errorRate: 310 / 38215, avgDurationMs: 158.2, p95DurationMs: 0, p99DurationMs: 0 },
    ],
  },
  'billing-service': {
    byType: [
      { identifier: 'Acme.Billing.PublishInvoiceRequest', executedCount: 8820, errorCount: 12, errorRate: 12 / 8820, avgDurationMs: 302.9, p95DurationMs: 0, p99DurationMs: 0 },
      { identifier: 'Acme.Billing.CalculateTaxRequest', executedCount: 6410, errorCount: 5, errorRate: 5 / 6410, avgDurationMs: 88.1, p95DurationMs: 0, p99DurationMs: 0 },
    ],
    byHandler: [
      { identifier: 'Acme.Billing.PublishInvoiceHandler', executedCount: 8820, errorCount: 12, errorRate: 12 / 8820, avgDurationMs: 302.9, p95DurationMs: 0, p99DurationMs: 0 },
    ],
  },
};

// ============================================================
// Global per-type / per-handler execution metrics (GET /jobs/metrics — the JobsByTypePage header).
// App-agnostic read, so percentiles ARE populated. Identifiers mirror the demo job-type pool so the
// header renders for any type reachable from the demo Jobs surfaces.
// ============================================================

export const demoJobMetrics: JobExecutionMetricsModel = {
  byType: [
    { identifier: 'Acme.Orders.ProcessOrderRequest', executedCount: 62140, errorCount: 148, errorRate: 148 / 62140, avgDurationMs: 218.4, p95DurationMs: 512.0, p99DurationMs: 1140.0 },
    { identifier: 'Acme.Shipping.ShipItemRequest', executedCount: 54890, errorCount: 402, errorRate: 402 / 54890, avgDurationMs: 161.7, p95DurationMs: 388.0, p99DurationMs: 902.0 },
    { identifier: 'Acme.Billing.PublishInvoiceRequest', executedCount: 15330, errorCount: 24, errorRate: 24 / 15330, avgDurationMs: 305.2, p95DurationMs: 640.0, p99DurationMs: 1320.0 },
    { identifier: 'Acme.Notifications.SendEmailRequest', executedCount: 40218, errorCount: 1123, errorRate: 1123 / 40218, avgDurationMs: 96.8, p95DurationMs: 240.0, p99DurationMs: 610.0 },
    { identifier: 'Acme.Billing.CalculateTaxRequest', executedCount: 19240, errorCount: 61, errorRate: 61 / 19240, avgDurationMs: 89.3, p95DurationMs: 205.0, p99DurationMs: 470.0 },
  ],
  byHandler: [
    { identifier: 'Acme.Orders.ProcessOrderHandler', executedCount: 62140, errorCount: 148, errorRate: 148 / 62140, avgDurationMs: 218.4, p95DurationMs: 512.0, p99DurationMs: 1140.0 },
    { identifier: 'Acme.Shipping.ShipItemHandler', executedCount: 54890, errorCount: 402, errorRate: 402 / 54890, avgDurationMs: 161.7, p95DurationMs: 388.0, p99DurationMs: 902.0 },
    { identifier: 'Acme.Notifications.SendEmailCommand', executedCount: 40218, errorCount: 1123, errorRate: 1123 / 40218, avgDurationMs: 96.8, p95DurationMs: 240.0, p99DurationMs: 610.0 },
    { identifier: 'Acme.Billing.PublishInvoiceHandler', executedCount: 15330, errorCount: 24, errorRate: 24 / 15330, avgDurationMs: 305.2, p95DurationMs: 640.0, p99DurationMs: 1320.0 },
  ],
};

// URL-safe base64 of the application name → the name (mirror of encodeAppId / UrlSafeId.Decode) so
// the router maps the {id} path segment back to a fixture key.
export function decodeAppId(id: string): string {
  const b64 = id.replace(/-/g, '+').replace(/_/g, '/');
  const padded = b64 + '='.repeat((4 - (b64.length % 4)) % 4);

  return decodeURIComponent(
    atob(padded)
      .split('')
      .map((c) => `%${c.charCodeAt(0).toString(16).padStart(2, '0')}`)
      .join(''),
  );
}
