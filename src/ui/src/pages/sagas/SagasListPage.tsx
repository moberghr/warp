import { useState, useEffect, useCallback } from 'react';
import { Link } from 'react-router-dom';
import axios from 'axios';
import { Panel } from '@/components/v2/Panel';
import { Pagination } from '@/components/Pagination';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { useRefreshKey } from '@/hooks/useRefreshKey';
import { useRealtimeRefetch } from '@/hooks/useRealtimeRefetch';
import { usePageStore } from '@/stores/page';
import type { SagaListItem, SagaStats, PagedList } from '@/types';
import * as api from '@/api';

export default function SagasListPage() {
  const [data, setData] = useState<PagedList<SagaListItem> | null>(null);
  const [types, setTypes] = useState<string[]>([]);
  const [stats, setStats] = useState<SagaStats | null>(null);
  const [typeFilter, setTypeFilter] = useState<string>('');
  const [keyFilter, setKeyFilter] = useState<string>('');
  const [error, setError] = useState<string | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePersistedPageSize();
  const refreshKey = useRefreshKey();

  useEffect(() => {
    usePageStore.getState().set({ title: 'Sagas' });
    return () => usePageStore.getState().reset();
  }, []);

  const fetchAll = useCallback(async () => {
    try {
      const [list, typeList, statsData] = await Promise.all([
        api.listSagas(page, pageSize, typeFilter || undefined, keyFilter || undefined),
        api.getSagaTypes(),
        api.getSagaStats(),
      ]);
      setData(list);
      setTypes(typeList);
      setStats(statsData);
      setError(null);
      setUnavailable(false);
    } catch (e) {
      if (axios.isAxiosError(e) && e.response?.status === 404) {
        setUnavailable(true);
        setError(null);
        return;
      }
      setError('Unable to load sagas');
    }
  }, [page, pageSize, typeFilter, keyFilter]);

  useEffect(() => {
    fetchAll();
  }, [refreshKey, fetchAll]);

  // Live updates: saga lifecycle is driven by message arrivals (proxy commits inside
  // SaveChanges, which emits MessageEnqueued for routed children and JobFinalized when
  // those jobs settle). Either event indicates a saga may have changed.
  useRealtimeRefetch(['JobFinalized', 'MessageEnqueued'], fetchAll);

  if (unavailable) {
    return (
      <div className="flex flex-col gap-3 py-5">
        <Panel>
          <div className="py-8 text-center text-[13px] text-text-mute">
            Sagas addon is not registered. Call <code className="font-mono text-xs text-text-default">opt.AddSagas()</code> in your Warp configuration to enable.
          </div>
        </Panel>
      </div>
    );
  }

  if (error) return <ErrorState message={error} />;
  if (!data) return <LoadingState />;

  return (
    <div className="flex flex-col gap-3 py-5">
      {stats && (
        <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
          <Panel>
            <div className="px-4 py-3">
              <div className="text-[12px] text-text-mute">Live sagas</div>
              <div className="font-display text-[22px] font-semibold tracking-tight tabular-nums">{stats.liveSagas}</div>
            </div>
          </Panel>
          <Panel>
            <div className="px-4 py-3">
              <div className="text-[12px] text-text-mute">Started today</div>
              <div className="font-display text-[22px] font-semibold tracking-tight tabular-nums">{stats.startedToday}</div>
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
      />
    </div>
  );
}

function shortName(assemblyQualifiedName: string): string {
  const typeName = assemblyQualifiedName.split(',')[0];
  return typeName.split('.').pop() ?? typeName;
}
