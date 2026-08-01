import axios from 'axios';
import api from './client';
import type { DashboardStatistics, JobModel, JobGroupModel, JobGroupDetailModel, RecurringJobModel, RecurringJobDetailModel, RecurringJobHistoryModel, ServerModel, ServerTaskSummary, ServerLogModel, PagedList, BulkResult, StatsHistoryPoint, CounterModel, CounterHistoryPoint, ConcurrencyLimitInfo, RateLimitInfo, TypeCountModel, WorkerDetailModel, WorkerJobLogModel, TraceJobModel, UnifiedJobDetailModel, SagaListItem, SagaDetail, SagaActivityResponse, SagaStats, AuthStatus, WarpAddonsInfo } from '@/types';
import type { AdapterListItem, AdapterDetail, AdapterCallDetail, AdapterHistoryPoint } from '@/types/adapters';
import type {
  ApplicationSummaryModel,
  ApplicationDetailModel,
  ApplicationInstanceDetailModel,
  JobExecutionMetricsModel,
  QueueMetricsModel,
} from '@/types/applications';
import type { EndpointListItem, EndpointDetail, EndpointCallDetail, EndpointHistoryPoint } from '@/types/endpoints';
import type { ClientObservabilitySummary, ClientEventPage, ClientEventDetail, ClientSession } from '@/types/client';
import type { TraceOverview } from '@/types/trace';
import type { ErrorGroupList, ErrorGroupDetail } from '@/types/issues';
import type {
  WebhookDeliveryListItem,
  WebhookDeliveryDetail,
  WebhookDeliverySummary,
  WebhookDeliveryFilter,
  WebhookGroupModel,
  WebhookGroupBy,
  WebhookDeliveryHistoryPoint,
} from '@/types/webhooks';
import type { ExtensionManifest } from '@/extensions/types';

// Dashboard
export const getStatus = () => api.get<DashboardStatistics>('/status').then(r => r.data);

// Addon discovery — one call replaces three speculative hide-on-404 probes from MainLayout.
// Always 200; per-addon booleans reflect server-side DI registration.
export const getAddons = () => api.get<WarpAddonsInfo>('/addons').then(r => r.data);

// Jobs by state
export const getEnqueuedJobs = (page = 0, pageSize = 20) =>
  api.get<PagedList<JobModel>>('/jobs/enqueued', { params: { page, pageSize } }).then(r => r.data);

export const getCompletedJobs = (page = 0, pageSize = 20) =>
  api.get<PagedList<JobModel>>('/jobs/completed', { params: { page, pageSize } }).then(r => r.data);

export const getFailedJobs = (page = 0, pageSize = 20) =>
  api.get<PagedList<JobModel>>('/jobs/failed', { params: { page, pageSize } }).then(r => r.data);

export const getFailedJobTypes = () =>
  api.get<TypeCountModel[]>('/jobs/failed/types').then(r => r.data);

export const getFailedJobsByType = (type: string, page = 0, pageSize = 20) =>
  api.get<PagedList<JobModel>>('/jobs/failed/by-type', { params: { type, page, pageSize } }).then(r => r.data);

export const getJobsByType = (type: string, page = 0, pageSize = 20, state?: string) =>
  api.get<PagedList<JobModel>>('/jobs/by-type', { params: { type, page, pageSize, state } }).then(r => r.data);

// Durable per-type / per-handler execution metrics (folded Statistic aggregates; survive Job-row cleanup).
// Optional application filter narrows to a single executor application (percentiles are 0 for that slice).
export const getJobMetrics = (application?: string) =>
  api.get<JobExecutionMetricsModel>('/jobs/metrics', { params: application ? { application } : undefined }).then(r => r.data);

export const getQueueMetrics = (application?: string) =>
  api.get<QueueMetricsModel>('/queues/metrics', { params: application ? { application } : undefined }).then(r => r.data);

export const getClientSummary = (application?: string) =>
  api.get<ClientObservabilitySummary>('/client/summary', { params: application ? { application } : undefined }).then(r => r.data);

export const getClientApplications = () =>
  api.get<string[]>('/client/applications').then(r => r.data);

export const getClientEvents = (params: { application?: string; type?: number; session?: string; page?: number; pageSize?: number }) =>
  api.get<ClientEventPage>('/client/events', { params }).then(r => r.data);

