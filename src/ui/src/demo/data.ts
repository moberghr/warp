import { State, CancellationMode } from '@/types';
import type {
  DashboardStatistics,
  JobModel,
  JobGroupModel,
  RecurringJobModel,
  RecurringJobDetailModel,
  RecurringJobHistoryModel,
  ServerModel,
  ServerTaskSummary,
  ServerLogModel,
  WorkerDetailModel,
  WorkerJobLogModel,
  UnifiedJobDetailModel,
  JobLogModel,
  TraceJobModel,
  StatsHistoryPoint,
  TypeCountModel,
  PagedList,
  ConcurrencyLimitInfo,
  RateLimitInfo,
} from '@/types';
import type { RealtimePoint } from '@/stores/dashboard';
import type {
  BackgroundServiceListItem,
  BackgroundServiceDetail,
  BackgroundServiceLeaseDto,
  BackgroundServiceLogDto,
} from '@/types/backgroundServices';
import { ServiceScope, BackgroundServiceStatus, BackgroundServiceLogSource, LogLevel } from '@/types/backgroundServices';

// ============================================================
// Helpers
// ============================================================

const NOW = Date.now();

function ago(seconds: number): string {
  return new Date(NOW - seconds * 1000).toISOString();
}

function future(seconds: number): string {
  return new Date(NOW + seconds * 1000).toISOString();
}

/** Deterministic pseudo-random [0,1) from integer seed */
function seeded(n: number): number {
  const x = Math.sin(n * 127.1 + 311.7) * 43758.5453;
  return x - Math.floor(x);
}

/** Deterministic UUID-looking string from a numeric seed */
function uid(n: number): string {
  const h = (v: number) =>
    ((v * 2654435761 + 1013904223) >>> 0).toString(16).padStart(8, '0');
  const a = h(n);
  const b = h(n + 7919);
  const c = h(n + 104729);
  return `${a}-${b.slice(0, 4)}-4${b.slice(5, 8)}-a${c.slice(1, 4)}-${c}${a.slice(0, 4)}`;
}

export function paginate<T>(
  items: T[],
  page = 0,
  pageSize = 20,
  totalOverride?: number,
): PagedList<T> {
  const total = totalOverride ?? items.length;
  return {
    totalCount: total,
    pageCount: Math.ceil(total / pageSize),
    items: items.slice(page * pageSize, (page + 1) * pageSize),
  };
}

// ============================================================
// Stable IDs (cross-referenced across endpoints)
// ============================================================

export const IDS = {
  server1: 'c7e3a1b4-5d8f-4e2a-9b6c-1d3e5f7a9b0d',
  server2: 'd8f4b2c5-6e9a-5f3b-0c7d-2e4f6a8b0c1e',
  worker1: 'a1b2c3d4-0001-4000-a000-000000000001',
  traceId: '4bf92f35-77b3-4da6-a3ce-929d0e0e4736',
  failedJob: 'e5f6a7b8-c9d0-4e1f-2a3b-4c5d6e7f8a9b',
  completedJobWithTrace: 'b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e',
  processingJob: 'f1e2d3c4-b5a6-4978-8a9b-c0d1e2f3a4b5',
  batch1: 'a9b8c7d6-e5f4-4321-0fed-cba987654321',
  message1: '12345678-abcd-4ef0-1234-567890abcdef',
  // Trace tree nodes
  trProcessOrder: 'a1b2c3d4-e5f6-4789-abcd-111111111111',
  trShipmentBatch: 'b2c3d4e5-f6a7-4890-bcde-222222222222',
  trShipItem1: 'c3d4e5f6-a7b8-4901-cdef-333333333301',
  trShipItem2: 'c3d4e5f6-a7b8-4901-cdef-333333333302',
  trShipItem3: 'c3d4e5f6-a7b8-4901-cdef-333333333303',
  trShipItem4: 'c3d4e5f6-a7b8-4901-cdef-333333333304',
  trShipItem5: 'c3d4e5f6-a7b8-4901-cdef-333333333305',
  trPublishInvoice: 'd4e5f6a7-b8c9-4012-defa-444444444444',
  trNotification: 'e5f6a7b8-c9d0-4123-efab-555555555555',
  trSendEmail: 'f6a7b8c9-d0e1-4234-fabc-666666666601',
  trNotifyCustomer: 'f6a7b8c9-d0e1-4234-fabc-666666666602',
  trCalculateTax: 'a7b8c9d0-e1f2-4345-abcd-777777777777',
  workerGroup1: 'wg-001-default',
  workerGroup2: 'wg-002-priority',
};

// ============================================================
// Job type pool
// ============================================================

const TYPES = [
  { type: 'Acme.Orders.ProcessOrderRequest', handler: 'Acme.Orders.ProcessOrderHandler' },
  { type: 'Acme.Shipping.ShipItemRequest', handler: 'Acme.Shipping.ShipItemHandler' },
  { type: 'Acme.Billing.PublishInvoiceRequest', handler: 'Acme.Billing.PublishInvoiceHandler' },
  { type: 'Acme.Notifications.SendEmailRequest', handler: 'Acme.Notifications.SendEmailCommand' },
  { type: 'Acme.Reports.GenerateReportRequest', handler: 'Acme.Reports.GenerateReportHandler' },
  { type: 'Acme.Inventory.SyncInventoryRequest', handler: 'Acme.Inventory.SyncInventoryHandler' },
  { type: 'Acme.Payments.ProcessPaymentRequest', handler: 'Acme.Payments.ProcessPaymentHandler' },
  { type: 'Acme.Notifications.NotifyCustomerRequest', handler: 'Acme.Notifications.NotifyCustomerHandler' },
  { type: 'Acme.Products.ImportProductsRequest', handler: 'Acme.Products.ImportProductsHandler' },
  { type: 'Acme.Billing.CalculateTaxRequest', handler: 'Acme.Billing.CalculateTaxHandler' },
];

function jobType(i: number) {
  return TYPES[i % TYPES.length];
}

// ============================================================
// Factory helpers
// ============================================================

function makeJob(seed: number, state: number, timeOffset: number, scheduled = false): JobModel {
  const t = jobType(seed);
  return {
    id: uid(seed + state * 1000 + (scheduled ? 7000 : 0)),
    type: t.type,
    message: JSON.stringify({ orderId: seed + 1000, customerId: `cust-${seed % 50}` }),
    createTime: ago(timeOffset),
    scheduleTime: scheduled ? future(seed * 600 + 300) : ago(timeOffset),
    processedTime:
      state === State.Completed || state === State.Failed || state === State.Deleted
        ? ago(timeOffset - 2)
        : null,
    currentState: state as typeof State.Enqueued,
    cancellationMode: CancellationMode.None,
    handlerType: t.handler,
  };
}

function makeJobs(count: number, state: number, baseOffset: number): JobModel[] {
  return Array.from({ length: count }, (_, i) =>
    makeJob(i, state, baseOffset + i * 30),
  );
}

function makeLog(
  id: string,
  eventType: string,
  secondsAgo: number,
  message: string | null,
  durationMs: number | null,
  exception: string | null = null,
  level = 'Information',
  workerId: string | null = null,
): JobLogModel {
  return {
    id,
    eventType,
    timestamp: ago(secondsAgo),
    level,
    message: message ?? '',
    exception,
    durationMs,
    workerId,
    name: null,
    value: null,
  };
}

function makeProgress(
  id: string,
  secondsAgo: number,
  name: string,
  value: number,
  workerId: string | null = null,
): JobLogModel {
  return {
    id,
    eventType: 'Progress',
    timestamp: ago(secondsAgo),
    level: 'Information',
    message: '',
    exception: null,
    durationMs: null,
    workerId,
    name,
    value,
  };
}

// ============================================================
// Dashboard statistics (incrementing counters for realtime chart)
// ============================================================

// Every field is fixed so the demo reads as a stable point-in-time snapshot. The
// realtime chart is pre-seeded with a static 60s window and the live feed is disabled
// in demo (see startRealtimeFeed / isDemoMode), so totalSucceeded/totalFailed never need
// to advance — keeping them constant stops the chart's Current/Avg/Peak from drifting.
export function getDashboardStats(): DashboardStatistics {
  return {
    total: 15847,
    pending: 23,
    scheduled: 12,
    created: 23,
    completed: 15692,
    failed: 47,
    processing: 8,
    servers: 2,
    awaiting: 3,
    deleted: 62,
    batchesProcessing: 5,
    batchesAwaiting: 2,
    batchesDeleted: 1,
    batchesCompleted: 23,
    batchesFailed: 3,
    messagesEnqueued: 5,
    messagesProcessing: 3,
    messagesCompleted: 143,
    messagesFailed: 5,
    messages: 156,
    totalSucceeded: 15692,
    totalFailed: 47,
    totalDeleted: 62,
    totalCreated: 15847,
    batches: 34,
    databaseConnection: 'PostgreSQL',
  };
}

