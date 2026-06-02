import { useEffect, useMemo, useState } from 'react';
import axios from 'axios';
import {
  flexRender,
  getCoreRowModel,
  getSortedRowModel,
  useReactTable,
  type ColumnDef,
  type SortingState,
} from '@tanstack/react-table';
import { Panel } from '@/components/v2/Panel';
import { Button } from '@/components/ui/button';
import { ErrorState } from '@/components/PageState';
import { RelativeTime } from '@/components/RelativeTime';
import { Pencil, Plus, Trash2, ChevronDown, ChevronUp, ChevronsUpDown } from 'lucide-react';
import { TableSkeleton } from '@/components/skeletons/TableSkeleton';
import { ConcurrencyLimitFormDialog } from '@/components/forms/ConcurrencyLimitFormDialog';
import { ConfirmDialog } from '@/components/forms/ConfirmDialog';
import { usePageStore } from '@/stores/page';
import type { ConcurrencyLimitFormValues } from '@/lib/schemas/concurrencyLimit';
import type { ConcurrencyLimitInfo } from '@/types';
import {
  useConcurrencyLimits,
  useUpsertConcurrencyLimit,
  useDeleteConcurrencyLimit,
} from '@/api/hooks/useConcurrencyLimits';

type EditState =
  | { mode: 'create' }
  | { mode: 'edit'; initial: ConcurrencyLimitFormValues }
  | null;

