import { useEffect, useMemo, useState } from 'react';
import { useParams, Link } from 'react-router-dom';
import type { ColumnDef } from '@tanstack/react-table';
import { Button } from '@/components/ui/button';
import { StateBadge } from '@/components/StateBadge';
import { shortType, shortId } from '@/utils/format';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { DataTable } from '@/components/DataTable';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import {
  useJobsList,
  useFailedJobsByType,
  useFailedJobTypes,
  useRequeueJob,
  useDeleteJob,
  useBulkRequeueJobs,
  useBulkDeleteJobs,
  useRequeueFailedJobsByType,
  useDeleteFailedJobsByType,
} from '@/api/hooks/useJobs';
import { State } from '@/types';
import type { JobModel } from '@/types';

type PendingAction =
  | { kind: 'requeue-one'; id: string }
  | { kind: 'delete-one'; id: string }
  | { kind: 'requeue-bulk'; ids: string[] }
  | { kind: 'delete-bulk'; ids: string[] }
  | { kind: 'requeue-by-type'; type: string }
  | { kind: 'delete-by-type'; type: string };

export default function JobListPage() {
  const { state } = useParams<{ state: string }>();
  const activeState = state ?? 'enqueued';
  const isFailed = activeState === 'failed';

  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePersistedPageSize();
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [selectedType, setSelectedType] = useState<string | null>(null);

  useEffect(() => {
    setPage(0);
    setSelectedIds(new Set());
    setSelectedType(null);
  }, [state]);

  const listQuery = useJobsList(activeState, page, pageSize);
  const typedQuery = useFailedJobsByType(selectedType ?? '', page, pageSize, isFailed && !!selectedType);
  const typeCounts = useFailedJobTypes(isFailed);

  const requeue = useRequeueJob();
  const remove = useDeleteJob();
  const bulkRequeue = useBulkRequeueJobs();
  const bulkDelete = useBulkDeleteJobs();
  const requeueByType = useRequeueFailedJobsByType();
  const deleteByType = useDeleteFailedJobsByType();

  const [pending, setPending] = useState<PendingAction | null>(null);

  const runPending = () => {
    if (!pending) return;
    switch (pending.kind) {
      case 'requeue-one':
        requeue.mutate(pending.id);
        break;
      case 'delete-one':
        remove.mutate(pending.id);
        break;
      case 'requeue-bulk':
        bulkRequeue.mutate(pending.ids, { onSuccess: () => setSelectedIds(new Set()) });
        break;
      case 'delete-bulk':
        bulkDelete.mutate(pending.ids, { onSuccess: () => setSelectedIds(new Set()) });
        break;
      case 'requeue-by-type':
        requeueByType.mutate(pending.type);
        break;
      case 'delete-by-type':
        deleteByType.mutate(pending.type);
        break;
    }
    setPending(null);
  };

  const active = isFailed && selectedType ? typedQuery : listQuery;
  const data = active.data;

  const columns = useMemo<ColumnDef<JobModel>[]>(
    () => [
      {
        id: 'select',
        header: () => {
          const allSelected =
            (data?.items.length ?? 0) > 0 && (data?.items.every((j) => selectedIds.has(j.id)) ?? false);

          return (
            <input
              type="checkbox"
              checked={allSelected}
              onChange={(e) => {
                if (e.target.checked) {
                  setSelectedIds(new Set(data?.items.map((j) => j.id) ?? []));
                } else {
                  setSelectedIds(new Set());
                }
              }}
              className="rounded"
            />
          );
        },
        cell: ({ row }) => (
          <input
            type="checkbox"
            checked={selectedIds.has(row.original.id)}
            onChange={(e) => {
              const next = new Set(selectedIds);
              if (e.target.checked) {
                next.add(row.original.id);
              } else {
                next.delete(row.original.id);
              }
              setSelectedIds(next);
            }}
            className="rounded"
            onClick={(e) => e.stopPropagation()}
          />
        ),
        meta: { headerClassName: 'w-[40px]' },
      },
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
        cell: ({ row }) => (
          <div>
            <div>{shortType(row.original.type)}</div>
            {row.original.handlerType && (
              <div className="text-xs text-muted-foreground">{shortType(row.original.handlerType)}</div>
            )}
          </div>
        ),
      },
      {
        accessorKey: 'currentState',
        header: 'State',
        cell: ({ row }) => (
          <StateBadge state={row.original.currentState} cancellationMode={row.original.cancellationMode} />
        ),
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
      // Only the retrying listing computes retryCount — everywhere else it is null, which means
      // "not computed", not "never retried", so the column is not offered there.
      ...(activeState === 'retrying'
        ? [
            {
              id: 'attempt',
              header: 'Attempt',
              cell: ({ row }) =>
                row.original.retryCount == null ? (
                  <span className="text-sm text-muted-foreground">—</span>
                ) : (
                  <span className="text-sm">
                    #{row.original.retryCount + 1}
                    <span className="text-muted-foreground"> ({row.original.retryCount} failed)</span>
                  </span>
                ),
            } as ColumnDef<JobModel>,
          ]
        : []),
      ...(activeState === 'scheduled' || activeState === 'retrying'
        ? [
            {
              id: 'scheduled',
              header: activeState === 'retrying' ? 'Next attempt' : 'Scheduled',
              // Countdown only for a row still waiting on its instant. The Retrying listing spans
              // Scheduled AND Enqueued (an empty retry schedule requeues straight to Enqueued), and
              // an Enqueued row is already released — its ScheduleTime is in the past by definition,
              // so a countdown would read "overdue by 5 minutes" over an ordinary queue backlog and
              // accuse a healthy system. Same gate as the job detail page.
              cell: ({ row }) => (
                <span className="text-sm text-muted-foreground">
                  <RelativeTime
                    date={row.original.scheduleTime ?? row.original.createTime}
                    tense={row.original.currentState === State.Scheduled ? 'countdown' : 'past'}
                  />
                </span>
              ),
            } as ColumnDef<JobModel>,
          ]
        : []),
      {
        id: 'actions',
        header: 'Actions',
        cell: ({ row }) => (
          <>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => setPending({ kind: 'requeue-one', id: row.original.id })}
            >
              Requeue
            </Button>
            <Button
              variant="ghost"
              size="sm"
              className="text-destructive"
              onClick={() => setPending({ kind: 'delete-one', id: row.original.id })}
            >
              Delete
            </Button>
          </>
        ),
        meta: { headerClassName: 'text-right', cellClassName: 'text-right' },
      },
    ],
    [data, selectedIds, activeState],
  );

  if (active.isError) return <ErrorState message="Unable to load jobs" />;
  if (active.isLoading || !data) return <LoadingState />;

  const title = activeState.charAt(0).toUpperCase() + activeState.slice(1);
  const handlePageSizeChange = (size: number) => {
    setPageSize(size);
    setPage(0);
    setSelectedIds(new Set());
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-2xl font-bold">{title} Jobs</h1>
        <span className="text-sm text-muted-foreground">{data.totalCount} total</span>
      </div>

      {/* Failed type filter bar */}
      {isFailed && (typeCounts.data?.length ?? 0) > 0 && (
        <div className="flex flex-wrap items-center gap-2 mb-3">
          <Button
            variant={selectedType === null ? 'default' : 'outline'}
            size="sm"
            onClick={() => {
              setSelectedType(null);
              setPage(0);
              setSelectedIds(new Set());
            }}
          >
            All
          </Button>
          {typeCounts.data?.map((tc) => (
            <Button
              key={tc.type}
              variant={selectedType === tc.type ? 'default' : 'outline'}
              size="sm"
              onClick={() => {
                setSelectedType(tc.type);
                setPage(0);
                setSelectedIds(new Set());
              }}
            >
              {shortType(tc.type)} ({tc.count})
            </Button>
          ))}
        </div>
      )}

      {/* Bulk actions for type filter */}
      {isFailed && selectedType && (
        <div className="flex items-center gap-3 mb-3 p-3 bg-muted rounded-md">
          <span className="text-sm font-medium">Filtered: {shortType(selectedType)}</span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => setPending({ kind: 'requeue-by-type', type: selectedType })}
          >
            Requeue All
          </Button>
          <Button
            variant="outline"
            size="sm"
            className="text-destructive"
            onClick={() => setPending({ kind: 'delete-by-type', type: selectedType })}
          >
            Delete All
          </Button>
        </div>
      )}

      {selectedIds.size > 0 && (
        <div className="flex items-center gap-3 mb-3 p-3 bg-muted rounded-md">
          <span className="text-sm font-medium">{selectedIds.size} selected</span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => setPending({ kind: 'requeue-bulk', ids: Array.from(selectedIds) })}
          >
            Requeue
          </Button>
          <Button
            variant="outline"
            size="sm"
            className="text-destructive"
            onClick={() => setPending({ kind: 'delete-bulk', ids: Array.from(selectedIds) })}
          >
            Delete
          </Button>
          <Button variant="ghost" size="sm" onClick={() => setSelectedIds(new Set())}>
            Clear
          </Button>
        </div>
      )}

      <DataTable
        columns={columns}
        data={data.items}
        emptyMessage="No jobs found"
        getRowId={(row) => row.id}
        pagination={{
          page,
          pageSize,
          pageCount: data.pageCount,
          onPageChange: setPage,
          onPageSizeChange: handlePageSizeChange,
        }}
      />

      <ConfirmDialog
        open={pending !== null}
        onOpenChange={(open) => !open && setPending(null)}
        title={pending ? pendingTitle(pending) : ''}
        description={pending ? pendingDescription(pending) : null}
        confirmLabel={pending ? pendingConfirmLabel(pending) : 'Confirm'}
        variant={pending && pending.kind.startsWith('delete') ? 'destructive' : 'default'}
        onConfirm={runPending}
      />
    </div>
  );
}

