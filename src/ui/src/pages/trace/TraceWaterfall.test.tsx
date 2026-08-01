import { describe, it, expect, beforeAll, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { AxiosError, type AxiosAdapter, type AxiosResponse } from 'axios';
import api from '@/api/client';
import { createDemoAdapter } from '@/demo/adapter';
import TraceWaterfall from './TraceWaterfall';

// An adapter that rejects every request with a real AxiosError carrying the given HTTP status — exercising the
// component's actual error branch (isAxiosError + response.status), which distinguishes 404 from a real error.
const statusAdapter =
  (status: number): AxiosAdapter =>
  (config) =>
    Promise.reject(
      new AxiosError('failed', 'ERR_BAD_RESPONSE', config, null, { status, data: null, statusText: '', headers: {}, config } as AxiosResponse),
    );

// The unified trace waterfall renders every span source for a trace (§8.28), from the demo mock. Proves the
// "single screen for a trace" shows client + server + jobs + outbound and links spans to their detail.
beforeAll(() => {
  api.defaults.adapter = createDemoAdapter(false);
});

function renderWaterfall() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <TraceWaterfall traceId="4bf92f3577b34da6a3ce929d0e0e4736" />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('trace waterfall', () => {
  it('renders every span source for the trace', async () => {
    renderWaterfall();

    // Client + server request (same name → two rows), a job (short name), and the outbound call — one screen.
    expect((await screen.findAllByText('POST /api/checkout')).length).toBeGreaterThanOrEqual(2);
    expect(await screen.findByText('ProcessOrderRequest')).toBeTruthy();      // job (short-named)
    expect(await screen.findByText('payments.Charge')).toBeTruthy();          // outbound adapter call
  });

  it('links a job span to its detail', async () => {
    renderWaterfall();

    const jobLink = await screen.findByText('ProcessOrderRequest');
    expect(jobLink.closest('a')?.getAttribute('href')).toContain('/detail/');
  });
});

describe('trace waterfall failure states', () => {
  afterEach(() => {
    api.defaults.adapter = createDemoAdapter(false);
  });

  it('surfaces a non-404 fetch failure instead of rendering blank', async () => {
    api.defaults.adapter = statusAdapter(500);
    renderWaterfall();

    expect(await screen.findByText(/couldn't load the trace waterfall/i)).toBeTruthy();
  });

  it('renders nothing (not an error) for a 404 — the expected empty-trace case', async () => {
    api.defaults.adapter = statusAdapter(404);
    const { container } = renderWaterfall();

    // The waterfall yields to the job graph below; no error strip.
    await new Promise((r) => setTimeout(r, 0));
    expect(screen.queryByText(/couldn't load/i)).toBeNull();
    expect(container.querySelector('.text-destructive')).toBeNull();
  });
});
