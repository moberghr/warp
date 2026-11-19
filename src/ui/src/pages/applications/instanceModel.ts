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

/** Map an application-detail InstanceView onto the shared row shape. */
export function fromInstanceView(instance: InstanceView, appId: string): NormalizedInstance {
  return {
    id: instance.id,
    machineName: instance.machineName,
    isServer: instance.isServer,
    isLive: instance.isLive,
    cpuUsagePercent: instance.cpuUsagePercent,
    memoryWorkingSetBytes: instance.memoryWorkingSetBytes,
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
    version: null,
    environment: null,
    startedAt: server.startedTime,
    lastHeartbeatAt: server.lastHeartbeatTime,
    pausedAt: server.pausedAt,
    href: `/servers/${server.id}`,
  };
}
