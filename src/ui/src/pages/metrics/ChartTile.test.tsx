import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MetricKind, MetricTier } from '@/types/metrics';
import { MetricTileKind, type MetricBoardTileResult } from '@/types/metricBoard';
import { emptyTileDraft } from './boardModel';
import { ChartTileCard } from './ChartTile';

const getMetricSeries = vi.fn();
vi.mock('@/api', () => ({ getMetricSeries: (...args: unknown[]) => getMetricSeries(...args) }));

// A controllable IntersectionObserver: each observed element waits until the test says it scrolled into view.
let observers: { callback: IntersectionObserverCallback; target: Element | null }[] = [];

class FakeIntersectionObserver {
  private readonly entry: { callback: IntersectionObserverCallback; target: Element | null };

  constructor(callback: IntersectionObserverCallback) {
    this.entry = { callback, target: null };
    observers.push(this.entry);
  }

  observe(target: Element) {
    this.entry.target = target;
  }

  disconnect() {
    observers = observers.filter((x) => x !== this.entry);
  }

  unobserve() {}

  takeRecords() {
    return [];
  }
}

function scroll(visible: boolean) {
  act(() => {
    for (const { callback, target } of observers) {
      callback([{ isIntersecting: visible, target } as unknown as IntersectionObserverEntry], {} as IntersectionObserver);
    }
  });
}

function chartResult(id: number, chartQuery: string | null): MetricBoardTileResult {
  return {
    tile: { ...emptyTileDraft, id, name: `Chart ${id}`, instrument: 'payment.results', kind: MetricTileKind.Chart, chartQuery },
    instrumentKind: MetricKind.Counter,
    unit: '{payment}',
    current: null,
    dayAgo: null,
    weekAgo: null,
    status: null,
    unacknowledgedStatus: null,
    annotations: [],
    sparkline: [],
  };
}

function renderCard(result: MetricBoardTileResult, refetchMs = 60_000) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <ChartTileCard result={result} selection={{ range: '24h', custom: null }} refetchMs={refetchMs} deploys={[]} onEdit={() => {}} onDelete={() => {}} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('ChartTileCard', () => {
  beforeEach(() => {
    observers = [];
    getMetricSeries.mockReset();
    getMetricSeries.mockResolvedValue({
      instrument: 'payment.results',
      kind: MetricKind.Counter,
      unit: '{payment}',
      from: '2026-11-18T12:00:00.000Z',
      to: '2026-11-19T12:00:00.000Z',
      resolution: MetricTier.Hourly,
      completeUntil: '2026-11-19T12:00:00.000Z',
      series: [],
      headlines: [],
    });
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('stops refetching while scrolled out of view, and resumes when back', async () => {
    vi.useFakeTimers();
    renderCard(chartResult(3, 'breakdown=gateway'), 1_000);
    const advance = (ms: number) => act(async () => {
      await vi.advanceTimersByTimeAsync(ms);
    });

    scroll(true);
    await advance(0);
    expect(getMetricSeries).toHaveBeenCalledTimes(1);
    await advance(1_000);
    expect(getMetricSeries).toHaveBeenCalledTimes(2);

    scroll(false);
    await advance(10_000);
    expect(getMetricSeries).toHaveBeenCalledTimes(2);

    scroll(true);
    await advance(1_000);
    expect(getMetricSeries.mock.calls.length).toBeGreaterThan(2);
  });

  it('disconnects its observer on unmount', () => {
    const { unmount } = renderCard(chartResult(4, null));
    expect(observers).toHaveLength(1);
    expect(observers[0].target).not.toBeNull();

    unmount();

    expect(observers).toHaveLength(0);
  });

  it('reads no series until the tile scrolls into view', async () => {
    renderCard(chartResult(1, 'breakdown=gateway&compare=7d'));

    expect(screen.getByText('Loads when in view.')).toBeTruthy();
    expect(getMetricSeries).not.toHaveBeenCalled();

    scroll(true);

    await waitFor(() => expect(getMetricSeries).toHaveBeenCalledTimes(1));
    const [instrument, query] = getMetricSeries.mock.calls[0] as [string, string];
    expect(instrument).toBe('payment.results');
    const params = new URLSearchParams(query);
    expect(params.get('breakdown')).toBe('gateway');
    expect(params.get('compare')).toBe('7d');
    expect(params.has('from')).toBe(true);
    expect(params.has('to')).toBe(true);
  });

  it('links the title to the metric page with the tile controls and the board range', () => {
    renderCard(chartResult(2, 'breakdown=gateway'));

    const href = screen.getByText('Chart 2').closest('a')!.getAttribute('href')!;

    expect(href).toContain('breakdown=gateway');
    expect(href).not.toContain('range=');
    expect(href.startsWith('/metrics/')).toBe(true);
  });
});