// ============================================================
// Stats history (24h / 7d)
// ============================================================

export function getStatsHistoryPoints(hours: number): StatsHistoryPoint[] {
  const now = new Date(NOW);
  now.setMinutes(0, 0, 0);

  return Array.from({ length: hours }, (_, i) => {
    const hourDate = new Date(now.getTime() - (hours - 1 - i) * 3600000);
    const h = hourDate.getHours();

    let base: number;
    if (h >= 9 && h <= 17) {
      base = 800 + seeded(i + 10) * 500;
    } else if (h >= 6 && h <= 8) {
      base = 200 + seeded(i + 20) * 400;
    } else if (h >= 18 && h <= 21) {
      base = 300 + seeded(i + 30) * 400;
    } else {
      base = 50 + seeded(i + 40) * 150;
    }

    return {
      hour: hourDate.toISOString(),
      succeeded: Math.round(base),
      failed: Math.round(base * (0.01 + seeded(i + 50) * 0.04)),
    };
  });
}

export function getCountersDemo() {
  return [
    { key: 'stats:succeeded', value: 15847 },
    { key: 'stats:failed', value: 47 },
    { key: 'stats:deleted', value: 62 },
    { key: 'stats:requeued', value: 18 },
  ];
}

export function getCountersHistoryDemo(hours: number) {
  const now = new Date(NOW);
  now.setMinutes(0, 0, 0);
  const points: { hour: string; key: string; value: number }[] = [];

  for (let i = hours - 1; i >= 0; i--) {
    const hourDate = new Date(now.getTime() - i * 3600000);
    const h = hourDate.getHours();
    const business = h >= 9 && h <= 17;
    const base = business ? 800 + seeded(i + 10) * 500 : 50 + seeded(i + 40) * 150;

    points.push({ hour: hourDate.toISOString(), key: 'stats:succeeded', value: Math.round(base) });
    points.push({ hour: hourDate.toISOString(), key: 'stats:failed', value: Math.round(base * (0.01 + seeded(i + 50) * 0.04)) });
    points.push({ hour: hourDate.toISOString(), key: 'stats:deleted', value: Math.round(base * 0.005) });
    points.push({ hour: hourDate.toISOString(), key: 'stats:requeued', value: Math.round(base * (0.005 + seeded(i + 60) * 0.02)) });
  }

  return points;
}

// ============================================================
// Concurrency limits
// ============================================================

export const demoConcurrencyLimits: ConcurrencyLimitInfo[] = [
  { name: 'payment-api', limit: 5, updatedAt: ago(60 * 30) },
  { name: 'report-generation', limit: 3, updatedAt: ago(60 * 60 * 6) },
  { name: 'email-throttle', limit: 10, updatedAt: ago(60 * 60 * 24 * 2) },
];

// ============================================================
// Rate limits
// ============================================================

export const demoRateLimits: RateLimitInfo[] = [
  { name: 'external-api', count: 100, windowSeconds: 60, updatedAt: ago(60 * 15) },
  { name: 'newsletter-send', count: 500, windowSeconds: 3600, updatedAt: ago(60 * 60 * 4) },
];

// ============================================================
// Realtime chart seed (60 seconds of pre-populated data)
// ============================================================

export function generateRealtimeHistory(): RealtimePoint[] {
  const nowSec = Math.floor(NOW / 1000);
  // The chart's frozen-clock window spans [now-62s, now-2s] (RealtimeChart). With the
  // demo clock pinned, no live points scroll in to fill it, so the seed must cover the
  // whole window itself. Generate 66 points ending at `now` (oldest = now-65s) so the
  // line reaches past both edges — otherwise a 60-point [now-59s, now] seed leaves a
  // ~3s gap on the left of the window.
  const points = 66;
  return Array.from({ length: points }, (_, i) => ({
    ts: nowSec - (points - 1 - i),
    succeeded: Math.round(15 + seeded(i) * 10 + Math.sin(i * 0.4) * 3),
    failed: seeded(i + 200) > 0.85 ? Math.round(1 + seeded(i + 300) * 2) : 0,
  }));
}

// ============================================================
// Job lists by state
// ============================================================

export const enqueuedJobs = makeJobs(23, State.Enqueued, 120);

export const processingJobs: JobModel[] = [
  {
    ...makeJob(0, State.Processing, 15),
    id: IDS.processingJob,
    type: 'Acme.Reports.GenerateQuarterlyReport',
    handlerType: 'Acme.Reports.GenerateQuarterlyReportHandler',
  },
  ...makeJobs(7, State.Processing, 30).map((j, i) => ({ ...j, id: uid(i + 100) })),
];


export const scheduledJobs = Array.from({ length: 12 }, (_, i) =>
  makeJob(i, State.Enqueued, 120 + i * 30, true),
);

export const completedJobs: JobModel[] = [
  {
    ...makeJob(0, State.Completed, 180),
    id: IDS.completedJobWithTrace,
    type: 'Acme.Orders.ProcessOrderRequest',
    handlerType: 'Acme.Orders.ProcessOrderHandler',
  },
  ...makeJobs(19, State.Completed, 60),
];

export const failedJobs: JobModel[] = [
  {
    ...makeJob(0, State.Failed, 300),
    id: IDS.failedJob,
    type: 'Acme.Notifications.SendEmailRequest',
    handlerType: 'Acme.Notifications.SendEmailCommand',
  },
  ...Array.from({ length: 26 }, (_, i) => ({
    ...makeJob(i + 1, State.Failed, 300 + i * 30),
    type: 'Acme.Notifications.SendEmailRequest',
    handlerType: 'Acme.Notifications.SendEmailCommand',
  })),
  ...Array.from({ length: 12 }, (_, i) => ({
    ...makeJob(i + 30, State.Failed, 400 + i * 30),
    type: 'Acme.Payments.ProcessPaymentRequest',
    handlerType: 'Acme.Payments.ProcessPaymentHandler',
  })),
  ...Array.from({ length: 8 }, (_, i) => ({
    ...makeJob(i + 50, State.Failed, 500 + i * 30),
    type: 'Acme.Inventory.SyncInventoryRequest',
    handlerType: 'Acme.Inventory.SyncInventoryHandler',
  })),
];

export const awaitingJobs = makeJobs(3, State.Awaiting, 90);

export const deletedJobs = makeJobs(20, State.Deleted, 600);

// ============================================================
// Failed job type breakdown
// ============================================================

export const failedJobTypes: TypeCountModel[] = [
  { type: 'Acme.Notifications.SendEmailRequest', count: 27 },
  { type: 'Acme.Payments.ProcessPaymentRequest', count: 12 },
  { type: 'Acme.Inventory.SyncInventoryRequest', count: 8 },
];

// ============================================================
// Messages
// ============================================================

function makeMessage(
  seed: number,
  state: number,
  totalJobs: number,
  completed: number,
  failed: number,
): JobGroupModel {
  return {
    id: uid(seed + 5000),
    kind: 2,
    currentState: state as typeof State.Enqueued,
    jobCount: totalJobs,
    createTime: ago(300 + seed * 60),
    type: TYPES[seed % TYPES.length].type,
    payload: null,
    queue: seed % 3 === 0 ? 'high-priority' : 'default',
    totalJobs,
    completedJobs: completed,
    failedJobs: failed,
    continuationOptions: null,
  };
}

const messagesAll: JobGroupModel[] = [
  { ...makeMessage(0, State.Enqueued, 4, 0, 0), id: IDS.message1 },
  makeMessage(1, State.Enqueued, 3, 0, 0),
  makeMessage(2, State.Enqueued, 6, 0, 0),
  makeMessage(3, State.Enqueued, 2, 0, 0),
  makeMessage(4, State.Enqueued, 5, 0, 0),
  makeMessage(5, State.Processing, 8, 3, 0),
  makeMessage(6, State.Processing, 6, 2, 1),
  makeMessage(7, State.Processing, 10, 5, 0),
  ...Array.from({ length: 10 }, (_, i) =>
    makeMessage(10 + i, State.Completed, 4 + (i % 5), 4 + (i % 5), 0),
  ),
  ...Array.from({ length: 5 }, (_, i) =>
    makeMessage(30 + i, State.Failed, 5, 3, 2),
  ),
];

