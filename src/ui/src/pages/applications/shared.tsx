import { Link } from 'react-router-dom';
import { Badge } from '@/components/ui/badge';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { RelativeTime } from '@/components/RelativeTime';
import { formatBytes } from '@/utils/format';
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

/** Green when live, amber when paused, red when stale/dead. */
export function StatusDot({ isLive, pausedAt }: { isLive: boolean; pausedAt?: string | null }) {
  const color = pausedAt ? 'bg-amber-500' : isLive ? 'bg-green-500' : 'bg-red-500';

  return <span className={`inline-block w-2 h-2 rounded-full ${color}`} />;
}

/** Server vs non-server kind badge. */
export function KindBadge({ isServer }: { isServer: boolean }) {
  return isServer ? (
    <Badge variant="outline" className="text-xs">Server</Badge>
  ) : (
    <Badge variant="outline" className="text-xs text-muted-foreground">Process</Badge>
  );
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
  const stale = Date.now() - new Date(server.lastHeartbeatTime).getTime() > 30_000;

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

export function InstancesTable({ instances }: { instances: NormalizedInstance[] }) {
  if (instances.length === 0) {
    return <p className="text-sm text-muted-foreground py-4 text-center">No instances</p>;
  }

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>Instance</TableHead>
          <TableHead className="w-24">Kind</TableHead>
          <TableHead className="text-right w-20">CPU</TableHead>
          <TableHead className="text-right w-24">Memory</TableHead>
          <TableHead className="hidden sm:table-cell w-28">Version</TableHead>
          <TableHead className="hidden md:table-cell w-28">Environment</TableHead>
          <TableHead className="hidden lg:table-cell w-40">Started</TableHead>
          <TableHead className="w-40">Heartbeat</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {instances.map((x) => (
          <TableRow key={x.id}>
            <TableCell>
              <Link to={x.href} className="flex items-center gap-2 text-primary hover:underline font-medium">
                <StatusDot isLive={x.isLive} pausedAt={x.pausedAt} />
                {x.machineName}
                {x.pausedAt && <Badge variant="outline" className="text-amber-600 border-amber-300 text-xs">Paused</Badge>}
              </Link>
            </TableCell>
            <TableCell><KindBadge isServer={x.isServer} /></TableCell>
            <TableCell className="text-right tabular-nums text-sm">{formatCpu(x.cpuUsagePercent)}</TableCell>
            <TableCell className="text-right tabular-nums text-sm">{formatMem(x.memoryWorkingSetBytes)}</TableCell>
            <TableCell className="hidden sm:table-cell text-sm text-muted-foreground">{x.version ?? '—'}</TableCell>
            <TableCell className="hidden md:table-cell text-sm text-muted-foreground">{x.environment ?? '—'}</TableCell>
            <TableCell className="hidden lg:table-cell text-sm text-muted-foreground"><RelativeTime date={x.startedAt} /></TableCell>
            <TableCell className="text-sm text-muted-foreground"><RelativeTime date={x.lastHeartbeatAt} /></TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

/** A comma-joined spread of distinct version/environment values (or a placeholder when empty). */
export function Spread({ label, values }: { label: string; values: string[] }) {
  if (values.length === 0) {
    return null;
  }

  return (
    <span className="text-xs text-muted-foreground">
      {label}: <span className="font-mono">{values.join(', ')}</span>
    </span>
  );
}
