// DTOs for the Applications feature (multi-app observability, §8.19). These mirror the backend models
// in Warp.Core/Models/ApplicationSummaryModel.cs, ApplicationDetailModel.cs, InstanceView.cs and
// JobExecutionMetricsModel.cs, serialized camelCase.

import { encodeUrlSafeId } from '@/lib/urlSafeId';

// ApplicationInstanceEventType — numeric on the wire (§8.11, starts at 1), matching
// Warp.Core.Enums.ApplicationInstanceEventType.
export const ApplicationInstanceEventType = {
  Registered: 1,
  HeartbeatLost: 2,
  Recovered: 3,
  Stopped: 4,
  StaleSwept: 5,
} as const;
export type ApplicationInstanceEventType =
  (typeof ApplicationInstanceEventType)[keyof typeof ApplicationInstanceEventType];

/** One row on the Applications roster: all instances of a single logical application rolled up. */
export interface ApplicationSummaryModel {
  name: string;
  instanceCount: number;
  liveInstanceCount: number;
  /** Sum of CpuUsagePercent across live instances that report it; null when none do. */
  totalCpuUsagePercent: number | null;
  /** Sum of MemoryWorkingSetBytes across live instances that report it; null when none do. */
  totalMemoryWorkingSetBytes: number | null;
  versions: string[];
  environments: string[];
}

/** A single running Warp process — server or non-server — merged into one shape for the roster. */
export interface InstanceView {
  /** A Server.Id when isServer, else an ApplicationInstance.Id. */
  id: string;
  application: string;
  machineName: string;
  startedAt: string;
  lastHeartbeatAt: string;
  cpuUsagePercent: number | null;
  memoryWorkingSetBytes: number | null;
  /** True for a Server row (job worker / server-task host); false for a non-server instance. */
  isServer: boolean;
  version: string | null;
  environment: string | null;
  /** True when the last heartbeat is within the liveness window of "now". */
  isLive: boolean;
}

/** The application detail payload: unified instance list plus version/environment spread. */
export interface ApplicationDetailModel {
  name: string;
  instances: InstanceView[];
  versions: string[];
  environments: string[];
}

/** One lifecycle event row (register / heartbeat-lost / recovered / stopped / stale-swept). */
export interface ApplicationInstanceLogModel {
  id: string;
  instanceId: string;
  applicationName: string;
  timestamp: string;
  eventType: ApplicationInstanceEventType;
  message: string | null;
}

/** A single instance's detail: its unified view plus its most-recent lifecycle events (newest first). */
export interface ApplicationInstanceDetailModel {
  instance: InstanceView;
  recentEvents: ApplicationInstanceLogModel[];
}

/**
 * One detected deploy (#325): the first instance of an application to register with a version not seen
 * before for that application + environment. Mirrors Warp.Core/Models/ApplicationDeployModel.cs.
 */
export interface ApplicationDeployModel {
  id: string;
  application: string;
  version: string;
  /** Null when the reporting instance set no ApplicationEnvironment. */
  environment: string | null;
  firstSeenAt: string;
  /** The first instance seen with this version (a server or a non-server process) — it may since be gone. */
  instanceId: string;
  instanceName: string | null;
}

/** Post-deploy verdict values (GET /applications/verdict). Mirrors Warp.Core/Models/DeployVerdictModels.cs. */
export type DeployVerdictValue = 'pass' | 'warn' | 'fail' | 'pending' | 'noData';

export type DeployVerdictCheckState = 'healthy' | 'warning' | 'breaching' | 'acknowledged' | 'noData';

export interface DeployVerdictCheck {
  source: 'tile' | 'slo';
  id: number;
  name: string;
  /** Null when the tile or SLO covers every application. */
  application: string | null;
  state: DeployVerdictCheckState;
  /** For an acknowledged tile, the state its rule gives. */
  unacknowledgedState: DeployVerdictCheckState | null;
  acknowledged: boolean;
  /** The tile's value, or the SLO's attainment (a share for rate kinds and metric latency / gauge objectives). */
  value: number | null;
  baseline: number | null;
  /** On `value`'s scale: for a metric latency or gauge SLO, the required share (`percentile / 100`). */
  target: number | null;
  /** A metric latency or gauge SLO's threshold (instrument unit); null for every other check. */
  threshold: number | null;
  budgetRemaining: number | null;
  reason: string;
}

export interface DeployVerdictModel {
  verdict: DeployVerdictValue;
  summary: string;
  /** How many of `checks` had data (any state but noData); a pass resting on few checks is weak evidence. */
  checksWithData: number;
  /** The number of `checks`. */
  checksTotal: number;
  /** The board whose tiles were judged (its stored name), or null when every board's tiles counted. */
  board: string | null;
  deploy: {
    application: string;
    version: string;
    environment: string | null;
    firstSeenAt: string;
    instanceName: string | null;
    previousVersion: string | null;
    supersededBy: string | null;
  };
  window: {
    from: string;
    to: string;
    minWindowSeconds: number;
    settlesAt: string;
    settled: boolean;
  };
  checks: DeployVerdictCheck[];
}

/** Execution metrics for a single job type or handler, folded from the durable Statistic aggregates. */
export interface JobExecutionStatModel {
  /** The job Type or HandlerType assembly-qualified name this row aggregates. */
  identifier: string;
  executedCount: number;
  errorCount: number;
  errorRate: number;
  avgDurationMs: number;
  /** Populated for the app-agnostic read; 0 for a per-application slice. */
  p95DurationMs: number;
  p99DurationMs: number;
}

/** Per-job-TYPE and per-HANDLER execution metrics (survive Job-row cleanup). */
export interface JobExecutionMetricsModel {
  byType: JobExecutionStatModel[];
  byHandler: JobExecutionStatModel[];
}

/**
 * URL-safe base64 of an application name for the detail route segment. One codec shared with the
 * other name-addressed routes (endpoints, recurring jobs) — see lib/urlSafeId.
 */
export function encodeAppId(name: string): string {
  return encodeUrlSafeId(name);
}

