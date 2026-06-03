import type { InternalAxiosRequestConfig, AxiosResponse } from 'axios';
import * as data from './data';
import type { ConcurrencyLimitInfo, RateLimitInfo, SagaDetail, SagaStats } from '@/types';
import type { BackgroundServiceLeaseDto } from '@/types/backgroundServices';

// Mutable copy so demo upsert/delete feel real across the session.
const concurrencyLimits: ConcurrencyLimitInfo[] = [...data.demoConcurrencyLimits];
const rateLimits: RateLimitInfo[] = [...data.demoRateLimits];

export function createDemoAdapter(isLoginMode: boolean) {
  let loginActive = isLoginMode;

  return (config: InternalAxiosRequestConfig): Promise<AxiosResponse> => {
    const rawUrl = config.url ?? '';
    // Axios resolves baseURL before calling the adapter, so strip the prefix
    const base = (config.baseURL ?? '').replace(/\/$/, '');
    const url = rawUrl.startsWith(base) ? rawUrl.slice(base.length) : rawUrl;
    const params: Record<string, unknown> = config.params ?? {};
    const method = (config.method ?? 'get').toLowerCase();

    // Login mode: reject with 401 until POST /auth/login succeeds
    if (loginActive) {
      if (method === 'post' && url.includes('/auth/login')) {
        loginActive = false;

        return resolve({}, config);
      }

      return Promise.reject({
        response: { status: 401, statusText: 'Unauthorized', data: {}, headers: {}, config },
      });
    }

    // Background services
    const bgServicesResult = routeBackgroundServices(method, url, config);
    if (bgServicesResult !== undefined) {
      return bgServicesResult;
    }

    // Concurrency limits: handle CRUD against the local mutable list.
    const concurrencyResult = routeConcurrency(method, url, config);
    if (concurrencyResult !== undefined) {
      return concurrencyResult;
    }

    const rateLimitResult = routeRateLimits(method, url, config);
    if (rateLimitResult !== undefined) {
      return rateLimitResult;
    }

    // Sagas: /addons reports sagas:true in demo mode (see routeGet), so the nav is visible
    // and these routes return mock data for the screenshots.
    const sagaResult = routeSagas(method, url, config);
    if (sagaResult !== undefined) {
      return sagaResult;
    }

    // All POST/DELETE routes return success
    if (method === 'post') {
      if (url.includes('/bulk/')) {
        return resolve({ succeeded: 5, skipped: 0 }, config);
      }

      return resolve({}, config);
    }
    if (method === 'delete') {
      return resolve({}, config);
    }
    if (method === 'put') {
      return resolve({}, config);
    }

    // GET routes — return mock data
    return resolve(routeGet(url, params), config);
  };
}

function routeSagas(
  method: string,
  url: string,
  config: InternalAxiosRequestConfig,
): Promise<AxiosResponse> | undefined {
  if (!url.startsWith('/sagas')) {
    return undefined;
  }

  // Reserved sub-routes that the detailMatch regex must NOT swallow.
  const RESERVED = new Set(['types', 'stats']);

  if (method === 'get' && (url === '/sagas' || url.startsWith('/sagas?'))) {
    return resolve(
      {
        items: data.demoSagas,
        totalCount: data.demoSagas.length,
        page: 0,
        pageSize: 20,
      },
      config,
    );
  }
  if (method === 'get' && url === '/sagas/types') {
    return resolve([...new Set(data.demoSagas.map((s) => s.type))].sort(), config);
  }
  if (method === 'get' && url === '/sagas/stats') {
    return resolve(
      { liveSagas: data.demoSagas.length, startedToday: 5, completedToday: 0 } as SagaStats,
      config,
    );
  }
  const activityMatch = url.match(/^\/sagas\/([^/]+)\/activity$/);
  if (method === 'get' && activityMatch && !RESERVED.has(activityMatch[1])) {
    return resolve(
      {
        entries: data.demoSagaActivity,
        totalInvocations: data.demoSagaActivity.length,
        isTruncated: false,
      },
      config,
    );
  }
  const detailMatch = url.match(/^\/sagas\/([^/?]+)$/);
  if (method === 'get' && detailMatch && !RESERVED.has(detailMatch[1])) {
    const id = detailMatch[1];
    const found = data.demoSagas.find((s) => s.id === id);
    if (!found) {
      return Promise.reject({ response: { status: 404, statusText: 'Not Found', data: {}, headers: {}, config } });
    }
    const detail: SagaDetail = {
      ...found,
      stateJson: JSON.stringify({ OrderId: found.correlationKey, PaymentCaptured: true, InventoryReserved: false }, null, 2),
      version: '00000000-0000-0000-0000-000000000001',
    };

    return resolve(detail, config);
  }
  if (method === 'delete' && detailMatch && !RESERVED.has(detailMatch[1])) {
    return resolve({}, config);
  }

  return undefined;
}

