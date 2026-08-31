export const State = {
  Enqueued: 1,
  Awaiting: 2,
  Processing: 3,
  Completed: 4,
  Failed: 5,
  Deleted: 6,
  Scheduled: 7,
} as const;
export type State = (typeof State)[keyof typeof State];

export const CancellationMode = {
  None: 0,
  Graceful: 1,
} as const;
export type CancellationMode = (typeof CancellationMode)[keyof typeof CancellationMode];

export interface DashboardStatistics {
  total: number;
  pending: number;
  scheduled: number;
  created: number;
  completed: number;
  failed: number;
  processing: number;
  servers: number;
  awaiting: number;
  deleted: number;
  batchesProcessing: number;
  batchesAwaiting: number;
  batchesDeleted: number;
  batchesCompleted: number;
  batchesFailed: number;
  messagesEnqueued: number;
  messagesProcessing: number;
  messagesCompleted: number;
  messagesFailed: number;
  messages: number;
  totalSucceeded: number;
  totalFailed: number;
  totalDeleted: number;
  totalCreated: number;
  adapterRecordsDropped: number;
  endpointRecordsDropped: number;
  clientRecordsDropped: number;
  batches: number;
  databaseConnection: string | null;
}

export interface JobModel {
  id: string;
  type: string;
  message: string;
  createTime: string;
  scheduleTime: string | null;
  processedTime: string | null;
  currentState: State;
  cancellationMode: CancellationMode;
  handlerType: string | null;
}

export interface JobLogModel {
  id: string;
  eventType: string;
  timestamp: string;
  level: string;
  message: string;
  exception: string | null;
  durationMs: number | null;
  workerId: string | null;
  name: string | null;
  value: number | null;
}


export interface JobGroupModel {
  id: string;
  kind: number;
  currentState: State;
  jobCount: number;
  createTime: string;
  type: string | null;
  payload: string | null;
  queue: string | null;
  totalJobs: number;
  completedJobs: number;
  failedJobs: number;
  continuationOptions: number | null;
}

export interface ContinuationInfo {
  id: string;
  kind: number;
  currentState: State;
  type: string | null;
  handlerType: string | null;
}

export interface JobGroupDetailModel extends JobGroupModel {
  parentJobId: string | null;
  parentJobKind: number | null;
  traceId: string | null;
  spawnedJobsCount: number;
  continuations: ContinuationInfo[];
  logs: JobLogModel[];
}

export interface UnifiedJobDetailModel {
  id: string;
  kind: number;
  type: string | null;
  currentState: State;
  createTime: string;
  cancellationMode: CancellationMode;
  message: string | null;
  handlerType: string | null;
  scheduleTime: string | null;
  retriedTimes: number;
  maxRetries: number;
  totalJobs: number;
  completedJobs: number;
  failedJobs: number;
  continuationOptions: number | null;
  queue: string | null;
  traceId: string | null;
  parentJob: ContinuationInfo | null;
  spawnedByJob: ContinuationInfo | null;
  continuations: ContinuationInfo[];
  spawnedJobs: ContinuationInfo[];
  origin: JobOriginModel | null;
  metadata: Record<string, string> | null;
  logs: JobLogModel[];
}

export interface JobOriginModel {
  method: string;
  routeTemplate: string;
  user: string | null;
  callId: string;
  endpointId: string;
}

export interface TraceJobModel {
  id: string;
  kind: number;
  type: string | null;
  handlerType: string | null;
  currentState: State;
  parentJobId: string | null;
  spawnedByJobId: string | null;
  createTime: string;
}

export interface RecurringJobModel {
  id: number;
  name: string;
  cron: string;
  type: string;
  nextExecution: string | null;
  lastExecution: string | null;
  createdAt: string;
  disabledAt: string | null;
  hasLastRun: boolean;
  lastJobId: string | null;
  // Live while the job row exists, then the outcome ExpirationCleanup stamped on the audit row.
  lastState: State | null;
  // lastState came from the stamp: the outcome is known, but there is no job detail page to open.
  lastRunCleanedUp: boolean;
}

export interface RecurringJobDetailModel extends RecurringJobModel {
  message: string | null;
  updatedAt: string | null;
}

