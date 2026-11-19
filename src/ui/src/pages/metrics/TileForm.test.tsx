import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { queryKeys } from '@/lib/queryClient';
import { MetricTileKind, type MetricBoardSummary, type MetricBoardTile } from '@/types/metricBoard';
import { SloKind, SloState, type SloList } from '@/types/slo';
import { emptyTileDraft } from './boardModel';
import { MAX_CHART_TILES } from './chartTileModel';
import { TileForm } from './MetricBoardPage';

const getSlos = vi.fn();
const getMetrics = vi.fn();
const getMetricBoardTiles = vi.fn();
vi.mock('@/api', () => ({
  getSlos: () => getSlos(),
  getMetrics: () => getMetrics(),
  getMetricBoardTiles: () => getMetricBoardTiles(),
  getMetricSeries: () => new Promise(() => {}),
}));

const boards: MetricBoardSummary[] = [
  { id: 1, name: 'Default', description: null, position: 0, application: null, isDefault: true, tileCount: 1, createdBy: null },
  { id: 5, name: 'Payments', description: null, position: 1, application: null, isDefault: false, tileCount: MAX_CHART_TILES, createdBy: null },
];

// Default holds one chart; Payments is full of charts.
const stored: MetricBoardTile[] = [
  { ...emptyTileDraft, id: 1, boardId: null, name: 'Default chart', instrument: 'c', kind: MetricTileKind.Chart },
  ...Array.from({ length: MAX_CHART_TILES }, (_, i) => ({ ...emptyTileDraft, id: 100 + i, boardId: 5, name: `Chart ${i}`, instrument: 'c', kind: MetricTileKind.Chart })),
];

function renderForm(draft: MetricBoardTile, seed?: (client: QueryClient) => void) {
  // Nothing goes stale in the test, so a seeded cache entry is read without a fetch.
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  seed?.(client);

  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <TileForm
          draft={draft}
          boards={boards}
          selection={{ range: '24h', custom: null }}
          refetchMs={false}
          deploys={[]}
          onChange={() => {}}
          onCancel={() => {}}
          onSave={() => {}}
          saving={false}
          serverError={null}
        />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('TileForm', () => {
  afterEach(() => {
    cleanup();
    vi.resetAllMocks();
  });

  it('reads the objectives from the SLO page cache (one key for the SLO list)', async () => {
    getMetrics.mockResolvedValue([]);
    getSlos.mockReturnValue(new Promise(() => {}));
    const list: SloList = {
      items: [{
        id: 4, name: 'Checkout success', kind: SloKind.SuccessRate, dimension: '*', application: null, targetValue: 0.99, percentile: null,
        windowSeconds: 3600, enabled: true, evaluated: true, attainment: 1, budgetRemaining: 1, burnRateShort: 0, burnRateLong: 0,
        state: SloState.Healthy, acknowledgedUntil: null, lastEvaluatedAt: null,
      }],
    };

    renderForm({ ...emptyTileDraft, kind: MetricTileKind.Slo }, (client) => client.setQueryData(queryKeys.slos, list));

    await waitFor(() => expect(screen.getByRole('option', { name: /Checkout success/ })).toBeTruthy());
    expect(getSlos).not.toHaveBeenCalled();
  });

  it('refuses a chart moved onto a board already full of charts, though the board it was opened from has room', async () => {
    getMetrics.mockResolvedValue([]);
    getMetricBoardTiles.mockResolvedValue(stored);

    // An edit of Default's chart, moved to Payments in the Board select.
    renderForm({ ...stored[0], boardId: 5 });

    await waitFor(() => expect(screen.getByText(/already holds 12 charts/)).toBeTruthy());
    expect((screen.getByText('Save') as HTMLButtonElement).disabled).toBe(true);
  });

  it('allows a chart moved off a full board onto one with room', async () => {
    getMetrics.mockResolvedValue([]);
    getMetricBoardTiles.mockResolvedValue(stored);

    // An edit of a Payments chart, moved to Default.
    renderForm({ ...stored[1], boardId: 1 });

    await waitFor(() => expect(getMetricBoardTiles).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByText(/already holds 12 charts/)).toBeNull());
  });
});
