import { useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import type { ColumnDef } from '@tanstack/react-table';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { DataTable } from '@/components/DataTable';
import { MetricCard } from '@/components/MetricCard';
import { PerformanceChart } from '@/components/PerformanceChart';
import { LoadingState, ErrorState } from '@/components/PageState';
import * as api from '@/api';
import type { EndpointListItem } from '@/types/endpoints';
import { HealthPill, adapterHealth, formatPercent, formatMs } from '../adapters/shared';

export default function EndpointsPage() {
  const navigate = useNavigate();
  const query = useQuery({
    queryKey: ['endpoints', 'list'] as const,
    queryFn: () => api.getEndpoints(),
  });

  const historyQuery = useQuery({
    queryKey: ['endpoints', 'history', 'global'] as const,
    queryFn: () => api.getEndpointGlobalHistory(),
  });

  const endpoints = useMemo(() => (Array.isArray(query.data) ? query.data : []), [query.data]);

  const summary = useMemo(() => {
    const totalCalls = endpoints.reduce((sum, x) => sum + x.totalCalls, 0);
    const totalErrors = endpoints.reduce((sum, x) => sum + x.errorCount, 0);

    return {
      count: endpoints.length,
      totalCalls,
      errorRate: totalCalls > 0 ? totalErrors / totalCalls : 0,
    };
  }, [endpoints]);

  const columns = useMemo<ColumnDef<EndpointListItem>[]>(
    () => [
      {
        accessorKey: 'route',
        header: 'Route',
        cell: ({ row }) => (
          <span className="font-mono text-sm flex items-center gap-2">
            <span className="rounded bg-muted px-1.5 py-0.5 text-[11px] font-semibold uppercase text-muted-foreground">
              {row.original.method}
            </span>
            <span className="font-medium">{row.original.routeTemplate}</span>
          </span>
        ),
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
    ],
    [],
  );

  if (query.isError) return <ErrorState message="Unable to load endpoints" />;
  if (query.isLoading) return <LoadingState />;

  return (
    <div>
      <h1 className="text-2xl font-bold mb-1">Endpoints</h1>
      <p className="text-sm text-muted-foreground mb-4">
        Inbound HTTP requests — calls, error rates, and latency across the fleet.
      </p>

      <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mb-4">
        <MetricCard label="Endpoints" value={summary.count} />
        <MetricCard label="Calls (recorded)" value={summary.totalCalls} />
        <MetricCard
          label="Error rate"
          value={Math.round(summary.errorRate * 1000) / 10}
          color={summary.errorRate > 0 ? 'text-destructive' : undefined}
        />
      </div>

      <Card className="mb-4">
        <CardHeader><CardTitle className="text-base">Performance over time (all endpoints)</CardTitle></CardHeader>
        <CardContent>
          <PerformanceChart points={historyQuery.data ?? []} />
        </CardContent>
      </Card>

      <DataTable
        columns={columns}
        data={endpoints}
        emptyMessage="No endpoints have recorded requests yet."
        getRowId={(row) => row.id}
        onRowClick={(row) => navigate(`/endpoints/${encodeURIComponent(row.id)}`)}
      />
    </div>
  );
}
