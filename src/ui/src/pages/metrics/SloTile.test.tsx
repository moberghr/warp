import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MetricTileKind, type MetricBoardTileResult } from '@/types/metricBoard';
import { SloKind, SloState, type SloObjective } from '@/types/slo';
import { emptyTileDraft } from './boardModel';
import { SloTileCard } from './SloTile';
import { AddToBoardButton } from './AddToBoardButton';

const getMetricBoards = vi.fn();
const getMetricBoardTiles = vi.fn();
const upsertMetricBoardTile = vi.fn();
vi.mock('@/api', () => ({
  getMetricBoards: () => getMetricBoards(),
  getMetricBoardTiles: () => getMetricBoardTiles(),
  upsertMetricBoardTile: (body: unknown) => upsertMetricBoardTile(body),
}));

const objective: SloObjective = {
  id: 4,
  name: 'Payment success ratio ≥ 90%',
  kind: SloKind.MetricRatio,
  dimension: 'payment.results',
  application: null,
  targetValue: 0.9,
  percentile: null,
  windowSeconds: 3600,
  enabled: true,
  evaluated: true,
  attainment: 0.9431,
  budgetRemaining: 0.43,
  burnRateShort: 0.57,
  burnRateLong: 0.61,
  state: SloState.Healthy,
  acknowledgedUntil: null,
  lastEvaluatedAt: '2026-11-19T11:59:00Z',
};

function result(slo: SloObjective | null, missing = false): MetricBoardTileResult {
  return {
    tile: { ...emptyTileDraft, id: 14, name: 'Payment success', kind: MetricTileKind.Slo, sloDefinitionId: 4 },
    instrumentKind: null,
    unit: null,
    current: null,
    dayAgo: null,
    weekAgo: null,
    status: null,
    unacknowledgedStatus: null,
    annotations: [],
    sparkline: [],
    slo,
    missing,
  };
}

function wrap(node: React.ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>{node}</MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('SloTileCard', () => {
  afterEach(() => cleanup());

  it.each([
    [SloState.Healthy, 'Healthy'],
    [SloState.Warning, 'Warning'],
    [SloState.Breaching, 'Breaching'],
  ])('shows a %s objective with its attainment, target, budget and burns, linked to the SLO page', (state, label) => {
    wrap(<SloTileCard result={result({ ...objective, state })} onEdit={() => {}} onDelete={() => {}} />);

    expect(screen.getByText(label)).toBeTruthy();
    expect(screen.getByText('94.31%')).toBeTruthy();
    expect(screen.getByText('target 90.00%')).toBeTruthy();
    expect(screen.getByText('43%')).toBeTruthy();
    expect(screen.getByText('0.57× / 0.61×')).toBeTruthy();
    expect(screen.getByText(/^Evaluated /)).toBeTruthy();
    expect(screen.getByText('Payment success').closest('a')!.getAttribute('href')).toBe('/slo/4');
  });

  it('shows an acknowledged objective with when the acknowledgement ends', () => {
    wrap(<SloTileCard result={result({ ...objective, state: SloState.Acknowledged, acknowledgedUntil: '2026-11-19T13:00:00Z' })} onEdit={() => {}} onDelete={() => {}} />);

    expect(screen.getByText('Acknowledged')).toBeTruthy();
    expect(screen.getByText(/^Acknowledged until /)).toBeTruthy();
    expect(screen.getByText('94.31%')).toBeTruthy();
  });

  it('shows a NoData objective without its placeholder numbers', () => {
    wrap(<SloTileCard result={result({ ...objective, state: SloState.NoData })} onEdit={() => {}} onDelete={() => {}} />);

    expect(screen.getAllByText('No data').length).toBeGreaterThan(0);
    expect(screen.queryByText('94.31%')).toBeNull();
    expect(screen.queryByText('43%')).toBeNull();
    expect(screen.getByText('target 90.00%')).toBeTruthy();
  });

  it('shows a never-evaluated objective as Not evaluated with a neutral border, never a green Healthy', () => {
    // The API projects Healthy for an objective with no evaluation yet.
    wrap(<SloTileCard result={result({ ...objective, evaluated: false, state: SloState.Healthy, lastEvaluatedAt: null })} onEdit={() => {}} onDelete={() => {}} />);

    expect(screen.getByText('Not evaluated')).toBeTruthy();
    expect(screen.queryByText('Healthy')).toBeNull();
    expect(screen.queryByText('94.31%')).toBeNull();
    const card = screen.getByTestId('slo-tile');
    expect(card.className).toContain('border-border');
    expect(card.className).not.toContain('border-green');
  });

  it('shows an evaluated healthy objective with a green border', () => {
    wrap(<SloTileCard result={result(objective)} onEdit={() => {}} onDelete={() => {}} />);

    expect(screen.getByTestId('slo-tile').className).toContain('border-green');
  });

  it('shows a missing objective and removes the tile from it', () => {
    const onDelete = vi.fn();
    wrap(<SloTileCard result={result(null, true)} onEdit={() => {}} onDelete={onDelete} />);

    expect(screen.getByText('Missing objective')).toBeTruthy();
    fireEvent.click(screen.getByText('Remove tile'));

    expect(onDelete).toHaveBeenCalledTimes(1);
  });
});

describe('AddToBoardButton on the SLO page', () => {
  afterEach(() => {
    cleanup();
    vi.resetAllMocks();
  });

  it('saves an SLO tile of the objective on the picked board, named after the objective', async () => {
    getMetricBoards.mockResolvedValue([
      { id: 1, name: 'Default', description: null, position: 0, application: null, isDefault: true, tileCount: 0, createdBy: null },
      { id: 3, name: 'Payments', description: null, position: 1, application: null, isDefault: false, tileCount: 0, createdBy: null },
    ]);
    getMetricBoardTiles.mockResolvedValue([]);
    upsertMetricBoardTile.mockResolvedValue({ id: 99 });
    wrap(<AddToBoardButton source={{ kind: 'slo', objective }} />);

    fireEvent.click(screen.getByText('Add to board'));
    await waitFor(() => expect((screen.getByLabelText('Board') as HTMLSelectElement).options.length).toBe(2));
    fireEvent.change(screen.getByLabelText('Board'), { target: { value: '3' } });
    expect((screen.getByLabelText('Tile name') as HTMLInputElement).value).toBe(objective.name);
    await waitFor(() => expect((screen.getByText('Add') as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByText('Add'));

    await waitFor(() => expect(upsertMetricBoardTile).toHaveBeenCalledTimes(1));
    expect(upsertMetricBoardTile.mock.calls[0][0]).toMatchObject({
      id: 0,
      boardId: 3,
      name: objective.name,
      kind: MetricTileKind.Slo,
      sloDefinitionId: 4,
      instrument: '',
      chartQuery: null,
    });
    await waitFor(() => expect(screen.getByText('Payments')).toBeTruthy());
  });
});