export const getClientEvent = (id: string) =>
  api.get<ClientEventDetail>(`/client/events/${id}`).then(r => r.data);

export const getClientSession = (sessionId: string) =>
  api.get<ClientSession>(`/client/sessions/${encodeURIComponent(sessionId)}`).then(r => r.data);

export const deleteFailedJobsByType = (type: string) =>
  api.post<BulkResult>('/jobs/failed/delete-by-type', null, { params: { type } }).then(r => r.data);

export const requeueFailedJobsByType = (type: string) =>
  api.post<BulkResult>('/jobs/failed/requeue-by-type', null, { params: { type } }).then(r => r.data);

export const getProcessingJobs = (page = 0, pageSize = 20) =>
  api.get<PagedList<JobModel>>('/jobs/processing', { params: { page, pageSize } }).then(r => r.data);

export const getScheduledJobs = (page = 0, pageSize = 20) =>
  api.get<PagedList<JobModel>>('/jobs/scheduled', { params: { page, pageSize } }).then(r => r.data);

export const getAwaitingJobs = (page = 0, pageSize = 20) =>
  api.get<PagedList<JobModel>>('/jobs/awaiting', { params: { page, pageSize } }).then(r => r.data);

export const getDeletedJobs = (page = 0, pageSize = 20) =>
  api.get<PagedList<JobModel>>('/jobs/deleted', { params: { page, pageSize } }).then(r => r.data);

// Job details & actions
export const getSiblingJobs = (jobId: string, page = 0, pageSize = 20) =>
  api.get<PagedList<JobModel>>(`/jobs/${jobId}/siblings`, { params: { page, pageSize } }).then(r => r.data);

export const getChildJobs = (jobId: string, page = 0, pageSize = 20) =>
  api.get<PagedList<JobModel>>(`/jobs/${jobId}/children`, { params: { page, pageSize } }).then(r => r.data);

export const getTraceJobs = (jobId: string, page = 0, pageSize = 20) =>
  api.get<PagedList<JobModel>>(`/jobs/${jobId}/trace`, { params: { page, pageSize } }).then(r => r.data);

export const getTraceTree = (traceId: string) =>
  api.get<TraceJobModel[]>(`/trace/${traceId}`).then(r => r.data);

export const getTrace = (traceId: string) =>
  api.get<TraceOverview>(`/traces/${traceId}`).then(r => r.data);

export const getDetail = (id: string) =>
  api.get<UnifiedJobDetailModel>(`/detail/${id}`).then(r => r.data);

export const requeueJob = (jobId: string) => api.post(`/jobs/${jobId}/requeue`);
export const deleteJob = (jobId: string) => api.post(`/jobs/${jobId}/delete`);

// Messages
export const getMessages = (page = 0, pageSize = 20, state?: string) =>
  api.get<PagedList<JobGroupModel>>('/messages', { params: { page, pageSize, state } }).then(r => r.data);

export const getMessageById = (messageId: string) =>
  api.get<JobGroupDetailModel>(`/messages/${messageId}`).then(r => r.data);

export const getMessageJobCounts = (messageId: string) =>
  api.get<Record<string, number>>(`/messages/${messageId}/jobs/counts`).then(r => r.data);

export const getMessageJobs = (messageId: string, page = 0, pageSize = 20, state?: string) =>
  api.get<PagedList<JobModel>>(`/messages/${messageId}/jobs`, { params: { page, pageSize, state } }).then(r => r.data);

// Recurring jobs
export const getRecurringJobs = (page = 0, pageSize = 20) =>
  api.get<PagedList<RecurringJobModel>>('/recurring', { params: { page, pageSize } }).then(r => r.data);

export const getRecurringJobById = (id: number) =>
  api.get<RecurringJobDetailModel>(`/recurring/${id}`).then(r => r.data);

export const getRecurringJobJobs = (id: number, page = 0, pageSize = 20) =>
  api.get<PagedList<RecurringJobHistoryModel>>(`/recurring/${id}/jobs`, { params: { page, pageSize } }).then(r => r.data);

