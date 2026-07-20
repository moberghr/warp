import { useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import type { ColumnDef } from '@tanstack/react-table';
import { AlertTriangle } from 'lucide-react';
import { DataTable } from '@/components/DataTable';
import { MetricCard } from '@/components/MetricCard';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import * as api from '@/api';
import type { AdapterListItem } from '@/types/adapters';
import { HealthPill, adapterHealth, Sparkline, formatPercent, formatMs } from './shared';

export default function AdaptersPage() {
  const navigate = useNavigate();
  const query = useQuery({
    queryKey: ['adapters', 'list'] as const,
    queryFn: () => api.getAdapters(),
  });

  const adapters = useMemo(() => (Array.isArray(query.data) ? query.data : []), [query.data]);

  const summary = useMemo(() => {
    const totalCalls = adapters.reduce((sum, x) => sum + x.totalCalls, 0);
    const totalErrors = adapters.reduce((sum, x) => sum + x.errorCount, 0);

    return {
      count: adapters.length,
      totalCalls,
      errorRate: totalCalls > 0 ? totalErrors / totalCalls : 0,
    };
  }, [adapters]);

  const columns = useMemo<ColumnDef<AdapterListItem>[]>(
    () => [
      {
        accessorKey: 'name',
        header: 'Adapter',
        cell: ({ row }) => (
          <div className="flex flex-col gap-0.5">
            <span className="font-medium flex items-center gap-2">
              {row.original.name}
              {row.original.hasPolicyConflict && (
                <span
                  className="inline-flex items-center gap-1 rounded-full bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300 px-1.5 py-0.5 text-[11px] font-medium"
                  title="This process reported a shared rate-limit policy that differs from the persisted definition; the persisted policy is being enforced."
                >
                  <AlertTriangle className="h-3 w-3" />
                  Conflict
                </span>
              )}
            </span>
            {row.original.configSummary && (
              <span className="text-xs text-muted-foreground font-mono truncate max-w-md">
                {row.original.configSummary}
              </span>
            )}
          </div>
        ),
      },
      {
        id: 'trend',
        header: 'Trend',
        meta: { headerClassName: 'w-28' },
        cell: ({ row }) => <Sparkline values={row.original.trend} />,
      },
      {
        accessorKey: 'totalCalls',
        header: 'Calls',
        meta: { headerClassName: 'text-right w-24', cellClassName: 'text-right tabular-nums' },
        cell: ({ row }) => row.original.totalCalls.toLocaleString(),
      },
      {
        accessorKey: 'errorRate',
        header: 'Error %',
        meta: { headerClassName: 'text-right w-24', cellClassName: 'text-right tabular-nums' },
        cell: ({ row }) => (
          <span className={row.original.errorRate > 0 ? 'text-destructive' : ''}>
            {formatPercent(row.original.errorRate)}
          </span>
        ),
      },
      {
        accessorKey: 'avgDurationMs',
        header: 'Avg latency',
        meta: { headerClassName: 'text-right w-28', cellClassName: 'text-right tabular-nums' },
        cell: ({ row }) => formatMs(row.original.avgDurationMs),
      },
      {
        id: 'health',
        header: 'Health',
        meta: { headerClassName: 'w-32' },
        cell: ({ row }) => <HealthPill health={adapterHealth(row.original)} />,
      },
      {
        accessorKey: 'lastSeenAt',
        header: 'Last seen',
        meta: { headerClassName: 'w-56' },
        cell: ({ row }) => (
          <span className="text-sm text-muted-foreground">
            <RelativeTime date={row.original.lastSeenAt} />
          </span>
        ),
      },
    ],
    [],
  );

  if (query.isError) return <ErrorState message="Unable to load adapters" />;
  if (query.isLoading) return <LoadingState />;

  return (
    <div>
      <h1 className="text-2xl font-bold mb-1">Adapters</h1>
      <p className="text-sm text-muted-foreground mb-4">
        Outbound service dependencies — calls, error rates, and latency across the fleet.
      </p>

      <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mb-4">
        <MetricCard label="Adapters" value={summary.count} />
        <MetricCard label="Calls (recorded)" value={summary.totalCalls} />
        <MetricCard
          label="Error rate"
          value={Math.round(summary.errorRate * 1000) / 10}
          color={summary.errorRate > 0 ? 'text-destructive' : undefined}
        />
      </div>

      <DataTable
        columns={columns}
        data={adapters}
        emptyMessage="No adapters have recorded calls yet."
        getRowId={(row) => row.name}
        onRowClick={(row) => navigate(`/adapters/${encodeURIComponent(row.name)}`)}
      />
    </div>
  );
}
