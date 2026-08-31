import { useEffect, useState } from 'react';
import axios from 'axios';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { LoadingState, ErrorState } from '@/components/PageState';
import { RelativeTime } from '@/components/RelativeTime';
import {
  useConcurrencyLimits,
  useUpsertConcurrencyLimit,
  useDeleteConcurrencyLimit,
} from '@/api/hooks/useConcurrencyLimits';
import { Check, Pencil, Plus, Trash2, X } from 'lucide-react';
import type { ConcurrencyLimitInfo } from '@/types';
import { PageHeading } from '@/components/PageHeading';
import { Hint } from '@/components/ui/tooltip';

type ConfirmDelete = { name: string } | null;

export default function ConcurrencyLimitsPage() {
  const { data: limits, isLoading, isError, error } = useConcurrencyLimits();
  const upsert = useUpsertConcurrencyLimit();
  const remove = useDeleteConcurrencyLimit();

  const [editingName, setEditingName] = useState<string | null>(null);
  const [editValue, setEditValue] = useState<string>('');
  const [editError, setEditError] = useState<string | null>(null);
  const [showAdd, setShowAdd] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState<ConfirmDelete>(null);

  const unavailable = isError && axios.isAxiosError(error) && error.response?.status === 404;

  const startEdit = (limit: ConcurrencyLimitInfo) => {
    setEditingName(limit.name);
    setEditValue(String(limit.limit));
    setEditError(null);
  };

  const cancelEdit = () => {
    setEditingName(null);
    setEditValue('');
    setEditError(null);
  };

  const saveEdit = (name: string) => {
    const parsed = Number(editValue);
    if (!Number.isInteger(parsed) || parsed < 1) {
      setEditError('Limit must be a positive integer');

      return;
    }
    upsert.mutate(
      { name, limit: parsed },
      {
        onSuccess: () => cancelEdit(),
        onError: () => setEditError('Failed to save'),
      },
    );
  };

  const handleDelete = (name: string) => {
    remove.mutate(name, { onSuccess: () => setConfirmDelete(null) });
  };

  if (isError && !unavailable) return <ErrorState message="Unable to load concurrency limits" />;
  if (isLoading || !limits) {
    if (unavailable) {
      return (
        <div>
          <PageHeading className="mb-2">Concurrency Limits</PageHeading>
          <Card>
            <CardContent className="py-8 text-center text-muted-foreground">
              Concurrency limits addon not registered. Add <code className="font-mono">opt.AddConcurrency()</code> to enable.
            </CardContent>
          </Card>
        </div>
      );
    }

    return <LoadingState />;
  }

  const sorted = [...limits].sort((a, b) => a.name.localeCompare(b.name));

  return (
    <div>
      <div className="flex items-center justify-between mb-2">
        <PageHeading className="">Concurrency Limits</PageHeading>
        <Button onClick={() => setShowAdd(true)}>
          <Plus className="h-4 w-4" />
          Add limit
        </Button>
      </div>
      <p className="text-sm text-muted-foreground mb-4">
        Runtime overrides for <code>[Mutex]</code> and <code>[Semaphore]</code> keys. Admin row beats the attribute limit; takes effect on next pickup.
      </p>

      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead className="w-40">Limit</TableHead>
                <TableHead>Updated</TableHead>
                <TableHead className="text-right w-32">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {sorted.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={4} className="text-center text-muted-foreground py-8">
                    No concurrency limits defined.
                  </TableCell>
                </TableRow>
              ) : (
                sorted.map((limit) => {
                  const isEditing = editingName === limit.name;

                  return (
                    <TableRow key={limit.name}>
                      <TableCell className="font-mono">{limit.name}</TableCell>
                      <TableCell>
                        {isEditing ? (
                          <div className="flex items-center gap-1">
                            <input
                              type="number"
                              min={1}
                              autoFocus
                              value={editValue}
                              onChange={(e: React.ChangeEvent<HTMLInputElement>) => setEditValue(e.target.value)}
                              onKeyDown={(e: React.KeyboardEvent<HTMLInputElement>) => {
                                if (e.key === 'Enter') {
                                  saveEdit(limit.name);
                                } else if (e.key === 'Escape') {
                                  cancelEdit();
                                }
                              }}
                              className="w-20 rounded-md border border-input bg-background px-2 py-1 text-sm"
                            />
                            <Hint text="Save">
                              <Button variant="ghost" size="icon-sm" onClick={() => saveEdit(limit.name)} aria-label="Save">
                                <Check className="h-4 w-4" />
                              </Button>
                            </Hint>
                            <Hint text="Cancel">
                              <Button variant="ghost" size="icon-sm" onClick={cancelEdit} aria-label="Cancel">
                                <X className="h-4 w-4" />
                              </Button>
                            </Hint>
                            {editError && (
                              <span className="text-xs text-destructive ml-2">{editError}</span>
                            )}
                          </div>
                        ) : (
                          <Hint text="Click to edit">
                            <button
                              type="button"
                              onClick={() => startEdit(limit)}
                              className="font-mono hover:underline focus:outline-none"
                            >
                              {limit.limit}
                            </button>
                          </Hint>
                        )}
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">
                        <RelativeTime date={limit.updatedAt} />
                      </TableCell>
                      <TableCell className="text-right">
                        {!isEditing && (
                          <>
                            <Button variant="ghost" size="sm" onClick={() => startEdit(limit)}>
                              <Pencil className="h-4 w-4" />
                              Edit
                            </Button>
                            <Button
                              variant="ghost"
                              size="sm"
                              className="text-destructive"
                              onClick={() => setConfirmDelete({ name: limit.name })}
                            >
                              <Trash2 className="h-4 w-4" />
                              Delete
                            </Button>
                          </>
                        )}
                      </TableCell>
                    </TableRow>
                  );
                })
              )}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      {showAdd && (
        <AddLimitModal
          onClose={() => setShowAdd(false)}
          onSaved={() => setShowAdd(false)}
          existingNames={new Set(limits.map((x) => x.name))}
        />
      )}

      {confirmDelete && (
        <ConfirmDeleteModal
          name={confirmDelete.name}
          onCancel={() => setConfirmDelete(null)}
          onConfirm={() => handleDelete(confirmDelete.name)}
        />
      )}
    </div>
  );
}