function routeBackgroundServices(
  method: string,
  url: string,
  config: InternalAxiosRequestConfig,
): Promise<AxiosResponse> | undefined {
  if (!url.startsWith('/services')) {
    return undefined;
  }

  const params: Record<string, unknown> = config.params ?? {};

  // GET /services — list
  if (method === 'get' && (url === '/services' || url.startsWith('/services?'))) {
    return resolve(data.demoBackgroundServices, config);
  }

  // GET /services/{name}/lease
  const leaseMatch = url.match(/^\/services\/([^/?]+)\/lease$/);
  if (method === 'get' && leaseMatch) {
    const name = decodeURIComponent(leaseMatch[1]);
    const lease: BackgroundServiceLeaseDto | undefined = data.demoBackgroundServiceLeases[name];
    if (!lease) {
      return Promise.reject({ response: { status: 404, statusText: 'Not Found', data: {}, headers: {}, config } });
    }

    return resolve(lease, config);
  }

  // GET /services/{name}/logs
  const logsMatch = url.match(/^\/services\/([^/?]+)\/logs$/);
  if (method === 'get' && logsMatch) {
    const name = decodeURIComponent(logsMatch[1]);
    const source = params.source !== undefined ? Number(params.source) : undefined;
    const level = params.level !== undefined ? Number(params.level) : undefined;
    const fromId = params.fromId !== undefined ? Number(params.fromId) : undefined;
    const logs = data.getBackgroundServiceLogs(name, source, level, fromId);

    return resolve(logs, config);
  }

  // GET /services/{name} — detail
  const detailMatch = url.match(/^\/services\/([^/?]+)$/);
  if (method === 'get' && detailMatch) {
    const name = decodeURIComponent(detailMatch[1]);
    const detail = data.demoBackgroundServiceDetails[name];
    if (!detail) {
      return Promise.reject({ response: { status: 404, statusText: 'Not Found', data: {}, headers: {}, config } });
    }

    return resolve(detail, config);
  }

  return undefined;
}

function routeConcurrency(
  method: string,
  url: string,
  config: InternalAxiosRequestConfig,
): Promise<AxiosResponse> | undefined {
  if (!url.startsWith('/concurrency')) {
    return undefined;
  }

  const nameMatch = url.match(/^\/concurrency\/([^/?]+)/);
  const name = nameMatch ? decodeURIComponent(nameMatch[1]) : null;

  if (method === 'get' && name === null) {
    return resolve([...concurrencyLimits].sort((a, b) => a.name.localeCompare(b.name)), config);
  }
  if (method === 'get' && name !== null) {
    const found = concurrencyLimits.find((x) => x.name === name);

    return resolve(found ?? null, config);
  }
  if ((method === 'put' || method === 'post') && name !== null) {
    const body = parseBody(config.data) as { limit?: number } | null;
    const limit = Number(body?.limit ?? 1);
    const now = new Date().toISOString();
    const existing = concurrencyLimits.find((x) => x.name === name);
    if (existing) {
      existing.limit = limit;
      existing.updatedAt = now;

      return resolve({ ...existing }, config);
    }
    const created: ConcurrencyLimitInfo = { name, limit, updatedAt: now };
    concurrencyLimits.push(created);

    return resolve({ ...created }, config);
  }
  if (method === 'post' && name === null) {
    const body = parseBody(config.data) as { name?: string; limit?: number } | null;
    const newName = String(body?.name ?? '');
    const limit = Number(body?.limit ?? 1);
    const now = new Date().toISOString();
    const existing = concurrencyLimits.find((x) => x.name === newName);
    if (existing) {
      existing.limit = limit;
      existing.updatedAt = now;

      return resolve({ ...existing }, config);
    }
    const created: ConcurrencyLimitInfo = { name: newName, limit, updatedAt: now };
    concurrencyLimits.push(created);

    return resolve({ ...created }, config);
  }
  if (method === 'delete' && name !== null) {
    const idx = concurrencyLimits.findIndex((x) => x.name === name);
    if (idx >= 0) {
      concurrencyLimits.splice(idx, 1);
    }

    return resolve({}, config);
  }

  return undefined;
}

