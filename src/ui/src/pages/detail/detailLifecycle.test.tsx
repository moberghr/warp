import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import type { AxiosAdapter, AxiosResponse, InternalAxiosRequestConfig } from 'axios';
import { toast } from 'sonner';
import api from '@/api/client';
import { createDemoAdapter } from '@/demo/adapter';
import { emit } from '@/lib/realtimeBus';
import * as data from '@/demo/data';
import { State } from '@/types';
import DetailPage from './DetailPage';

// How the detail page's live cards follow the job's state: output and progress poll while a job can
// still write them, read once more when it finishes, go quiet, and resume after a requeue.
const demo = createDemoAdapter(false);

afterEach(() => {
  api.defaults.adapter = demo;
  vi.useRealTimers();
  vi.restoreAllMocks();
});

const isLogs = (config: InternalAxiosRequestConfig) => /\/jobs\/[^/]+\/logs$/.test(config.url ?? '');
const isProgress = (config: InternalAxiosRequestConfig) => /\/jobs\/[^/]+\/progress$/.test(config.url ?? '');
const isDetail = (config: InternalAxiosRequestConfig) => /^\/detail\//.test(config.url ?? '');

// A server whose job state the test controls, and which counts what the page asks for.
function controllableServer(initial: State) {
  const server = { state: initial as State, logs: 0, progress: 0, failProgress: false, failRequeue: false };
  const adapter: AxiosAdapter = (config) => {
    if (isLogs(config)) server.logs++;
    if (isProgress(config)) {
      server.progress++;
      if (server.failProgress) return Promise.reject(new Error('down'));
    }
    if (config.method === 'post' && /\/requeue$/.test(config.url ?? '')) {
      if (server.failRequeue) return Promise.reject(new Error('Requeue refused'));
      server.state = State.Enqueued;
    }
    const response = demo(config);
    if (!isDetail(config)) return response;

    return response.then((r): AxiosResponse => ({ ...r, data: { ...(r.data as object), currentState: server.state } }));
  };

  return { server, adapter };
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

describe('job detail live cards', () => {
  it('read once more when the job finishes, stop, and resume after a requeue', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { server, adapter } = controllableServer(State.Processing);
    renderJob(data.IDS.processingJob, adapter);
    await screen.findByText('Page 1');

    // Live: both cards poll.
    const beforePoll = server.logs;
    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    expect(server.logs).toBeGreaterThan(beforePoll);

    // The job finishes; the page learns it on the next JobFinalized.
    server.state = State.Completed;
    const atFinish = { logs: server.logs, progress: server.progress };
    act(() => emit('JobFinalized'));
    await waitFor(() => expect(server.logs).toBe(atFinish.logs + 1));
    await waitFor(() => expect(server.progress).toBe(atFinish.progress + 1));

    // Finished: silent.
    const settled = { logs: server.logs, progress: server.progress };
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
    expect(server.logs).toBe(settled.logs);
    expect(server.progress).toBe(settled.progress);

    // Requeued from the page: live again.
    fireEvent.click(screen.getByRole('button', { name: 'Requeue' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Requeue' }));
    await waitFor(() => expect(server.logs).toBeGreaterThan(settled.logs));
    const resumed = server.logs;
    await act(async () => { await vi.advanceTimersByTimeAsync(4_000); });
    expect(server.logs).toBeGreaterThan(resumed);
  });

  it('do not poll a batch, which writes neither output nor progress', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { server, adapter } = controllableServer(State.Processing);
    renderJob(data.IDS.batch1, adapter);
    await screen.findByText('History');
    await waitFor(() => expect(server.progress).toBe(1));

    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });

    expect(server.logs).toBe(1);
    expect(server.progress).toBe(1);
  });

  it('marks reported progress when its refresh starts failing, instead of showing old bars as current', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { server, adapter } = controllableServer(State.Processing);
    renderJob(data.IDS.processingJob, adapter);
    await screen.findByText('Reported Progress');

    server.failProgress = true;
    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });

    expect(await screen.findByText('refresh failed')).toBeTruthy();
  });

  it('reports a failed requeue instead of swallowing it', async () => {
    const error = vi.spyOn(toast, 'error').mockImplementation(() => 0);
    const { server, adapter } = controllableServer(State.Completed);
    server.failRequeue = true;
    renderJob(data.IDS.completedJobWithTrace, adapter);
    await screen.findByText('History');

    fireEvent.click(screen.getByRole('button', { name: 'Requeue' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Requeue' }));

    await waitFor(() => expect(error).toHaveBeenCalledWith('Requeue refused'));
  });
});
