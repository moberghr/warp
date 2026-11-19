import { useMemo } from 'react';
import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import axios from 'axios';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { LoadingState, ErrorState } from '@/components/PageState';
import * as api from '@/api';
import { Spread, InstancesTable } from './shared';
import { fromInstanceView } from './instanceModel';
import { withPreviousVersion, type DeployRow } from './deployModel';
import {
  checkStateLabel,
  coverageLabel,
  describeDeploy,
  orderChecks,
  shouldLoadVerdict,
  verdictClass,
  verdictLabel,
  verdictPanel,
  verdictRefetchMs,
  type VerdictPanel,
} from './verdictModel';
import { errorMessage } from '@/lib/apiError';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { RelativeTime } from '@/components/RelativeTime';
import { DASHBOARD_LOCALE } from '@/utils/format';
import { INSTANCE_ROSTER_POLL_MS } from '@/lib/queryClient';
import { useLiveInstanceCount } from '@/hooks/useTicking';

export default function ApplicationDetailPage() {
  const { id: rawId } = useParams<{ id: string }>();
  const id = rawId ? decodeURIComponent(rawId) : '';

  const detailQuery = useQuery({
    queryKey: ['applications', 'detail', 'byId', id] as const,
    queryFn: () => api.getApplicationDetail(id),
    enabled: !!id,
    refetchInterval: INSTANCE_ROSTER_POLL_MS,
  });

  // Rolled-up per-type execution metrics for this application (durable, survive Job-row cleanup).
  const statsQuery = useQuery({
    queryKey: ['applications', 'jobstats', id] as const,
    queryFn: () => api.getApplicationJobStats(id),
    enabled: !!id,
  });

  // Detected deploys (#325) — first instance of each new version; kept after the instances are gone.
  const deploysQuery = useQuery({
    queryKey: ['applications', 'deploys', id] as const,
    queryFn: () => api.getApplicationDeploys(id),
    enabled: !!id,
  });

  const deploys = useMemo(() => withPreviousVersion(deploysQuery.data ?? []), [deploysQuery.data]);

  // Post-deploy verdict of the latest deploy, from the same endpoint a CI step calls. Asked once there is a deploy to
  // judge (or the deploy history failed to load, so the verdict can still answer); it polls while pending so the panel
  // settles on its own.
  const applicationName = detailQuery.data?.name;
  const verdictQuery = useQuery({
    queryKey: ['applications', 'verdict', applicationName] as const,
    queryFn: () => api.getDeployVerdict({ application: applicationName! }),
    enabled: shouldLoadVerdict(applicationName, { isError: deploysQuery.isError, count: deploys.length }),
    refetchInterval: (query) => verdictRefetchMs(query.state.data?.verdict),
  });
  const verdict = verdictPanel({ data: verdictQuery.data, isError: verdictQuery.isError, error: verdictQuery.error });

  const detail = detailQuery.data;

  const instances = useMemo(
    () => (detail?.instances ?? []).map((x) => fromInstanceView(x, id)),
    [detail, id],
  );

  // Above the early returns, as every hook must be — and re-derived on the clock, so the headline
  // count drops on its own when an instance stops checking in.
  const liveCount = useLiveInstanceCount(instances);

  const activity = useMemo(() => {
    const byType = statsQuery.data?.byType ?? [];
    const executed = byType.reduce((sum, x) => sum + x.executedCount, 0);
    const errors = byType.reduce((sum, x) => sum + x.errorCount, 0);

    return { executed, errors, errorRate: executed > 0 ? errors / executed : 0 };
  }, [statsQuery.data]);

  const notFound =
    detailQuery.isError &&
    axios.isAxiosError(detailQuery.error) &&
    detailQuery.error.response?.status === 404;

  if (notFound) {
    return (
      <div>
        <div className="mb-4">
          <Link to="/applications" className="text-sm text-muted-foreground hover:underline">← Applications</Link>
        </div>
        <Card>
          <CardContent className="py-8 text-center text-muted-foreground">
            No application <code className="font-mono text-xs">{id}</code> has any registered instances.
          </CardContent>
        </Card>
      </div>
    );
  }

  if (detailQuery.isError) return <ErrorState message="Unable to load application" />;
  if (detailQuery.isLoading || !detail) return <LoadingState />;

  return (
    <div>
      <div className="mb-4">
        <Link to="/applications" className="text-sm text-muted-foreground hover:underline">← Applications</Link>
        <h1 className="text-2xl font-bold mt-1">{detail.name}</h1>
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1 mt-1">
          <span className="text-sm text-muted-foreground">{liveCount}/{detail.instances.length} live instances</span>
          <Spread label="Versions" values={detail.versions} />
          <Spread label="Envs" values={detail.environments} />
        </div>
      </div>

      {/* Rolled-up job activity — only shown when this application has executed jobs */}
      {activity.executed > 0 && (
        <div className="grid grid-cols-2 md:grid-cols-3 gap-4 mb-4">
          <StatTile label="Jobs executed" value={activity.executed.toLocaleString(DASHBOARD_LOCALE)} />
          <StatTile
            label="Error rate"
            value={`${(Math.round(activity.errorRate * 1000) / 10).toFixed(1)}%`}
            emphasis={activity.errorRate > 0 ? 'text-destructive' : undefined}
          />
          <StatTile label="Errors" value={activity.errors.toLocaleString(DASHBOARD_LOCALE)} />
        </div>
      )}

      <Card>
        <CardHeader><CardTitle className="text-base">Instances</CardTitle></CardHeader>
        <CardContent className="p-0">
          <InstancesTable instances={instances} />
        </CardContent>
      </Card>

      {verdict.kind !== 'hidden' && <VerdictCard panel={verdict} />}

      <Card className="mt-4">
        <CardHeader><CardTitle className="text-base">Deploys</CardTitle></CardHeader>
        <CardContent className="p-0">
          {deploysQuery.isError ? (
            <p role="alert" className="text-sm text-destructive py-4 text-center">
              Unable to load the deploys: {errorMessage(deploysQuery.error, 'the request failed.')}
            </p>
          ) : deploysQuery.isLoading ? (
            <p className="text-sm text-muted-foreground py-4 text-center">Loading…</p>
          ) : (
            <DeploysTable deploys={deploys} />
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function DeploysTable({ deploys }: { deploys: DeployRow[] }) {
  if (deploys.length === 0) {
    return (
      <p className="text-sm text-muted-foreground py-4 text-center">
        No deploys recorded. Set <code className="font-mono text-xs">ApplicationVersion</code> to track them.
      </p>
    );
  }

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>Version</TableHead>
          <TableHead className="hidden sm:table-cell w-32">Environment</TableHead>
          <TableHead className="hidden md:table-cell">First instance</TableHead>
          <TableHead className="w-40">Deployed</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {deploys.map((x) => (
          <TableRow key={x.id}>
            <TableCell className="font-mono text-sm">
              {x.previousVersion && <span className="text-muted-foreground">{x.previousVersion} &rarr; </span>}
              {x.version}
            </TableCell>
            <TableCell className="hidden sm:table-cell text-sm text-muted-foreground">{x.environment ?? '—'}</TableCell>
            <TableCell className="hidden md:table-cell text-sm text-muted-foreground">{x.instanceName ?? x.instanceId}</TableCell>
            <TableCell className="text-sm text-muted-foreground" title={new Date(x.firstSeenAt).toLocaleString(DASHBOARD_LOCALE)}>
              <RelativeTime date={x.firstSeenAt} />
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function VerdictCard({ panel }: { panel: Exclude<VerdictPanel, { kind: 'hidden' }> }) {
  if (panel.kind !== 'verdict') {
    return (
      <Card className="mt-4">
        <CardHeader>
          <CardTitle className="text-base">Latest deploy verdict</CardTitle>
          {panel.kind === 'noMatch' ? (
            <p className="text-sm text-muted-foreground">No matching deploy: none of this application's recorded deploys matches.</p>
          ) : (
            <p role="alert" className="text-sm text-destructive">Unable to load the verdict: {panel.message}</p>
          )}
        </CardHeader>
      </Card>
    );
  }

  const { verdict } = panel;
  const checks = orderChecks(verdict.checks);

  return (
    <Card className="mt-4">
      <CardHeader>
        <CardTitle className="text-base flex flex-wrap items-center gap-2">
          Latest deploy verdict
          <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${verdictClass(verdict.verdict)}`}>
            {verdictLabel(verdict.verdict)}
          </span>
          <span className="text-xs font-normal text-muted-foreground">{coverageLabel(verdict)}</span>
        </CardTitle>
        {panel.staleError && (
          <p role="alert" className="text-sm text-destructive">Showing the last verdict; refreshing it failed: {panel.staleError}</p>
        )}
        <p className="text-sm text-muted-foreground">
          <span className="font-mono">{describeDeploy(verdict.deploy)}</span>
          {' · deployed '}
          <RelativeTime date={verdict.deploy.firstSeenAt} />
          {' · '}
          {verdict.summary}
        </p>
      </CardHeader>
      {checks.length > 0 && (
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Check</TableHead>
                <TableHead className="w-32">State</TableHead>
                <TableHead className="hidden md:table-cell">Reason</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {checks.map((x) => (
                <TableRow key={`${x.source}-${x.id}`}>
                  <TableCell className="text-sm">
                    <span className="text-muted-foreground text-xs uppercase mr-2">{x.source === 'tile' ? 'Tile' : 'SLO'}</span>
                    {x.name}
                  </TableCell>
                  <TableCell className="text-sm">{checkStateLabel(x.state)}</TableCell>
                  <TableCell className="hidden md:table-cell text-sm text-muted-foreground">{x.reason}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      )}
    </Card>
  );
}

function StatTile({ label, value, emphasis }: { label: string; value: string; emphasis?: string }) {
  return (
    <Card>
      <CardContent className="p-4">
        <div className="text-sm text-muted-foreground">{label}</div>
        <div className={`text-2xl font-bold ${emphasis ?? ''}`}>{value}</div>
      </CardContent>
    </Card>
  );
}
