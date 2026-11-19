import { useMemo } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import type { ColumnDef } from '@tanstack/react-table';
import { DataTable } from '@/components/DataTable';
import { LoadingState, ErrorState } from '@/components/PageState';
import { PageHeading } from '@/components/PageHeading';
import { RelativeTime } from '@/components/RelativeTime';
import { Badge } from '@/components/ui/badge';
import { Hint } from '@/components/ui/tooltip';
import * as api from '@/api';
import { queryKeys } from '@/lib/queryClient';
import { encodeUrlSafeId } from '@/lib/urlSafeId';
import type { MetricInstrument } from '@/types/metrics';
import { collapseTooltip, kindLabel, unitLabel } from './metricModel';
import { MetricsTabs } from './MetricsTabs';

export default function MetricsPage() {
  const navigate = useNavigate();
  const query = useQuery({
    queryKey: queryKeys.metrics,
    queryFn: () => api.getMetrics(),
    refetchInterval: 30_000,
  });

  // MetricQueryService.ListInstruments already orders the registry by name.
  const instruments = useMemo(() => (Array.isArray(query.data) ? query.data : []), [query.data]);

  const columns = useMemo<ColumnDef<MetricInstrument>[]>(
    () => [
      {
        accessorKey: 'name',
        header: 'Name',
        cell: ({ row }) => (
          <div className="flex items-center gap-2">
            <Link
              to={`/metrics/${encodeUrlSafeId(row.original.name)}`}
              className="font-mono text-sm font-medium text-primary hover:underline"
              onClick={(e) => e.stopPropagation()}
            >
              {row.original.name}
            </Link>
            {row.original.collapsedAt && (
              <Hint text={collapseTooltip(row.original)}>
                <Badge variant="destructive">collapsed</Badge>
              </Hint>
            )}
          </div>
        ),
      },
      {
        accessorKey: 'meter',
        header: 'Meter',
        cell: ({ row }) => <span className="font-mono text-xs text-muted-foreground">{row.original.meter}</span>,
      },
      {
        accessorKey: 'kind',
        header: 'Kind',
        meta: { headerClassName: 'w-36' },
        cell: ({ row }) => kindLabel(row.original.kind),
      },
      {
        accessorKey: 'unit',
        header: 'Unit',
        meta: { headerClassName: 'w-24' },
        cell: ({ row }) => unitLabel(row.original.unit) || '—',
      },
      {
        id: 'tagKeys',
        header: 'Tag keys',
        cell: ({ row }) =>
          row.original.tagKeys.length === 0 ? (
            <span className="text-muted-foreground">—</span>
          ) : (
            <div className="flex flex-wrap gap-1">
              {row.original.tagKeys.map((key) => (
                <span key={key} className="rounded bg-muted px-1.5 py-0.5 font-mono text-[11px]">
                  {key}
                </span>
              ))}
            </div>
          ),
      },
      {
        accessorKey: 'lastSeenAt',
        header: 'Last seen',
        cell: ({ row }) => <RelativeTime date={row.original.lastSeenAt} display="relative" />,
      },
    ],
    [],
  );

  if (query.isError) return <ErrorState message="Unable to load metrics" />;
  if (query.isLoading) return <LoadingState />;

  return (
    <div>
      <PageHeading className="mb-1">Metrics</PageHeading>
      <p className="text-sm text-muted-foreground mb-4">
        Application metrics collected from your OpenTelemetry meters — pick one to chart it, break it down by tag
        and compare it with yesterday or last week.
      </p>
      <MetricsTabs />

      <DataTable
        columns={columns}
        data={instruments}
        emptyMessage="No metrics collected yet. Register a meter with AddMetrics() and record a measurement."
        getRowId={(row) => row.name}
        onRowClick={(row) => navigate(`/metrics/${encodeUrlSafeId(row.name)}`)}
      />
    </div>
  );
}
