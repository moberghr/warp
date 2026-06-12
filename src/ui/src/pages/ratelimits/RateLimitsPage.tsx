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
import { RateLimitFormDialog } from '@/components/forms/RateLimitFormDialog';
import { ConfirmDialog } from '@/components/forms/ConfirmDialog';
import { usePageStore } from '@/stores/page';
import type { RateLimitFormValues } from '@/lib/schemas/rateLimit';
import type { RateLimitInfo } from '@/types';
import {
  useRateLimits,
  useUpsertRateLimit,
  useDeleteRateLimit,
} from '@/api/hooks/useRateLimits';

type EditState =
  | { mode: 'create' }
  | { mode: 'edit'; initial: RateLimitFormValues }
  | null;

export default function RateLimitsPage() {
  const [editState, setEditState] = useState<EditState>(null);
  const [confirmDelete, setConfirmDelete] = useState<{ name: string } | null>(null);
  const [sorting, setSorting] = useState<SortingState>([{ id: 'name', desc: false }]);

  const query = useRateLimits();
  const upsert = useUpsertRateLimit();
  const remove = useDeleteRateLimit();

  const limits = useMemo(() => query.data ?? [], [query.data]);
  const existingNames = useMemo(() => new Set(limits.map((x) => x.name)), [limits]);

  useEffect(() => {
    usePageStore.getState().set({
      title: 'Rate Limits',
      subtitle: 'Runtime overrides for [RateLimit] keys (count + window only; style and policy stay on the handler attribute)',
    });
    return () => usePageStore.getState().reset();
  }, []);

  const columns = useMemo<ColumnDef<RateLimitInfo>[]>(() => [
    {
      accessorKey: 'name',
      header: 'Name',
      cell: ({ row }) => <span className="font-mono">{row.original.name}</span>,
    },
    {
      accessorKey: 'count',
      header: 'Count',
      cell: ({ row }) => <span className="font-mono">{row.original.count}</span>,
    },
    {
      accessorKey: 'windowSeconds',
      header: 'Window (s)',
      cell: ({ row }) => <span className="font-mono">{row.original.windowSeconds}</span>,
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
                initial: {
                  name: row.original.name,
                  count: row.original.count,
                  windowSeconds: row.original.windowSeconds,
                },
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
            Rate limits addon not registered. Add <code className="font-mono">opt.AddRateLimit()</code> to enable.
          </div>
        </Panel>
      </div>
    );
  }

  const handleSubmit = async (values: RateLimitFormValues) => {
    await upsert.mutateAsync({
      name: values.name,
      count: values.count,
      windowSeconds: values.windowSeconds,
    });
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
          Runtime overrides for <code>[RateLimit]</code> keys. Admin row beats the attribute count and window; takes effect on next pickup.
        </p>
        <Button onClick={() => setEditState({ mode: 'create' })}>
          <Plus className="h-4 w-4" />
          Add rate limit
        </Button>
      </div>

      {!query.data ? (
        <TableSkeleton rows={6} headers={['Name', 'Count', 'Window (s)', 'Updated', '']} />
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
                      No rate limits defined.
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

      <RateLimitFormDialog
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
        title="Delete rate limit?"
        description={
          confirmDelete ? (
            <>
              Remove the override for <code className="font-mono">{confirmDelete.name}</code>? Jobs will fall back to the attribute count and window.
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
