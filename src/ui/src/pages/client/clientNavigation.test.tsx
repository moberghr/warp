import { describe, it, expect, beforeAll } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import api from '@/api/client';
import { createDemoAdapter } from '@/demo/adapter';
import ClientPage from './ClientPage';
import ClientEventDetailPage from './ClientEventDetailPage';
import ClientSessionPage from './ClientSessionPage';

// Navigation coverage for the client-observability dashboard pages, driven against the demo mock adapter (the
// same data the marketing screenshots use). Proves each page LOADS and each list row is CLICKABLE and lands on
// its detail — the class of regression that shipped twice (a page that 400'd, rows that linked nowhere).
beforeAll(() => {
  // Swap the axios transport for the in-memory demo adapter, so the pages' react-query calls get mock data.
  api.defaults.adapter = createDemoAdapter(false);
});

function renderAt(path: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/client" element={<ClientPage />} />
          <Route path="/client/events/:id" element={<ClientEventDetailPage />} />
          <Route path="/client/sessions/:id" element={<ClientSessionPage />} />
          <Route path="/trace/:traceId" element={<div>trace-stub</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('client dashboard navigation', () => {
  it('Client page loads and lists clickable event rows', async () => {
    renderAt('/client');

    expect(await screen.findByRole('heading', { name: 'Traffic / Client' })).toBeTruthy();
    // The event stream rendered with rows that link into the detail route (the row IS clickable).
    const links = await screen.findAllByRole('link', undefined, { timeout: 5000 });
    expect(links.some(a => a.getAttribute('href')?.includes('/client/events/'))).toBe(true);
  });

  it('clicking an event row navigates to its detail (stack + message)', async () => {
    renderAt('/client');

    const links = await screen.findAllByRole('link', undefined, { timeout: 5000 });
    const eventLink = links.find(a => a.getAttribute('href')?.includes('/client/events/'));
    expect(eventLink).toBeTruthy();

    fireEvent.click(eventLink!);

    // The detail page fetched and rendered the event's message + stack.
    expect(await screen.findByRole('heading', { name: 'Message' })).toBeTruthy();
    expect(await screen.findByRole('heading', { name: 'Stack' })).toBeTruthy();
    expect((await screen.findAllByText(/Cannot read properties of undefined/)).length).toBeGreaterThan(0);
  });

  it('clicking a session link navigates to the session timeline with a trace link', async () => {
    renderAt('/client');

    // Session cell renders a truncated session id link.
    const sessionLink = (await screen.findAllByRole('link')).find(a => a.getAttribute('href')?.includes('/client/sessions/'));
    expect(sessionLink).toBeTruthy();

    fireEvent.click(sessionLink!);

    expect(await screen.findByRole('heading', { name: 'Session timeline' })).toBeTruthy();
    // The merged timeline exposes a drill-down to the job trace waterfall.
    await waitFor(() => {
      const traceLink = screen.getAllByRole('link').find(a => a.getAttribute('href')?.includes('/trace/'));
      expect(traceLink).toBeTruthy();
    });
  });

  it('event detail page loads directly by id', async () => {
    renderAt('/client/events/ce-1');

    expect(await screen.findByRole('heading', { name: 'Stack' })).toBeTruthy();
    expect((await screen.findAllByText(/Cannot read properties of undefined/)).length).toBeGreaterThan(0);
  });
});
