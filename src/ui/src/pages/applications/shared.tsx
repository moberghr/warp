import { Link } from 'react-router-dom';
import { Badge } from '@/components/ui/badge';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { RelativeTime } from '@/components/RelativeTime';
import { statusDotColor } from '@/lib/liveness';
import { useInstanceLive } from '@/hooks/useTicking';
import { clockSkewLabel, formatCpu, formatMem, type NormalizedInstance } from './instanceModel';

/**
 * Green when live, amber when paused, red when stale/dead — re-derived every second from the heartbeat
 * so a process that dies while the page is open goes red on its own, instead of holding the answer the
 * API gave when the row was fetched.
 */
export function StatusDot({
  isLive,
  pausedAt,
  lastHeartbeatAt,
}: {
  isLive: boolean;
  pausedAt?: string | null;
  lastHeartbeatAt: string;
}) {
  const live = useInstanceLive(lastHeartbeatAt, isLive);

  return <span className={`inline-block w-2 h-2 rounded-full ${statusDotColor(!live, pausedAt ?? null)}`} />;
}

/** Server vs non-server kind badge. */
export function KindBadge({ isServer }: { isServer: boolean }) {
  return isServer ? (
    <Badge variant="outline" className="text-xs">Server</Badge>
  ) : (
    <Badge variant="outline" className="text-xs text-muted-foreground">Process</Badge>
  );
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
                <StatusDot isLive={x.isLive} pausedAt={x.pausedAt} lastHeartbeatAt={x.lastHeartbeatAt} />
                {x.machineName}
                {x.pausedAt && <Badge variant="outline" className="text-amber-600 border-amber-300 text-xs">Paused</Badge>}
                {clockSkewLabel(x.clockOffsetMs) && (
                  <Badge variant="outline" className="text-amber-600 border-amber-300 text-xs" title="This server's clock is off from the database's. Warp corrects for it; fix the clock (NTP).">
                    {clockSkewLabel(x.clockOffsetMs)}
                  </Badge>
                )}
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
