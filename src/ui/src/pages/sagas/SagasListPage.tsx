import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import axios from 'axios';
import { Panel } from '@/components/v2/Panel';
import { Pagination } from '@/components/Pagination';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { usePageParam } from '@/hooks/usePageParam';
import { usePageStore } from '@/stores/page';
import { useSagasList, useSagaTypes, useSagaStats } from '@/api/hooks/useSagas';

export default function SagasListPage() {
  const [typeFilter, setTypeFilter] = useState<string>('');
  const [keyFilter, setKeyFilter] = useState<string>('');
  const [page, setPage] = usePageParam();
  const [pageSize, setPageSize] = usePersistedPageSize();

  const listQuery = useSagasList(page, pageSize, typeFilter || undefined, keyFilter || undefined);
  const typesQuery = useSagaTypes();
  const statsQuery = useSagaStats();

  useEffect(() => {
    usePageStore.getState().set({
      title: 'Sagas',
      subtitle: 'Long-running message-driven workflows',
    });
    return () => usePageStore.getState().reset();
  }, []);

  const unavailable =
    axios.isAxiosError(listQuery.error) && listQuery.error.response?.status === 404;

  if (unavailable) {
    return (
      <div className="flex flex-col gap-3 py-5">
        <Panel>
          <div className="py-8 text-center text-[13px] text-text-mute">
            Sagas addon is not registered. Call <code className="font-mono text-xs text-foreground">opt.AddSagas()</code> in your Warp configuration to enable.
          </div>
        </Panel>
      </div>
    );
  }

  if (listQuery.error) return <ErrorState message="Unable to load sagas" />;

  const data = listQuery.data;
  if (!data) return <LoadingState />;

  const types = typesQuery.data ?? [];
  const stats = statsQuery.data ?? null;

  return (
    <div className="flex flex-col gap-3 py-5">
      {stats && (
        <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
          <Panel>
            <div className="px-4 py-3">
              <div className="text-[12px] text-text-mute">Live sagas</div>
              <div className="font-display text-[22px] font-semibold tracking-tight tabular-nums">{stats.liveSagas.toLocaleString()}</div>
            </div>
          </Panel>
          <Panel>
            <div className="px-4 py-3">
              <div className="text-[12px] text-text-mute">Started today</div>
              <div className="font-display text-[22px] font-semibold tracking-tight tabular-nums">{stats.startedToday.toLocaleString()}</div>
            </div>
          </Panel>
          <Panel>
            <div className="px-4 py-3">
              <div className="text-[12px] text-text-mute">Types in use</div>
              <div className="font-display text-[22px] font-semibold tracking-tight tabular-nums">{types.length}</div>
            </div>
          </Panel>
        </div>
      )}

      <div className="flex gap-2">
        <select
          className="border rounded-md px-2 py-1 text-sm bg-background"
          aria-label="Filter by saga type"
          value={typeFilter}
          onChange={(e) => { setTypeFilter(e.target.value); setPage(0); }}
        >
          <option value="">All types</option>
          {types.map(t => <option key={t} value={t}>{shortName(t)}</option>)}
        </select>
        <input
          type="text"
          className="border rounded-md px-2 py-1 text-sm bg-background flex-1 max-w-xs"
          placeholder="Search correlation key…"
          aria-label="Search correlation key"
          value={keyFilter}
          onChange={(e) => { setKeyFilter(e.target.value); setPage(0); }}
        />
      </div>

      <Panel className="overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full border-collapse">
            <thead>
              <tr className="bg-panel-2 border-b border-border">
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Type</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Correlation key</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Updated</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Created</th>
              </tr>
            </thead>
            <tbody>
              {data.items.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-3.5 py-8 text-center text-[12.5px] text-text-mute">
                    No sagas found
                  </td>
                </tr>
              ) : (
                data.items.map((s) => (
                  <tr key={s.id} className="border-b border-border last:border-b-0 hover:bg-panel-2/60">
                    <td className="px-3.5 py-2 text-[12.5px] font-medium">{shortName(s.type)}</td>
                    <td className="px-3.5 py-2 font-mono text-[12.5px]">
                      <Link to={`/sagas/${s.id}`} className="text-primary hover:underline">
                        {s.correlationKey}
                      </Link>
                    </td>
                    <td className="px-3.5 py-2 text-[12.5px]"><RelativeTime date={s.updatedAt} /></td>
                    <td className="px-3.5 py-2 text-[12.5px] text-text-mute"><RelativeTime date={s.createdAt} /></td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </Panel>

      <Pagination
        page={page}
        pageSize={pageSize}
        pageCount={Math.ceil(data.totalCount / pageSize)}
        onPageChange={setPage}
        onPageSizeChange={(size) => { setPageSize(size); setPage(0); }}
        totalCount={data.totalCount}
      />
    </div>
  );
}

function shortName(assemblyQualifiedName: string): string {
  const typeName = assemblyQualifiedName.split(',')[0];
  return typeName.split('.').pop() ?? typeName;
}
