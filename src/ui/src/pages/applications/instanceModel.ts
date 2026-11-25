import { formatBytes } from '@/utils/format';
import { isStale } from '@/lib/liveness';
import type { InstanceView } from '@/types/applications';
import type { ServerModel } from '@/types';

/** A row in the instances table — the common shape for both InstanceView and (fallback) ServerModel. */
export interface NormalizedInstance {
  id: string;
  machineName: string;
  isServer: boolean;
  isLive: boolean;
  cpuUsagePercent: number | null;
  memoryWorkingSetBytes: number | null;
  clockOffsetMs: number | null;
  version: string | null;
  environment: string | null;
  startedAt: string;
  lastHeartbeatAt: string;
  pausedAt: string | null;
  /** Where clicking the row navigates (server → the existing Server detail; non-server → instance detail). */
  href: string;
}

export function formatCpu(pct: number | null): string {
  return pct != null ? `${pct.toFixed(pct >= 10 ? 0 : 1)}%` : 'N/A';
}

export function formatMem(bytes: number | null): string {
  return bytes != null ? formatBytes(bytes) : 'N/A';
}

/** The server's default ClockSkewWarningThreshold: past it the server reports its clock as off. */
export const CLOCK_SKEW_THRESHOLD_MS = 1000;

/**
 * How far a server's clock is from the database's, as a short label ("clock 3.2 s ahead"), or null when it is
 * within the threshold or unknown. Warp corrects for the offset; the label is there so the clock gets fixed.
 * The offset is database minus server, so a negative one means the server's clock is ahead.
 */
export function clockSkewLabel(offsetMs: number | null | undefined): string | null {
  if (offsetMs == null || Math.abs(offsetMs) <= CLOCK_SKEW_THRESHOLD_MS) {
    return null;
  }

  const seconds = Math.abs(offsetMs) / 1000;
  const amount = seconds >= 60 ? `${(seconds / 60).toFixed(seconds >= 600 ? 0 : 1)} min` : `${seconds.toFixed(seconds >= 10 ? 0 : 1)} s`;

  return `clock ${amount} ${offsetMs < 0 ? 'ahead' : 'behind'}`;
}

/** Map an application-detail InstanceView onto the shared row shape. */
export function fromInstanceView(instance: InstanceView, appId: string): NormalizedInstance {
  return {
    id: instance.id,
    machineName: instance.machineName,
    isServer: instance.isServer,
    isLive: instance.isLive,
    cpuUsagePercent: instance.cpuUsagePercent,
    memoryWorkingSetBytes: instance.memoryWorkingSetBytes,
    clockOffsetMs: instance.clockOffsetMs ?? null,
    version: instance.version,
    environment: instance.environment,
    startedAt: instance.startedAt,
    lastHeartbeatAt: instance.lastHeartbeatAt,
    pausedAt: null,
    href: instance.isServer
      ? `/servers/${instance.id}`
      : `/applications/${encodeURIComponent(appId)}/instances/${encodeURIComponent(instance.id)}`,
  };
}

/** Map a (fallback) ServerModel onto the shared row shape — every server is a live-or-stale instance. */
export function fromServer(server: ServerModel): NormalizedInstance {
  // Seeds the row; StatusDot re-derives it every second from the same threshold the API uses, so
  // this and the application path no longer disagree about what silence means.
  const stale = isStale(server.lastHeartbeatTime);

  return {
    id: server.id,
    machineName: server.serverName,
    isServer: true,
    isLive: !stale,
    cpuUsagePercent: server.cpuUsagePercent,
    memoryWorkingSetBytes: server.memoryWorkingSetBytes,
    clockOffsetMs: server.clockOffsetMs ?? null,
    version: null,
    environment: null,
    startedAt: server.startedTime,
    lastHeartbeatAt: server.lastHeartbeatTime,
    pausedAt: server.pausedAt,
    href: `/servers/${server.id}`,
  };
}
