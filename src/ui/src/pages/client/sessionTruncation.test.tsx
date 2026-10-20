import { describe, it, expect, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import type { AxiosResponse } from 'axios';
import api from '@/api/client';
import { createDemoAdapter } from '@/demo/adapter';
import ClientSessionPage from './ClientSessionPage';

// A session timeline holds the most recent 500 entries of each source; a longer session must say so.
const demo = createDemoAdapter(false);

afterEach(() => {
  api.defaults.adapter = demo;
});

function renderSession(isTruncated: boolean) {
  api.defaults.adapter = (config) => {
    const response = demo(config);
    if (!/^\/client\/sessions\//.test(config.url ?? '')) {
      return response;
    }

    return response.then((r): AxiosResponse => ({ ...r, data: { ...(r.data as object), isTruncated } }));
  };
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={['/client/sessions/s1']}>
        <Routes>
          <Route path="/client/sessions/:id" element={<ClientSessionPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('client session timeline cap', () => {
  it('says the session is longer than the timeline shows', async () => {
    renderSession(true);

    expect(await screen.findByText(/showing its most recent 500 client events/)).toBeTruthy();
  });

  it('says nothing extra for a session that fits', async () => {
    renderSession(false);

    await screen.findByText('Session timeline');
    expect(screen.queryByText(/most recent 500/)).toBeNull();
  });
});