export const triggerRecurringJob = (id: number) => api.post(`/recurring/${id}/trigger`);
export const enableRecurringJob = (id: number) => api.post(`/recurring/${id}/enable`);
export const disableRecurringJob = (id: number) => api.post(`/recurring/${id}/disable`);
export const deleteRecurringJob = (id: number) => api.delete(`/recurring/${id}`);

// Bulk actions
export const bulkDeleteJobs = (jobIds: string[]) =>
  api.post<BulkResult>('/jobs/bulk/delete', { jobIds }).then(r => r.data);

export const bulkRequeueJobs = (jobIds: string[]) =>
  api.post<BulkResult>('/jobs/bulk/requeue', { jobIds }).then(r => r.data);

// Batches
export const getBatches = (page = 0, pageSize = 20, state?: string) =>
  api.get<PagedList<JobGroupModel>>('/batches', { params: { page, pageSize, state } }).then(r => r.data);

export const getBatchById = (batchId: string) =>
  api.get<JobGroupDetailModel>(`/batches/${batchId}`).then(r => r.data);

export const getBatchJobCounts = (batchId: string) =>
  api.get<Record<string, number>>(`/batches/${batchId}/jobs/counts`).then(r => r.data);

export const getBatchJobs = (batchId: string, page = 0, pageSize = 20, state?: string) =>
  api.get<PagedList<JobModel>>(`/batches/${batchId}/jobs`, { params: { page, pageSize, state } }).then(r => r.data);

export const cancelBatch = (batchId: string) =>
  api.post<BulkResult>(`/batches/${batchId}/cancel`).then(r => r.data);

// Servers
export const getServers = () => api.get<ServerModel[]>('/servers').then(r => r.data);

export const getServerById = (serverId: string) =>
  api.get<ServerModel>(`/servers/${serverId}`).then(r => r.data);

export const getServerTaskSummaries = (serverId: string) =>
  api.get<ServerTaskSummary[]>(`/servers/${serverId}/tasks`).then(r => r.data);

export const getServerLogs = (serverId: string, page = 0, pageSize = 20, taskName?: string) =>
  api.get<PagedList<ServerLogModel>>(`/servers/${serverId}/logs`, { params: { page, pageSize, taskName } }).then(r => r.data);

export const getWorkerById = (workerId: string) =>
  api.get<WorkerDetailModel>(`/workers/${workerId}`).then(r => r.data);

export const getWorkerJobLogs = (workerId: string, page = 0, pageSize = 20) =>
  api.get<PagedList<WorkerJobLogModel>>(`/workers/${workerId}/logs`, { params: { page, pageSize } }).then(r => r.data);

export const pauseServer = (serverId: string) => api.post(`/servers/${serverId}/pause`);
export const resumeServer = (serverId: string) => api.post(`/servers/${serverId}/resume`);
export const pauseWorkerGroup = (groupId: string) => api.post(`/groups/${groupId}/pause`);
export const resumeWorkerGroup = (groupId: string) => api.post(`/groups/${groupId}/resume`);

// Applications — multi-app observability roster (§8.19). The renamed Servers surface: IApplicationQueryService
// is always registered by AddWarp, so these resolve in dashboard-only processes. The {id} path segment is the
// URL-safe base64 of the application name (encodeAppId, mirrors the backend UrlSafeId.Encode).
export const getApplications = () =>
  api.get<ApplicationSummaryModel[]>('/applications').then(r => r.data);

export const getApplicationDetail = (id: string) =>
  api.get<ApplicationDetailModel>(`/applications/${encodeURIComponent(id)}`).then(r => r.data);

export const getInstanceDetail = (id: string, instanceId: string) =>
  api.get<ApplicationInstanceDetailModel>(`/applications/${encodeURIComponent(id)}/instances/${encodeURIComponent(instanceId)}`).then(r => r.data);

export const getApplicationJobStats = (id: string) =>
  api.get<JobExecutionMetricsModel>(`/applications/${encodeURIComponent(id)}/jobstats`).then(r => r.data);

export const getStatsHistory = (hours = 24) =>
  api.get<StatsHistoryPoint[]>('/stats/history', { params: { hours } }).then(r => r.data);

export const getCounters = () =>
  api.get<CounterModel[]>('/stats/counters').then(r => r.data);

