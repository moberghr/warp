import { describe, it, expect } from 'vitest';
import type { ReactNode } from 'react';
import { renderHook } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { useRefreshKey } from './useRefreshKey';

describe('useRefreshKey', () => {
  it('reads refreshKey from the router location state', () => {
    const wrapper = ({ children }: { children: ReactNode }) => (
      <MemoryRouter initialEntries={[{ pathname: '/', state: { refreshKey: 42 } }]}>{children}</MemoryRouter>
    );
    const { result } = renderHook(() => useRefreshKey(), { wrapper });
    expect(result.current).toBe(42);
  });

  it('is undefined when the location carries no state', () => {
    const wrapper = ({ children }: { children: ReactNode }) => (
      <MemoryRouter initialEntries={['/']}>{children}</MemoryRouter>
    );
    const { result } = renderHook(() => useRefreshKey(), { wrapper });
    expect(result.current).toBeUndefined();
  });
});
