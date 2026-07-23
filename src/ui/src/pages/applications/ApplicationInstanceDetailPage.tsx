import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import axios from 'axios';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import * as api from '@/api';
import { ApplicationInstanceEventType } from '@/types/applications';
import { StatusDot, KindBadge, formatCpu, formatMem } from './shared';

const eventLabels: Record<number, string> = {
  [ApplicationInstanceEventType.Registered]: 'Registered',
  [ApplicationInstanceEventType.HeartbeatLost]: 'Heartbeat lost',
  [ApplicationInstanceEventType.Recovered]: 'Recovered',
  [ApplicationInstanceEventType.Stopped]: 'Stopped',
  [ApplicationInstanceEventType.StaleSwept]: 'Stale swept',
};

// A light per-instance detail for non-server processes (publishers/APIs/dashboards). Server instances
// drill into the full ServerDetailPage instead; this covers the instances that have no worker groups.
export default function ApplicationInstanceDetailPage() {
  const { id: rawId, instanceId } = useParams<{ id: string; instanceId: string }>();
  const id = rawId ? decodeURIComponent(rawId) : '';

  const query = useQuery({
    queryKey: ['applications', 'instance', id, instanceId] as const,
    queryFn: () => api.getInstanceDetail(id, instanceId!),
    enabled: !!id && !!instanceId,
  });

  const backLink = `/applications/${encodeURIComponent(rawId ?? '')}`;

  const notFound =
    query.isError && axios.isAxiosError(query.error) && query.error.response?.status === 404;

  if (notFound) {
    return (
      <div>
        <div className="mb-4">
          <Link to={backLink} className="text-sm text-muted-foreground hover:underline">← {id}</Link>
        </div>
        <Card>
          <CardContent className="py-8 text-center text-muted-foreground">Instance not found.</CardContent>
        </Card>
      </div>
    );
  }

  if (query.isError) return <ErrorState message="Unable to load instance" />;
  if (query.isLoading || !query.data) return <LoadingState />;

  const { instance, recentEvents } = query.data;

  return (
    <div>
      <div className="mb-4">
        <Link to={backLink} className="text-sm text-muted-foreground hover:underline">← {instance.application}</Link>
        <div className="flex items-center gap-3 mt-1">
          <StatusDot isLive={instance.isLive} />
          <h1 className="text-2xl font-bold">{instance.machineName}</h1>
          <KindBadge isServer={instance.isServer} />
        </div>
      </div>

      <Card className="mb-4">
        <CardHeader className="pb-2"><CardTitle className="text-sm">Details</CardTitle></CardHeader>
        <CardContent className="space-y-2 text-sm">
          <div><span className="text-muted-foreground">Status:</span> {instance.isLive ? 'Live' : 'Inactive'}</div>
          <div><span className="text-muted-foreground">CPU:</span> {formatCpu(instance.cpuUsagePercent)}</div>
          <div><span className="text-muted-foreground">Memory:</span> {formatMem(instance.memoryWorkingSetBytes)}</div>
          <div><span className="text-muted-foreground">Version:</span> {instance.version ?? '—'}</div>
          <div><span className="text-muted-foreground">Environment:</span> {instance.environment ?? '—'}</div>
          <div><span className="text-muted-foreground">Started:</span> <RelativeTime date={instance.startedAt} /></div>
          <div><span className="text-muted-foreground">Heartbeat:</span> <RelativeTime date={instance.lastHeartbeatAt} /></div>
          <div><span className="text-muted-foreground">ID:</span> <span className="font-mono text-xs">{instance.id}</span></div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle className="text-base">Lifecycle events</CardTitle></CardHeader>
        <CardContent className="p-0">
          {recentEvents.length === 0 ? (
            <p className="text-sm text-muted-foreground py-6 text-center">No lifecycle events recorded.</p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="w-40">Event</TableHead>
                  <TableHead>Message</TableHead>
                  <TableHead className="w-40">Time</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {recentEvents.map((event) => (
                  <TableRow key={event.id}>
                    <TableCell className="font-medium text-sm">{eventLabels[event.eventType] ?? 'Unknown'}</TableCell>
                    <TableCell className="text-sm text-muted-foreground">{event.message ?? '—'}</TableCell>
                    <TableCell className="text-sm text-muted-foreground"><RelativeTime date={event.timestamp} /></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
