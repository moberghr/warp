import { useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';

/**
 * Page index kept in the `?page=` URL param so refresh and back-navigation
 * preserve the user's position. Page 0 removes the param; other query params
 * are left untouched.
 */
export function usePageParam(): [number, (next: number) => void] {
  const [searchParams, setSearchParams] = useSearchParams();
  const page = Number(searchParams.get('page') ?? '0') || 0;

  const setPage = useCallback(
    (next: number) => {
      setSearchParams(
        (prev) => {
          const params = new URLSearchParams(prev);
          if (next <= 0) {
            params.delete('page');
          } else {
            params.set('page', String(next));
          }

          return params;
        },
        { replace: true },
      );
    },
    [setSearchParams],
  );

  return [page, setPage];
}