export function getMessages(state?: string): JobGroupModel[] {
  if (!state) {
    return messagesAll;
  }
  const stateMap: Record<string, number> = {
    enqueued: State.Enqueued,
    processing: State.Processing,
    completed: State.Completed,
    failed: State.Failed,
  };
  const s = stateMap[state];
  return s != null ? messagesAll.filter((m) => m.currentState === s) : messagesAll;
}

// ============================================================
// Batches
// ============================================================

function makeBatch(
  seed: number,
  state: number,
  totalJobs: number,
  completed: number,
  failed: number,
): JobGroupModel {
  return {
    id: uid(seed + 6000),
    kind: 3,
    currentState: state as typeof State.Enqueued,
    jobCount: totalJobs,
    createTime: ago(200 + seed * 45),
    type: null,
    payload: null,
    queue: 'default',
    totalJobs,
    completedJobs: completed,
    failedJobs: failed,
    continuationOptions: seed % 4 === 0 ? 1 : null,
  };
}

const batchesAll: JobGroupModel[] = [
  { ...makeBatch(0, State.Processing, 25, 18, 1), id: IDS.batch1 },
  makeBatch(1, State.Processing, 50, 35, 2),
  makeBatch(2, State.Processing, 10, 3, 0),
  makeBatch(3, State.Processing, 100, 72, 5),
  makeBatch(4, State.Processing, 8, 6, 0),
  makeBatch(5, State.Awaiting, 15, 0, 0),
  makeBatch(6, State.Awaiting, 20, 0, 0),
  ...Array.from({ length: 10 }, (_, i) =>
    makeBatch(10 + i, State.Completed, 20 + i * 5, 20 + i * 5, 0),
  ),
  ...Array.from({ length: 3 }, (_, i) =>
    makeBatch(25 + i, State.Failed, 30, 25, 5),
  ),
  makeBatch(30, State.Deleted, 10, 8, 2),
];

export function getBatches(state?: string): JobGroupModel[] {
  if (!state) {
    return batchesAll;
  }
  const stateMap: Record<string, number> = {
    processing: State.Processing,
    awaiting: State.Awaiting,
    completed: State.Completed,
    failed: State.Failed,
    deleted: State.Deleted,
  };
  const s = stateMap[state];
  return s != null ? batchesAll.filter((b) => b.currentState === s) : batchesAll;
}

// ============================================================
// Recurring jobs
// ============================================================

export const recurringJobs: RecurringJobModel[] = [
  {
    id: 1, name: 'Daily Report', cron: '0 8 * * *',
    type: 'Acme.Reports.GenerateReportRequest',
    nextExecution: future(3600), lastExecution: ago(82800), createdAt: ago(86400 * 30),
    disabledAt: null,
  },
  {
    id: 2, name: 'Inventory Sync', cron: '*/15 * * * *',
    type: 'Acme.Inventory.SyncInventoryRequest',
    nextExecution: future(600), lastExecution: ago(300), createdAt: ago(86400 * 60),
    disabledAt: null,
  },
  {
    id: 3, name: 'Email Digest', cron: '0 18 * * 1-5',
    type: 'Acme.Notifications.SendEmailRequest',
    nextExecution: future(86400), lastExecution: ago(86400), createdAt: ago(86400 * 90),
    disabledAt: ago(3600),
  },
  {
    id: 4, name: 'Tax Calculation', cron: '0 0 1 * *',
    type: 'Acme.Billing.CalculateTaxRequest',
    nextExecution: future(86400 * 15), lastExecution: ago(86400 * 15), createdAt: ago(86400 * 180),
    disabledAt: null,
  },
  {
    id: 5, name: 'Order Cleanup', cron: '0 3 * * *',
    type: 'Acme.Orders.ProcessOrderRequest',
    nextExecution: future(28800), lastExecution: ago(57600), createdAt: ago(86400 * 7),
    disabledAt: null,
  },
];

export function getRecurringDetail(id: number): RecurringJobDetailModel {
  const rj = recurringJobs.find((r) => r.id === id) ?? recurringJobs[0];
  return {
    ...rj,
    message: JSON.stringify({ filter: 'stale', maxAge: '30d' }),
    updatedAt: ago(86400 * 2),
  };
}

export function getRecurringHistory(id: number): RecurringJobHistoryModel[] {
  const rj = recurringJobs.find((r) => r.id === id);
  return Array.from({ length: 15 }, (_, i) => ({
    jobId: i < 12 ? uid(8000 + id * 100 + i) : null,
    createdAt: ago(i * 86400 + id * 3600),
    jobExists: i < 12,
    type: rj?.type ?? null,
    currentState: i < 12 ? (i === 3 ? State.Failed : State.Completed) : null,
    skipped: i >= 12 && i < 14,
  }));
}

// ============================================================
// Servers & workers
// ============================================================

function makeWorkers(serverId: string, count: number, startSeed: number): import('@/types').WorkerModel[] {
  return Array.from({ length: count }, (_, i) => ({
    workerId:
      i === 0 && serverId === IDS.server1
        ? IDS.worker1
        : uid(startSeed + i),
    startedTime: ago(7200 + i * 60),
    lastHeartbeatTime: ago(Math.round(seeded(startSeed + i) * 15)),
    currentJobId: i < 3 ? uid(9000 + startSeed + i) : null,
    currentJobType: i < 3 ? TYPES[i % TYPES.length].type : null,
    queues: i < 5 ? 'default' : 'default,high-priority',
    pollingIntervalMs: 1000,
    workerGroupId: i < 5 ? IDS.workerGroup1 : IDS.workerGroup2,
    workerGroupPausedAt: null,
  }));
}

export const servers: ServerModel[] = [
  {
    id: IDS.server1,
    serverName: 'warp-prod-server-1',
    startedTime: ago(7200),
    lastHeartbeatTime: ago(3),
    serviceCount: 10,
    cpuUsagePercent: 12.4,
    memoryWorkingSetBytes: 287000000,
    pausedAt: null,
    workers: makeWorkers(IDS.server1, 10, 200),
  },
  {
    id: IDS.server2,
    serverName: 'warp-prod-server-2',
    startedTime: ago(3600),
    lastHeartbeatTime: ago(5),
    serviceCount: 5,
    cpuUsagePercent: 4.8,
    memoryWorkingSetBytes: 142000000,
    pausedAt: null,
    workers: makeWorkers(IDS.server2, 5, 300),
  },
];

// ============================================================
// Server tasks & logs
// ============================================================

export const serverTasks: ServerTaskSummary[] = [
  { taskName: 'HeartbeatTask', lastStatus: 'Completed', lastMessage: 'Heartbeat sent', lastRun: ago(10), lastDurationMs: 12, intervalSeconds: 15 },
  { taskName: 'CounterAggregatorTask', lastStatus: 'Completed', lastMessage: 'Aggregated 847 counters', lastRun: ago(28), lastDurationMs: 145, intervalSeconds: 60 },
  { taskName: 'MessageRoutingTask', lastStatus: 'Completed', lastMessage: 'Routed 3 messages', lastRun: ago(2), lastDurationMs: 34, intervalSeconds: 1 },
  { taskName: 'OrchestrationTask', lastStatus: 'Completed', lastMessage: 'Finalized 2 batches', lastRun: ago(5), lastDurationMs: 67, intervalSeconds: 10 },
  { taskName: 'StaleJobRecoveryTask', lastStatus: 'Completed', lastMessage: 'No stale jobs', lastRun: ago(120), lastDurationMs: 8, intervalSeconds: 300 },
  { taskName: 'ExpirationCleanupTask', lastStatus: 'Completed', lastMessage: 'Deleted 156 expired jobs', lastRun: ago(60), lastDurationMs: 892, intervalSeconds: 300 },
  { taskName: 'RecurringJobSchedulerTask', lastStatus: 'Completed', lastMessage: 'Scheduled 1 job', lastRun: ago(45), lastDurationMs: 23, intervalSeconds: 60 },
  { taskName: 'ServerCleanupTask', lastStatus: 'Completed', lastMessage: 'No stale servers', lastRun: ago(300), lastDurationMs: 5, intervalSeconds: 600 },
];

export function getServerLogs(taskName?: string): ServerLogModel[] {
  const tasks = taskName ? [taskName] : serverTasks.map((t) => t.taskName);
  const logs: ServerLogModel[] = [];
  let logId = 1;
  for (const task of tasks) {
    for (let i = 0; i < 10; i++) {
      const isWarning = i === 4 && task === 'ExpirationCleanupTask';
      logs.push({
        id: logId++,
        taskName: task,
        status: isWarning ? 'Warning' : 'Completed',
        message: isWarning
          ? 'Lock contention on warp.jobs, retrying...'
          : `${task} executed successfully`,
        timestamp: ago(i * 60 + tasks.indexOf(task) * 10),
        durationMs: Math.round(10 + seeded(logId) * 200),
      });
    }
  }

  return logs.sort((a, b) => b.timestamp.localeCompare(a.timestamp));
}

