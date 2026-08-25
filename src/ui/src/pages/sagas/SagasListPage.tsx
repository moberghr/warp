import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import axios from 'axios';
import type { ColumnDef } from '@tanstack/react-table';
import { Card, CardContent } from '@/components/ui/card';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { DataTable } from '@/components/DataTable';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { useSagasList, useSagaTypes, useSagaStats } from '@/api/hooks/useSagas';
import type { SagaListItem } from '@/types';
import { PageHeading } from '@/components/PageHeading';

export default function SagasListPage() {
  const [typeFilter, setTypeFilter] = useState<string>('');
  const [keyFilter, setKeyFilter] = useState<string>('');
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePersistedPageSize();

  const list = useSagasList(page, pageSize, typeFilter || undefined, keyFilter || undefined);
  const typesQuery = useSagaTypes();
  const statsQuery = useSagaStats();

  const unavailable =
    list.isError && axios.isAxiosError(list.error) && list.error.response?.status === 404;

  const columns = useMemo<ColumnDef<SagaListItem>[]>(
    () => [
      {
        accessorKey: 'type',
        header: 'Type',
        cell: ({ row }) => <span className="font-medium">{shortName(row.original.type)}</span>,
      },
      {
        accessorKey: 'correlationKey',
        header: 'Correlation key',
        cell: ({ row }) => (
          <Link to={`/sagas/${row.original.id}`} className="font-mono text-xs text-primary hover:underline">
            {row.original.correlationKey}
          </Link>
        ),
      },
      {
        accessorKey: 'updatedAt',
        header: 'Updated',
        cell: ({ row }) => (
          <span className="text-sm">
            <RelativeTime date={row.original.updatedAt} />
          </span>
        ),
      },
      {
        accessorKey: 'createdAt',
        header: 'Created',
        cell: ({ row }) => (
          <span className="text-sm text-muted-foreground">
            <RelativeTime date={row.original.createdAt} />
          </span>
        ),
      },
    ],
    [],
  );

  if (unavailable) {
    return (
      <div>
        <PageHeading className="mb-4">Sagas</PageHeading>
        <Card>
          <CardContent className="py-8 text-center text-muted-foreground">
            Sagas addon is not registered. Call <code className="font-mono text-xs">opt.AddSagas()</code> in your Warp configuration to enable.
          </CardContent>
        </Card>
      </div>
    );
  }

  if (list.isError) return <ErrorState message="Unable to load sagas" />;
  if (list.isLoading || !list.data) return <LoadingState />;

  const data = list.data;
  const types = typesQuery.data ?? [];
  const stats = statsQuery.data;

  return (
    <div>
      <PageHeading className="mb-4">Sagas</PageHeading>

      {stats && (
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mb-4">
          <Card>
            <CardContent className="py-4">
              <div className="text-sm text-muted-foreground">Live sagas</div>
              <div className="text-2xl font-bold">{stats.liveSagas}</div>
            </CardContent>
          </Card>
          <Card>
            <CardContent className="py-4">
              <div className="text-sm text-muted-foreground">Started today</div>
              <div className="text-2xl font-bold">{stats.startedToday}</div>
            </CardContent>
          </Card>
          <Card>
            <CardContent className="py-4">
              <div className="text-sm text-muted-foreground">Types in use</div>
              <div className="text-2xl font-bold">{types.length}</div>
            </CardContent>
          </Card>
        </div>
      )}

      <div className="flex gap-2 mb-4">
        <select
          className="border rounded-md px-2 py-1 text-sm bg-background"
          value={typeFilter}
          onChange={(e) => { setTypeFilter(e.target.value); setPage(0); }}
        >
          <option value="">All types</option>
          {types.map((t) => (
            <option key={t} value={t}>
              {shortName(t)}
            </option>
          ))}
        </select>
        <input
          type="text"
          className="border rounded-md px-2 py-1 text-sm bg-background flex-1 max-w-xs"
          placeholder="Search correlation key…"
          value={keyFilter}
          onChange={(e) => { setKeyFilter(e.target.value); setPage(0); }}
        />
      </div>

      <DataTable
        columns={columns}
        data={data.items}
        emptyMessage="No sagas found"
        getRowId={(row) => row.id}
        pagination={{
          page,
          pageSize,
          pageCount: Math.ceil(data.totalCount / pageSize),
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

function shortName(assemblyQualifiedName: string): string {
  const typeName = assemblyQualifiedName.split(',')[0];

  return typeName.split('.').pop() ?? typeName;
}
