import { describe, it, expect, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import type { AxiosResponse } from 'axios';
import api from '@/api/client';
import { createDemoAdapter } from '@/demo/adapter';
import * as data from '@/demo/data';
import TracePage from './TracePage';

// The job graph reads a capped trace (500 jobs). A truncated one must say so rather than present
// the first 500 as the whole trace.
const demo = createDemoAdapter(false);

afterEach(() => {
  api.defaults.adapter = demo;
});

function renderTrace(isTruncated: boolean) {
  api.defaults.adapter = (config) => {
    const response = demo(config);
    if (!/^\/trace\//.test(config.url ?? '')) {
      return response;
    }

    return response.then((r): AxiosResponse => ({ ...r, data: { ...(r.data as object), isTruncated } }));
  };
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[`/trace/${data.IDS.traceId}`]}>
        <Routes>
          <Route path="/trace/:traceId" element={<TracePage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('trace job graph cap', () => {
  it('says the trace has more jobs when the graph was truncated', async () => {
    renderTrace(true);

    expect(await screen.findByText(/jobs shown; the trace has more/)).toBeTruthy();
  });

  it('shows a plain job count when the whole trace fits', async () => {
    renderTrace(false);

    expect(await screen.findByText(new RegExp(`^${data.traceJobs.length} jobs$`))).toBeTruthy();
  });
});

