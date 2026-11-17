import { describe, it, expect, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Routes, Route, matchRoutes, useLocation } from 'react-router-dom';
import type { DashboardStatistics } from '@/types';
import { useDashboardStore } from '@/stores/dashboard';
import { appRoutes } from '@/appRoutes';
import DashboardPage from './DashboardPage';

// The count cards link into the list pages. A card pointing at a path App.tsx has no route for (the Messages card once
// linked to "/messages", which only exists as "/messages/:state") lands on a blank page, so each card is clicked and
// must reach a real route: the clicked location is matched against App.tsx's own route table (appRoutes).
vi.mock('@/api', () => ({ getStatsHistory: vi.fn(() => Promise.resolve([])) }));
vi.mock('@/components/RealtimeChart', () => ({ RealtimeChart: () => null }));
// happy-dom has no layout for Chart.js to measure; the history chart is not what this test is about.
vi.mock('chart.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('chart.js')>();

  class StubChart {
    static register() {}
    data = { labels: [] as unknown[], datasets: [{ data: [] as unknown[] }, { data: [] as unknown[] }] };
    update() {}
    destroy() {}
  }

  return { ...actual, Chart: StubChart };
});

// Every link lands here; the test then resolves the location against the real route table.
function Landed() {
  const { pathname } = useLocation();

  return <p data-testid="landed">{pathname}</p>;
}

function renderDashboard() {
  return render(
    <MemoryRouter initialEntries={['/']}>
      <Routes>
        <Route path="/" element={<DashboardPage />} />
        <Route path="*" element={<Landed />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  useDashboardStore.setState({
    stats: {
      created: 1,
      processing: 2,
      scheduled: 3,
      failed: 4,
      messages: 5,
      batchesProcessing: 6,
      adapterRecordsDropped: 0,
      endpointRecordsDropped: 0,
      clientRecordsDropped: 0,
    } as unknown as DashboardStatistics,
  });
});

describe('dashboard count cards', () => {
  it.each([
    ['Enqueued', '/jobs/:state', 'enqueued'],
    ['Processing', '/jobs/:state', 'processing'],
    ['Scheduled', '/jobs/:state', 'scheduled'],
    ['Failed', '/jobs/:state', 'failed'],
    ['Messages', '/messages/:state', 'enqueued'],
    ['Batches', '/batches/:state', 'processing'],
  ])('the %s card opens a list page that has a route', (label, route, state) => {
    renderDashboard();

    fireEvent.click(screen.getByText(label));

    const matches = matchRoutes(appRoutes, screen.getByTestId('landed').textContent ?? '');
    expect(matches?.[0].route.path).toBe(route);
    expect(matches?.[0].params.state).toBe(state);
  });

  it('an unknown path matches no route', () => {
    expect(matchRoutes(appRoutes, '/messages')).toBeNull();
  });
});
