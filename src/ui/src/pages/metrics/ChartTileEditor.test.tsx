import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MetricKind, type MetricInstrument } from '@/types/metrics';
import { ChartTileEditor } from './ChartTileEditor';

const getMetricSeries = vi.fn();
vi.mock('@/api', () => ({ getMetricSeries: (...args: unknown[]) => getMetricSeries(...args) }));

const instrument: MetricInstrument = {
  name: 'payment.results',
  meter: 'Shop',
  kind: MetricKind.Counter,
  unit: '{payment}',
  description: null,
  bounds: [],
  tagKeys: ['status'],
  firstSeenAt: '2026-11-19T00:00:00Z',
  lastSeenAt: '2026-11-19T00:00:00Z',
  collapsedAt: null,
  collapsedTags: [],
};

function renderEditor(query: string | null, onChange: (query: string | null) => void) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <ChartTileEditor instrument={instrument} query={query} onChange={onChange} selection={{ range: '24h', custom: null }} refetchMs={false} deploys={[]} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('ChartTileEditor text controls', () => {
  afterEach(() => {
    cleanup();
    vi.resetAllMocks();
  });

  it('writes the application back once, on blur, not per keystroke (each write refetches the preview)', () => {
    getMetricSeries.mockReturnValue(new Promise(() => {}));
    const onChange = vi.fn();
    renderEditor(null, onChange);
    const input = screen.getByLabelText('Application') as HTMLInputElement;

    for (const text of ['s', 'sh', 'sho', 'shop ']) {
      fireEvent.change(input, { target: { value: text } });
    }

    expect(onChange).not.toHaveBeenCalled();
    expect(input.value).toBe('shop ');
    fireEvent.blur(input);

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange).toHaveBeenCalledWith('application=shop');
  });

  it('writes the application back on Enter', () => {
    getMetricSeries.mockReturnValue(new Promise(() => {}));
    const onChange = vi.fn();
    renderEditor('application=old', onChange);
    const input = screen.getByLabelText('Application');

    fireEvent.change(input, { target: { value: '' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange).toHaveBeenCalledWith(null);
  });

  it('keeps a numerator that is invalid halfway through typing, and writes it once it is valid', () => {
    getMetricSeries.mockReturnValue(new Promise(() => {}));
    const onChange = vi.fn();
    renderEditor('breakdown=status&view=ratio', onChange);
    const input = screen.getByLabelText('Numerator pattern') as HTMLInputElement;

    fireEvent.change(input, { target: { value: '5*0' } });
    fireEvent.blur(input);

    expect(input.value).toBe('5*0');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(onChange).not.toHaveBeenCalled();

    fireEvent.change(input, { target: { value: '5*' } });
    fireEvent.blur(input);

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange).toHaveBeenCalledWith('breakdown=status&view=ratio&numerator=5*');
  });
});