// ============================================================
// Worker detail & logs
// ============================================================

export function getWorkerDetail(workerId: string): WorkerDetailModel {
  for (const server of servers) {
    const worker = server.workers.find((w) => w.workerId === workerId);
    if (worker) {
      return {
        workerId: worker.workerId,
        startedTime: worker.startedTime,
        lastHeartbeatTime: worker.lastHeartbeatTime,
        currentJobId: worker.currentJobId,
        currentJobType: worker.currentJobType,
        serverId: server.id,
        serverName: server.serverName,
        queues: worker.queues,
        pollingIntervalMs: worker.pollingIntervalMs,
        serverPausedAt: server.pausedAt,
        workerGroupId: worker.workerGroupId,
        workerGroupPausedAt: worker.workerGroupPausedAt,
      };
    }
  }

  return {
    workerId,
    startedTime: ago(7200),
    lastHeartbeatTime: ago(5),
    currentJobId: null,
    currentJobType: null,
    serverId: IDS.server1,
    serverName: 'warp-prod-server-1',
    queues: 'default',
    pollingIntervalMs: 1000,
    serverPausedAt: null,
    workerGroupId: null,
    workerGroupPausedAt: null,
  };
}

export function getWorkerLogs(): WorkerJobLogModel[] {
  return Array.from({ length: 25 }, (_, i) => {
    const eventType =
      i % 5 === 4 ? 'Failed' : i % 2 === 0 ? 'Processing' : 'Completed';
    const t = TYPES[i % TYPES.length];
    const dur = Math.round(50 + seeded(i) * 500);
    return {
      id: uid(7000 + i),
      jobId: uid(7500 + i),
      jobType: t.type,
      eventType,
      timestamp: ago(i * 45 + 10),
      level: eventType === 'Failed' ? 'Error' : 'Information',
      message:
        eventType === 'Failed'
          ? 'System.TimeoutException: The operation has timed out.'
          : eventType === 'Processing'
            ? 'Job started'
            : `Completed in ${dur}ms`,
      exception:
        eventType === 'Failed'
          ? [
              'System.TimeoutException: The operation has timed out.',
              '   at Acme.Notifications.SmtpEmailClient.SendAsync(EmailMessage msg, CancellationToken ct)',
              '   at Acme.Notifications.SendEmailCommand.HandleAsync(SendEmailRequest request, CancellationToken ct)',
              '   at Warp.Worker.WarpWorkerService.ExecuteJobAsync(Job job, CancellationToken ct)',
            ].join('\n')
          : null,
      durationMs: eventType === 'Completed' ? dur : null,
    };
  });
}

// ============================================================
// Job detail — failed job
// ============================================================

export const jobDetailFailed: UnifiedJobDetailModel = {
  id: IDS.failedJob,
  kind: 1,
  type: 'Acme.Notifications.SendEmailRequest',
  currentState: State.Failed,
  createTime: ago(300),
  cancellationMode: CancellationMode.None,
  message: JSON.stringify({
    to: 'customer@example.com',
    subject: 'Order Confirmation #1042',
    template: 'order-confirmation',
    orderId: 1042,
  }),
  handlerType: 'Acme.Notifications.SendEmailCommand',
  scheduleTime: ago(300),
  retriedTimes: 3,
  maxRetries: 3,
  totalJobs: 0,
  completedJobs: 0,
  failedJobs: 0,
  continuationOptions: null,
  queue: 'default',
  traceId: null,
  parentJob: null,
  spawnedByJob: null,
  continuations: [],
  spawnedJobs: [],
  origin: null,
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  metadata: { correlationId: 'order-1042', source: 'OrderService', MaxRetries: 3, RetriedTimes: 3, RetryDelays: [15, 60, 300] } as any,
  logs: [
    makeLog('log-f1', 'Created', 300, null, null),
    makeLog('log-f2', 'Processing', 299, null, null, null, 'Information', IDS.worker1),
    makeLog(
      'log-f3', 'Failed', 297,
      'System.Net.Sockets.SocketException: Connection refused',
      1850,
      [
        'System.Net.Sockets.SocketException: Connection refused',
        '   at System.Net.Sockets.Socket.AwaitableSocketAsyncEventArgs.ThrowException(SocketError error)',
        '   at System.Net.Sockets.Socket.ConnectAsync(EndPoint remoteEP, CancellationToken ct)',
        '   at Acme.Notifications.SmtpEmailClient.SendAsync(EmailMessage msg, CancellationToken ct)',
        '   at Acme.Notifications.SendEmailCommand.HandleAsync(SendEmailRequest request, CancellationToken ct)',
      ].join('\n'),
      'Error', IDS.worker1,
    ),
    makeLog('log-f4', 'Requeued', 250, 'Retry 1/3', null),
    makeLog('log-f5', 'Processing', 249, null, null, null, 'Information', IDS.worker1),
    makeLog('log-f6', 'Failed', 247, 'System.Net.Sockets.SocketException: Connection refused', 1720, null, 'Error', IDS.worker1),
    makeLog('log-f7', 'Requeued', 200, 'Retry 2/3', null),
    makeLog('log-f8', 'Processing', 199, null, null, null, 'Information', IDS.worker1),
    makeLog('log-f9', 'Failed', 197, 'System.Net.Sockets.SocketException: Connection refused', 1650, null, 'Error', IDS.worker1),
    makeLog('log-f10', 'Requeued', 150, 'Retry 3/3', null),
    makeLog('log-f11', 'Processing', 149, null, null, null, 'Information', IDS.worker1),
    makeLog('log-f12', 'Failed', 147, 'System.Net.Sockets.SocketException: Connection refused', 1580, null, 'Error', IDS.worker1),
  ],
};

// ============================================================
// Job detail — completed job with trace
// ============================================================

export const jobDetailCompleted: UnifiedJobDetailModel = {
  id: IDS.completedJobWithTrace,
  kind: 1,
  type: 'Acme.Orders.ProcessOrderRequest',
  currentState: State.Completed,
  createTime: ago(600),
  cancellationMode: CancellationMode.None,
  message: JSON.stringify({
    orderId: 2847,
    items: [
      { sku: 'WIDGET-001', qty: 3, price: 29.99 },
      { sku: 'GADGET-002', qty: 1, price: 149.99 },
    ],
    customerId: 'cust-42',
    shippingMethod: 'express',
  }),
  handlerType: 'Acme.Orders.ProcessOrderHandler',
  scheduleTime: ago(600),
  retriedTimes: 0,
  maxRetries: 3,
  totalJobs: 0,
  completedJobs: 0,
  failedJobs: 0,
  continuationOptions: null,
  queue: 'default',
  traceId: IDS.traceId,
  parentJob: null,
  spawnedByJob: null,
  continuations: [
    { id: IDS.trShipmentBatch, kind: 3, currentState: State.Completed, type: null, handlerType: null },
  ],
  spawnedJobs: [
    { id: IDS.trCalculateTax, kind: 1, currentState: State.Completed, type: 'Acme.Billing.CalculateTaxRequest', handlerType: 'Acme.Billing.CalculateTaxHandler' },
  ],
  origin: { method: 'POST', routeTemplate: '/orders', user: 'alice', callId: '00000000-0000-0000-0000-0000000000aa', endpointId: 'UE9TVCAvb3JkZXJz' },
  metadata: { correlationId: 'order-2847', source: 'WebApp', priority: 'high' },
  logs: [
    makeLog('log-c1', 'Created', 600, null, null),
    makeLog('log-c2', 'Processing', 599, null, null, null, 'Information', IDS.worker1),
    makeLog('log-c3', 'Log', 598, 'Validating order #2847...', null, null, 'Information', IDS.worker1),
    makeLog('log-c4', 'Log', 597, 'Order validated successfully. Processing payment...', null, null, 'Information', IDS.worker1),
    makeLog('log-c5', 'Log', 596, 'Payment of $239.96 processed via Stripe.', null, null, 'Information', IDS.worker1),
    makeLog('log-c6', 'Log', 595, 'Creating shipment batch for 2 items...', null, null, 'Information', IDS.worker1),
    makeLog('log-c7', 'Log', 594, 'Spawning tax calculation job.', null, null, 'Information', IDS.worker1),
    makeLog('log-c8', 'Completed', 593, null, 4250, null, 'Information', IDS.worker1),
  ],
};

// ============================================================
// Processing-job detail with reported progress bars
// ============================================================