export const getCountersHistory = (hours = 24) =>
  api.get<CounterHistoryPoint[]>('/stats/counters/history', { params: { hours } }).then(r => r.data);

// Concurrency limits
export const listConcurrencyLimits = () =>
  api.get<ConcurrencyLimitInfo[]>('/concurrency').then(r => r.data);

export const getConcurrencyLimit = (name: string) =>
  api.get<ConcurrencyLimitInfo | null>(`/concurrency/${encodeURIComponent(name)}`).then(r => r.data);

export const upsertConcurrencyLimit = (name: string, limit: number) =>
  api.put<ConcurrencyLimitInfo>(`/concurrency/${encodeURIComponent(name)}`, { limit }).then(r => r.data);

export const deleteConcurrencyLimit = (name: string) =>
  api.delete(`/concurrency/${encodeURIComponent(name)}`).then(() => undefined);

// Rate limits
export const listRateLimits = () =>
  api.get<RateLimitInfo[]>('/ratelimits').then(r => r.data);

export const getRateLimit = (name: string) =>
  api.get<RateLimitInfo | null>(`/ratelimits/${encodeURIComponent(name)}`).then(r => r.data);

export const upsertRateLimit = (name: string, count: number, windowSeconds: number) =>
  api.put<RateLimitInfo>(`/ratelimits/${encodeURIComponent(name)}`, { count, windowSeconds }).then(r => r.data);

export const deleteRateLimit = (name: string) =>
  api.delete(`/ratelimits/${encodeURIComponent(name)}`).then(() => undefined);

// Sagas
export const listSagas = (page = 0, pageSize = 20, type?: string, key?: string) => {
  const params: Record<string, string | number> = { page, pageSize };
  if (type) params.type = type;
  if (key) params.key = key;
  return api.get<PagedList<SagaListItem>>('/sagas', { params }).then(r => r.data);
};

export const getSagaTypes = () =>
  api.get<string[]>('/sagas/types').then(r => r.data);

export const getSagaStats = () =>
  api.get<SagaStats>('/sagas/stats').then(r => r.data);

export const getSagaById = (id: string) =>
  api.get<SagaDetail>(`/sagas/${encodeURIComponent(id)}`).then(r => r.data);

export const getSagaActivity = (id: string) =>
  api.get<SagaActivityResponse>(`/sagas/${encodeURIComponent(id)}/activity`).then(r => r.data);

export const forceCompleteSaga = (id: string) =>
  api.delete(`/sagas/${encodeURIComponent(id)}`).then(() => undefined);

// Adapters — outbound service-call observability. Nav gated on addons.adapters; the
// endpoints themselves are always registered (dashboard-only processes resolve them).
export const getAdapters = () =>
  api.get<AdapterListItem[]>('/adapters').then(r => r.data);

export const getAdapterDetail = (name: string) =>
  api.get<AdapterDetail>(`/adapters/${encodeURIComponent(name)}`).then(r => r.data);

export const getAdapterCall = (name: string, id: string) =>
  api.get<AdapterCallDetail>(`/adapters/${encodeURIComponent(name)}/calls/${encodeURIComponent(id)}`).then(r => r.data);

export const getAdapterGlobalHistory = () =>
  api.get<AdapterHistoryPoint[]>('/adapters/history').then(r => r.data);

// Endpoints — inbound HTTP request observability, the mirror of adapters. Nav gated on
// addons.endpoints; the query endpoints themselves are always registered (dashboard-only
// processes resolve them).
export const getEndpoints = () =>
  api.get<EndpointListItem[]>('/endpoints').then(r => r.data);

export const getEndpointDetail = (id: string) =>
  api.get<EndpointDetail>(`/endpoints/${encodeURIComponent(id)}`).then(r => r.data);

export const getEndpointGlobalHistory = () =>
  api.get<EndpointHistoryPoint[]>('/endpoints/history').then(r => r.data);

export const getEndpointCall = (id: string, callId: string) =>
  api.get<EndpointCallDetail>(`/endpoints/${encodeURIComponent(id)}/calls/${encodeURIComponent(callId)}`).then(r => r.data);