function pendingTitle(p: PendingAction): string {
  switch (p.kind) {
    case 'requeue-one':
      return 'Requeue job?';
    case 'delete-one':
      return 'Delete job?';
    case 'requeue-bulk':
      return `Requeue ${p.ids.length} job${p.ids.length === 1 ? '' : 's'}?`;
    case 'delete-bulk':
      return `Delete ${p.ids.length} job${p.ids.length === 1 ? '' : 's'}?`;
    case 'requeue-by-type':
      return `Requeue all failed ${shortType(p.type)} jobs?`;
    case 'delete-by-type':
      return `Delete all failed ${shortType(p.type)} jobs?`;
  }
}

function pendingDescription(p: PendingAction): string {
  switch (p.kind) {
    case 'requeue-one':
      return 'The job will be re-enqueued and picked up by a worker on the next poll.';
    case 'delete-one':
      return 'The job will be removed permanently. This cannot be undone.';
    case 'requeue-bulk':
      return `All ${p.ids.length} selected job${p.ids.length === 1 ? '' : 's'} will be re-enqueued.`;
    case 'delete-bulk':
      return `${p.ids.length} selected job${p.ids.length === 1 ? '' : 's'} will be removed permanently. This cannot be undone.`;
    case 'requeue-by-type':
      return 'All failed jobs of this type will be re-enqueued. Depending on the type, this may be a large number of jobs.';
    case 'delete-by-type':
      return 'All failed jobs of this type will be removed permanently. This cannot be undone.';
  }
}

function pendingConfirmLabel(p: PendingAction): string {
  return p.kind.startsWith('delete') ? 'Delete' : 'Requeue';
}
