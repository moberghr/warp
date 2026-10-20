import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import type { AxiosAdapter, AxiosResponse, InternalAxiosRequestConfig } from 'axios';
import api from '@/api/client';
import { createDemoAdapter } from '@/demo/adapter';
import * as data from '@/demo/data';
import { State } from '@/types';
import DetailPage from './DetailPage';

// The job detail page reads handler output (/jobs/{id}/logs, paged, newest first) and reported
// progress (/jobs/{id}/progress, latest value per bar) separately from the detail. These cover the
// wiring a review found fragile: out-of-order responses, a failed load reading as "no output",
// refetching or polling a finished job, and the extension slot's timing.
const demo = createDemoAdapter(false);

afterEach(() => {
  api.defaults.adapter = demo;
  vi.useRealTimers();
});

function isLogs(config: InternalAxiosRequestConfig) {
  return /\/jobs\/[^/]+\/logs$/.test(config.url ?? '');
}

function isProgress(config: InternalAxiosRequestConfig) {
  return /\/jobs\/[^/]+\/progress$/.test(config.url ?? '');
}

function counting(matches: (config: InternalAxiosRequestConfig) => boolean) {
  const counter = { requests: 0 };
  const adapter: AxiosAdapter = (config) => {
    if (matches(config)) {
      counter.requests++;
    }

    return demo(config);
  };

  return { counter, adapter };
}

// Serves the detail with its state overridden, e.g. a page that has not yet seen the job start.
function withState(adapter: AxiosAdapter, currentState: State): AxiosAdapter {
  return (config) => {
    const response = adapter(config);
    if (!/\/detail\//.test(config.url ?? '')) {
      return response;
    }

    return response.then((r) => ({ ...r, data: { ...(r.data as object), currentState } }));
  };
}