export const jobDetailProcessing: UnifiedJobDetailModel = {
  id: IDS.processingJob,
  kind: 1,
  type: 'Acme.Reports.GenerateQuarterlyReport',
  currentState: State.Processing,
  createTime: ago(120),
  cancellationMode: CancellationMode.None,
  message: '{ "quarter": "Q1-2026", "tenantId": "acme-corp" }',
  handlerType: 'Acme.Reports.GenerateQuarterlyReportHandler',
  scheduleTime: null,
  retriedTimes: 0,
  maxRetries: 0,
  totalJobs: 0,
  completedJobs: 0,
  failedJobs: 0,
  continuationOptions: 1,
  queue: 'reports',
  traceId: IDS.traceId,
  parentJob: null,
  spawnedByJob: null,
  continuations: [],
  spawnedJobs: [],
  origin: null,
  metadata: { tenantId: 'acme-corp', priority: 'normal' },
  logs: [
    makeLog('log-p1', 'Created', 120, null, null),
    makeLog('log-p2', 'Processing', 119, null, null, null, 'Information', IDS.worker1),
    makeLog('log-p3', 'Log', 118, 'Connecting to data warehouse...', null, null, 'Information', IDS.worker1),
    makeLog('log-p4', 'Log', 110, 'Streaming source rows (42M)...', null, null, 'Information', IDS.worker1),
    makeProgress('log-p-d1', 110, 'download', 10, IDS.worker1),
    makeProgress('log-p-d2', 95, 'download', 35, IDS.worker1),
    makeProgress('log-p-d3', 80, 'download', 72, IDS.worker1),
    makeProgress('log-p-d4', 60, 'download', 100, IDS.worker1),
    makeLog('log-p5', 'Log', 55, 'Aggregating by region...', null, null, 'Information', IDS.worker1),
    makeProgress('log-p-a1', 55, 'aggregate', 15, IDS.worker1),
    makeProgress('log-p-a2', 40, 'aggregate', 48, IDS.worker1),
    makeProgress('log-p-a3', 20, 'aggregate', 67, IDS.worker1),
    makeLog('log-p6', 'Log', 10, 'Rendering PDF...', null, null, 'Information', IDS.worker1),
    makeProgress('log-p-r1', 10, 'render', 8, IDS.worker1),
    makeProgress('log-p-r2', 5, 'render', 22, IDS.worker1),
  ],
};

// ============================================================
// Batch detail (unified format for /detail/{id})
// ============================================================

export const batchDetailUnified: UnifiedJobDetailModel = {
  id: IDS.batch1,
  kind: 3,
  type: null,
  currentState: State.Processing,
  createTime: ago(590),
  cancellationMode: CancellationMode.None,
  message: null,
  handlerType: null,
  scheduleTime: null,
  retriedTimes: 0,
  maxRetries: 0,
  totalJobs: 25,
  completedJobs: 18,
  failedJobs: 1,
  continuationOptions: 1,
  queue: 'default',
  traceId: IDS.traceId,
  parentJob: {
    id: IDS.completedJobWithTrace,
    kind: 1,
    currentState: State.Completed,
    type: 'Acme.Orders.ProcessOrderRequest',
    handlerType: 'Acme.Orders.ProcessOrderHandler',
  },
  spawnedByJob: null,
  continuations: [
    { id: IDS.trPublishInvoice, kind: 1, currentState: State.Awaiting, type: 'Acme.Billing.PublishInvoiceRequest', handlerType: 'Acme.Billing.PublishInvoiceHandler' },
  ],
  spawnedJobs: [],
  origin: null,
  metadata: null,
  logs: [
    makeLog('log-b1', 'Created', 590, null, null),
    makeLog('log-b2', 'Processing', 589, '25 child jobs created', null),
  ],
};

const batchChildJobs: JobModel[] = Array.from({ length: 25 }, (_, i) => ({
  id: uid(4000 + i),
  type: 'Acme.Shipping.ShipItemRequest',
  message: JSON.stringify({ itemId: `ITEM-${1000 + i}`, destination: 'Warehouse B' }),
  createTime: ago(580 - i * 2),
  scheduleTime: ago(580 - i * 2),
  processedTime:
    i < 18
      ? ago(570 - i * 2)
      : i === 23
        ? ago(550)
        : null,
  currentState:
    i < 18
      ? State.Completed
      : i === 23
        ? State.Failed
        : i < 21
          ? State.Processing
          : State.Enqueued,
  cancellationMode: CancellationMode.None,
  handlerType: 'Acme.Shipping.ShipItemHandler',
}));

export const batchJobCounts: Record<string, number> = {
  enqueued: 2,
  processing: 4,
  completed: 18,
  failed: 1,
  awaiting: 0,
  deleted: 0,
};

export function getBatchChildren(state?: string): JobModel[] {
  if (!state) {
    return batchChildJobs;
  }
  const stateMap: Record<string, number> = {
    enqueued: State.Enqueued,
    processing: State.Processing,
    completed: State.Completed,
    failed: State.Failed,
    awaiting: State.Awaiting,
    deleted: State.Deleted,
  };
  const s = stateMap[state];
  return s != null ? batchChildJobs.filter((j) => j.currentState === s) : batchChildJobs;
}

// ============================================================
// Message detail (unified format for /detail/{id})
// ============================================================

export const messageDetailUnified: UnifiedJobDetailModel = {
  id: IDS.message1,
  kind: 2,
  type: 'Acme.Notifications.SendEmailRequest',
  currentState: State.Processing,
  createTime: ago(410),
  cancellationMode: CancellationMode.None,
  message: JSON.stringify({ subject: 'Weekly Digest', campaign: 'weekly-2026-w15' }),
  handlerType: null,
  scheduleTime: null,
  retriedTimes: 0,
  maxRetries: 0,
  totalJobs: 4,
  completedJobs: 3,
  failedJobs: 0,
  continuationOptions: null,
  queue: 'default',
  traceId: null,
  parentJob: null,
  spawnedByJob: null,
  continuations: [],
  spawnedJobs: [],
  origin: null,
  metadata: null,
  logs: [
    makeLog('log-m1', 'Created', 410, null, null),
    makeLog('log-m2', 'Processing', 409, '4 handler jobs created', null),
  ],
};

const messageChildJobs: JobModel[] = Array.from({ length: 4 }, (_, i) => ({
  id: uid(4500 + i),
  type:
    i < 2
      ? 'Acme.Notifications.SendEmailRequest'
      : 'Acme.Notifications.NotifyCustomerRequest',
  message: JSON.stringify({ recipientId: `user-${100 + i}` }),
  createTime: ago(400 - i * 5),
  scheduleTime: ago(400 - i * 5),
  processedTime: i < 3 ? ago(395 - i * 5) : null,
  currentState: i < 3 ? State.Completed : State.Processing,
  cancellationMode: CancellationMode.None,
  handlerType:
    i < 2
      ? 'Acme.Notifications.SendEmailCommand'
      : 'Acme.Notifications.NotifyCustomerHandler',
}));

export const messageJobCounts: Record<string, number> = {
  enqueued: 0,
  processing: 1,
  completed: 3,
  failed: 0,
  awaiting: 0,
  deleted: 0,
};

export function getMessageChildren(state?: string): JobModel[] {
  if (!state) {
    return messageChildJobs;
  }
  const stateMap: Record<string, number> = {
    enqueued: State.Enqueued,
    processing: State.Processing,
    completed: State.Completed,
    failed: State.Failed,
    awaiting: State.Awaiting,
    deleted: State.Deleted,
  };
  const s = stateMap[state];
  return s != null ? messageChildJobs.filter((j) => j.currentState === s) : messageChildJobs;
}

// ============================================================
// Trace tree
// ============================================================

