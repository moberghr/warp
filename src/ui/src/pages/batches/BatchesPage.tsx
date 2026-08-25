import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import type { ColumnDef } from '@tanstack/react-table';
import { StateBadge } from '@/components/StateBadge';
import { shortId } from '@/utils/format';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { DataTable } from '@/components/DataTable';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { useBatchesList } from '@/api/hooks/useBatches';
import type { JobGroupModel } from '@/types';
import { PageHeading } from '@/components/PageHeading';

export default function BatchesPage() {
  const { state } = useParams<{ state?: string }>();
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePersistedPageSize();

  useEffect(() => {
    setPage(0);
  }, [state]);

  const { data, isLoading, isError } = useBatchesList(state, page, pageSize);

  const columns = useMemo<ColumnDef<JobGroupModel>[]>(
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
        id: 'progress',
        header: 'Progress',
        cell: ({ row }) => {
          const batch = row.original;
          const done = batch.completedJobs + batch.failedJobs;
          const pct = batch.totalJobs > 0 ? Math.round((done / batch.totalJobs) * 100) : 0;
          const greenPct = batch.totalJobs > 0 ? (batch.completedJobs / batch.totalJobs) * 100 : 0;
          const redPct = batch.totalJobs > 0 ? (batch.failedJobs / batch.totalJobs) * 100 : 0;

          return (
            <div className="flex items-center gap-2">
              <div className="flex-1 h-2 bg-muted rounded-full overflow-hidden flex">
                {greenPct > 0 && <div className="h-full bg-green-500 transition-all" style={{ width: `${greenPct}%` }} />}
                {redPct > 0 && <div className="h-full bg-red-500 transition-all" style={{ width: `${redPct}%` }} />}
              </div>
              <span className="text-xs text-muted-foreground w-28 text-right shrink-0">
                {done}/{batch.totalJobs} ({pct}%)
              </span>
            </div>
          );
        },
      },
      {
        accessorKey: 'currentState',
        header: 'State',
        cell: ({ row }) => <StateBadge state={row.original.currentState} />,
        meta: { headerClassName: 'w-[100px] text-right', cellClassName: 'text-right' },
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

  if (isError) return <ErrorState message="Unable to load batches" />;
  if (isLoading || !data) return <LoadingState />;

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <PageHeading className="">
          {state ? `${state.charAt(0).toUpperCase() + state.slice(1)} Batches` : 'Batches'}
        </PageHeading>
        <span className="text-sm text-muted-foreground">{data.totalCount} total</span>
      </div>

      <DataTable
        columns={columns}
        data={data.items}
        emptyMessage="No batches found"
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