export default function ConcurrencyLimitsPage() {
  const [editState, setEditState] = useState<EditState>(null);
  const [confirmDelete, setConfirmDelete] = useState<{ name: string } | null>(null);
  const [sorting, setSorting] = useState<SortingState>([{ id: 'name', desc: false }]);

  const query = useConcurrencyLimits();
  const upsert = useUpsertConcurrencyLimit();
  const remove = useDeleteConcurrencyLimit();

  const limits = useMemo(() => query.data ?? [], [query.data]);
  const existingNames = useMemo(() => new Set(limits.map((x) => x.name)), [limits]);

  useEffect(() => {
    usePageStore.getState().set({
      title: 'Concurrency Limits',
      subtitle: 'Runtime overrides for [Mutex] and [Semaphore] keys',
    });
    return () => usePageStore.getState().reset();
  }, []);

  const columns = useMemo<ColumnDef<ConcurrencyLimitInfo>[]>(() => [
    {
      accessorKey: 'name',
      header: 'Name',
      cell: ({ row }) => <span className="font-mono">{row.original.name}</span>,
    },
    {
      accessorKey: 'limit',
      header: 'Limit',
      cell: ({ row }) => <span className="font-mono">{row.original.limit}</span>,
    },
    {
      id: 'kind',
      header: 'Kind',
      enableSorting: false,
      cell: ({ row }) => {
        const isMutex = row.original.limit === 1;

        return (
          <span
            className={
              'inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium ' +
              (isMutex
                ? 'bg-warp-purple-soft text-warp-purple'
                : 'bg-warp-blue-soft text-warp-blue')
            }
            title={isMutex ? 'Limit = 1 (Mutex semantics)' : 'Limit ≥ 2 (Semaphore semantics)'}
          >
            {isMutex ? 'Mutex' : 'Semaphore'}
          </span>
        );
      },
    },
    {
      accessorKey: 'updatedAt',
      header: 'Updated',
      cell: ({ row }) => (
        <span className="text-text-mute">
          <RelativeTime date={row.original.updatedAt} />
        </span>
      ),
    },
    {
      id: 'actions',
      header: '',
      enableSorting: false,
      cell: ({ row }) => (
        <div className="text-right">
          <Button
            variant="ghost"
            size="sm"
            onClick={() =>
              setEditState({
                mode: 'edit',
                initial: { name: row.original.name, limit: row.original.limit },
              })
            }
          >
            <Pencil className="h-4 w-4" />
            Edit
          </Button>
          <Button
            variant="ghost"
            size="sm"
            className="text-destructive"
            onClick={() => setConfirmDelete({ name: row.original.name })}
          >
            <Trash2 className="h-4 w-4" />
            Delete
          </Button>
        </div>
      ),
    },
  ], []);

  const table = useReactTable({
    data: limits,
    columns,
    state: { sorting },
    onSortingChange: setSorting,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
  });

  const unavailable =
    query.error !== null &&
    query.error !== undefined &&
    axios.isAxiosError(query.error) &&
    query.error.response?.status === 404;

  if (query.error && !unavailable) return <ErrorState message={(query.error as Error).message} />;

  if (unavailable) {
    return (
      <div className="flex flex-col gap-3 py-5">
        <Panel>
          <div className="py-10 text-center text-[13px] text-text-mute">
            Concurrency limits addon not registered. Add <code className="font-mono">opt.AddConcurrency()</code> to enable.
          </div>
        </Panel>
      </div>
    );
  }

  const handleSubmit = async (values: ConcurrencyLimitFormValues) => {
    await upsert.mutateAsync({ name: values.name, limit: values.limit });
  };

  const handleDelete = async () => {
    if (!confirmDelete) return;
    await remove.mutateAsync(confirmDelete.name);
    setConfirmDelete(null);
  };

  return (
    <div className="flex flex-col gap-3 py-5">
      <div className="flex items-center justify-between">
        <p className="text-[12.5px] text-text-mute">
          Runtime overrides for <code>[Mutex]</code> and <code>[Semaphore]</code> keys. Admin row beats the attribute limit; takes effect on next pickup.
        </p>
        <Button onClick={() => setEditState({ mode: 'create' })}>
          <Plus className="h-4 w-4" />
          Add limit
        </Button>
      </div>

      {!query.data ? (
        <TableSkeleton rows={6} headers={['Name', 'Limit', 'Kind', 'Updated', '']} />
      ) : (
        <Panel className="overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full border-collapse">
              <thead>
                {table.getHeaderGroups().map((hg) => (
                  <tr key={hg.id} className="bg-panel-2 border-b border-border">
                    {hg.headers.map((header) => {
                      const canSort = header.column.getCanSort();
                      const sorted = header.column.getIsSorted();

                      return (
                        <th
                          key={header.id}
                          className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold"
                        >
                          {header.isPlaceholder ? null : canSort ? (
                            <button
                              type="button"
                              onClick={header.column.getToggleSortingHandler()}
                              className="inline-flex items-center gap-1 hover:text-foreground transition-colors"
                            >
                              {flexRender(header.column.columnDef.header, header.getContext())}
                              {sorted === 'asc' ? (
                                <ChevronUp className="h-3 w-3" />
                              ) : sorted === 'desc' ? (
                                <ChevronDown className="h-3 w-3" />
                              ) : (
                                <ChevronsUpDown className="h-3 w-3 opacity-40" />
                              )}
                            </button>
                          ) : (
                            flexRender(header.column.columnDef.header, header.getContext())
                          )}
                        </th>
                      );
                    })}
                  </tr>
                ))}
              </thead>
              <tbody>
                {table.getRowModel().rows.length === 0 ? (
                  <tr>
                    <td colSpan={columns.length} className="text-center text-text-mute py-10 text-[13px]">
                      No concurrency limits defined.
                    </td>
                  </tr>
                ) : (
                  table.getRowModel().rows.map((row) => (
                    <tr key={row.id} className="border-b border-border last:border-b-0 hover:bg-panel-2/60">
                      {row.getVisibleCells().map((cell) => (
                        <td key={cell.id} className="px-3.5 py-2 text-[12.5px]">
                          {flexRender(cell.column.columnDef.cell, cell.getContext())}
                        </td>
                      ))}
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </Panel>
      )}

      <ConcurrencyLimitFormDialog
        open={editState !== null}
        onOpenChange={(open) => {
          if (!open) {
            setEditState(null);
          }
        }}
        mode={editState?.mode ?? 'create'}
        initial={editState?.mode === 'edit' ? editState.initial : undefined}
        existingNames={existingNames}
        onSubmit={handleSubmit}
      />

      <ConfirmDialog
        open={confirmDelete !== null}
        onOpenChange={(open) => {
          if (!open) {
            setConfirmDelete(null);
          }
        }}
        title="Delete concurrency limit?"
        description={
          confirmDelete ? (
            <>
              Remove the override for <code className="font-mono">{confirmDelete.name}</code>? Jobs will fall back to the attribute limit.
            </>
          ) : null
        }
        confirmLabel="Delete"
        destructive
        onConfirm={handleDelete}
      />
    </div>
  );
}