export const traceJobs: TraceJobModel[] = [
  { id: IDS.trProcessOrder, kind: 1, type: 'Acme.Orders.ProcessOrderRequest', handlerType: 'Acme.Orders.ProcessOrderHandler', currentState: State.Completed, parentJobId: null, spawnedByJobId: null, createTime: ago(600) },
  { id: IDS.trShipmentBatch, kind: 3, type: null, handlerType: null, currentState: State.Completed, parentJobId: IDS.trProcessOrder, spawnedByJobId: null, createTime: ago(595) },
  { id: IDS.trShipItem1, kind: 1, type: 'Acme.Shipping.ShipItemRequest', handlerType: 'Acme.Shipping.ShipItemHandler', currentState: State.Completed, parentJobId: IDS.trShipmentBatch, spawnedByJobId: null, createTime: ago(594) },
  { id: IDS.trShipItem2, kind: 1, type: 'Acme.Shipping.ShipItemRequest', handlerType: 'Acme.Shipping.ShipItemHandler', currentState: State.Completed, parentJobId: IDS.trShipmentBatch, spawnedByJobId: null, createTime: ago(593) },
  { id: IDS.trShipItem3, kind: 1, type: 'Acme.Shipping.ShipItemRequest', handlerType: 'Acme.Shipping.ShipItemHandler', currentState: State.Completed, parentJobId: IDS.trShipmentBatch, spawnedByJobId: null, createTime: ago(592) },
  { id: IDS.trShipItem4, kind: 1, type: 'Acme.Shipping.ShipItemRequest', handlerType: 'Acme.Shipping.ShipItemHandler', currentState: State.Completed, parentJobId: IDS.trShipmentBatch, spawnedByJobId: null, createTime: ago(591) },
  { id: IDS.trShipItem5, kind: 1, type: 'Acme.Shipping.ShipItemRequest', handlerType: 'Acme.Shipping.ShipItemHandler', currentState: State.Completed, parentJobId: IDS.trShipmentBatch, spawnedByJobId: null, createTime: ago(590) },
  { id: IDS.trPublishInvoice, kind: 1, type: 'Acme.Billing.PublishInvoiceRequest', handlerType: 'Acme.Billing.PublishInvoiceHandler', currentState: State.Completed, parentJobId: null, spawnedByJobId: IDS.trShipItem1, createTime: ago(585) },
  { id: IDS.trNotification, kind: 2, type: 'Acme.Notifications.InvoiceNotification', handlerType: null, currentState: State.Completed, parentJobId: null, spawnedByJobId: IDS.trPublishInvoice, createTime: ago(580) },
  { id: IDS.trSendEmail, kind: 1, type: 'Acme.Notifications.SendEmailRequest', handlerType: 'Acme.Notifications.SendEmailCommand', currentState: State.Completed, parentJobId: IDS.trNotification, spawnedByJobId: null, createTime: ago(579) },
  { id: IDS.trNotifyCustomer, kind: 1, type: 'Acme.Notifications.NotifyCustomerRequest', handlerType: 'Acme.Notifications.NotifyCustomerHandler', currentState: State.Completed, parentJobId: IDS.trNotification, spawnedByJobId: null, createTime: ago(578) },
  { id: IDS.trCalculateTax, kind: 1, type: 'Acme.Billing.CalculateTaxRequest', handlerType: 'Acme.Billing.CalculateTaxHandler', currentState: State.Completed, parentJobId: null, spawnedByJobId: IDS.trProcessOrder, createTime: ago(598) },
];

const sagaMin = (m: number) => new Date(Date.now() - m * 60 * 1000).toISOString();

export const demoSagas = [
  { id: '11111111-1111-1111-1111-111111111111', type: 'Acme.Orders.OrderSaga', correlationKey: 'O-1042', createdAt: sagaMin(8), updatedAt: sagaMin(2) },
  { id: '22222222-2222-2222-2222-222222222222', type: 'Acme.Orders.OrderSaga', correlationKey: 'O-1041', createdAt: sagaMin(15), updatedAt: sagaMin(4) },
  { id: '33333333-3333-3333-3333-333333333333', type: 'Acme.Billing.InvoiceSaga', correlationKey: 'inv-2026-05-1147', createdAt: sagaMin(35), updatedAt: sagaMin(10) },
  { id: '44444444-4444-4444-4444-444444444444', type: 'Acme.Approvals.ApprovalSaga', correlationKey: 'doc-9923', createdAt: sagaMin(120), updatedAt: sagaMin(45) },
  { id: '55555555-5555-5555-5555-555555555555', type: 'Acme.Orders.OrderSaga', correlationKey: 'O-1038', createdAt: sagaMin(720), updatedAt: sagaMin(700) },
];

// ============================================================
// Background Services fixtures
// ============================================================

// Stable server IDs for the demo — distinct from dashboard server IDs so logs
// look like they come from a different (worker) host pool.
const BS_SERVER1_ID = uid(90001);
const BS_SERVER2_ID = uid(90002);
const BS_SERVER1_NAME = 'warp-demo-server';
const BS_SERVER2_NAME = 'warp-demo-worker-2';

export const demoBackgroundServices: BackgroundServiceListItem[] = [
  {
    name: 'JobStatsLoggerService',
    scope: ServiceScope.Singleton,
    runningCount: 1,
    waitingCount: 1,
    faultedCount: 0,
    configurationMismatchCount: 0,
    totalInstances: 2,
    totalRestartCount: 1,
    lastErrorType: null,
  },
  {
    name: 'OutboxDrainerService',
    scope: ServiceScope.Singleton,
    runningCount: 1,
    waitingCount: 0,
    faultedCount: 0,
    configurationMismatchCount: 0,
    totalInstances: 1,
    totalRestartCount: 0,
    lastErrorType: null,
  },
  {
    name: 'TickCounterService',
    scope: ServiceScope.PerServer,
    runningCount: 2,
    waitingCount: 0,
    faultedCount: 0,
    configurationMismatchCount: 0,
    totalInstances: 2,
    totalRestartCount: 0,
    lastErrorType: null,
  },
];

export const demoBackgroundServiceDetails: Record<string, BackgroundServiceDetail> = {
  TickCounterService: {
    name: 'TickCounterService',
    declaredScope: ServiceScope.PerServer,
    firstSeenAt: ago(60 * 30),
    lastSeenAt: ago(4),
    instances: [
      {
        serverId: BS_SERVER1_ID,
        serverName: BS_SERVER1_NAME,
        serviceName: 'TickCounterService',
        declaredScope: ServiceScope.PerServer,
        status: BackgroundServiceStatus.Running,
        startedAt: ago(60 * 30),
        lastHeartbeatAt: ago(4),
        lastError: null,
        lastErrorAt: null,
        restartCount: 0,
      },
      {
        serverId: BS_SERVER2_ID,
        serverName: BS_SERVER2_NAME,
        serviceName: 'TickCounterService',
        declaredScope: ServiceScope.PerServer,
        status: BackgroundServiceStatus.Running,
        startedAt: ago(60 * 25),
        lastHeartbeatAt: ago(6),
        lastError: null,
        lastErrorAt: null,
        restartCount: 0,
      },
    ],
  },
  JobStatsLoggerService: {
    name: 'JobStatsLoggerService',
    declaredScope: ServiceScope.Singleton,
    firstSeenAt: ago(60 * 45),
    lastSeenAt: ago(2),
    instances: [
      {
        serverId: BS_SERVER1_ID,
        serverName: BS_SERVER1_NAME,
        serviceName: 'JobStatsLoggerService',
        declaredScope: ServiceScope.Singleton,
        status: BackgroundServiceStatus.Running,
        startedAt: ago(60 * 45),
        lastHeartbeatAt: ago(2),
        lastError: null,
        lastErrorAt: null,
        restartCount: 0,
      },
      {
        serverId: BS_SERVER2_ID,
        serverName: BS_SERVER2_NAME,
        serviceName: 'JobStatsLoggerService',
        declaredScope: ServiceScope.Singleton,
        status: BackgroundServiceStatus.Waiting,
        startedAt: ago(60 * 40),
        lastHeartbeatAt: ago(7),
        lastError: 'System.InvalidOperationException: Stats DB context returned null for aggregation window 2026-05-18T03:00:00Z.\n   at Acme.BackgroundServices.JobStatsLoggerService.LogAsync(CancellationToken ct)\n   at Warp.Core.BackgroundServices.WarpBackgroundService.ExecuteAsync(CancellationToken ct)',
        lastErrorAt: ago(60 * 38),
        restartCount: 1,
      },
    ],
  },
  OutboxDrainerService: {
    name: 'OutboxDrainerService',
    declaredScope: ServiceScope.Singleton,
    firstSeenAt: ago(60 * 120),
    lastSeenAt: ago(3),
    instances: [
      {
        serverId: BS_SERVER1_ID,
        serverName: BS_SERVER1_NAME,
        serviceName: 'OutboxDrainerService',
        declaredScope: ServiceScope.Singleton,
        status: BackgroundServiceStatus.Running,
        startedAt: ago(60 * 120),
        lastHeartbeatAt: ago(3),
        lastError: null,
        lastErrorAt: null,
        restartCount: 0,
      },
    ],
  },
};

export const demoBackgroundServiceLeases: Record<string, BackgroundServiceLeaseDto> = {
  JobStatsLoggerService: {
    serviceName: 'JobStatsLoggerService',
    holderServerId: BS_SERVER1_ID,
    holderServerName: BS_SERVER1_NAME,
    leaseExpiresAt: future(25),
  },
  OutboxDrainerService: {
    serviceName: 'OutboxDrainerService',
    holderServerId: BS_SERVER1_ID,
    holderServerName: BS_SERVER1_NAME,
    leaseExpiresAt: future(18),
  },
};