function routeRateLimits(
  method: string,
  url: string,
  config: InternalAxiosRequestConfig,
): Promise<AxiosResponse> | undefined {
  if (!url.startsWith('/ratelimits')) {
    return undefined;
  }

  const nameMatch = url.match(/^\/ratelimits\/([^/?]+)/);
  const name = nameMatch ? decodeURIComponent(nameMatch[1]) : null;

  if (method === 'get' && name === null) {
    return resolve([...rateLimits].sort((a, b) => a.name.localeCompare(b.name)), config);
  }
  if (method === 'get' && name !== null) {
    const found = rateLimits.find((x) => x.name === name);

    return resolve(found ?? null, config);
  }
  if ((method === 'put' || method === 'post') && name !== null) {
    const body = parseBody(config.data) as { count?: number; windowSeconds?: number } | null;
    const count = Number(body?.count ?? 1);
    const windowSeconds = Number(body?.windowSeconds ?? 60);
    const now = new Date().toISOString();
    const existing = rateLimits.find((x) => x.name === name);
    if (existing) {
      existing.count = count;
      existing.windowSeconds = windowSeconds;
      existing.updatedAt = now;

      return resolve({ ...existing }, config);
    }
    const created: RateLimitInfo = { name, count, windowSeconds, updatedAt: now };
    rateLimits.push(created);

    return resolve({ ...created }, config);
  }
  if (method === 'post' && name === null) {
    const body = parseBody(config.data) as { name?: string; count?: number; windowSeconds?: number } | null;
    const newName = String(body?.name ?? '');
    const count = Number(body?.count ?? 1);
    const windowSeconds = Number(body?.windowSeconds ?? 60);
    const now = new Date().toISOString();
    const existing = rateLimits.find((x) => x.name === newName);
    if (existing) {
      existing.count = count;
      existing.windowSeconds = windowSeconds;
      existing.updatedAt = now;

      return resolve({ ...existing }, config);
    }
    const created: RateLimitInfo = { name: newName, count, windowSeconds, updatedAt: now };
    rateLimits.push(created);

    return resolve({ ...created }, config);
  }
  if (method === 'delete' && name !== null) {
    const idx = rateLimits.findIndex((x) => x.name === name);
    if (idx >= 0) {
      rateLimits.splice(idx, 1);
    }

    return resolve({}, config);
  }

  return undefined;
}

function parseBody(raw: unknown): unknown {
  if (raw == null) {
    return null;
  }
  if (typeof raw === 'string') {
    try {
      return JSON.parse(raw);
    } catch {
      return null;
    }
  }

  return raw;
}

function resolve(
  responseData: unknown,
  config: InternalAxiosRequestConfig,
): Promise<AxiosResponse> {
  return Promise.resolve({
    data: responseData,
    status: 200,
    statusText: 'OK',
    headers: {},
    config,
  } as AxiosResponse);
}

