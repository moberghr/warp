import { useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import type { ColumnDef } from '@tanstack/react-table';
import { LoadingState, ErrorState } from '@/components/PageState';
import { DataTable } from '@/components/DataTable';
import { useBackgroundServices } from '@/api/hooks/useBackgroundServices';
import { ServiceScope } from '@/types/backgroundServices';
import type { BackgroundServiceListItem } from '@/types/backgroundServices';

export default function BackgroundServicesList() {
  const navigate = useNavigate();
  const { data: items, isLoading, isError } = useBackgroundServices();

  const columns = useMemo<ColumnDef<BackgroundServiceListItem>[]>(
    () => [
      {
        accessorKey: 'name',
        header: 'Name',
        cell: ({ row }) => (
          <span className="flex items-center gap-2 font-medium">
            {row.original.name}
            {row.original.configurationMismatchCount > 0 && (
              <span
                className="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400"
                title={`${row.original.configurationMismatchCount} instance(s) have a configuration mismatch`}
              >
                Mismatch
              </span>
            )}
          </span>
        ),
      },
      {
        accessorKey: 'scope',
        header: 'Scope',
        cell: ({ row }) => <ScopeBadge scope={row.original.scope} />,
      },
      {
        id: 'status',
        header: 'Status',
        cell: ({ row }) => <StatusSummary item={row.original} />,
      },
      {
        accessorKey: 'totalRestartCount',
        header: 'Restarts',
        cell: ({ row }) =>
          row.original.totalRestartCount > 0 ? (
            <span className="tabular-nums text-amber-600 dark:text-amber-400">
              {row.original.totalRestartCount}
            </span>
          ) : (
            <span className="tabular-nums text-muted-foreground">0</span>
          ),
      },
      {
        accessorKey: 'lastErrorType',
        header: 'Last Error',
        cell: ({ row }) =>
          row.original.lastErrorType ? (
            <span className="text-red-600 dark:text-red-400 font-mono text-xs">
              {shortTypeName(row.original.lastErrorType)}
            </span>
          ) : (
            <span className="text-muted-foreground">—</span>
          ),
      },
    ],
    [],
  );

  if (isError) return <ErrorState message="Unable to load background services" />;
  if (isLoading || !items) return <LoadingState />;

  return (
    <div>
      <h1 className="text-2xl font-bold mb-4">Background Services</h1>

      <DataTable
        columns={columns}
        data={items}
        emptyMessage="No background services registered"
        getRowId={(row) => row.name}
        onRowClick={(row) => navigate(`/services/${encodeURIComponent(row.name)}`)}
      />
    </div>
  );
}

function ScopeBadge({ scope }: { scope: number }) {
  const label = scope === ServiceScope.Singleton ? 'Singleton' : 'Per Server';
  const cls =
    scope === ServiceScope.Singleton
      ? 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-400'
      : 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400';

  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${cls}`}>
      {label}
    </span>
  );
}

function StatusSummary({ item }: { item: BackgroundServiceListItem }) {
  const parts: string[] = [];

  if (item.scope === ServiceScope.PerServer) {
    parts.push(`Running ${item.runningCount}/${item.totalInstances}`);
  } else {
    if (item.runningCount > 0) {
      parts.push(`Running on ${item.runningCount}`);
    }
    if (item.waitingCount > 0) {
      parts.push(`Waiting ${item.waitingCount}`);
    }
    if (item.runningCount === 0 && item.waitingCount === 0) {
      parts.push(`${item.totalInstances} instances`);
    }
  }

  const hasFault = item.faultedCount > 0;

  return (
    <span className="flex items-center gap-1.5 flex-wrap">
      <span className="text-sm">{parts.join(', ')}</span>
      {hasFault && (
        <span className="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400">
          {item.faultedCount} faulted
        </span>
      )}
      {item.configurationMismatchCount > 0 && (
        <span className="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium bg-orange-100 text-orange-800 dark:bg-orange-900/30 dark:text-orange-400">
          {item.configurationMismatchCount} mismatch
        </span>
      )}
    </span>
  );
}

function shortTypeName(fullName: string): string {
  const withoutAssembly = fullName.split(',')[0].trim();

  return withoutAssembly.split('.').pop() ?? fullName;
}
