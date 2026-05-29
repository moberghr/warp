import { useEffect, useMemo, useState } from 'react';
import { useParams, useSearchParams, Link } from 'react-router-dom';
import {
  flexRender,
  getCoreRowModel,
  getSortedRowModel,
  useReactTable,
  type ColumnDef,
  type SortingState,
  type RowSelectionState,
} from '@tanstack/react-table';
import { toast } from 'sonner';
import { Checkbox } from '@/components/ui/checkbox';
import { Panel } from '@/components/v2/Panel';
import { StateBadge } from '@/components/StateBadge';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { shortType, shortId } from '@/utils/format';
import { getStateTone } from '@/lib/styles';
import { usePageStore } from '@/stores/page';
import { useDashboardStore } from '@/stores/dashboard';
import {
  useJobsList,
  useFailedJobsByType,
  useFailedJobTypes,
  useBulkRequeueJobs,
  useBulkDeleteJobs,
  useRequeueFailedJobsByType,
  useRequeueJob,
  useDeleteJob,
} from '@/api/hooks/useJobs';
import type { JobModel } from '@/types';
import { State } from '@/types';
import { JobsStateRail } from './JobsStateRail';
import { JobTypeBar } from './JobTypeBar';
import { BulkActionBar } from './BulkActionBar';
import { useConfirm } from '@/components/forms/useConfirm';

const PAGE_SIZE = 20;

const SUBTEXT: Record<string, string> = {
  enqueued: 'Awaiting a worker pickup.',
  scheduled: 'Future-dated runs.',
  processing: 'Currently executing.',
  completed: 'Recent successful runs.',
  failed: 'Retries exhausted. Filter by type to bulk requeue or delete.',
  awaiting: 'Awaiting parent / dependency.',
  deleted: 'Soft-deleted; recoverable until cleanup.',
};

function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