function routeGet(url: string, params: Record<string, unknown>): unknown {
  const page = Number(params.page ?? 0);
  const pageSize = Number(params.pageSize ?? 20);
  const state = params.state as string | undefined;

  // Extensions
  if (url === '/extensions') {
    return [{ name: 'retry', scriptUrl: '/_ext/retry/index.js', pages: [] }];
  }

  // Addons discovery — demo mode reports the addon-conditional nav items (Concurrency,
  // Rate Limits, Sagas) as OFF so the top nav stays compact in marketing screenshots.
  // The dedicated pages still render fine via direct URL — this flag only controls
  // whether they appear in the top nav (hide-on-404 pattern). push:false keeps SignalR
  // off in demo (no backend hub).
  if (url === '/addons') {
    return { concurrency: false, rateLimits: false, push: false, sagas: false, services: true };
  }

  // Dashboard
  if (url === '/status') {
    return data.getDashboardStats();
  }
  if (url === '/stats/history') {
    return data.getStatsHistoryPoints(Number(params.hours ?? 24));
  }
  if (url === '/stats/counters') {
    return data.getCountersDemo();
  }
  if (url === '/stats/counters/history') {
    return data.getCountersHistoryDemo(Number(params.hours ?? 24));
  }

  // Jobs by state
  if (url === '/jobs/enqueued') {
    return data.paginate(data.enqueuedJobs, page, pageSize);
  }
  if (url === '/jobs/processing') {
    return data.paginate(data.processingJobs, page, pageSize);
  }
  if (url === '/jobs/scheduled') {
    return data.paginate(data.scheduledJobs, page, pageSize);
  }
  if (url === '/jobs/completed') {
    return data.paginate(data.completedJobs, page, pageSize, 15692);
  }
  if (url === '/jobs/failed') {
    return data.paginate(data.failedJobs, page, pageSize);
  }
  if (url === '/jobs/failed/types') {
    return data.failedJobTypes;
  }
  if (url === '/jobs/failed/by-type') {
    const type = params.type as string;

    return data.paginate(
      data.failedJobs.filter((j) => j.type === type),
      page,
      pageSize,
    );
  }
  if (url === '/jobs/awaiting') {
    return data.paginate(data.awaitingJobs, page, pageSize);
  }
  if (url === '/jobs/deleted') {
    return data.paginate(data.deletedJobs, page, pageSize, 62);
  }

  // Messages
  if (url === '/messages') {
    return data.paginate(data.getMessages(state), page, pageSize);
  }
  if (/^\/messages\/[^/]+\/jobs\/counts$/.test(url)) {
    return data.messageJobCounts;
  }
  if (/^\/messages\/[^/]+\/jobs$/.test(url)) {
    return data.paginate(data.getMessageChildren(state), page, pageSize);
  }
  if (/^\/messages\/[^/]+$/.test(url)) {
    return data.messageDetailUnified;
  }

  // Batches
  if (url === '/batches') {
    return data.paginate(data.getBatches(state), page, pageSize);
  }
  if (/^\/batches\/[^/]+\/jobs\/counts$/.test(url)) {
    return data.batchJobCounts;
  }
  if (/^\/batches\/[^/]+\/jobs$/.test(url)) {
    return data.paginate(data.getBatchChildren(state), page, pageSize);
  }
  if (/^\/batches\/[^/]+$/.test(url)) {
    return data.batchDetailUnified;
  }

  // Recurring jobs
  if (url === '/recurring') {
    return data.paginate(data.recurringJobs, page, pageSize);
  }
  if (/^\/recurring\/\d+\/jobs$/.test(url)) {
    const id = Number(url.split('/')[2]);

    return data.paginate(data.getRecurringHistory(id), page, pageSize);
  }
  if (/^\/recurring\/\d+$/.test(url)) {
    const id = Number(url.split('/').pop());

    return data.getRecurringDetail(id);
  }

  // Servers
  if (url === '/servers') {
    return data.servers;
  }
  if (/^\/servers\/[^/]+\/tasks$/.test(url)) {
    return data.serverTasks;
  }
  if (/^\/servers\/[^/]+\/logs$/.test(url)) {
    return data.paginate(
      data.getServerLogs(params.taskName as string | undefined),
      page,
      pageSize,
    );
  }
  if (/^\/servers\/[^/]+$/.test(url)) {
    const id = url.split('/').pop()!;

    return data.servers.find((s) => s.id === id) ?? data.servers[0];
  }

  // Workers
  if (/^\/workers\/[^/]+\/logs$/.test(url)) {
    return data.paginate(data.getWorkerLogs(), page, pageSize);
  }
  if (/^\/workers\/[^/]+$/.test(url)) {
    const id = url.split('/').pop()!;

    return data.getWorkerDetail(id);
  }

  // Unified detail
  if (/^\/detail\/[^/]+$/.test(url)) {
    const id = url.split('/').pop()!;
    if (id === data.IDS.failedJob) {
      return data.jobDetailFailed;
    }
    if (id === data.IDS.completedJobWithTrace) {
      return data.jobDetailCompleted;
    }
    if (id === data.IDS.batch1) {
      return data.batchDetailUnified;
    }
    if (id === data.IDS.message1) {
      return data.messageDetailUnified;
    }
    if (id === data.IDS.processingJob) {
      return data.jobDetailProcessing;
    }

    return { ...data.jobDetailCompleted, id };
  }

  // Trace
  if (/^\/trace\/[^/]+$/.test(url)) {
    return data.traceJobs;
  }

  // Job relations (siblings, children, trace)
  if (/^\/jobs\/[^/]+\/(siblings|children|trace)$/.test(url)) {
    return data.paginate(data.completedJobs.slice(0, 5), page, pageSize);
  }

  // Fallback
  console.warn('[Demo] Unhandled GET route:', url);

  return {};
}
