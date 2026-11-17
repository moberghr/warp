import { describe, expect, it } from 'vitest';
import { matchRoutes } from 'react-router-dom';
import { appRoutes } from '@/appRoutes';
import { decodeUrlSafeId } from '@/lib/urlSafeId';
import type { SavedView, SavedViewList } from '@/types/savedViews';
import {
  ANONYMOUS_USER,
  SAVED_VIEW_PAGES,
  activeView,
  canChangeSharing,
  currentViewQuery,
  defaultShared,
  pagePath,
  queryKeysFor,
  sameQuery,
  splitViews,
  toUpsert,
  validateViewName,
  validateViewQuery,
  viewCountsByInstrument,
  viewHref,
  viewsFor,
} from './savedViewsModel';

function view(partial: Partial<SavedView> & Pick<SavedView, 'id'>): SavedView {
  return {
    name: `View ${partial.id}`,
    page: 'metric',
    target: 'payment.results',
    query: '',
    shared: false,
    author: 'alice',
    createdAt: '2026-11-17T10:00:00Z',
    updatedAt: '2026-11-17T10:00:00Z',
    ...partial,
  };
}

describe('saved views model', () => {
  it('links every page to a real route, with the target in the path', () => {
    const cases: [SavedView, string][] = [
      [view({ id: 1, page: 'metric', target: 'http.server.request.duration', query: 'range=7d' }), '/metrics/:id'],
      [view({ id: 2, page: 'issues', target: null, query: 'source=endpoint' }), '/issues'],
      [view({ id: 3, page: 'jobs', target: 'failed', query: 'type=A.B' }), '/jobs/:state'],
      [view({ id: 4, page: 'jobs-by-type', target: 'Acme.Orders/Process Order', query: '' }), '/jobs/by-type/:type'],
    ];

    for (const [v, route] of cases) {
      const [path, query = ''] = viewHref(v).split('?');
      expect(matchRoutes(appRoutes, path)?.at(-1)?.route.path).toBe(route);
      expect(query).toBe(v.query);
    }

    expect(decodeUrlSafeId(pagePath('metric', 'http.server.request.duration').split('/')[2])).toBe('http.server.request.duration');
    expect(decodeURIComponent(pagePath('jobs-by-type', 'Acme.Orders/Process Order').split('/by-type/')[1])).toBe('Acme.Orders/Process Order');
    expect(viewHref(view({ id: 5, page: 'issues', target: null, query: '' }))).toBe('/issues');
  });

  it('keeps only the keys the page reads from the current URL, in URL order and with repeats', () => {
    expect(currentViewQuery('metric', '?range=7d&utm=x&filter=a%3Ab&filter=c%3Ad')).toBe('range=7d&filter=a%3Ab&filter=c%3Ad');
    expect(currentViewQuery('issues', 'status=resolved&page=2')).toBe('status=resolved');
    expect(currentViewQuery('jobs', new URLSearchParams('page=2&type=T&metrics=handler'))).toBe('page=2&type=T');
    expect(currentViewQuery('jobs-by-type', 'page=1&metrics=handler&type=T')).toBe('page=1&metrics=handler');
    expect(currentViewQuery('issues', '')).toBe('');
  });

  it("names the four pages, each with its page model's key list", () => {
    expect([...SAVED_VIEW_PAGES]).toEqual(['metric', 'issues', 'jobs', 'jobs-by-type']);
    expect(queryKeysFor('issues')).toEqual(['source', 'status', 'application', 'show4xx']);
    expect(queryKeysFor('jobs')).toEqual(['page', 'type']);
    expect(queryKeysFor('jobs-by-type')).toEqual(['page', 'metrics']);
    expect(queryKeysFor('metric')).toContain('filter');
  });

  it('compares queries ignoring parameter order', () => {
    expect(sameQuery('a=1&b=2', 'b=2&a=1')).toBe(true);
    expect(sameQuery('filter=x&filter=y', 'filter=y&filter=x')).toBe(true);
    expect(sameQuery('a=1', 'a=2')).toBe(false);
    expect(sameQuery('', '')).toBe(true);
  });

  it('splits Mine (authored by the caller, personal or shared) from Shared (shared by others)', () => {
    const list: SavedViewList = {
      user: 'alice',
      views: [
        view({ id: 1, author: 'alice', shared: false }),
        view({ id: 2, author: 'alice', shared: true }),
        view({ id: 3, author: 'bob', shared: true }),
        view({ id: 4, author: 'bob', shared: false }),
      ],
    };

    const { mine, shared } = splitViews(list);

    expect(mine.map((x) => x.id)).toEqual([1, 2]);
    expect(shared.map((x) => x.id)).toEqual([3]);
  });

  it('defaults a new view to shared on an open dashboard and to personal for a signed-in or unknown user', () => {
    expect(defaultShared(ANONYMOUS_USER)).toBe(true);
    expect(defaultShared(undefined)).toBe(false);
    expect(defaultShared('alice')).toBe(false);
  });

  it('lets only the author change whether a view is shared', () => {
    expect(canChangeSharing({ author: 'alice' }, 'alice')).toBe(true);
    expect(canChangeSharing({ author: 'alice' }, 'bob')).toBe(false);
    expect(canChangeSharing({ author: 'alice' }, undefined)).toBe(false);
  });

  it('picks the selected view, else the one whose query matches, else none', () => {
    const views = [view({ id: 1, query: 'range=7d' }), view({ id: 2, query: 'range=1h&breakdown=x' })];

    expect(activeView(views, 'breakdown=x&range=1h', null)?.id).toBe(2);
    expect(activeView(views, 'range=30d', 1)?.id).toBe(1);
    expect(activeView(views, 'range=30d', 99)).toBeNull();
    expect(activeView(views, 'range=30d', null)).toBeNull();
  });

  it('filters the views of one page and target', () => {
    const views = [
      view({ id: 1, page: 'metric', target: 'a' }),
      view({ id: 2, page: 'metric', target: 'b' }),
      view({ id: 3, page: 'issues', target: null }),
    ];

    expect(viewsFor(views, 'metric', 'a').map((x) => x.id)).toEqual([1]);
    expect(viewsFor(views, 'issues', null).map((x) => x.id)).toEqual([3]);
  });

  it('checks the name and the query length like the server', () => {
    expect(validateViewName('')).toBe('A name is required.');
    expect(validateViewName('   ')).toBe('A name is required.');
    expect(validateViewName('n'.repeat(201))).toBe('At most 200 characters.');
    expect(validateViewName(' Checkout ')).toBeNull();
    expect(validateViewQuery('q'.repeat(2001))).toContain('2000');
    expect(validateViewQuery('range=7d')).toBeNull();
  });

  it('builds the upsert body with a trimmed name and no target on the issues page', () => {
    expect(toUpsert(0, ' Errors ', true, 'issues', 'ignored', 'source=job')).toEqual({ id: 0, name: 'Errors', page: 'issues', target: null, query: 'source=job', shared: true });
    expect(toUpsert(4, 'x', false, 'metric', 'c', 'range=7d')).toEqual({ id: 4, name: 'x', page: 'metric', target: 'c', query: 'range=7d', shared: false });
  });

  it('counts the metric views per instrument for the Metrics list', () => {
    const counts = viewCountsByInstrument([
      view({ id: 1, target: 'a' }),
      view({ id: 2, target: 'a' }),
      view({ id: 3, target: 'b' }),
      view({ id: 4, page: 'issues', target: null }),
    ]);

    expect([...counts]).toEqual([['a', 2], ['b', 1]]);
  });
});
