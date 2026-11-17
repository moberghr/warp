// The Views menu's pure model (saved views, §8.34): which page a view belongs to, the link it opens, the query it stores,
// the "Mine" / "Shared" split, the active view and the save form's defaults and checks. A view stores a page id plus
// that page's URL query string; the page's own *Model.ts owns which keys it reads, and the server's
// SavedViewValidation mirrors those lists (SavedViewQueryKeysSyncTests reads them from the files, and SAVED_VIEW_PAGES
// from this one).

import { encodeUrlSafeId } from '@/lib/urlSafeId';
import { issuesQueryKeys } from '@/pages/issues/issuesModel';
import { jobsByTypeQueryKeys, jobsQueryKeys } from '@/pages/jobs/jobsModel';
import { metricQueryKeys } from '@/pages/metrics/metricModel';
import type { SavedView, SavedViewList, SavedViewPage, SavedViewUpsert } from '@/types/savedViews';

/** The pages a view can be saved on, as the server's SavedViewValidation.Pages. */
export const SAVED_VIEW_PAGES = ['metric', 'issues', 'jobs', 'jobs-by-type'] as const;

export const MAX_VIEW_NAME_LENGTH = 200;

export const MAX_VIEW_QUERY_LENGTH = 2000;

/** The query key of every saved-view read; a write invalidates them all (the Metrics list counts read them too). */
export const SAVED_VIEWS_QUERY_KEY = ['saved-views'] as const;

/** The author the server stores when the dashboard has no signed-in user (an open dashboard). */
export const ANONYMOUS_USER = 'anonymous';

/** The query keys `page` reads; a saved query holds only these. */
export function queryKeysFor(page: SavedViewPage): readonly string[] {
  switch (page) {
    case 'metric':
      return metricQueryKeys;
    case 'issues':
      return issuesQueryKeys;
    case 'jobs':
      return jobsQueryKeys;
    case 'jobs-by-type':
      return jobsByTypeQueryKeys;
  }
}

/** The route of `page`: the metric id is the URL-safe base64 of the instrument, the job type is URI-encoded. */
export function pagePath(page: SavedViewPage, target: string | null): string {
  switch (page) {
    case 'metric':
      return `/metrics/${encodeUrlSafeId(target ?? '')}`;
    case 'issues':
      return '/issues';
    case 'jobs':
      return `/jobs/${encodeURIComponent(target ?? 'enqueued')}`;
    case 'jobs-by-type':
      return `/jobs/by-type/${encodeURIComponent(target ?? '')}`;
  }
}

/** Where choosing `view` navigates: its page's path plus `?query`. */
export function viewHref(view: Pick<SavedView, 'page' | 'target' | 'query'>): string {
  const path = pagePath(view.page, view.target);

  return view.query ? `${path}?${view.query}` : path;
}

/**
 * The query a view saved now would store: the current URL's parameters that `page` reads, in URL order. Anything else
 * in the URL (a parameter another feature added) is left out, so the server never rejects the save for it.
 */
export function currentViewQuery(page: SavedViewPage, search: string | URLSearchParams): string {
  const params = typeof search === 'string' ? new URLSearchParams(search) : search;
  const keys = queryKeysFor(page);
  const kept = new URLSearchParams();
  for (const [key, value] of params) {
    if (keys.includes(key)) {
      kept.append(key, value);
    }
  }

  return kept.toString();
}

/** Whether two queries hold the same parameters, ignoring their order. */
export function sameQuery(a: string, b: string): boolean {
  const canonical = (q: string) => [...new URLSearchParams(q)].map(([k, v]) => `${k}=${v}`).sort().join('&');

  return canonical(a) === canonical(b);
}

/** The views of one page and target (the server filters too; the demo and a stale cache may not). */
export function viewsFor(views: SavedView[], page: SavedViewPage, target: string | null): SavedView[] {
  return views.filter((x) => x.page === page && (x.target ?? null) === (target ?? null));
}

/**
 * The menu's two lists: "Mine" = views the caller authored (personal or shared), "Shared" = shared views of other users.
 * On an open dashboard every author is `anonymous`, so every view is the caller's.
 */
export function splitViews(list: SavedViewList, views: SavedView[] = list.views): { mine: SavedView[]; shared: SavedView[] } {
  return {
    mine: views.filter((x) => x.author === list.user),
    shared: views.filter((x) => x.author !== list.user && x.shared),
  };
}

/**
 * A new view defaults to shared only on an open dashboard (no user to keep a personal view for). An unknown user (the
 * list read is loading or failed) defaults to personal, so a view on an authenticated dashboard is never published by
 * accident.
 */
export function defaultShared(user: string | undefined): boolean {
  return user === ANONYMOUS_USER;
}

/** Only the author decides whether a view is shared (the server answers 403 otherwise). */
export function canChangeSharing(view: Pick<SavedView, 'author'>, user: string | undefined): boolean {
  return user !== undefined && view.author === user;
}

/**
 * The view the page is showing: the one the user picked (while it still exists on this page), else one whose stored
 * query equals the current one. Null when the page shows no saved view.
 */
export function activeView(views: SavedView[], query: string, selectedId: number | null): SavedView | null {
  const selected = selectedId === null ? undefined : views.find((x) => x.id === selectedId);

  return selected ?? views.find((x) => sameQuery(x.query, query)) ?? null;
}

/** The save form's check: a name is required and bounded; null when it is valid. */
export function validateViewName(name: string): string | null {
  const trimmed = name.trim();
  if (trimmed === '') {
    return 'A name is required.';
  }

  return trimmed.length > MAX_VIEW_NAME_LENGTH ? `At most ${MAX_VIEW_NAME_LENGTH} characters.` : null;
}

/** The query's check before a save: bounded like the server's column. */
export function validateViewQuery(query: string): string | null {
  return query.length > MAX_VIEW_QUERY_LENGTH ? `This page's settings are too long to save (over ${MAX_VIEW_QUERY_LENGTH} characters).` : null;
}

/** The body that saves `name` / `shared` with the page's current state; id 0 creates. */
export function toUpsert(
  id: number,
  name: string,
  shared: boolean,
  page: SavedViewPage,
  target: string | null,
  query: string,
): SavedViewUpsert {
  return { id, name: name.trim(), page, target: page === 'issues' ? null : target, query, shared };
}

/** The Metrics list's per-instrument count of the metric views the caller can see. */
export function viewCountsByInstrument(views: SavedView[]): Map<string, number> {
  const counts = new Map<string, number>();
  for (const view of views) {
    if (view.page === 'metric' && view.target) {
      counts.set(view.target, (counts.get(view.target) ?? 0) + 1);
    }
  }

  return counts;
}
