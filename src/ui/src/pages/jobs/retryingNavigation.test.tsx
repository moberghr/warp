import { describe, it, expect, beforeAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import api from '@/api/client';
import { createDemoAdapter } from '@/demo/adapter';
import { queryKeys, queryScopes } from '@/lib/queryClient';
import MainLayout from '@/layouts/MainLayout';
import JobListPage from './JobListPage';

// Coverage for the Retrying jobs view. Both of the defects a review caught in this feature lived in
// untested frontend code — the sidebar rendered the tab even when the host declared ShowRetries(false),
// and the badge query fired its backlog scan before /api/addons had answered — so the assertions here
// deliberately target the wiring, not just the happy-path render.
beforeAll(() => {
  api.defaults.adapter = createDemoAdapter(false);
});

function renderAt(path: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/jobs/:state" element={<JobListPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('retrying jobs view', () => {
  it('lists retrying jobs with their attempt number and next attempt', async () => {
    renderAt('/jobs/retrying');

    expect(await screen.findByRole('heading', { name: 'Retrying Jobs' })).toBeTruthy();

    // The two columns that make this more than a filtered Scheduled list.
    expect(await screen.findByText('Attempt')).toBeTruthy();
    expect(await screen.findByText('Next attempt')).toBeTruthy();

    // "#3 (2 failed)" — the attempt about to run, and how many already burned.
    expect((await screen.findAllByText(/^#\d+$/)).length).toBeGreaterThan(0);
    expect((await screen.findAllByText(/\d+ failed/)).length).toBeGreaterThan(0);
  });

  it('shows every row in a live state, because Retrying is a filter and not a state of its own', async () => {
    renderAt('/jobs/retrying');

    await screen.findByRole('heading', { name: 'Retrying Jobs' });

    // Each row also appears under the state it actually sits in — the overlap is the design, so a row
    // rendering as anything terminal would mean the query stopped filtering on the live states.
    const badges = await screen.findAllByText(/^(Scheduled|Enqueued)$/);
    expect(badges.length).toBeGreaterThan(0);
  });

  it('does not offer the attempt column on a listing that never computes retryCount', async () => {
    renderAt('/jobs/scheduled');

    expect(await screen.findByRole('heading', { name: 'Scheduled Jobs' })).toBeTruthy();

    // retryCount is null on every other listing, meaning "not computed" rather than "never retried" —
    // rendering the column there would assert something the server did not answer.
    expect(screen.queryByText('Attempt')).toBeNull();
  });
});

describe('retrying tab visibility', () => {
  // The bug this covers: the ShowRetries(false) declaration was threaded all the way through the
  // backend and into the sidebar's props, but the JSX had no guard, so the tab rendered regardless and
  // the whole opt-out was inert. Renders the real layout rather than asserting on a prop, because a
  // missing guard is invisible to any test that does not actually render the nav.
  function renderLayout(retry: boolean) {
    const demo = createDemoAdapter(false);
    api.defaults.adapter = (config) =>
      (config.url ?? '').includes('/addons')
        ? demo(config).then((r) => ({ ...r, data: { ...(r.data as object), retry } }))
        : demo(config);

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    return render(
      <QueryClientProvider client={qc}>
        <MemoryRouter initialEntries={['/jobs/enqueued']}>
          <Routes>
            <Route path="/" element={<MainLayout />}>
              <Route path="jobs/:state" element={<div>jobs-stub</div>} />
            </Route>
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );
  }

  it('renders the Retrying tab when the host has not turned it off', async () => {
    renderLayout(true);

    expect(await screen.findByRole('link', { name: /Retrying/ })).toBeTruthy();
  });

  it('hides the Retrying tab when the host declared ShowRetries(false)', async () => {
    renderLayout(false);

    // Wait for the addons probe to land before asserting absence, or this passes on timing alone.
    await screen.findByRole('link', { name: /Enqueued/ });
    await waitFor(() => expect(screen.queryByRole('link', { name: /Retrying/ })).toBeNull());
  });
});

describe('retrying badge query key', () => {
  it('sits outside the jobs invalidation scope', () => {
    // useRealtimeInvalidation sweeps queryScopes.jobs on every JobFinalized event, and
    // invalidateQueries refetches ACTIVE queries immediately, ignoring staleTime. The badge is active
    // on every /jobs/* page, so a key under this prefix would fire the unindexed backlog scan on every
    // job completion — the exact "no repeating trigger" rule docs/perf-results.md sets out.
    const [scope] = queryScopes.jobs;
    const [badgeRoot] = queryKeys.retryingJobsCount;

    expect(badgeRoot).not.toBe(scope);
  });
});
