import api from './client';
import type { DashboardStatistics, JobModel, JobGroupModel, JobGroupDetailModel, RecurringJobModel, RecurringJobDetailModel, RecurringJobHistoryModel, ServerModel, ServerTaskSummary, ServerLogModel, PagedList, BulkResult, StatsHistoryPoint, CounterModel, CounterHistoryPoint, ConcurrencyLimitInfo, RateLimitInfo, TypeCountModel, WorkerDetailModel, WorkerJobLogModel, TraceJobModel, UnifiedJobDetailModel, SagaListItem, SagaDetail, SagaActivityResponse, SagaStats, AuthStatus, WarpAddonsInfo, WarpInfo } from '@/types';
import type { ExtensionManifest } from '@/extensions/types';

// Dashboard
export const getStatus = () => api.get<DashboardStatistics>('/status').then(r => r.data);

// Addon discovery — one call replaces three speculative hide-on-404 probes from MainLayout.
// Always 200; per-addon booleans reflect server-side DI registration.
export const getAddons = () => api.get<WarpAddonsInfo>('/addons').then(r => r.data);

// One-shot deployment metadata for the statusbar (version, provider, host, db, schema).
export const getInfo = () => api.get<WarpInfo>('/info').then(r => r.data);

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
