import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import type { ColumnDef } from '@tanstack/react-table';
import { StateBadge } from '@/components/StateBadge';
import { shortType, shortId } from '@/utils/format';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { DataTable } from '@/components/DataTable';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { useJobsByType } from '@/api/hooks/useJobs';
import type { JobModel } from '@/types';

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

  // Reset paging when navigating between types (the route component instance is reused).
  useEffect(() => { setPage(0); }, [type]);

  const { data, isLoading, isError } = useJobsByType(type, page, pageSize);

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
