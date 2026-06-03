import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Panel } from '@/components/v2/Panel';
import { StateBadge } from '@/components/StateBadge';
import { Pagination } from '@/components/Pagination';
import { shortId } from '@/utils/format';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { usePageStore } from '@/stores/page';
import { useBatchesList } from '@/api/hooks/useBatches';
import { GroupStateRail } from '@/pages/jobs/GroupStateRail';

export default function BatchesPage() {
  const { state } = useParams<{ state?: string }>();
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePersistedPageSize();

  useEffect(() => { setPage(0); }, [state]);

  const query = useBatchesList(state, page, pageSize);

  useEffect(() => {
    const title = state ? `${state.charAt(0).toUpperCase() + state.slice(1)} Batches` : 'Batches';
    const subtitle = query.data ? `${query.data.totalCount} total` : undefined;
    usePageStore.getState().set({ title, subtitle });
    return () => usePageStore.getState().reset();
  }, [state, query.data]);

  if (query.error) return <ErrorState message={(query.error as Error).message} />;
  if (!query.data) return <LoadingState />;

  const data = query.data;

  return (
    <div className="flex flex-col lg:flex-row">
      <GroupStateRail kind="batches" active={state} />
      <div className="flex-1 p-5 min-w-0 flex flex-col gap-3">
      <Panel className="overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full border-collapse">
            <thead>
              <tr className="bg-panel-2 border-b border-border">
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold w-[100px]">ID</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Progress</th>
                <th className="warp-eyebrow text-right px-3.5 py-2.5 text-text-mute font-semibold w-[100px]">State</th>
                <th className="warp-eyebrow text-right px-3.5 py-2.5 text-text-mute font-semibold w-[120px]">Created</th>
              </tr>
            </thead>
            <tbody>
              {data.items.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-3.5 py-8 text-center text-[12.5px] text-text-mute">
                    No batches found
                  </td>
                </tr>
              ) : (
                data.items.map((batch) => {
                  const done = batch.completedJobs + batch.failedJobs;
                  const pct = batch.totalJobs > 0 ? Math.round((done / batch.totalJobs) * 100) : 0;
                  const greenPct = batch.totalJobs > 0 ? (batch.completedJobs / batch.totalJobs) * 100 : 0;
                  const redPct = batch.totalJobs > 0 ? (batch.failedJobs / batch.totalJobs) * 100 : 0;
                  return (
                    <tr key={batch.id} className="border-b border-border last:border-b-0 hover:bg-panel-2/60">
                      <td className="px-3.5 py-2 font-mono text-[12.5px]">
                        <Link to={`/detail/${batch.id}`} className="text-primary hover:underline">
                          {shortId(batch.id)}
                        </Link>
                      </td>
                      <td className="px-3.5 py-2 text-[12.5px]">
                        <div className="flex items-center gap-2">
                          <div className="flex-1 h-2 bg-muted rounded-full overflow-hidden flex">
                            {greenPct > 0 && <div className="h-full bg-green-500 transition-all" style={{ width: `${greenPct}%` }} />}
                            {redPct > 0 && <div className="h-full bg-red-500 transition-all" style={{ width: `${redPct}%` }} />}
                          </div>
                          <span className="text-[11.5px] text-text-mute w-28 text-right shrink-0">
                            {done}/{batch.totalJobs} ({pct}%)
                          </span>
                        </div>
                      </td>
                      <td className="px-3.5 py-2 text-right text-[12.5px]"><StateBadge state={batch.currentState} /></td>
                      <td className="px-3.5 py-2 text-right text-[12.5px] text-text-mute">
                        <RelativeTime date={batch.createTime} />
                      </td>
                    </tr>
                  );
                })
              )}
            </tbody>
          </table>
        </div>
      </Panel>

      <Pagination page={page} pageCount={data.pageCount} onPageChange={setPage} pageSize={pageSize} onPageSizeChange={(s) => { setPageSize(s); setPage(0); }} />
      </div>
    </div>
  );
}
