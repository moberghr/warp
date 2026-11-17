import { useMemo, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import * as api from '@/api';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { errorMessage, errorStatus } from '@/lib/apiError';
import {
  activeView,
  canChangeSharing,
  currentViewQuery,
  defaultShared,
  SAVED_VIEWS_QUERY_KEY,
  sameQuery,
  splitViews,
  toUpsert,
  validateViewName,
  validateViewQuery,
  viewHref,
  viewsFor,
} from '@/lib/savedViewsModel';
import type { SavedView, SavedViewPage } from '@/types/savedViews';

const buttonClass = 'rounded-md border border-border px-2.5 py-1 text-sm hover:bg-muted disabled:opacity-50';
const inputClass = 'h-8 w-full rounded-md border border-border bg-background px-2 text-sm';

type Draft = { id: number; name: string; shared: boolean; query: string; sharingLocked: boolean };

/**
 * The "Views" menu of a page header (saved views, §8.34): save the page's current URL state under a name (personal, or
 * shared with everyone), reopen a view from "Mine" or "Shared", and update / rename / delete the active one. The author
 * is never sent: the server takes it from the signed-in dashboard user.
 */
export function ViewsMenu({ page, target }: { page: SavedViewPage; target: string | null }) {
  const location = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [pendingDelete, setPendingDelete] = useState<SavedView | null>(null);

  const listQuery = useQuery({
    queryKey: [...SAVED_VIEWS_QUERY_KEY, page, target],
    queryFn: () => api.getSavedViews(target === null ? { page } : { page, target }),
  });
  const refresh = () => queryClient.invalidateQueries({ queryKey: SAVED_VIEWS_QUERY_KEY });

  const query = currentViewQuery(page, location.search);
  const views = useMemo(() => viewsFor(listQuery.data?.views ?? [], page, target), [listQuery.data, page, target]);
  const lists = listQuery.data ? splitViews(listQuery.data, views) : { mine: [], shared: [] };
  const active = activeView(views, query, selectedId);
  const user = listQuery.data?.user;

  const saveMutation = useMutation({
    mutationFn: (d: Draft) => api.upsertSavedView(toUpsert(d.id, d.name, d.shared, page, target, d.query)),
    onSuccess: (result) => {
      setDraft(null);
      setSelectedId(result.id);
      void refresh();
    },
    // A 404: the view was deleted meanwhile; its { error } stays in the form, and the list stops showing it.
    onError: (error) => {
      if (errorStatus(error) === 404) {
        void refresh();
      }
    },
  });
  const updateMutation = useMutation({
    mutationFn: (view: SavedView) => api.upsertSavedView(toUpsert(view.id, view.name, view.shared, page, target, query)),
    onSettled: () => void refresh(),
  });
  const deleteMutation = useMutation({
    mutationFn: (id: number) => api.deleteSavedView(id),
    onSuccess: () => setSelectedId(null),
    onSettled: () => void refresh(),
  });

  const openForm = (d: Draft) => {
    saveMutation.reset();
    setDraft(d);
    setOpen(false);
  };
  const choose = (view: SavedView) => {
    setSelectedId(view.id);
    setOpen(false);
    updateMutation.reset();
    deleteMutation.reset();
    navigate(viewHref(view));
  };

  return (
    <div className="relative">
      <button type="button" className={buttonClass} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen((x) => !x)}>
        Views{active ? `: ${active.name}` : ''}
      </button>
      {open && (
        <>
          <div className="fixed inset-0 z-40" onClick={() => setOpen(false)} />
          <div role="menu" className="absolute right-0 z-50 mt-1 w-80 rounded-lg border border-border bg-card p-3 text-sm shadow-lg">
            <button
              type="button"
              className="w-full rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
              onClick={() => openForm({ id: 0, name: '', shared: defaultShared(user), query, sharingLocked: false })}
            >
              Save view
            </button>

            {active && (
              <div className="mt-3 rounded-md border border-border p-2">
                <div className="truncate font-medium" title={active.name}>{active.name}</div>
                <div className="text-xs text-muted-foreground">{active.shared ? 'Shared' : 'Personal'} · {active.author}</div>
                <div className="mt-2 flex gap-2">
                  <button
                    type="button"
                    className={buttonClass}
                    disabled={sameQuery(active.query, query) || updateMutation.isPending}
                    title="Store the page's current settings in this view"
                    onClick={() => updateMutation.mutate(active)}
                  >
                    Update
                  </button>
                  <button
                    type="button"
                    className={buttonClass}
                    onClick={() => openForm({ id: active.id, name: active.name, shared: active.shared, query: active.query, sharingLocked: !canChangeSharing(active, user) })}
                  >
                    Rename
                  </button>
                  <button type="button" className={`${buttonClass} text-red-600`} onClick={() => { setPendingDelete(active); setOpen(false); }}>
                    Delete
                  </button>
                </div>
              </div>
            )}

            {listQuery.isError ? (
              <p role="alert" className="mt-3 text-xs text-destructive">{errorMessage(listQuery.error, 'Unable to load the saved views.')}</p>
            ) : listQuery.isLoading ? (
              <p className="mt-3 text-xs text-muted-foreground">Loading…</p>
            ) : (
              <>
                <ViewList title="Mine" views={lists.mine} activeId={active?.id ?? null} onChoose={choose} empty="No views of yours on this page yet." />
                <ViewList title="Shared" views={lists.shared} activeId={active?.id ?? null} onChoose={choose} empty="No views shared by others." />
              </>
            )}
          </div>
        </>
      )}
      {updateMutation.isError && (
        <p role="alert" className="absolute right-0 mt-1 w-72 text-right text-xs text-red-600">{errorMessage(updateMutation.error, 'Failed to update the view.')}</p>
      )}
      {deleteMutation.isError && (
        <p role="alert" className="absolute right-0 mt-1 w-72 text-right text-xs text-red-600">{errorMessage(deleteMutation.error, 'Failed to delete the view.')}</p>
      )}

      <ConfirmDialog
        open={pendingDelete !== null}
        onOpenChange={(isOpen) => !isOpen && setPendingDelete(null)}
        title="Delete this saved view?"
        description={pendingDelete ? (
          <span className="break-words">
            {pendingDelete.name}
            {pendingDelete.shared ? ' is shared: it is deleted for everyone.' : ''}
          </span>
        ) : undefined}
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
        <ViewForm
          draft={draft}
          onChange={setDraft}
          onCancel={() => setDraft(null)}
          onSave={() => saveMutation.mutate(draft)}
          saving={saveMutation.isPending}
          serverError={saveMutation.isError ? errorMessage(saveMutation.error, 'Failed to save the view.') : null}
        />
      )}
    </div>
  );
}

