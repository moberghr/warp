import { describe, it, expect, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import type { AxiosAdapter, InternalAxiosRequestConfig } from 'axios';
import api from '@/api/client';
import { createDemoAdapter } from '@/demo/adapter';
import { emit } from '@/lib/realtimeBus';
import * as data from '@/demo/data';
import { FlowCard } from '@/components/FlowCard';
import { DASHBOARD_LOCALE } from '@/utils/format';
import { State } from '@/types';
import type { ContinuationInfo } from '@/types';
import DetailPage from './DetailPage';

// The job detail used to be refetched on every cluster-wide JobFinalized (up to ~10/s under load)
// even for a job that had long finished, and it listed every spawned job and continuation.
const demo = createDemoAdapter(false);

afterEach(() => {
  api.defaults.adapter = demo;
});

function isDetail(config: InternalAxiosRequestConfig) {
  return /^\/detail\//.test(config.url ?? '');
}

function countingDetail() {
  const counter = { requests: 0 };
  const adapter: AxiosAdapter = (config) => {
    if (isDetail(config)) {
      counter.requests++;
    }

    return demo(config);
  };

  return { counter, adapter };
}

function renderJob(id: string, adapter: AxiosAdapter) {
  api.defaults.adapter = adapter;
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[`/detail/${id}`]}>
        <Routes>
          <Route path="/detail/:id" element={<DetailPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('job detail refresh', () => {
  it('ignores JobFinalized once the job has finished', async () => {
    const { counter, adapter } = countingDetail();
    renderJob(data.IDS.completedJobWithTrace, adapter);
    await screen.findByText('History');
    await waitFor(() => expect(counter.requests).toBe(1));

    act(() => emit('JobFinalized'));
    await act(async () => {});

    expect(counter.requests).toBe(1);
  });

  it('still refreshes on JobFinalized while the job is running', async () => {
    const { counter, adapter } = countingDetail();
    renderJob(data.IDS.processingJob, adapter);
    await screen.findByText('History');
    await waitFor(() => expect(counter.requests).toBe(1));

    act(() => emit('JobFinalized'));

    await waitFor(() => expect(counter.requests).toBe(2));
  });

  it('refreshes straight after a requeue, without waiting for an event', async () => {
    const { counter, adapter } = countingDetail();
    renderJob(data.IDS.completedJobWithTrace, adapter);
    await screen.findByText('History');
    await waitFor(() => expect(counter.requests).toBe(1));

    fireEvent.click(screen.getByRole('button', { name: 'Requeue' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Requeue' }));

    await waitFor(() => expect(counter.requests).toBe(2));
  });
});

describe('flow card', () => {
  const item = (i: number): ContinuationInfo => ({
    id: `00000000-0000-0000-0000-${String(i).padStart(12, '0')}`,
    kind: 1,
    currentState: State.Completed,
    type: 'Acme.Work',
    handlerType: null,
  });

  it('shows the total and says when the list is capped', () => {
    render(
      <MemoryRouter>
        <FlowCard
          continuations={[]}
          continuationCount={0}
          spawnedJobs={Array.from({ length: 50 }, (_, i) => item(i))}
          spawnedJobCount={60_000}
        />
      </MemoryRouter>,
    );

    expect(screen.getByText('Spawned jobs (60000)')).toBeTruthy();
    expect(screen.getByText(`Showing the first 50 of ${(60_000).toLocaleString(DASHBOARD_LOCALE)}.`)).toBeTruthy();
  });

  it('says nothing extra when everything is listed', () => {
    render(
      <MemoryRouter>
        <FlowCard continuations={[item(1)]} continuationCount={1} spawnedJobs={[]} spawnedJobCount={0} />
      </MemoryRouter>,
    );

    expect(screen.queryByText(/Showing the first/)).toBeNull();
  });
});
