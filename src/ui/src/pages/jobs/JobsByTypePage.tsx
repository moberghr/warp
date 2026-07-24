import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import type { ColumnDef } from '@tanstack/react-table';
import { StateBadge } from '@/components/StateBadge';
import { Card, CardContent } from '@/components/ui/card';
import { shortType, shortId } from '@/utils/format';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { DataTable } from '@/components/DataTable';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { useJobsByType } from '@/api/hooks/useJobs';
import * as api from '@/api';
import type { JobModel } from '@/types';
import type { JobExecutionStatModel } from '@/types/applications';

// #1: the destination for a clickable job type — every job of a given type across all states.
export default function JobsByTypePage() {
  const { type: rawType } = useParams<{ type: string }>();
  const type = useMemo(() => {
    if (!rawType) return '';
    // A hand-crafted URL with a stray "%" throws in decodeURIComponent — fall back to the raw value.
    try { return decodeURIComponent(rawType); } catch { return rawType; }
  }, [rawType]);
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePersistedPageSize();
  const [dimension, setDimension] = useState<'type' | 'handler'>('type');

  // Reset paging when navigating between types (the route component instance is reused).
  useEffect(() => { setPage(0); }, [type]);

  const { data, isLoading, isError } = useJobsByType(type, page, pageSize);

  // Durable execution metrics (folded Statistic aggregates) for the header — these survive Job-row cleanup,
  // so the numbers stay meaningful even after the live rows below have been expired.
  const metricsQuery = useQuery({
    queryKey: ['jobs', 'metrics', 'global'] as const,
    queryFn: () => api.getJobMetrics(),
  });

  // Look this exact identifier up in the chosen dimension. Self-handling jobs match in both; a routed
  // message matches its type in byType and its handler in byHandler (identifiers differ), so the toggle
  // gracefully shows "no metrics" when the current type isn't present in the selected view.
  const stat = useMemo(() => {
    const rows = dimension === 'type' ? metricsQuery.data?.byType : metricsQuery.data?.byHandler;

    return (rows ?? []).find((x) => x.identifier === type) ?? null;
  }, [metricsQuery.data, dimension, type]);

  const columns = useMemo<ColumnDef<JobModel>[]>(
    () => [
      {
        accessorKey: 'id',
        header: 'ID',
        cell: ({ row }) => (
          <Link to={`/detail/${row.original.id}`} className="font-mono text-xs text-primary hover:underline">
            {shortId(row.original.id)}
          </Link>
        ),
        meta: { headerClassName: 'w-[100px]' },
      },
      {
        accessorKey: 'currentState',
        header: 'State',
        cell: ({ row }) => <StateBadge state={row.original.currentState} cancellationMode={row.original.cancellationMode} />,
        meta: { headerClassName: 'w-[110px] text-right', cellClassName: 'text-right' },
      },
      {
        accessorKey: 'createTime',
        header: 'Created',
        cell: ({ row }) => (
          <span className="text-sm text-muted-foreground">
            <RelativeTime date={row.original.createTime} />
          </span>
        ),
        meta: { headerClassName: 'w-[120px] text-right', cellClassName: 'text-sm text-muted-foreground text-right' },
      },
    ],
    [],
  );

  if (isError) return <ErrorState message="Unable to load jobs" />;
  if (isLoading || !data) return <LoadingState />;

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-2xl font-bold">
          Jobs · <span className="font-mono">{shortType(type)}</span>
        </h1>
        <span className="text-sm text-muted-foreground">{data.totalCount} total</span>
      </div>

      <MetricsHeader stat={stat} dimension={dimension} onDimensionChange={setDimension} />

      <DataTable
        columns={columns}
        data={data.items}
        emptyMessage="No jobs found for this type"
        getRowId={(row) => row.id}
        pagination={{
          page,
          pageSize,
          pageCount: data.pageCount,
          onPageChange: setPage,
          onPageSizeChange: (size) => {
            setPageSize(size);
            setPage(0);
          },
        }}
      />
    </div>
  );
}

// Durable execution-metrics header: throughput + latency + error rate for the current type, with a
// by-TYPE / by-HANDLER view toggle. Sourced from the folded Statistic aggregates (survive Job-row expiry).
function MetricsHeader({
  stat,
  dimension,
  onDimensionChange,
}: {
  stat: JobExecutionStatModel | null;
  dimension: 'type' | 'handler';
  onDimensionChange: (dimension: 'type' | 'handler') => void;
}) {
  return (
    <div className="mb-4">
      <div className="flex items-center justify-between mb-2">
        <h2 className="text-sm font-semibold text-muted-foreground uppercase">Execution metrics</h2>
        <div className="inline-flex rounded-md border p-0.5 text-xs">
          <ToggleButton active={dimension === 'type'} onClick={() => onDimensionChange('type')}>By type</ToggleButton>
          <ToggleButton active={dimension === 'handler'} onClick={() => onDimensionChange('handler')}>By handler</ToggleButton>
        </div>
      </div>

      {stat ? (
        <div className="grid grid-cols-2 md:grid-cols-5 gap-3">
          <MetricTile label="Executed" value={stat.executedCount.toLocaleString()} />
          <MetricTile
            label="Error rate"
            value={`${(Math.round(stat.errorRate * 1000) / 10).toFixed(1)}%`}
            emphasis={stat.errorRate > 0 ? 'text-destructive' : undefined}
          />
          <MetricTile label="Avg" value={formatDuration(stat.avgDurationMs)} />
          <MetricTile label="p95" value={formatDuration(stat.p95DurationMs)} />
          <MetricTile label="p99" value={formatDuration(stat.p99DurationMs)} />
        </div>
      ) : (
        <Card>
          <CardContent className="py-4 text-center text-sm text-muted-foreground">
            No {dimension === 'type' ? 'type-level' : 'handler-level'} execution metrics recorded for this type yet.
          </CardContent>
        </Card>
      )}
    </div>
  );
}

function ToggleButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`px-2.5 py-1 rounded font-medium transition-colors ${
        active ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:text-foreground'
      }`}
    >
      {children}
    </button>
  );
}

function MetricTile({ label, value, emphasis }: { label: string; value: string; emphasis?: string }) {
  return (
    <Card>
      <CardContent className="p-3">
        <div className="text-xs text-muted-foreground">{label}</div>
        <div className={`text-xl font-bold tabular-nums ${emphasis ?? ''}`}>{value}</div>
      </CardContent>
    </Card>
  );
}

function formatDuration(ms: number): string {
  if (ms <= 0) {
    return '—';
  }
  if (ms < 1000) {
    return `${Math.round(ms)}ms`;
  }

  return `${(ms / 1000).toFixed(2)}s`;
}
