import type { InternalAxiosRequestConfig, AxiosResponse } from 'axios';
import * as data from './data';
import { demoAdapters, demoAdapterDetails, demoAdapterCalls } from './data/adapters';
import { demoWebhooks, demoWebhookDetails, demoWebhookSummary } from './data/webhooks';
import {
  demoApplications,
  demoApplicationDetails,
  demoApplicationJobStats,
  demoInstanceDetails,
  demoJobMetrics,
  decodeAppId,
} from './data/applications';
import { WebhookDeliveryStatus } from '@/types/webhooks';
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

    // Adapters: /addons reports adapters:true in demo mode (see routeGet), so the nav is visible
    // and these routes serve the static fixtures for the screenshots.
    const adapterResult = routeAdapters(method, url, config);
    if (adapterResult !== undefined) {
      return adapterResult;
    }

    // Webhooks: /addons reports webhooks:true in demo mode (see routeGet), so the nav is visible
    // and these routes serve the static fixtures for the screenshots.
    const webhookResult = routeWebhooks(method, url, config);
    if (webhookResult !== undefined) {
      return webhookResult;
    }

    // Applications: the renamed Servers surface (§8.19). /addons reports applications:true in demo
    // mode (see routeGet); these routes serve the static roster/detail/instance/jobstats fixtures.
    const applicationResult = routeApplications(method, url, config);
    if (applicationResult !== undefined) {
      return applicationResult;
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

function routeAdapters(
  method: string,
  url: string,
  config: InternalAxiosRequestConfig,
): Promise<AxiosResponse> | undefined {
  if (!url.startsWith('/adapters')) {
    return undefined;
  }

  // GET /adapters — list
  if (method === 'get' && (url === '/adapters' || url.startsWith('/adapters?'))) {
    return resolve(demoAdapters, config);
  }

  // GET /adapters/history — global overview, aggregated across every demo adapter's per-hour history.
  if (method === 'get' && url.startsWith('/adapters/history')) {
    const map = new Map<string, { hour: string; calls: number; errors: number; durSum: number }>();
    for (const detail of Object.values(demoAdapterDetails)) {
      for (const p of detail.history) {
        const g = map.get(p.hour) ?? { hour: p.hour, calls: 0, errors: 0, durSum: 0 };
        g.calls += p.calls;
        g.errors += p.errors;
        g.durSum += p.avgDurationMs * p.calls;
        map.set(p.hour, g);
      }
    }
    const points = [...map.values()]
      .sort((a, b) => (a.hour < b.hour ? -1 : 1))
      .map((g) => ({
        hour: g.hour,
        calls: g.calls,
        errors: g.errors,
        errorRate: g.calls === 0 ? 0 : g.errors / g.calls,
        avgDurationMs: g.calls === 0 ? 0 : g.durSum / g.calls,
      }));

    return resolve(points, config);
  }

  // GET /adapters/{name}/calls/{id} — call detail (checked before the {name} detail route)
  const callMatch = url.match(/^\/adapters\/([^/?]+)\/calls\/([^/?]+)$/);
  if (method === 'get' && callMatch) {
    const id = decodeURIComponent(callMatch[2]);
    const call = demoAdapterCalls[id];
    if (!call) {
      return Promise.reject({ response: { status: 404, statusText: 'Not Found', data: {}, headers: {}, config } });
    }

    return resolve(call, config);
  }

  // GET /adapters/{name} — detail
  const detailMatch = url.match(/^\/adapters\/([^/?]+)$/);
  if (method === 'get' && detailMatch) {
    const name = decodeURIComponent(detailMatch[1]);
    const detail = demoAdapterDetails[name];
    if (!detail) {
      return Promise.reject({ response: { status: 404, statusText: 'Not Found', data: {}, headers: {}, config } });
    }

    return resolve(detail, config);
  }

  return undefined;
}

function routeWebhooks(
  method: string,
  url: string,
  config: InternalAxiosRequestConfig,
): Promise<AxiosResponse> | undefined {
  if (!url.startsWith('/webhooks')) {
    return undefined;
  }

  const params: Record<string, unknown> = config.params ?? {};

  // GET /webhooks/summary — tile counts (checked before the {id} detail route).
  if (method === 'get' && url === '/webhooks/summary') {
    return resolve(demoWebhookSummary, config);
  }

  // GET /webhooks/groups?by=type|endpoint — grouped counts (checked before the list + {id} routes).
  if (method === 'get' && url.startsWith('/webhooks/groups')) {
    const by = String(params.by ?? 'type');
    const keyOf = (x: (typeof demoWebhooks)[number]) => (by === 'endpoint' ? (x.groupName ?? x.url) : x.eventType);
    const map = new Map<string, { key: string; total: number; pending: number; delivered: number; exhausted: number; lastActivityAt: string }>();
    for (const x of demoWebhooks) {
      const key = keyOf(x);
      const g = map.get(key) ?? { key, total: 0, pending: 0, delivered: 0, exhausted: 0, lastActivityAt: x.createdAt };
      g.total += 1;
      if (x.status === WebhookDeliveryStatus.Pending) g.pending += 1;
      if (x.status === WebhookDeliveryStatus.Delivered) g.delivered += 1;
      if (x.status === WebhookDeliveryStatus.Exhausted) g.exhausted += 1;
      if (x.createdAt > g.lastActivityAt) g.lastActivityAt = x.createdAt;
      map.set(key, g);
    }
    const groups = [...map.values()].sort((a, b) => (a.lastActivityAt < b.lastActivityAt ? 1 : -1));

    return resolve(groups, config);
  }

  // GET /webhooks/history?eventType=&group= — hourly delivery stats by status (global or scoped).
  if (method === 'get' && url.startsWith('/webhooks/history')) {
    const eventType = params.eventType ? String(params.eventType) : undefined;
    const group = params.group ? String(params.group) : undefined;
    const scoped = demoWebhooks.filter((x) => {
      if (eventType && x.eventType !== eventType) return false;
      if (group && (x.groupName ?? x.url) !== group) return false;

      return true;
    });
    const map = new Map<string, { hour: string; delivered: number; exhausted: number; pending: number; total: number }>();
    for (const x of scoped) {
      const hour = `${x.createdAt.slice(0, 13)}:00:00.000Z`;
      const g = map.get(hour) ?? { hour, delivered: 0, exhausted: 0, pending: 0, total: 0 };
      g.total += 1;
      if (x.status === WebhookDeliveryStatus.Delivered) g.delivered += 1;
      if (x.status === WebhookDeliveryStatus.Exhausted) g.exhausted += 1;
      if (x.status === WebhookDeliveryStatus.Pending) g.pending += 1;
      map.set(hour, g);
    }
    const points = [...map.values()].sort((a, b) => (a.hour < b.hour ? -1 : 1));

    return resolve(points, config);
  }

  // GET /webhooks — filtered, paged list. Server does the real filtering; mirror
  // status/event/endpoint/reference + paging here so the demo filters feel live.
  if (method === 'get' && (url === '/webhooks' || url.startsWith('/webhooks?'))) {
    const status = params.status !== undefined ? Number(params.status) : undefined;
    const eventType = params.eventType ? String(params.eventType).toLowerCase() : undefined;
    const group = params.group ? String(params.group) : undefined;
    const reference = params.reference ? String(params.reference).toLowerCase() : undefined;
    const page = params.page !== undefined ? Number(params.page) : 0;
    const pageSize = params.pageSize !== undefined ? Number(params.pageSize) : 20;
    const filtered = demoWebhooks.filter((x) => {
      if (status !== undefined && x.status !== (status as WebhookDeliveryStatus)) {
        return false;
      }
      if (eventType && !x.eventType.toLowerCase().includes(eventType)) {
        return false;
      }
      if (group && (x.groupName ?? x.url) !== group) {
        return false;
      }
      if (reference && !(x.reference ?? '').toLowerCase().includes(reference)) {
        return false;
      }

      return true;
    });

    const pageCount = Math.max(1, Math.ceil(filtered.length / pageSize));
    const items = filtered.slice(page * pageSize, page * pageSize + pageSize);

    return resolve({ totalCount: filtered.length, pageCount, items }, config);
  }

  // POST /webhooks/{id}/redeliver — mirrors the real endpoint's status codes (checked before the {id}
  // detail route). A settled delivery redelivers (200); a Pending one already owns a live executor job,
  // so the server rejects it with 409 (Rejected). Unknown id is 404. Deterministic off the fixed fixture
  // status. Unavailable (no worker) can't occur in the single-process demo, so it is not modelled.
  const redeliverMatch = url.match(/^\/webhooks\/([^/?]+)\/redeliver$/);
  if (method === 'post' && redeliverMatch) {
    const id = decodeURIComponent(redeliverMatch[1]);
    const detail = demoWebhookDetails[id];
    if (!detail) {
      return Promise.reject({ response: { status: 404, statusText: 'Not Found', data: {}, headers: {}, config } });
    }
    if (detail.status === WebhookDeliveryStatus.Pending) {
      return Promise.reject({
        response: {
          status: 409,
          statusText: 'Conflict',
          data: { message: 'Delivery is already pending — it already has a live executor job.' },
          headers: {},
          config,
        },
      });
    }

    return resolve({}, config);
  }

  // GET /webhooks/{id} — detail.
  const detailMatch = url.match(/^\/webhooks\/([^/?]+)$/);
  if (method === 'get' && detailMatch) {
    const id = decodeURIComponent(detailMatch[1]);
    const detail = demoWebhookDetails[id];
    if (!detail) {
      return Promise.reject({ response: { status: 404, statusText: 'Not Found', data: {}, headers: {}, config } });
    }

    return resolve(detail, config);
  }

  return undefined;
}

function routeApplications(
  method: string,
  url: string,
  config: InternalAxiosRequestConfig,
): Promise<AxiosResponse> | undefined {
  if (!url.startsWith('/applications')) {
    return undefined;
  }

  // GET /applications — roster
  if (method === 'get' && (url === '/applications' || url.startsWith('/applications?'))) {
    return resolve(demoApplications, config);
  }

  // GET /applications/{id}/instances/{instanceId} — single-instance detail (checked before the {id} route).
  const instanceMatch = url.match(/^\/applications\/([^/?]+)\/instances\/([^/?]+)$/);
  if (method === 'get' && instanceMatch) {
    const instanceId = decodeURIComponent(instanceMatch[2]);
    const detail = demoInstanceDetails[instanceId];
    if (!detail) {
      return Promise.reject({ response: { status: 404, statusText: 'Not Found', data: {}, headers: {}, config } });
    }

    return resolve(detail, config);
  }

  // GET /applications/{id}/jobstats — per-application execution metrics (checked before the {id} route).
  const jobStatsMatch = url.match(/^\/applications\/([^/?]+)\/jobstats$/);
  if (method === 'get' && jobStatsMatch) {
    const name = decodeAppId(decodeURIComponent(jobStatsMatch[1]));

    return resolve(demoApplicationJobStats[name] ?? { byType: [], byHandler: [] }, config);
  }

  // GET /applications/{id} — application detail (id is the URL-safe base64 of the name).
  const detailMatch = url.match(/^\/applications\/([^/?]+)$/);
  if (method === 'get' && detailMatch) {
    const name = decodeAppId(decodeURIComponent(detailMatch[1]));
    const detail = demoApplicationDetails[name];
    if (!detail) {
      return Promise.reject({ response: { status: 404, statusText: 'Not Found', data: {}, headers: {}, config } });
    }

    return resolve(detail, config);
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
    return { concurrency: false, rateLimits: false, push: false, sagas: false, services: true, adapters: true, endpoints: true, client: true, webhooks: true, applications: true };
  }

  // Queue metrics (§8.26) — the Queues page (always-on nav).
  if (url === '/queues/metrics') {
    return data.getQueueMetricsDemo();
  }

  // Client (browser) observability (§8.27).
  if (url === '/client/summary') {
    return data.getClientSummaryDemo();
  }
  if (url === '/client/applications') {
    return ['warp-demo-spa'];
  }
  // Detail must be matched before the list — both start with /client/events.
  const clientEventMatch = url.match(/^\/client\/events\/([^/?]+)$/);
  if (clientEventMatch) {
    return data.getClientEventDetailDemo(decodeURIComponent(clientEventMatch[1]));
  }
  if (url.startsWith('/client/events')) {
    return data.getClientEventsDemo();
  }
  const clientSessionMatch = url.match(/^\/client\/sessions\/([^/?]+)$/);
  if (clientSessionMatch) {
    return data.getClientSessionDemo(decodeURIComponent(clientSessionMatch[1]));
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
  // Every job of a given type across all states — the JobsByTypePage list (below its metrics header).
  if (url === '/jobs/by-type') {
    const type = params.type as string;
    const pool = [
      ...data.completedJobs,
      ...data.failedJobs,
      ...data.processingJobs,
      ...data.enqueuedJobs,
    ];

    return data.paginate(
      pool.filter((j) => j.type === type),
      page,
      pageSize,
    );
  }
  // Durable per-type / per-handler execution metrics (JobsByTypePage header; app detail composes
  // its own per-application slice via /applications/{id}/jobstats).
  if (url === '/jobs/metrics') {
    return demoJobMetrics;
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

  // Unified trace overview (waterfall) — matched before /trace/ (distinct path).
  if (/^\/traces\/[^/]+$/.test(url)) {
    return data.getTraceOverviewDemo();
  }

  // Trace (job DAG)
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