// Logs per service — IDs are sequential integers, newest-first (highest id = most recent).
// TickCounterService logs
const tickLogs: BackgroundServiceLogDto[] = [
  { id: 1030, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'TickCounterService', timestamp: ago(8), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'Tick #18421 — enqueued: 4, processing: 2', exceptionType: null, exceptionMessage: null },
  { id: 1029, serverId: BS_SERVER2_ID, serverName: BS_SERVER2_NAME, serviceName: 'TickCounterService', timestamp: ago(12), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'Tick #18421 — enqueued: 3, processing: 1', exceptionType: null, exceptionMessage: null },
  { id: 1028, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'TickCounterService', timestamp: ago(18), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'Tick #18420 — enqueued: 6, processing: 3', exceptionType: null, exceptionMessage: null },
  { id: 1027, serverId: BS_SERVER2_ID, serverName: BS_SERVER2_NAME, serviceName: 'TickCounterService', timestamp: ago(22), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'Tick #18420 — enqueued: 5, processing: 2', exceptionType: null, exceptionMessage: null },
  { id: 1026, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'TickCounterService', timestamp: ago(38), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'Tick #18419 — enqueued: 2, processing: 0', exceptionType: null, exceptionMessage: null },
  { id: 1025, serverId: BS_SERVER2_ID, serverName: BS_SERVER2_NAME, serviceName: 'TickCounterService', timestamp: ago(42), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'Tick #18419 — enqueued: 1, processing: 0', exceptionType: null, exceptionMessage: null },
  { id: 1002, serverId: BS_SERVER2_ID, serverName: BS_SERVER2_NAME, serviceName: 'TickCounterService', timestamp: ago(60 * 25 + 2), level: LogLevel.Information, source: BackgroundServiceLogSource.Lifecycle, message: 'Service started', exceptionType: null, exceptionMessage: null },
  { id: 1001, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'TickCounterService', timestamp: ago(60 * 30 + 1), level: LogLevel.Information, source: BackgroundServiceLogSource.Lifecycle, message: 'Service started', exceptionType: null, exceptionMessage: null },
];

// JobStatsLoggerService logs
const statsLogs: BackgroundServiceLogDto[] = [
  { id: 2040, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'JobStatsLoggerService', timestamp: ago(5), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'Stats snapshot — succeeded: 847, failed: 3, avg_duration_ms: 312', exceptionType: null, exceptionMessage: null },
  { id: 2039, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'JobStatsLoggerService', timestamp: ago(35), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'Stats snapshot — succeeded: 821, failed: 2, avg_duration_ms: 298', exceptionType: null, exceptionMessage: null },
  { id: 2038, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'JobStatsLoggerService', timestamp: ago(65), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'Stats snapshot — succeeded: 794, failed: 2, avg_duration_ms: 304', exceptionType: null, exceptionMessage: null },
  { id: 2037, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'JobStatsLoggerService', timestamp: ago(60 * 3), level: LogLevel.Information, source: BackgroundServiceLogSource.Lifecycle, message: 'Lease acquired — holder: warp-demo-server, expires in 30s', exceptionType: null, exceptionMessage: null },
  { id: 2036, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'JobStatsLoggerService', timestamp: ago(60 * 3 + 2), level: LogLevel.Information, source: BackgroundServiceLogSource.Lifecycle, message: 'Service started', exceptionType: null, exceptionMessage: null },
  { id: 2035, serverId: BS_SERVER2_ID, serverName: BS_SERVER2_NAME, serviceName: 'JobStatsLoggerService', timestamp: ago(60 * 38 + 5), level: LogLevel.Warning, source: BackgroundServiceLogSource.Lifecycle, message: 'Service restarting after fault (attempt 1)', exceptionType: null, exceptionMessage: null },
  { id: 2034, serverId: BS_SERVER2_ID, serverName: BS_SERVER2_NAME, serviceName: 'JobStatsLoggerService', timestamp: ago(60 * 38), level: LogLevel.Error, source: BackgroundServiceLogSource.Lifecycle, message: 'Unhandled exception — service will restart', exceptionType: 'System.InvalidOperationException', exceptionMessage: 'System.InvalidOperationException: Stats DB context returned null for aggregation window 2026-05-18T03:00:00Z.\n   at Acme.BackgroundServices.JobStatsLoggerService.LogAsync(CancellationToken ct)\n   at Warp.Core.BackgroundServices.WarpBackgroundService.ExecuteAsync(CancellationToken ct)' },
  { id: 2033, serverId: BS_SERVER2_ID, serverName: BS_SERVER2_NAME, serviceName: 'JobStatsLoggerService', timestamp: ago(60 * 40 + 3), level: LogLevel.Information, source: BackgroundServiceLogSource.Lifecycle, message: 'Service started (waiting for lease)', exceptionType: null, exceptionMessage: null },
  { id: 2032, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'JobStatsLoggerService', timestamp: ago(60 * 45 + 2), level: LogLevel.Information, source: BackgroundServiceLogSource.Lifecycle, message: 'Service started', exceptionType: null, exceptionMessage: null },
  { id: 2031, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'JobStatsLoggerService', timestamp: ago(60 * 45 + 1), level: LogLevel.Information, source: BackgroundServiceLogSource.Lifecycle, message: 'Lease acquired — holder: warp-demo-server, expires in 30s', exceptionType: null, exceptionMessage: null },
];

// OutboxDrainerService logs
const outboxLogs: BackgroundServiceLogDto[] = [
  { id: 3020, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'OutboxDrainerService', timestamp: ago(10), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'Drained 3 outbox messages', exceptionType: null, exceptionMessage: null },
  { id: 3019, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'OutboxDrainerService', timestamp: ago(20), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'No outbox messages to drain', exceptionType: null, exceptionMessage: null },
  { id: 3018, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'OutboxDrainerService', timestamp: ago(30), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'Drained 1 outbox message', exceptionType: null, exceptionMessage: null },
  { id: 3017, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'OutboxDrainerService', timestamp: ago(40), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'No outbox messages to drain', exceptionType: null, exceptionMessage: null },
  { id: 3016, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'OutboxDrainerService', timestamp: ago(50), level: LogLevel.Information, source: BackgroundServiceLogSource.User, message: 'Drained 7 outbox messages', exceptionType: null, exceptionMessage: null },
  { id: 3015, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'OutboxDrainerService', timestamp: ago(65), level: LogLevel.Warning, source: BackgroundServiceLogSource.User, message: 'Drain latency exceeded 200ms (observed 347ms) — consider increasing drain frequency', exceptionType: null, exceptionMessage: null },
  { id: 3002, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'OutboxDrainerService', timestamp: ago(60 * 120 + 2), level: LogLevel.Information, source: BackgroundServiceLogSource.Lifecycle, message: 'Lease acquired — holder: warp-demo-server, expires in 30s', exceptionType: null, exceptionMessage: null },
  { id: 3001, serverId: BS_SERVER1_ID, serverName: BS_SERVER1_NAME, serviceName: 'OutboxDrainerService', timestamp: ago(60 * 120 + 1), level: LogLevel.Information, source: BackgroundServiceLogSource.Lifecycle, message: 'Service started', exceptionType: null, exceptionMessage: null },
];

const ALL_BS_LOGS: Record<string, BackgroundServiceLogDto[]> = {
  TickCounterService: tickLogs,
  JobStatsLoggerService: statsLogs,
  OutboxDrainerService: outboxLogs,
};

export function getBackgroundServiceLogs(
  name: string,
  source?: number,
  level?: number,
  fromId?: number,
): BackgroundServiceLogDto[] {
  const all = ALL_BS_LOGS[name] ?? [];
  let filtered = all;
  if (source !== undefined && source !== 0) {
    filtered = filtered.filter((l) => l.source === source);
  }
  if (level !== undefined && level !== -1) {
    filtered = filtered.filter((l) => l.level >= level);
  }
  if (fromId !== undefined && fromId > 0) {
    filtered = filtered.filter((l) => l.id > fromId);
  }

  return filtered;
}

