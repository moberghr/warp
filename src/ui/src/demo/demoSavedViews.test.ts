import { beforeEach, describe, expect, it } from 'vitest';
import type { InternalAxiosRequestConfig } from 'axios';
import { matchRoutes } from 'react-router-dom';
import { appRoutes } from '@/appRoutes';
import { SAVED_VIEW_PAGES, currentViewQuery, sameQuery, viewHref } from '@/lib/savedViewsModel';
import type { SavedViewList } from '@/types/savedViews';
import { createDemoAdapter } from './adapter';
import { demoJobTypeNames } from './data';
import { demoMetrics } from './data/metrics';
import {
  DEMO_VIEW_USER,
  demoDeleteSavedView,
  demoSavedViewFixtures,
  demoSavedViewList,
  demoUpsertSavedView,
  resetDemoSavedViews,
} from './data/savedViews';

// The saved views the demo serves: every page with a Views menu has one, each opens a real route on a demo instrument or
// job type, and each stored query holds only the keys its page reads (the server would refuse anything else).
describe('demo saved views', () => {
  beforeEach(() => resetDemoSavedViews());

  it('covers every page, both owners and both visibilities', () => {
    const fixtures = demoSavedViewFixtures();

    expect(new Set(fixtures.map((x) => x.page))).toEqual(new Set(SAVED_VIEW_PAGES));
    expect(fixtures.some((x) => x.author === DEMO_VIEW_USER)).toBe(true);
    expect(fixtures.some((x) => x.author !== DEMO_VIEW_USER && x.shared)).toBe(true);
    expect(fixtures.some((x) => !x.shared)).toBe(true);
  });

  it('stores only the keys each page reads and links to a real route on demo data', () => {
    const instruments = new Set(demoMetrics.map((x) => x.name));
    const types = new Set(demoJobTypeNames);

    for (const v of demoSavedViewFixtures()) {
      expect(sameQuery(currentViewQuery(v.page, v.query), v.query)).toBe(true);
      expect(matchRoutes(appRoutes, viewHref(v).split('?')[0])).not.toBeNull();
      if (v.page === 'metric') {
        expect(instruments.has(v.target ?? '')).toBe(true);
      }
      if (v.page === 'jobs-by-type') {
        expect(types.has(v.target ?? '')).toBe(true);
      }
      if (v.page === 'issues') {
        expect(v.target).toBeNull();
      }
    }
  });

  it("lists the demo user's own and every shared view of a page", () => {
    const list = demoSavedViewList({ page: 'metric', target: 'payment.results' });

    expect(list.user).toBe(DEMO_VIEW_USER);
    expect(list.views.map((x) => x.name).sort()).toEqual(['Declines by gateway', 'Payments ok vs failed']);
  });

  it('saves, edits and deletes with the server rules', () => {
    const created = demoUpsertSavedView({ id: 0, name: 'Mine', page: 'issues', target: null, query: 'status=ignored', shared: false });
    const id = (created.body as { id: number }).id;
    const teammateShared = demoSavedViewFixtures().find((x) => x.author !== DEMO_VIEW_USER && x.shared);
    if (!teammateShared) {
      throw new Error('no shared teammate view');
    }

    expect(created.status).toBe(200);
    expect(demoSavedViewList({ page: 'issues' }).views.find((x) => x.id === id)?.author).toBe(DEMO_VIEW_USER);
    expect(demoUpsertSavedView({ ...teammateShared, name: 'Renamed' }).status).toBe(200);
    expect(demoUpsertSavedView({ ...teammateShared, shared: false }).status).toBe(403);
    expect(demoDeleteSavedView(id).status).toBe(204);
    expect(demoDeleteSavedView(id).status).toBe(404);
    expect(demoUpsertSavedView({ id, name: 'Back', page: 'issues', target: null, query: '', shared: false }).status).toBe(404);
  });

  it('serves the routes through the demo adapter', async () => {
    const adapter = createDemoAdapter(false);

    const response = await adapter({ url: '/views', baseURL: '', method: 'get', params: { page: 'jobs' } } as InternalAxiosRequestConfig);
    const missing = adapter({ url: '/views/999', baseURL: '', method: 'delete' } as InternalAxiosRequestConfig);

    expect((response.data as SavedViewList).views.map((x) => x.name)).toEqual(['Failed payments']);
    await expect(missing).rejects.toMatchObject({ response: { status: 404 } });
  });
});