function ModalShell({
  title,
  children,
  onClose,
}: {
  title: string;
  children: React.ReactNode;
  onClose: () => void;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
      }
    };
    window.addEventListener('keydown', onKey);

    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
      onClick={onClose}
    >
      <div
        className="w-full max-w-md rounded-xl bg-card p-6 ring-1 ring-foreground/10 shadow-lg"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="text-lg font-semibold mb-4">{title}</h2>
        {children}
      </div>
    </div>
  );
}

function AddLimitModal({
  onClose,
  onSaved,
  existingNames,
}: {
  onClose: () => void;
  onSaved: () => void;
  existingNames: Set<string>;
}) {
  const upsert = useUpsertConcurrencyLimit();
  const [name, setName] = useState('');
  const [limit, setLimit] = useState('5');
  const [error, setError] = useState<string | null>(null);

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    const trimmed = name.trim();
    if (!trimmed) {
      setError('Name is required');

      return;
    }
    if (existingNames.has(trimmed)) {
      setError('A limit with that name already exists');

      return;
    }
    const parsed = Number(limit);
    if (!Number.isInteger(parsed) || parsed < 1) {
      setError('Limit must be a positive integer');

      return;
    }

    upsert.mutate(
      { name: trimmed, limit: parsed },
      {
        onSuccess: () => onSaved(),
        onError: () => setError('Failed to save'),
      },
    );
  };

  return (
    <ModalShell title="Add concurrency limit" onClose={onClose}>
      <form onSubmit={submit} className="space-y-4">
        <div>
          <label htmlFor="cc-name" className="text-sm font-medium block mb-1">Name</label>
          <input
            id="cc-name"
            autoFocus
            value={name}
            onChange={(e: React.ChangeEvent<HTMLInputElement>) => setName(e.target.value)}
            placeholder="e.g. payment-api"
            className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm font-mono"
          />
        </div>
        <div>
          <label htmlFor="cc-limit" className="text-sm font-medium block mb-1">Limit</label>
          <input
            id="cc-limit"
            type="number"
            min={1}
            value={limit}
            onChange={(e: React.ChangeEvent<HTMLInputElement>) => setLimit(e.target.value)}
            className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
          />
        </div>
        {error && <div className="text-sm text-destructive">{error}</div>}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose} disabled={upsert.isPending}>
            Cancel
          </Button>
          <Button type="submit" disabled={upsert.isPending}>
            {upsert.isPending ? 'Saving...' : 'Save'}
          </Button>
        </div>
      </form>
    </ModalShell>
  );
}

function ConfirmDeleteModal({
  name,
  onCancel,
  onConfirm,
}: {
  name: string;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <ModalShell title="Delete concurrency limit?" onClose={onCancel}>
      <p className="text-sm text-muted-foreground mb-4">
        Remove the override for <code className="font-mono">{name}</code>? Jobs will fall back to the attribute limit.
      </p>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        <Button type="button" variant="destructive" onClick={onConfirm}>
          Delete
        </Button>
      </div>
    </ModalShell>
  );
}