function renderJob(id: string, adapter: AxiosAdapter = demo) {
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

describe('handler output', () => {
  it('shows the newest line first and pages back to the oldest', async () => {
    renderJob(data.IDS.processingJob);

    expect(await screen.findByText('Page 1')).toBeTruthy();
    const lines = screen.getAllByText(/Rendering PDF|Streamed batch 300\/300/).map((x) => x.textContent);
    expect(lines).toEqual(['Rendering PDF...', 'Streamed batch 300/300 (140,000 rows)']);

    for (const next of ['Page 2', 'Page 3', 'Page 4']) {
      fireEvent.click(screen.getByLabelText('Next page'));
      await screen.findByText(next);
    }

    expect(await screen.findByText('Connecting to data warehouse...')).toBeTruthy();
    await waitFor(() => expect((screen.getByLabelText('Next page') as HTMLButtonElement).disabled).toBe(true));
  });

  it('ignores a response that lands after the user moved to another page', async () => {
    let releaseStale: (() => void) | null = null;
    let holdNextPageZero = false;
    const adapter: AxiosAdapter = (config) => {
      if (isLogs(config) && holdNextPageZero && Number(config.params?.page ?? 0) === 0) {
        holdNextPageZero = false;
        const response = demo(config);

        return new Promise<AxiosResponse>((resolve) => {
          releaseStale = () => resolve(response);
        });
      }

      return demo(config);
    };
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderJob(data.IDS.processingJob, adapter);
    await screen.findByText('Page 1');

    // A tailing refetch of page 1 starts and stalls...
    holdNextPageZero = true;
    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    await waitFor(() => expect(releaseStale).not.toBeNull());

    // ...the user moves on, and that page answers first...
    fireEvent.click(screen.getByLabelText('Next page'));
    await screen.findByText('Page 2');
    await waitFor(() => expect(screen.queryByText('Rendering PDF...')).toBeNull());

    // ...then the stale page-1 response arrives and must not replace page 2's rows.
    await act(async () => releaseStale!());
    expect(screen.queryByText('Rendering PDF...')).toBeNull();
    expect(screen.getByText('Page 2')).toBeTruthy();
  });

  it('says so when the output fails to load instead of hiding the card', async () => {
    const adapter: AxiosAdapter = (config) =>
      isLogs(config) ? Promise.reject(new Error('boom')) : demo(config);
    renderJob(data.IDS.processingJob, adapter);

    expect(await screen.findByText(/Unable to load handler output/)).toBeTruthy();
  });

  it('shows the error, not the previous page, when a page change fails', async () => {
    const adapter: AxiosAdapter = (config) =>
      isLogs(config) && Number(config.params?.page ?? 0) === 1 ? Promise.reject(new Error('boom')) : demo(config);
    renderJob(data.IDS.processingJob, adapter);
    await screen.findByText('Rendering PDF...');

    fireEvent.click(screen.getByLabelText('Next page'));

    expect(await screen.findByText(/Unable to load handler output/)).toBeTruthy();
    expect(screen.getByText('Page 2')).toBeTruthy();
    expect(screen.queryByText('Rendering PDF...')).toBeNull();
  });

  it('tails the first page while the job is processing', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { counter, adapter } = counting(isLogs);
    renderJob(data.IDS.processingJob, adapter);
    await screen.findByText('Page 1');
    const before = counter.requests;

    await act(async () => { await vi.advanceTimersByTimeAsync(4_000); });

    expect(counter.requests).toBeGreaterThanOrEqual(before + 2);
  });

  it('tails a job the page still believes is enqueued, so a run that starts later is not missed', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { counter, adapter } = counting(isLogs);
    renderJob(data.IDS.processingJob, withState(adapter, State.Enqueued));
    await screen.findByText('Page 1');
    const before = counter.requests;

    await act(async () => { await vi.advanceTimersByTimeAsync(4_000); });

    expect(counter.requests).toBeGreaterThanOrEqual(before + 2);
  });

  it('does not poll a later page, so rows do not shift under the reader', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { counter, adapter } = counting(isLogs);
    renderJob(data.IDS.processingJob, adapter);
    await screen.findByText('Page 1');
    fireEvent.click(screen.getByLabelText('Next page'));
    await screen.findByText('Page 2');
    await waitFor(() => expect(screen.queryByText('Rendering PDF...')).toBeNull());
    const settled = counter.requests;

    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });

    expect(counter.requests).toBe(settled);
  });

  it('does not poll a finished job', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { counter, adapter } = counting(isLogs);
    renderJob(data.IDS.completedJobWithTrace, adapter);
    await screen.findByText('Handler Output');
    await waitFor(() => expect(counter.requests).toBe(1));

    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });

    expect(counter.requests).toBe(1);
  });

  it('renders the detail.logs slot with the page, before the output has loaded', async () => {
    const adapter: AxiosAdapter = (config) =>
      isLogs(config) ? new Promise<AxiosResponse>(() => {}) : demo(config);
    const { container } = renderJob(data.IDS.processingJob, adapter);

    await screen.findByText('History');
    expect(container.querySelector('[data-warp-slot="detail.logs"]')).not.toBeNull();
  });

  it('gives the progress and output cards distinct React keys', async () => {
    const errors = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      renderJob(data.IDS.processingJob);
      await screen.findByText('Rendering PDF...');

      const duplicateKey = errors.mock.calls.some((call) => String(call[0]).includes('same key'));
      expect(duplicateKey).toBe(false);
    } finally {
      errors.mockRestore();
    }
  });
});

describe('reported progress', () => {
  it('shows the latest value of each bar from the progress endpoint', async () => {
    renderJob(data.IDS.processingJob);

    expect(await screen.findByText('Reported Progress')).toBeTruthy();
    for (const [name, value] of [['download', '100%'], ['aggregate', '67%'], ['render', '22%']]) {
      expect(screen.getByText(name).nextElementSibling?.textContent).toBe(value);
    }
  });

  it('polls while the job is live, without refetching the detail', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let detailRequests = 0;
    const { counter, adapter } = counting(isProgress);
    renderJob(data.IDS.processingJob, (config) => {
      if (/\/detail\//.test(config.url ?? '')) {
        detailRequests++;
      }

      return adapter(config);
    });
    await screen.findByText('Reported Progress');
    const detailBefore = detailRequests;
    const progressBefore = counter.requests;

    await act(async () => { await vi.advanceTimersByTimeAsync(4_000); });

    expect(counter.requests).toBeGreaterThanOrEqual(progressBefore + 2);
    expect(detailRequests).toBe(detailBefore);
  });

  it('does not poll a finished job', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { counter, adapter } = counting(isProgress);
    renderJob(data.IDS.completedJobWithTrace, adapter);
    await screen.findByText('History');
    await waitFor(() => expect(counter.requests).toBe(1));

    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });

    expect(counter.requests).toBe(1);
  });
});