// Webhooks — durable outbound delivery. Nav gated on addons.webhooks (IWebhookRedeliveryEnqueuer
// presence); the query endpoints themselves are always registered (dashboard-only processes resolve
// them). The list returns a paged envelope (PagedList) — page/pageSize params, items/pageCount/totalCount.
export const getWebhooks = (filter: WebhookDeliveryFilter = {}) => {
  const params: Record<string, string | number> = {};
  if (filter.status !== undefined) params.status = filter.status;
  if (filter.eventType) params.eventType = filter.eventType;
  if (filter.reference) params.reference = filter.reference;
  if (filter.group) params.group = filter.group;
  if (filter.since) params.since = filter.since;
  if (filter.until) params.until = filter.until;
  if (filter.page !== undefined) params.page = filter.page;
  if (filter.pageSize !== undefined) params.pageSize = filter.pageSize;

  return api.get<PagedList<WebhookDeliveryListItem>>('/webhooks', { params }).then(r => r.data);
};

export const getWebhookGroups = (by: WebhookGroupBy) =>
  api.get<WebhookGroupModel[]>('/webhooks/groups', { params: { by } }).then(r => r.data);

export const getWebhookDeliveryHistory = (scope: { eventType?: string; group?: string } = {}) => {
  const params: Record<string, string> = {};
  if (scope.eventType) params.eventType = scope.eventType;
  if (scope.group) params.group = scope.group;

  return api.get<WebhookDeliveryHistoryPoint[]>('/webhooks/history', { params }).then(r => r.data);
};

export const getWebhookSummary = () =>
  api.get<WebhookDeliverySummary>('/webhooks/summary').then(r => r.data);

export const getWebhookDetail = (id: string) =>
  api.get<WebhookDeliveryDetail>(`/webhooks/${encodeURIComponent(id)}`).then(r => r.data);

// Redeliver outcome, mapped from the endpoint's distinct HTTP statuses (WebhookRedeliveryResult):
// 200 Enqueued, 404 NotFound, 409 Rejected (already in flight) / Unavailable (no worker in this
// process). The two 409s share a status code and are told apart by the response message body, so the
// caller can surface a distinct, accurate toast for each.
export type RedeliverOutcome = 'enqueued' | 'not-found' | 'in-flight' | 'unavailable' | 'error';

export const redeliverWebhook = async (id: string): Promise<RedeliverOutcome> => {
  try {
    await api.post(`/webhooks/${encodeURIComponent(id)}/redeliver`);

    return 'enqueued';
  } catch (error) {
    if (!axios.isAxiosError(error) || !error.response) {
      return 'error';
    }
    if (error.response.status === 404) {
      return 'not-found';
    }
    if (error.response.status === 409) {
      const message = String((error.response.data as { message?: string } | undefined)?.message ?? '');

      // Only the Unavailable body mentions the process/worker; everything else at 409 is Rejected.
      return message.toLowerCase().includes('unavailable') ? 'unavailable' : 'in-flight';
    }

    return 'error';
  }
};

// Issues — error grouping (§8.29). Fingerprints group errors across all four sources (jobs,
// endpoints, adapters, client). Registered by AddWarp itself (like IAdapterQueryService), so
// these resolve in dashboard-only processes; the Issues nav is always shown (Core feature).
export const getIssues = (params: { source?: number; status?: number; application?: string; kind?: number; page?: number; pageSize?: number } = {}) =>
  api.get<ErrorGroupList>('/issues', { params }).then(r => r.data);

export const getIssue = (fingerprint: string) =>
  api.get<ErrorGroupDetail>(`/issues/${encodeURIComponent(fingerprint)}`).then(r => r.data);

export const setIssueStatus = (fingerprint: string, status: number) =>
  api.post(`/issues/${encodeURIComponent(fingerprint)}/status`, { status });

// Extensions
export const getExtensions = () =>
  api.get<ExtensionManifest[]>('/extensions').then(r => r.data);

// Auth — cookie-free probe so the SPA can render the login page without firing a 401 first.
export const getAuthStatus = () =>
  api.get<AuthStatus>('/auth/status').then(r => r.data);

// Background services
export {
  getBackgroundServices,
  getBackgroundService,
  getBackgroundServiceLease,
  getBackgroundServiceLogs,
} from './backgroundServices';
export type { GetBackgroundServiceLogsOptions } from './backgroundServices';
