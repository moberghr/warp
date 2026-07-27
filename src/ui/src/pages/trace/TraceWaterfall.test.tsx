import { describe, it, expect, beforeAll } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import api from '@/api/client';
import { createDemoAdapter } from '@/demo/adapter';
import TraceWaterfall from './TraceWaterfall';

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