function ViewList({
  title,
  views,
  activeId,
  onChoose,
  empty,
}: {
  title: string;
  views: SavedView[];
  activeId: number | null;
  onChoose: (view: SavedView) => void;
  empty: string;
}) {
  return (
    <div className="mt-3">
      <div className="mb-1 text-xs font-medium uppercase tracking-wide text-muted-foreground">{title}</div>
      {views.length === 0 ? (
        <p className="text-xs text-muted-foreground">{empty}</p>
      ) : (
        <ul className="max-h-48 overflow-y-auto">
          {views.map((v) => (
            <li key={v.id}>
              <button
                type="button"
                role="menuitem"
                className={`flex w-full items-center justify-between gap-2 rounded px-2 py-1 text-left hover:bg-muted ${v.id === activeId ? 'font-medium' : ''}`}
                onClick={() => onChoose(v)}
              >
                <span className="truncate" title={v.name}>{v.name}</span>
                <span className="shrink-0 text-[11px] text-muted-foreground">{title === 'Mine' ? (v.shared ? 'shared' : 'personal') : v.author}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function ViewForm({
  draft,
  onChange,
  onCancel,
  onSave,
  saving,
  serverError,
}: {
  draft: Draft;
  onChange: (d: Draft) => void;
  onCancel: () => void;
  onSave: () => void;
  saving: boolean;
  serverError: string | null;
}) {
  const clientError = validateViewName(draft.name) ?? validateViewQuery(draft.query);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onCancel}>
      <div className="w-full max-w-md rounded-xl bg-white p-6 shadow-xl dark:bg-gray-900" onClick={(e) => e.stopPropagation()}>
        <h2 className="mb-4 text-lg font-semibold">{draft.id ? 'Rename view' : 'Save view'}</h2>
        <div className="space-y-3 text-sm">
          <label className="block">
            <span className="mb-1 block text-xs text-muted-foreground">Name</span>
            <input className={inputClass} value={draft.name} autoFocus onChange={(e) => onChange({ ...draft, name: e.target.value })} />
          </label>
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              checked={draft.shared}
              disabled={draft.sharingLocked}
              onChange={(e) => onChange({ ...draft, shared: e.target.checked })}
            />
            <span>Shared with everyone who can open the dashboard</span>
          </label>
          {draft.sharingLocked && <p className="text-xs text-muted-foreground">Only the view's author can change whether it is shared.</p>}
        </div>
        {clientError && draft.name !== '' && <p className="mt-4 text-xs text-amber-600">{clientError}</p>}
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
