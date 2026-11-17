// Demo fixtures for saved views (§8.34): a few views on each page that has a Views menu, some the demo user's own and
// some shared by a teammate, each pointing at a demo instrument (data/metrics.ts) or job type (data.ts). The demo
// store keeps the session's saves and deletes in memory, applying the server's rules (visibility, who may edit, 404).

import type { SavedView, SavedViewList, SavedViewPage, SavedViewUpsert } from '@/types/savedViews';

/** The user the demo dashboard acts as; views by anyone else are a teammate's. */
export const DEMO_VIEW_USER = 'ops@example.com';

const TEAMMATE = 'payments-team@example.com';

const created = '2026-11-10T09:00:00.000Z';

function view(partial: Omit<SavedView, 'createdAt' | 'updatedAt'>): SavedView {
  return { ...partial, createdAt: created, updatedAt: created };
}

/** The views the demo starts with: at least one per page, both owners, personal and shared. */
export function demoSavedViewFixtures(): SavedView[] {
  return [
    view({ id: 1, name: 'Declines by gateway', page: 'metric', target: 'payment.results', query: 'breakdown=gateway&filter=result%3Adeclined&range=7d', shared: true, author: TEAMMATE }),
    view({ id: 2, name: 'Payments ok vs failed', page: 'metric', target: 'payment.results', query: 'view=ratio&breakdown=result&numerator=ok', shared: false, author: DEMO_VIEW_USER }),
    view({ id: 3, name: '5xx by route, last week', page: 'metric', target: 'http.server.request.duration', query: 'range=7d&breakdown=http.route&filter=http.response.status_code%3A5*&compare=7d', shared: true, author: DEMO_VIEW_USER }),
    view({ id: 4, name: 'Unresolved endpoint errors', page: 'issues', target: null, query: 'source=endpoint', shared: true, author: TEAMMATE }),
    view({ id: 5, name: 'Resolved, incl. 4xx', page: 'issues', target: null, query: 'status=resolved&show4xx=1', shared: false, author: DEMO_VIEW_USER }),
    view({ id: 6, name: 'Failed payments', page: 'jobs', target: 'failed', query: 'type=Acme.Payments.ProcessPaymentRequest', shared: true, author: TEAMMATE }),
    view({ id: 7, name: 'Order processing by handler', page: 'jobs-by-type', target: 'Acme.Orders.ProcessOrderRequest', query: 'metrics=handler', shared: false, author: DEMO_VIEW_USER }),
  ];
}

let store: SavedView[] | null = null;

function views(): SavedView[] {
  store ??= demoSavedViewFixtures();

  return store;
}

/** Back to the fixtures (tests). */
export function resetDemoSavedViews(): void {
  store = null;
}

/** GET /views: the demo user's own views plus every shared view of `page` / `target`, newest first. */
export function demoSavedViewList(params: { page?: string; target?: string }): SavedViewList {
  const visible = views()
    .filter((x) => x.shared || x.author === DEMO_VIEW_USER)
    .filter((x) => !params.page || x.page === (params.page as SavedViewPage))
    .filter((x) => !params.target || x.target === params.target)
    .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt) || b.id - a.id);

  return { user: DEMO_VIEW_USER, views: visible };
}

/** A demo write's outcome: the HTTP status and the body the server would answer. */
export type DemoViewResult = { status: number; body: unknown };

/** POST /views: create (id 0) or edit, with the server's 403 / 404 rules; the author is the demo user. */
export function demoUpsertSavedView(body: SavedViewUpsert, now = Date.now()): DemoViewResult {
  const all = views();
  const at = new Date(now).toISOString();
  if (!body.id) {
    const id = Math.max(0, ...all.map((x) => x.id)) + 1;
    all.push({ ...body, id, target: body.target ?? null, author: DEMO_VIEW_USER, createdAt: at, updatedAt: at });

    return { status: 200, body: { id } };
  }

  const existing = all.find((x) => x.id === body.id);
  if (!existing) {
    return { status: 404, body: { error: 'The saved view no longer exists: it was deleted while you edited it.' } };
  }

  const author = existing.author === DEMO_VIEW_USER;
  if ((!existing.shared || existing.shared !== body.shared) && !author) {
    return { status: 403, body: { error: 'Only the author of a saved view can edit it while it is personal, or change whether it is shared.' } };
  }

  Object.assign(existing, { name: body.name, page: body.page, target: body.target ?? null, query: body.query, shared: body.shared, updatedAt: at });

  return { status: 200, body: { id: existing.id } };
}

/** DELETE /views/{id}: a shared view by anyone, a personal view by its author. */
export function demoDeleteSavedView(id: number): DemoViewResult {
  const all = views();
  const index = all.findIndex((x) => x.id === id);
  if (index < 0) {
    return { status: 404, body: { error: 'The saved view no longer exists.' } };
  }

  if (!all[index].shared && all[index].author !== DEMO_VIEW_USER) {
    return { status: 403, body: { error: 'Only the author of a personal saved view can delete it.' } };
  }

  all.splice(index, 1);

  return { status: 204, body: {} };
}
