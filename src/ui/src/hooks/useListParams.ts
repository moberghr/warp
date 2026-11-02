import { useSearchParams } from 'react-router-dom';

// Page and type filter live in the URL so Back, refresh and shared links keep them; page size stays in localStorage.
export function useListParams() {
  const [params, setParams] = useSearchParams();
  const parsed = Number(params.get('page'));
  const page = Number.isInteger(parsed) && parsed > 0 ? parsed : 0;
  const type = params.get('type') || null;

  const update = (changes: Record<string, string | null>) =>
    setParams((prev) => {
      const next = new URLSearchParams(prev);
      for (const [key, value] of Object.entries(changes)) {
        if (value) next.set(key, value);
        else next.delete(key);
      }
      return next;
    }, { replace: true });

  return {
    page,
    type,
    setPage: (p: number) => update({ page: p > 0 ? String(p) : null }),
    // A new filter starts from the first page.
    setType: (t: string | null) => update({ type: t, page: null }),
  };
}
