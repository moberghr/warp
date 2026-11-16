import { useState } from 'react';
import { useMutation, useQueryClient, type QueryKey } from '@tanstack/react-query';
import * as api from '@/api';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { ACKNOWLEDGED_NOISE_COLOR, ANNOTATION_COLOR } from '@/lib/deployMarkerPlugin';
import { errorMessage } from '@/lib/apiError';
import { MetricAnnotationTarget, type MetricAnnotation } from '@/types/annotations';
import {
  MAX_NOTE_LENGTH,
  describeScope,
  draftFromAnnotation,
  draftToUpsert,
  formatAnnotationRange,
  newAnnotationDraft,
  validateAnnotationDraft,
  type AnnotationDraft,
} from './annotationModel';

const inputClass = 'h-8 w-full rounded-md border border-border bg-background px-2 text-sm';

/**
 * The known-issue notes (#333) of one instrument or SLO objective: the list, and add / edit / delete in a modal. The
 * author is never typed: the server takes it from the signed-in dashboard user. `invalidate` is the query to refetch
 * after a change (the page's annotation read, and the board when it shows the same notes).
 */
export function AnnotationPanel({
  target,
  annotations,
  isTruncated,
  isError,
  invalidate,
}: {
  target: { instrument: string; tagFilter?: string | null } | { sloDefinitionId: number };
  annotations: MetricAnnotation[] | undefined;
  isTruncated: boolean;
  isError: boolean;
  invalidate: QueryKey;
}) {
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<AnnotationDraft | null>(null);
  const refresh = () => queryClient.invalidateQueries({ queryKey: invalidate });
  const saveMutation = useMutation({
    mutationFn: (d: AnnotationDraft) => api.upsertAnnotation(draftToUpsert(d)),
    onSuccess: () => {
      setDraft(null);
      void refresh();
    },
  });
  // Re-read on failure too: a 404 means the note is already gone, and the list should stop showing it.
  const deleteMutation = useMutation({ mutationFn: (id: number) => api.deleteAnnotation(id), onSettled: () => void refresh() });
  const [pendingDelete, setPendingDelete] = useState<MetricAnnotation | null>(null);
  const open = (d: AnnotationDraft | null) => {
    saveMutation.reset();
    setDraft(d);
  };

  return (
    <div className="rounded-xl border border-border p-4">
      <div className="mb-2 flex items-center justify-between">
        <h2 className="text-sm font-medium">Notes</h2>
        <button type="button" className="text-sm text-primary hover:underline" onClick={() => open(newAnnotationDraft(target, Date.now()))}>
          Add note
        </button>
      </div>
      {isError ? (
        <p className="text-sm text-destructive">Unable to load the notes.</p>
      ) : annotations === undefined ? (
        <p className="text-sm text-muted-foreground">Loading…</p>
      ) : annotations.length === 0 ? (
        <p className="text-sm text-muted-foreground">No notes. Record known, accepted noise here so it is not re-investigated.</p>
      ) : (
        <ul className="divide-y divide-border">
          {annotations.map((a) => (
            <li key={a.id} className="flex items-start justify-between gap-3 py-2 text-sm">
              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <span
                    className="inline-block h-3 w-3 shrink-0 rounded-sm"
                    style={{ backgroundColor: a.acknowledgedNoise ? ACKNOWLEDGED_NOISE_COLOR : ANNOTATION_COLOR }}
                  />
                  <span className="break-words">{a.note}</span>
                  {a.acknowledgedNoise && <span className="rounded bg-muted px-1.5 text-[11px] text-muted-foreground">acknowledged noise</span>}
                </div>
                <div className="mt-0.5 text-xs text-muted-foreground">
                  {formatAnnotationRange(a)} · {a.author} · <span className="font-mono">{describeScope(a)}</span>
                </div>
              </div>
              <span className="flex shrink-0 gap-3 text-xs">
                <button type="button" className="text-muted-foreground hover:underline" onClick={() => open(draftFromAnnotation(a))}>Edit</button>
                <button type="button" className="text-red-600 hover:underline" onClick={() => setPendingDelete(a)}>Delete</button>
              </span>
            </li>
          ))}
        </ul>
      )}
      {isTruncated && <p className="mt-2 text-xs text-muted-foreground">Showing the newest notes only.</p>}
      {deleteMutation.isError && (
        <p role="alert" className="mt-2 text-xs text-red-600">{errorMessage(deleteMutation.error, 'Failed to delete the note.')}</p>
      )}

      <ConfirmDialog
        open={pendingDelete !== null}
        onOpenChange={(isOpen) => !isOpen && setPendingDelete(null)}
        title="Delete this note?"
        description={pendingDelete ? <span className="break-words">{pendingDelete.note}</span> : undefined}
        confirmLabel="Delete"
        variant="destructive"
        onConfirm={() => {
          if (pendingDelete) {
            deleteMutation.mutate(pendingDelete.id);
          }
          setPendingDelete(null);
        }}
      />

      {draft && (
        <AnnotationForm
          draft={draft}
          onChange={setDraft}
          onCancel={() => open(null)}
          onSave={() => saveMutation.mutate(draft)}
          saving={saveMutation.isPending}
          serverError={saveMutation.isError ? errorMessage(saveMutation.error) : null}
        />
      )}
    </div>
  );
}

