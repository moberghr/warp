import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import type { ColumnDef } from '@tanstack/react-table';
import { StateBadge } from '@/components/StateBadge';
import { shortType, shortId } from '@/utils/format';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { DataTable } from '@/components/DataTable';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { useMessagesList } from '@/api/hooks/useMessages';
import type { JobGroupModel } from '@/types';
import { PageHeading } from '@/components/PageHeading';

export default function MessagesPage() {
  const { state } = useParams<{ state?: string }>();
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePersistedPageSize();

  useEffect(() => {
    setPage(0);
  }, [state]);

  const { data, isLoading, isError } = useMessagesList(state, page, pageSize);

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
        accessorKey: 'type',
        header: 'Type',
        cell: ({ row }) => (row.original.type ? shortType(row.original.type) : '—'),
      },
      {
        accessorKey: 'queue',
        header: 'Queue',
        cell: ({ row }) => row.original.queue ?? '—',
      },
      {
        accessorKey: 'currentState',
        header: 'State',
        cell: ({ row }) => <StateBadge state={row.original.currentState} />,
      },
      {
        accessorKey: 'totalJobs',
        header: 'Jobs',
      },
      {
        accessorKey: 'createTime',
        header: 'Created',
        cell: ({ row }) => (
          <span className="text-sm text-muted-foreground">
            <RelativeTime date={row.original.createTime} />
          </span>
        ),
      },
    ],
    [],
  );

  if (isError) return <ErrorState message="Unable to load messages" />;
  if (isLoading || !data) return <LoadingState />;

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <PageHeading className="">
          {state ? `${state.charAt(0).toUpperCase() + state.slice(1)} Messages` : 'Messages'}
        </PageHeading>
        <span className="text-sm text-muted-foreground">{data.totalCount} total</span>
      </div>

      <DataTable
        columns={columns}
        data={data.items}
        emptyMessage="No messages found"
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
