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
import { decodeUrlSafeId } from '@/lib/urlSafeId';
import { IDS } from '@/demo/data';
import { ApplicationInstanceEventType } from '@/types/applications';
import { MetricDeviationDirection, MetricTileStatusRule, type MetricBoardTileResult } from '@/types/metricBoard';
import { SloComparison, SloState, type SloObjective } from '@/types/slo';
import { demoMetricBoard } from './metricBoard';
import { demoSlos } from './slo';
import type {
  ApplicationDeployModel,
  DeployVerdictCheck,
  DeployVerdictCheckState,
  DeployVerdictModel,
  DeployVerdictValue,
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

// ============================================================
// Detected deploys (GET /applications/deploys, GET /applications/{id}/deploys), newest first.
// orders-api is mid rolling-deploy (2.3.0 and 2.3.1 instances live), so its newest deploy is recent.
// ============================================================

export const demoDeploys: ApplicationDeployModel[] = [
  { id: 'dep-oa-231', application: 'orders-api', version: '2.3.1', environment: 'production', firstSeenAt: ago(60 * 20), instanceId: ORDERS_API_2, instanceName: 'orders-api-4b2d' },
  { id: 'dep-cw-231', application: 'checkout-worker', version: '2.3.1', environment: 'production', firstSeenAt: ago(60 * 26), instanceId: IDS.server1, instanceName: 'warp-worker-01' },
  { id: 'dep-bs-220s', application: 'billing-service', version: '2.2.0', environment: 'staging', firstSeenAt: ago(60 * 30), instanceId: BILLING_PROC, instanceName: 'billing-cron-2' },
  { id: 'dep-oa-230', application: 'orders-api', version: '2.3.0', environment: 'production', firstSeenAt: ago(60 * 24 * 3), instanceId: ORDERS_API_1, instanceName: 'orders-api-7c9f' },
  { id: 'dep-cw-230', application: 'checkout-worker', version: '2.3.0', environment: 'production', firstSeenAt: ago(60 * 24 * 3 + 40), instanceId: IDS.server2, instanceName: 'warp-worker-02' },
  { id: 'dep-bs-220', application: 'billing-service', version: '2.2.0', environment: 'production', firstSeenAt: ago(60 * 24 * 5), instanceId: BILLING_SERVER, instanceName: 'billing-01' },
  { id: 'dep-oa-220', application: 'orders-api', version: '2.2.0', environment: 'production', firstSeenAt: ago(60 * 24 * 9), instanceId: ORDERS_API_1, instanceName: 'orders-api-7c9f' },
];

/** The demo router's filter over demoDeploys: by application name and a [from, to) range, either side optional. */
export function filterDemoDeploys(application?: string, from?: string, to?: string): ApplicationDeployModel[] {
  const lower = from ? Date.parse(from) : -Infinity;
  const upper = to ? Date.parse(to) : Infinity;

  return demoDeploys
    .filter((x) => !application || x.application === application)
    .filter((x) => Date.parse(x.firstSeenAt) >= lower && Date.parse(x.firstSeenAt) < upper);
}

// ============================================================
// Post-deploy verdicts (GET /applications/verdict), built the way the server builds them: the demo board's tiles with a
// status rule evaluated over the deploy's window, plus the enabled demo SLOs scoped to the application (or to none), so
// every check names a tile or objective that exists on the Board and SLOs pages. Each demo application has SLOs of its
// own, so their verdicts differ. A superseded deploy is judged over its own lifetime and without SLOs (their current
// state describes the newer release).
// ============================================================

const VERDICT_MIN_WINDOW_MS = 5 * 60_000;
const VERDICT_MAX_RANGE_MS = 90 * 24 * 3_600_000;

const STATE_TOKEN: Record<SloState, DeployVerdictCheckState> = {
  [SloState.Healthy]: 'healthy',
  [SloState.Warning]: 'warning',
  [SloState.Breaching]: 'breaching',
  [SloState.Acknowledged]: 'acknowledged',
  [SloState.NoData]: 'noData',
};

const phrase = (state: DeployVerdictCheckState) => (state === 'noData' ? 'no data' : state);
const capitalized = (text: string) => text.charAt(0).toUpperCase() + text.slice(1);
const num = (value: number | null) => (value === null || !Number.isFinite(value) ? 'n/a' : `${Number(value.toPrecision(4))}`);
const appliesTo = (scope: string | null, application: string) => scope === null || scope === application;

function tileCheck(result: MetricBoardTileResult): DeployVerdictCheck | null {
  if (result.status === null) {
    return null;
  }

  const { tile } = result;
  const state = STATE_TOKEN[result.status];
  const unacknowledged = STATE_TOKEN[result.unacknowledgedStatus ?? result.status];
  const deviation = tile.statusRule === MetricTileStatusRule.Deviation;
  const baseline = deviation ? (tile.baselineDays === 1 ? result.dayAgo : result.weekAgo) : null;
  const direction = tile.deviationDirection === MetricDeviationDirection.Drop
    ? 'drop'
    : tile.deviationDirection === MetricDeviationDirection.Rise ? 'rise' : 'change';
  const detail = deviation
    ? `${num(result.current)} vs ${tile.baselineDays ?? 7}-day baseline ${num(baseline)} (max ${direction} ${num(tile.maxDeviation)})`
    : `${num(result.current)} vs ${tile.comparison === SloComparison.AtLeast ? 'at least' : 'at most'} ${num(tile.threshold)}`;

  return {
    source: 'tile',
    id: tile.id,
    name: tile.name,
    application: tile.application,
    state,
    unacknowledgedState: state === 'acknowledged' ? unacknowledged : null,
    acknowledged: state === 'acknowledged',
    value: result.current,
    baseline,
    target: deviation ? tile.maxDeviation : tile.threshold,
    budgetRemaining: null,
    reason: state === 'acknowledged'
      ? `Acknowledged noise (would be ${phrase(unacknowledged)}): ${detail}.`
      : `${capitalized(phrase(state))}: ${detail}.`,
  };
}

function sloCheck(slo: SloObjective, deployedAt: number): DeployVerdictCheck {
  const stale = slo.lastEvaluatedAt !== null && Date.parse(slo.lastEvaluatedAt) < deployedAt;
  const state: DeployVerdictCheckState = !slo.evaluated || stale ? 'noData' : STATE_TOKEN[slo.state];
  const hasValue = slo.evaluated && state !== 'noData';
  const reason = !slo.evaluated
    ? 'Not evaluated yet.'
    : stale
      ? `Last evaluated at ${slo.lastEvaluatedAt}, before the deploy.`
      : `${capitalized(phrase(state))}: observed ${num(slo.attainment)} vs target ${num(slo.targetValue)}, budget remaining ${num(slo.budgetRemaining)}.`;

  return {
    source: 'slo',
    id: slo.id,
    name: slo.name,
    application: slo.application,
    state,
    unacknowledgedState: null,
    acknowledged: state === 'acknowledged',
    value: hasValue ? slo.attainment : null,
    baseline: null,
    target: slo.targetValue,
    budgetRemaining: hasValue ? slo.budgetRemaining : null,
    reason,
  };
}

/**
 * The server's coverage gate: the share of checks with data reaches `minCoverage`. Compared as a share with a 1e-9
 * epsilon, as the server does, so 14 of 25 meets 0.56 although `0.56 * 25` is 14.000000000000002 in floating point.
 */
export function coverageMet(withData: number, count: number, minCoverage: number): boolean {
  return count > 0 && withData / count >= minCoverage - 1e-9;
}

/** The server's aggregation: pending until settled, any breaching fails, thin coverage is noData, then warn, then pass. */
function aggregate(states: DeployVerdictCheckState[], settled: boolean, minCoverage: number): DeployVerdictValue {
  const withData = states.filter((x) => x !== 'noData').length;
  if (!settled) {
    return 'pending';
  }
  if (states.includes('breaching')) {
    return 'fail';
  }
  if (minCoverage > 0 && !coverageMet(withData, states.length, minCoverage)) {
    return 'noData';
  }
  if (states.includes('warning')) {
    return 'warn';
  }

  return states.some((x) => x === 'healthy' || x === 'acknowledged') ? 'pass' : 'noData';
}

function summary(verdict: DeployVerdictValue, states: DeployVerdictCheckState[], settlesAt: string): string {
  if (verdict === 'pending') {
    return `pending: settles at ${settlesAt}`;
  }

  const coverage = `${states.filter((x) => x !== 'noData').length} of ${states.length} checks had data`;
  if (states.length === 0) {
    return `${verdict} (${coverage}): no board tile with a rule and no SLO covers this application`;
  }

  const order: DeployVerdictCheckState[] = ['breaching', 'warning', 'acknowledged', 'healthy', 'noData'];
  const parts = order
    .map((state) => ({ state, count: states.filter((x) => x === state).length }))
    .filter((x) => x.count > 0)
    .map((x) => `${x.count} ${phrase(x.state)}`);

  return `${verdict} (${coverage}): ${parts.join(', ')}`;
}

/** The demo router's verdict: the deploy picked like the server does (newest match), checks from the board and SLO fixtures. */
export function demoDeployVerdict(application?: string, version?: string, environment?: string, minCoverage = 0): DeployVerdictModel | null {
  const history = demoDeploys
    .filter((x) => x.application === application)
    .sort((a, b) => Date.parse(b.firstSeenAt) - Date.parse(a.firstSeenAt));
  const deploy = history
    .filter((x) => !version || x.version === version)
    .find((x) => !environment || (x.environment ?? '') === environment);
  if (!deploy) {
    return null;
  }

  const sameEnvironment = history.filter((x) => (x.environment ?? '') === (deploy.environment ?? ''));
  const index = sameEnvironment.indexOf(deploy);
  const next = index > 0 ? sameEnvironment[index - 1] : null;
  const deployedAt = Date.parse(deploy.firstSeenAt);
  const to = Math.max(next ? Date.parse(next.firstSeenAt) : FROZEN_NOW, deployedAt);
  const from = Math.max(deployedAt, to - VERDICT_MAX_RANGE_MS);
  const settlesAt = next && to < deployedAt + VERDICT_MIN_WINDOW_MS ? to : deployedAt + VERDICT_MIN_WINDOW_MS;
  const settled = next !== null || to - deployedAt >= VERDICT_MIN_WINDOW_MS;

  const name = deploy.application;
  const tiles = to > from
    ? demoMetricBoard(new Date(from).toISOString(), new Date(to).toISOString()).tiles
      .filter((x) => appliesTo(x.tile.application, name))
      .map(tileCheck)
      .filter((x): x is DeployVerdictCheck => x !== null)
    : [];
  const slos = next
    ? []
    : demoSlos().items
      .filter((x) => x.enabled && appliesTo(x.application, name))
      .sort((a, b) => a.name.localeCompare(b.name, 'en'))
      .map((x) => sloCheck(x, deployedAt));
  const checks = [...tiles, ...slos];
  const states = checks.map((x) => x.state);
  const verdict = aggregate(states, settled, minCoverage);
  const settlesAtIso = new Date(settlesAt).toISOString();

  return {
    verdict,
    summary: summary(verdict, states, settlesAtIso),
    checksWithData: states.filter((x) => x !== 'noData').length,
    checksTotal: states.length,
    deploy: {
      application: deploy.application,
      version: deploy.version,
      environment: deploy.environment,
      firstSeenAt: deploy.firstSeenAt,
      instanceName: deploy.instanceName,
      previousVersion: sameEnvironment[index + 1]?.version ?? null,
      supersededBy: next?.version ?? null,
    },
    window: {
      from: new Date(from).toISOString(),
      to: new Date(to).toISOString(),
      minWindowSeconds: VERDICT_MIN_WINDOW_MS / 1000,
      settlesAt: settlesAtIso,
      settled,
    },
    checks,
  };
}

// URL-safe base64 of the application name → the name, so the demo router maps the {id} path segment
// back to a fixture key. One codec shared with the other name-addressed routes (see lib/urlSafeId).
export function decodeAppId(id: string): string {
  return decodeUrlSafeId(id);
}
