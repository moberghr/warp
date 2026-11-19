import { useSearchParams } from 'react-router-dom';
import { parseJobListParams, writeJobListParams } from '@/pages/jobs/jobsModel';

// Page and type filter live in the URL so Back, refresh and shared links keep them; page size stays in localStorage.
export function useListParams() {
  const [params, setParams] = useSearchParams();
  const { page, type } = parseJobListParams(params);

  const update = (changes: { page?: number; type?: string | null }) =>
    setParams((prev) => writeJobListParams(prev, { ...parseJobListParams(prev), ...changes }), { replace: true });

  return {
    page,
    type,
    setPage: (p: number) => update({ page: p }),
    // A new filter starts from the first page.
    setType: (t: string | null) => update({ type: t, page: 0 }),
  };
}
