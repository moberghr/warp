import { describe, it, expect, beforeAll } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import api from '@/api/client';
import { createDemoAdapter } from '@/demo/adapter';
import IssuesPage from './IssuesPage';
import IssueDetailPage from './IssueDetailPage';

// Navigation coverage for the Issues (error-grouping §8.29) dashboard pages, driven against the demo
// mock adapter. Proves the list LOADS across all four sources, each row is a CLICKABLE link into the
// detail, and the detail renders its sample + resolution workflow.
beforeAll(() => {
  api.defaults.adapter = createDemoAdapter(false);
});

function renderAt(path: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/issues" element={<IssuesPage />} />
          <Route path="/issues/:fingerprint" element={<IssueDetailPage />} />
          <Route path="/trace/:traceId" element={<div>trace-stub</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('issues dashboard navigation', () => {
  it('lists issues across all four sources with clickable rows', async () => {
    renderAt('/issues');

    expect(await screen.findByRole('heading', { name: 'Issues' })).toBeTruthy();

    // The 4xx (StatusCode) endpoint group is hidden by default — reveal it so every source is visible.
    fireEvent.click(screen.getByRole('checkbox'));

    expect(await screen.findByText('System.NullReferenceException')).toBeTruthy(); // job
    expect(await screen.findByText('HTTP 422')).toBeTruthy();                      // endpoint (4xx)
    expect(await screen.findByText('HttpRequestException')).toBeTruthy();          // adapter
    expect(await screen.findByText('TypeError')).toBeTruthy();                     // client

    const links = await screen.findAllByRole('link');
    expect(links.some((a) => a.getAttribute('href')?.includes('/issues/'))).toBe(true);
  });

  it('issue detail loads with the sample stack and a Resolve button', async () => {
    renderAt('/issues/job-nullref-processorder');

    expect(await screen.findByRole('heading', { name: 'Latest sample' })).toBeTruthy();
    expect((await screen.findAllByText(/Object reference not set/)).length).toBeGreaterThan(0);
    expect(await screen.findByRole('button', { name: 'Resolve' })).toBeTruthy();
  });
});
