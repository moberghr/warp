import { useMemo } from 'react';
import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import axios from 'axios';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { LoadingState, ErrorState } from '@/components/PageState';
import * as api from '@/api';
import { Spread, InstancesTable, fromInstanceView } from './shared';

export default function ApplicationDetailPage() {
  const { id: rawId } = useParams<{ id: string }>();
  const id = rawId ? decodeURIComponent(rawId) : '';

  const detailQuery = useQuery({
    queryKey: ['applications', 'detail', 'byId', id] as const,
    queryFn: () => api.getApplicationDetail(id),
    enabled: !!id,
  });

  // Rolled-up per-type execution metrics for this application (durable, survive Job-row cleanup).
  const statsQuery = useQuery({
    queryKey: ['applications', 'jobstats', id] as const,
    queryFn: () => api.getApplicationJobStats(id),
    enabled: !!id,
  });

  const detail = detailQuery.data;

  const instances = useMemo(
    () => (detail?.instances ?? []).map((x) => fromInstanceView(x, id)),
    [detail, id],
  );

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

  const liveCount = detail.instances.filter((x) => x.isLive).length;

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
          <StatTile label="Jobs executed" value={activity.executed.toLocaleString()} />
          <StatTile
            label="Error rate"
            value={`${(Math.round(activity.errorRate * 1000) / 10).toFixed(1)}%`}
            emphasis={activity.errorRate > 0 ? 'text-destructive' : undefined}
          />
          <StatTile label="Errors" value={activity.errors.toLocaleString()} />
        </div>
      )}

      <Card>
        <CardHeader><CardTitle className="text-base">Instances</CardTitle></CardHeader>
        <CardContent className="p-0">
          <InstancesTable instances={instances} />
        </CardContent>
      </Card>
    </div>
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