export interface RecurringJobHistoryModel {
  jobId: string | null;
  createdAt: string;
  // Whether there is still a job detail page to link to — the outcome below can outlive it.
  jobExists: boolean;
  type: string | null;
  currentState: State | null;
  skipped: boolean;
}

export interface ServerModel {
  id: string;
  serverName: string;
  startedTime: string;
  lastHeartbeatTime: string;
  serviceCount: number;
  cpuUsagePercent: number | null;
  memoryWorkingSetBytes: number | null;
  pausedAt: string | null;
  workers: WorkerModel[];
}

export interface ServerTaskSummary {
  taskName: string;
  lastStatus: string | null;
  lastMessage: string | null;
  lastRun: string | null;
  lastDurationMs: number | null;
  intervalSeconds: number | null;
}

export interface ServerLogModel {
  id: number;
  taskName: string;
  status: string;
  message: string | null;
  timestamp: string;
  durationMs: number | null;
}

export interface WorkerModel {
  workerId: string;
  startedTime: string;
  lastHeartbeatTime: string | null;
  currentJobId: string | null;
  currentJobType: string | null;
  queues: string | null;
  pollingIntervalMs: number | null;
  workerGroupId: string | null;
  workerGroupPausedAt: string | null;
}

export interface PagedList<T> {
  totalCount: number;
  pageCount: number;
  items: T[];
}

// BatchModel and MessageModel are now unified as JobGroupModel above

export interface BulkResult {
  succeeded: number;
  skipped: number;
}

export interface WorkerDetailModel {
  workerId: string;
  startedTime: string;
  lastHeartbeatTime: string | null;
  currentJobId: string | null;
  currentJobType: string | null;
  serverId: string;
  serverName: string;
  queues: string | null;
  pollingIntervalMs: number | null;
  serverPausedAt: string | null;
  workerGroupId: string | null;
  workerGroupPausedAt: string | null;
}

export interface WorkerJobLogModel {
  id: string;
  jobId: string;
  jobType: string | null;
  eventType: string;
  timestamp: string;
  level: string;
  message: string;
  exception: string | null;
  durationMs: number | null;
}

export interface TypeCountModel {
  type: string;
  count: number;
}

export interface StatsHistoryPoint {
  hour: string;
  succeeded: number;
  failed: number;
}

export interface CounterModel {
  key: string;
  value: number;
}

export interface CounterHistoryPoint {
  hour: string;
  key: string;
  value: number;
}

export interface ConcurrencyLimitInfo {
  name: string;
  limit: number;
  updatedAt: string;
}

export interface RateLimitInfo {
  name: string;
  count: number;
  windowSeconds: number;
  updatedAt: string;
}

export interface SagaListItem {
  id: string;
  type: string;
  correlationKey: string;
  createdAt: string;
  updatedAt: string;
}

export interface SagaDetail {
  id: string;
  type: string;
  correlationKey: string;
  stateJson: string;
  createdAt: string;
  updatedAt: string;
  version: string;
}

export interface SagaActivityEntry {
  jobId: string;
  messageType: string;
  jobState: string;
  createTime: string;
  logs: JobLogModel[];
}

export interface SagaActivityResponse {
  entries: SagaActivityEntry[];
  totalInvocations: number;
  isTruncated: boolean;
}

export interface SagaStats {
  liveSagas: number;
  startedToday: number;
  completedToday: number;
}

export interface AuthStatus {
  authenticated: boolean;
}

export interface WarpAddonsInfo {
  concurrency: boolean;
  rateLimits: boolean;
  push: boolean;
  sagas: boolean;
  adapters: boolean;
  endpoints: boolean;
  client: boolean;
  webhooks: boolean;
  applications: boolean;
  slo: boolean;
}

export type {
  ServiceScope,
  BackgroundServiceStatus,
  BackgroundServiceLogSource,
  LogLevel,
  BackgroundServiceListItem,
  BackgroundServiceInstance,
  BackgroundServiceDetail,
  BackgroundServiceLeaseDto,
  BackgroundServiceLogDto,
} from './backgroundServices';