function AnnotationForm({
  draft,
  onChange,
  onCancel,
  onSave,
  saving,
  serverError,
}: {
  draft: AnnotationDraft;
  onChange: (d: AnnotationDraft) => void;
  onCancel: () => void;
  onSave: () => void;
  saving: boolean;
  serverError: string | null;
}) {
  const set = (patch: Partial<AnnotationDraft>) => onChange({ ...draft, ...patch });
  const clientError = validateAnnotationDraft(draft);
  const instrument = draft.target === MetricAnnotationTarget.Instrument;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onCancel}>
      <div className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-xl bg-white p-6 shadow-xl dark:bg-gray-900" onClick={(e) => e.stopPropagation()}>
        <h2 className="mb-1 text-lg font-semibold">{draft.id ? 'Edit note' : 'New note'}</h2>
        <p className="mb-4 text-xs text-muted-foreground">
          {instrument ? <>On <span className="font-mono">{draft.instrument}</span></> : 'On this objective'}. Your dashboard user is recorded as the author.
        </p>
        <div className="space-y-3 text-sm">
          <label className="block">
            <span className="mb-1 block text-xs uppercase tracking-wide text-muted-foreground">Note</span>
            <textarea
              className="min-h-20 w-full rounded-md border border-border bg-background p-2 text-sm"
              placeholder="Duplicate provider notification race, about once a day, predates this release"
              value={draft.note}
              onChange={(e) => set({ note: e.target.value })}
            />
            <span className="text-xs text-muted-foreground">{draft.note.trim().length} / {MAX_NOTE_LENGTH}</span>
          </label>
          {instrument && (
            <label className="block">
              <span className="mb-1 block text-xs uppercase tracking-wide text-muted-foreground">Tag filter (optional; k=v,k=v, trailing * allowed)</span>
              <input className={`${inputClass} font-mono`} placeholder="error.type=duplicate" value={draft.tagFilter} onChange={(e) => set({ tagFilter: e.target.value })} />
            </label>
          )}
          <label className="block">
            <span className="mb-1 block text-xs uppercase tracking-wide text-muted-foreground">From</span>
            <input type="datetime-local" className={inputClass} value={draft.from} onChange={(e) => set({ from: e.target.value })} />
          </label>
          <label className="block">
            <span className="mb-1 block text-xs uppercase tracking-wide text-muted-foreground">To (empty = ongoing)</span>
            <input type="datetime-local" className={inputClass} value={draft.to} onChange={(e) => set({ to: e.target.value })} />
          </label>
          {instrument && (
            <label className="flex items-start gap-2">
              <input type="checkbox" className="mt-0.5" checked={draft.acknowledgedNoise} onChange={(e) => set({ acknowledgedNoise: e.target.checked })} />
              <span>
                Acknowledged noise
                <span className="block text-xs text-muted-foreground">
                  Board tiles this note applies to read Acknowledged instead of Warning or Breaching while it is active. The events still count.
                </span>
              </span>
            </label>
          )}
        </div>
        {clientError && <p className="mt-4 text-xs text-amber-600">{clientError}</p>}
        {serverError && <p role="alert" className="mt-2 text-sm text-red-600">{serverError}</p>}
        <div className="mt-6 flex justify-end gap-2">
          <button type="button" onClick={onCancel} className="rounded-lg px-4 py-2 text-sm text-gray-600 hover:bg-gray-100 dark:hover:bg-gray-800">Cancel</button>
          <button
            type="button"
            onClick={onSave}
            disabled={saving || clientError !== null}
            className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {saving ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}
