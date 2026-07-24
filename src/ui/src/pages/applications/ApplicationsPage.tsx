import { useMemo } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Card, CardContent, CardHeader } from '@/components/ui/card';
import { LoadingState, ErrorState } from '@/components/PageState';
import * as api from '@/api';
import { encodeAppId } from '@/types/applications';
import type { ApplicationSummaryModel } from '@/types/applications';
import { InstancesTable, Spread, formatCpu, formatMem, fromInstanceView, fromServer } from './shared';

// The renamed Servers surface. When the multi-app roster has data we group instances (server ∪ non-server)
// per application; otherwise we degrade to a flat server list so single-app deployments keep working.
export default function ApplicationsPage() {
  const appsQuery = useQuery({
    queryKey: ['applications', 'list'] as const,
    queryFn: () => api.getApplications(),
  });

  const apps = useMemo(() => (Array.isArray(appsQuery.data) ? appsQuery.data : []), [appsQuery.data]);

  if (appsQuery.isError) return <ErrorState message="Unable to load applications" />;
  if (appsQuery.isLoading) return <LoadingState />;

  if (apps.length === 0) {
    return <FlatServerList />;
  }

  return (
    <div>
      <h1 className="text-2xl font-bold mb-1">Applications</h1>
      <p className="text-sm text-muted-foreground mb-4">
        Every running Warp process — servers and non-server processes (publishers, APIs, dashboards) — grouped by application.
      </p>

      <div className="space-y-4">
        {apps.map((app) => (
          <ApplicationGroup key={app.name} app={app} />
        ))}
      </div>
    </div>
  );
}

// One application card: rollup header from the roster summary + its instance list from the detail read.
function ApplicationGroup({ app }: { app: ApplicationSummaryModel }) {
  const id = encodeAppId(app.name);
  const detailQuery = useQuery({
    queryKey: ['applications', 'detail', app.name] as const,
    queryFn: () => api.getApplicationDetail(id),
  });

  const instances = useMemo(
    () => (detailQuery.data?.instances ?? []).map((x) => fromInstanceView(x, id)),
    [detailQuery.data, id],
  );

  return (
    <Card>
      <CardHeader className="pb-2">
        <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1">
          <Link to={`/applications/${encodeURIComponent(id)}`} className="text-base font-semibold text-primary hover:underline">
            {app.name}
          </Link>
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground">
            <span>{app.liveInstanceCount}/{app.instanceCount} live</span>
            <span>CPU: {formatCpu(app.totalCpuUsagePercent)}</span>
            <span>Mem: {formatMem(app.totalMemoryWorkingSetBytes)}</span>
            <Spread label="Versions" values={app.versions} />
            <Spread label="Envs" values={app.environments} />
          </div>
        </div>
      </CardHeader>
      <CardContent className="p-0">
        {detailQuery.isLoading ? (
          <p className="text-sm text-muted-foreground py-4 text-center">Loading instances…</p>
        ) : (
          <InstancesTable instances={instances} />
        )}
      </CardContent>
    </Card>
  );
}

// Fallback for deployments without multi-app data (no ApplicationName set): the old flat server list.
function FlatServerList() {
  const serversQuery = useQuery({
    queryKey: ['servers', 'list'] as const,
    queryFn: () => api.getServers(),
  });

  const instances = useMemo(
    () => (serversQuery.data ?? []).map(fromServer),
    [serversQuery.data],
  );

  if (serversQuery.isError) return <ErrorState message="Unable to load servers" />;
  if (serversQuery.isLoading) return <LoadingState />;

  return (
    <div>
      <h1 className="text-2xl font-bold mb-1">Applications</h1>
      <p className="text-sm text-muted-foreground mb-4">Connected Warp servers.</p>

      {instances.length === 0 ? (
        <Card>
          <CardContent className="py-8 text-center text-muted-foreground">No servers connected</CardContent>
        </Card>
      ) : (
        <Card>
          <CardContent className="p-0">
            <InstancesTable instances={instances} />
          </CardContent>
        </Card>
      )}
    </div>
  );
}
