import { describe, it, expect, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Routes, Route, useParams } from 'react-router-dom';
import type { DashboardStatistics } from '@/types';
import { useDashboardStore } from '@/stores/dashboard';
import DashboardPage from './DashboardPage';

// The count cards link into the list pages. A card pointing at a path App.tsx has no route for (the Messages card once
// linked to "/messages", which only exists as "/messages/:state") lands on a blank page, so each card is clicked and
// must reach a real route. The routes below are App.tsx's list routes; anything else falls through to "*".
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

function ListPage({ section }: { section: string }) {
  const { state } = useParams();

  return <p>{`${section} ${state}`}</p>;
}

function renderDashboard() {
  return render(
    <MemoryRouter initialEntries={['/']}>
      <Routes>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/jobs/:state" element={<ListPage section="jobs" />} />
        <Route path="/messages/:state" element={<ListPage section="messages" />} />
        <Route path="/batches/:state" element={<ListPage section="batches" />} />
        <Route path="*" element={<p>no route</p>} />
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
    ['Enqueued', 'jobs enqueued'],
    ['Processing', 'jobs processing'],
    ['Scheduled', 'jobs scheduled'],
    ['Failed', 'jobs failed'],
    ['Messages', 'messages enqueued'],
    ['Batches', 'batches processing'],
  ])('the %s card opens a list page that has a route', (label, page) => {
    renderDashboard();

    fireEvent.click(screen.getByText(label));

    expect(screen.getByText(page)).toBeTruthy();
    expect(screen.queryByText('no route')).toBeNull();
  });
});
