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

