import { describe, it, expect } from 'vitest';
import type { ReactNode } from 'react';
import { act, renderHook } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { useListParams } from './useListParams';

const render = (url: string) => {
  const wrapper = ({ children }: { children: ReactNode }) => <MemoryRouter initialEntries={[url]}>{children}</MemoryRouter>;
  return renderHook(() => ({ params: useListParams(), search: useLocation().search }), { wrapper });
};

describe('useListParams', () => {
  it('reads page and type from the query string', () => {
    const { result } = render('/jobs/failed?type=Acme.SendInvoice&page=4');
    expect(result.current.params.page).toBe(4);
    expect(result.current.params.type).toBe('Acme.SendInvoice');
  });

  it.each(['', '?page=abc', '?page=-2', '?page=1.5'])('treats %s as page 0', (search) => {
    const { result } = render(`/jobs/failed${search}`);
    expect(result.current.params.page).toBe(0);
    expect(result.current.params.type).toBeNull();
  });

  it('changing type clears the page', () => {
    const { result } = render('/jobs/failed?type=A&page=4');
    act(() => result.current.params.setType('B'));
    expect(result.current.search).toBe('?type=B');
  });

  it('page 0 and a null type drop their params', () => {
    const { result } = render('/jobs/failed?type=A&page=4');
    act(() => result.current.params.setPage(0));
    act(() => result.current.params.setType(null));
    expect(result.current.search).toBe('');
  });
});