export default function JobListPage() {
  const { state } = useParams<{ state: string }>();
  const resolvedState = state ?? 'enqueued';
  const isFailed = resolvedState === 'failed';

  const [searchParams, setSearchParams] = useSearchParams();
  const page = Number(searchParams.get('page') ?? '0') || 0;
  const activeType = searchParams.get('type');

  const setPage = (next: number) => {
    const params = new URLSearchParams(searchParams);
    if (next === 0) {
      params.delete('page');
    } else {
      params.set('page', String(next));
    }
    setSearchParams(params, { replace: true });
  };

  const setActiveType = (type: string | null) => {
    const params = new URLSearchParams(searchParams);
    if (type) {
      params.set('type', type);
    } else {
      params.delete('type');
    }
    params.delete('page');
    setSearchParams(params, { replace: true });
  };

  const [rowSelection, setRowSelection] = useState<RowSelectionState>({});
  const [sorting, setSorting] = useState<SortingState>([{ id: 'createTime', desc: true }]);
  const { confirm, dialog: confirmDialog } = useConfirm();

  // Reset selection when navigating between state/type/page.
  useEffect(() => {
    setRowSelection({});
  }, [resolvedState, activeType, page]);

  const jobsQuery = useJobsList(resolvedState, page, PAGE_SIZE);
  const filteredQuery = useFailedJobsByType(
    activeType ?? '',
    page,
    PAGE_SIZE,
    isFailed && !!activeType,
  );
  const typeCountsQuery = useFailedJobTypes(isFailed);

  const activeQuery = isFailed && activeType ? filteredQuery : jobsQuery;
  const data = activeQuery.data;
  const error = activeQuery.error;
  const typeCounts = typeCountsQuery.data ?? [];

  const requeueJob = useRequeueJob();
  const deleteJob = useDeleteJob();
  const bulkRequeue = useBulkRequeueJobs();
  const bulkDelete = useBulkDeleteJobs();
  const requeueByType = useRequeueFailedJobsByType();

  const stats = useDashboardStore((s) => s.stats);

  // Drive the topbar via the page store.
  useEffect(() => {
    if (!stats) {
      usePageStore.getState().set({ title: 'Jobs', subtitle: undefined });

      return;
    }

    const subtitle = `${stats.failed} failed · ${stats.processing} processing · ${stats.created} enqueued`;
    usePageStore.getState().set({ title: 'Jobs', subtitle });
  }, [stats]);
  useEffect(() => () => usePageStore.getState().reset(), []);

  const rows = useMemo<JobModel[]>(() => data?.items ?? [], [data]);

  const columns = useMemo<ColumnDef<JobModel>[]>(
    () => [
      {
        id: 'select',
        header: ({ table }) => (
          <Checkbox
            checked={table.getIsAllRowsSelected()}
            indeterminate={table.getIsSomeRowsSelected()}
            onChange={table.getToggleAllRowsSelectedHandler()}
            aria-label="Select all"
          />
        ),
        cell: ({ row }) => (
          <Checkbox
            checked={row.getIsSelected()}
            onChange={row.getToggleSelectedHandler()}
            aria-label="Select row"
          />
        ),
        enableSorting: false,
        size: 36,
      },
      {
        accessorKey: 'id',
        header: 'ID',
        enableSorting: false,
        cell: ({ row }) => (
          <Link
            to={`/detail/${row.original.id}`}
            className="mono text-[12px] text-foreground hover:text-warp-blue transition-colors"
          >
            {shortId(row.original.id)}
          </Link>
        ),
      },
      {
        accessorKey: 'type',
        header: 'Type · Handler',
        enableSorting: false,
        cell: ({ row }) => (
          <div>
            <div className="text-[13px] font-medium text-foreground">{shortType(row.original.type)}</div>
            {row.original.handlerType && (
              <div className="mono text-[10.5px] text-text-mute mt-0.5">
                {shortType(row.original.handlerType)}
              </div>
            )}
          </div>
        ),
      },
      {
        accessorKey: 'currentState',
        header: 'State',
        enableSorting: false,
        cell: ({ row }) => (
          <StateBadge
            state={row.original.currentState}
            cancellationMode={row.original.cancellationMode}
          />
        ),
      },
      {
        accessorKey: 'createTime',
        header: 'Created',
        cell: ({ row }) => (
          <span className="text-[12px] text-text-dim mono">
            <RelativeTime date={row.original.createTime} />
          </span>
        ),
      },
      {
        id: 'actions',
        header: '',
        enableSorting: false,
        cell: ({ row }) => {
          const isFailedRow = row.original.currentState === State.Failed;

          return (
            <div className="flex justify-end gap-2">
              {isFailedRow && (
                <button
                  type="button"
                  onClick={() => requeueJob.mutate(row.original.id)}
                  className="px-2.5 py-1 text-[11.5px] font-medium rounded-md border border-border bg-panel-2 text-foreground hover:bg-panel transition-colors"
                >
                  Retry
                </button>
              )}
              <button
                type="button"
                onClick={async () => {
                  const ok = await confirm({
                    title: 'Delete job?',
                    description: `Delete ${shortId(row.original.id)} (${shortType(row.original.type)})? This cannot be undone.`,
                    confirmLabel: 'Delete',
                    destructive: true,
                  });
                  if (ok) {
                    deleteJob.mutate(row.original.id);
                  }
                }}
                className="px-2.5 py-1 text-[11.5px] font-medium rounded-md border border-warp-red text-warp-red hover:bg-warp-red-soft transition-colors"
              >
                Delete
              </button>
            </div>
          );
        },
      },
    ],
    [requeueJob, deleteJob, confirm],
  );

  const table = useReactTable({
    data: rows,
    columns,
    state: { sorting, rowSelection },
    onSortingChange: setSorting,
    onRowSelectionChange: setRowSelection,
    getRowId: (row) => row.id,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    enableRowSelection: true,
  });

  const selectedIds = Object.keys(rowSelection).filter((id) => rowSelection[id]);

  const handleBulkRequeue = () => {
    bulkRequeue.mutate(selectedIds, {
      onSuccess: () => setRowSelection({}),
    });
  };

  const handleBulkDelete = async () => {
    const ok = await confirm({
      title: `Delete ${selectedIds.length} jobs?`,
      description: 'This cannot be undone.',
      confirmLabel: `Delete ${selectedIds.length}`,
      destructive: true,
    });
    if (!ok) {
      return;
    }

    bulkDelete.mutate(selectedIds, {
      onSuccess: () => setRowSelection({}),
    });
  };

  const handleRequeueAllType = () => {
    if (!activeType) {
      return;
    }

    requeueByType.mutate(activeType, {
      onSuccess: () => {
        setRowSelection({});
        toast.success(`Requeued all ${shortType(activeType)}`);
      },
    });
  };

  if (error) {
    return <ErrorState message={(error as Error).message} />;
  }

  if (!data) {
    return <LoadingState />;
  }

  const stateLabel = resolvedState;
  const tone = getStateTone(resolvedState);
  const total = data.totalCount;
  const showingFrom = total === 0 ? 0 : page * PAGE_SIZE + 1;
  const showingTo = Math.min((page + 1) * PAGE_SIZE, total);

  return (
    <div className="flex flex-col lg:flex-row h-full min-h-0">
      {confirmDialog}
      <JobsStateRail active={resolvedState} />
      <div className="flex-1 overflow-auto p-5 min-w-0">
        <header className="mb-4">
          <h1 className="font-display text-[22px] font-semibold tracking-tight">
            {capitalize(stateLabel)} jobs
            <span
              className={`ml-2.5 inline-flex items-center rounded-md px-2 py-0.5 text-[13px] font-medium align-middle ${tone.bg} ${tone.text}`}
            >
              {total.toLocaleString()}
            </span>
          </h1>
          <p className="text-[12.5px] text-text-mute mt-1">{SUBTEXT[resolvedState] ?? ''}</p>
        </header>

        <BulkActionBar
          count={selectedIds.length}
          total={total}
          activeType={activeType ? shortType(activeType) : null}
          stateLabel={stateLabel}
          onRequeue={handleBulkRequeue}
          onDelete={handleBulkDelete}
          onRequeueAllType={isFailed && activeType ? handleRequeueAllType : undefined}
        />

        {isFailed && typeCounts.length > 0 && (
          <JobTypeBar types={typeCounts} activeType={activeType} onPick={setActiveType} />
        )}

        <Panel className="overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full border-collapse">
              <thead>
                {table.getHeaderGroups().map((hg) => (
                  <tr key={hg.id} className="bg-panel-2 border-b border-border">
                    {hg.headers.map((header) => (
                      <th
                        key={header.id}
                        className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold"
                        style={{ width: header.column.columnDef.size ? header.column.columnDef.size : undefined }}
                      >
                        {header.isPlaceholder
                          ? null
                          : flexRender(header.column.columnDef.header, header.getContext())}
                      </th>
                    ))}
                  </tr>
                ))}
              </thead>
              <tbody>
                {table.getRowModel().rows.length === 0 ? (
                  <tr>
                    <td colSpan={columns.length} className="text-center text-text-mute py-10 text-[13px]">
                      No jobs found.
                    </td>
                  </tr>
                ) : (
                  table.getRowModel().rows.map((row) => {
                    const isSelected = row.getIsSelected();
                    const selectedBg = isFailed ? 'bg-warp-red-soft' : 'bg-warp-blue-soft';

                    return (
                      <tr
                        key={row.id}
                        className={`border-b border-border last:border-b-0 transition-colors ${
                          isSelected ? selectedBg : 'hover:bg-panel-2'
                        }`}
                      >
                        {row.getVisibleCells().map((cell) => (
                          <td key={cell.id} className="px-3.5 py-3 align-middle">
                            {flexRender(cell.column.columnDef.cell, cell.getContext())}
                          </td>
                        ))}
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        </Panel>

        <div className="flex items-center justify-between mt-3 text-[12px] text-text-mute">
          <span className="mono">
            Showing {showingFrom}–{showingTo} of {total.toLocaleString()}
          </span>
          <div className="flex gap-1.5">
            <button
              type="button"
              onClick={() => setPage(page - 1)}
              disabled={page === 0}
              className="px-2.5 py-1 text-[11.5px] rounded-md border border-border bg-panel text-text-dim hover:bg-panel-2 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
            >
              ‹ Prev
            </button>
            <button
              type="button"
              onClick={() => setPage(page + 1)}
              disabled={page >= data.pageCount - 1}
              className="px-2.5 py-1 text-[11.5px] rounded-md border border-border bg-panel text-foreground hover:bg-panel-2 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
            >
              Next ›
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
