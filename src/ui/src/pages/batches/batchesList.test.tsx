import { describe, it, expect, beforeAll } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import api from '@/api/client';
import { createDemoAdapter } from '@/demo/adapter';
import * as data from '@/demo/data';
import BatchesPage from './BatchesPage';

// #323: the batch list showed only id + progress, so with several batches running nobody could tell
// which was which. The name (stored in Type) and the queue are now on every row.
beforeAll(() => {
  api.defaults.adapter = createDemoAdapter(false);
});

function renderList() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={['/batches']}>
        <Routes>
          <Route path="/batches" element={<BatchesPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function rowById(id: string) {
  const link = screen.getAllByRole('link').find((x) => x.getAttribute('href') === `/detail/${id}`);

  return link?.closest('tr') as HTMLTableRowElement;
}

describe('batch list', () => {
  it('shows each batch name and queue in its row', async () => {
    renderList();

    await screen.findAllByText('Departure seat sync');
    const named = data.getBatches().find((x) => x.type === 'Departure seat sync' && x.queue === 'bulk');
    expect(named).toBeDefined();

    const row = rowById(named!.id);
    expect(row.textContent).toContain('Departure seat sync');
    expect(row.textContent).toContain('bulk');
  });

  it('shows a dash for a batch published without a name', async () => {
    renderList();

    await screen.findAllByText('Departure seat sync');
    const unnamed = data.getBatches().find((x) => x.type == null);
    expect(unnamed).toBeDefined();

    const row = rowById(unnamed!.id);
    const nameCell = row.querySelectorAll('td')[1] as HTMLElement;
    expect(nameCell.textContent).toBe('—');
  });
});