export const demoSagaActivity = [
  {
    jobId: 'a0000000-0000-0000-0000-000000000001',
    messageType: 'OrderPlaced',
    jobState: 'Completed',
    createTime: sagaMin(8),
    logs: [
      { id: 'l1', eventType: 'Created', timestamp: sagaMin(8), level: 'Information', message: 'Job created', exception: null, durationMs: null, workerId: null },
      { id: 'l2', eventType: 'Completed', timestamp: sagaMin(8), level: 'Information', message: 'Job completed', exception: null, durationMs: 142, workerId: null },
    ],
  },
  {
    jobId: 'a0000000-0000-0000-0000-000000000002',
    messageType: 'PaymentCaptured',
    jobState: 'Completed',
    createTime: sagaMin(2),
    logs: [
      { id: 'l3', eventType: 'Created', timestamp: sagaMin(2), level: 'Information', message: 'Job created', exception: null, durationMs: null, workerId: null },
      { id: 'l4', eventType: 'Completed', timestamp: sagaMin(2), level: 'Information', message: 'Job completed', exception: null, durationMs: 67, workerId: null },
    ],
  },
];

// === Queue metrics (§8.26) — the Queues page ===
export function getQueueMetricsDemo() {
  return {
    queues: [
      { queue: 'a-critical', claimedCount: 48213, avgWaitMs: 42, p95WaitMs: 180, p99WaitMs: 420, backlogDepth: 3, oldestAgeSeconds: 8 },
      { queue: 'b-default', claimedCount: 129004, avgWaitMs: 310, p95WaitMs: 1250, p99WaitMs: 2600, backlogDepth: 27, oldestAgeSeconds: 74 },
      { queue: 'c-low', claimedCount: 15622, avgWaitMs: 1450, p95WaitMs: 5200, p99WaitMs: 9800, backlogDepth: 141, oldestAgeSeconds: 612 },
    ],
  };
}

// === Client (browser) observability (§8.27) ===
export function getClientSummaryDemo() {
  const now = Date.now();
  const hours = Array.from({ length: 12 }, (_, i) => {
    const d = new Date(now - (11 - i) * 3600_000);
    const hour = d.toISOString().slice(0, 13).replace('T', '-');

    return { hour, errors: Math.round(3 + Math.random() * 12), logs: Math.round(20 + Math.random() * 40), events: Math.round(10 + Math.random() * 30), vitals: Math.round(40 + Math.random() * 60) };
  });

  return {
    application: null,
    errorCount: 1284,
    logCount: 8213,
    eventCount: 4021,
    vitalCount: 15903,
    errorRate: 0.043,
    topErrors: [
      { name: 'TypeError', count: 612 },
      { name: 'ChunkLoadError', count: 288 },
      { name: 'NetworkError', count: 201 },
      { name: 'UnhandledRejection', count: 122 },
      { name: '{other}', count: 61 },
    ],
    topEvents: [
      { name: 'checkout_started', count: 1893 },
      { name: 'add_to_cart', count: 1204 },
      { name: 'search', count: 924 },
    ],
    vitals: [
      { name: 'LCP', sampleCount: 4210, avgValue: 2100, p75Value: 2450 },
      { name: 'INP', sampleCount: 4210, avgValue: 160, p75Value: 190 },
      { name: 'CLS', sampleCount: 4210, avgValue: 0.06, p75Value: 0.09 },
      { name: 'FCP', sampleCount: 4210, avgValue: 1400, p75Value: 1750 },
      { name: 'TTFB', sampleCount: 4210, avgValue: 520, p75Value: 780 },
    ],
    history: hours,
  };
}

export function getClientEventsDemo() {
  const now = Date.now();
  const iso = (offset: number) => new Date(now - offset).toISOString();
  const items = [
    { id: 'ce-1', application: 'warp-demo-spa', type: 1, name: 'TypeError', level: null, message: "Cannot read properties of undefined (reading 'total')", value: null, url: '/checkout', traceId: '0af7651916cd43dd8448eb211c80319c', sessionId: 'sess-8f3a2b1c', timestamp: iso(4000) },
    { id: 'ce-2', application: 'warp-demo-spa', type: 5, name: 'POST', level: null, message: null, value: 240, url: '/api/checkout', traceId: '0af7651916cd43dd8448eb211c80319c', sessionId: 'sess-8f3a2b1c', timestamp: iso(9000) },
    { id: 'ce-3', application: 'warp-demo-spa', type: 2, name: 'LCP', level: null, message: null, value: 2380, url: '/', traceId: null, sessionId: 'sess-8f3a2b1c', timestamp: iso(15000) },
    { id: 'ce-4', application: 'warp-demo-spa', type: 4, name: 'add_to_cart', level: null, message: null, value: null, url: '/product/42', traceId: null, sessionId: 'sess-1a2b3c4d', timestamp: iso(22000) },
    { id: 'ce-5', application: 'warp-demo-spa', type: 3, name: 'warn', level: 'warn', message: 'Retrying image load (attempt 2)', value: null, url: '/product/42', traceId: null, sessionId: 'sess-1a2b3c4d', timestamp: iso(30000) },
  ];

  return { items, total: items.length };
}

export function getClientSessionDemo(sessionId: string) {
  const now = Date.now();
  const iso = (offset: number) => new Date(now - offset).toISOString();
  const trace = '0af7651916cd43dd8448eb211c80319c';

  return {
    sessionId,
    application: 'warp-demo-spa',
    entries: [
      { kind: 'client', timestamp: iso(32000), traceId: null, eventId: 'e1', type: 4, name: 'page_view', level: null, message: null, value: null, url: '/checkout', method: null, route: null, statusCode: null, durationMs: null, outcome: null },
      { kind: 'client', timestamp: iso(30000), traceId: trace, eventId: 'e2', type: 5, name: 'POST', level: null, message: null, value: 240, url: '/api/checkout', method: null, route: null, statusCode: null, durationMs: null, outcome: null },
      { kind: 'endpoint', timestamp: iso(29800), traceId: trace, eventId: null, type: null, name: null, level: null, message: null, value: null, url: null, method: 'POST', route: '/api/checkout', statusCode: 500, durationMs: 212, outcome: 'Failed' },
      { kind: 'client', timestamp: iso(29500), traceId: trace, eventId: 'e3', type: 1, name: 'TypeError', level: null, message: "Cannot read properties of undefined (reading 'total')", value: null, url: '/checkout', method: null, route: null, statusCode: null, durationMs: null, outcome: null },
    ],
  };
}

export function getClientEventDetailDemo(id: string) {
  const now = Date.now();
  const iso = (offset: number) => new Date(now - offset).toISOString();

  return {
    id,
    application: 'warp-demo-spa',
    type: 1,
    name: 'TypeError',
    level: null,
    message: "Cannot read properties of undefined (reading 'total')",
    stack: "TypeError: Cannot read properties of undefined (reading 'total')\n    at Checkout.tsx:42:18\n    at onClick (Button.tsx:11:5)",
    value: null,
    url: '/checkout',
    traceId: '0af7651916cd43dd8448eb211c80319c',
    sessionId: 'sess-8f3a2b1c',
    release: '1.4.2',
    userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36',
    remoteIp: null,
    properties: '{"cartId":"c-9931","items":3}',
    breadcrumbs: '[{"type":"navigation","data":"/cart"},{"type":"click","data":"BUTTON#pay"}]',
    timestamp: iso(4000),
    receivedAt: iso(3800),
  };
}

export function getTraceOverviewDemo() {
  const base = Date.parse('2026-05-25T12:59:30.000Z');
  const iso = (offsetMs: number) => new Date(base + offsetMs).toISOString();

  return {
    traceId: '4bf92f3577b34da6a3ce929d0e0e4736',
    clientCount: 1,
    endpointCount: 1,
    jobCount: 3,
    adapterCount: 1,
    errorCount: 0,
    isTruncated: false,
    spans: [
      { source: 'client', id: 'a1b2c3d4-0000-0000-0000-000000000001', name: 'POST /api/checkout', startTime: iso(0), durationMs: 240, status: 'Request', isError: false, parentId: null },
      { source: 'endpoint', id: 'a1b2c3d4-0000-0000-0000-000000000002', name: 'POST /api/checkout', startTime: iso(5), durationMs: 212, status: 'Success', isError: false, parentId: null },
      { source: 'job', id: 'a1b2c3d4-0000-0000-0000-000000000010', name: 'Acme.Orders.ProcessOrderRequest', startTime: iso(60), durationMs: 130, status: 'Completed', isError: false, parentId: null },
      { source: 'adapter', id: 'a1b2c3d4-0000-0000-0000-000000000020', name: 'payments.Charge', startTime: iso(80), durationMs: 90, status: 'Success', isError: false, parentId: null },
      { source: 'job', id: 'a1b2c3d4-0000-0000-0000-000000000011', name: 'Acme.Orders.SendConfirmationRequest', startTime: iso(200), durationMs: 40, status: 'Completed', isError: false, parentId: 'a1b2c3d4-0000-0000-0000-000000000010' },
    ],
  };
}
